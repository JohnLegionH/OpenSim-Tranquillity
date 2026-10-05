/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Finds, checks and loads the patched joltc native for the platform the process runs on.
//
// The Jolt module ships its native under runtimes/<rid>/native/ next to the application's assemblies
// (the layout the BulletSim and ubODE modules use for theirs). Nothing is copied to the application root:
// this class picks the file for the running platform, checks its SHA-256 against the record below, loads it
// by full path, and hands that one handle to every P/Invoke into "joltc":
//   - JoltPhysicsSharp's own calls, through the binding's JoltApi.JoltDllImporterResolver hook, which its
//     resolver asks before it probes for a joltc of its own (JoltApi.cs in JoltPhysicsSharp at the commit
//     behind 2.19.1). The hook is on an internal class, so it is reached by reflection; without it the
//     binding would load whichever joltc its own probing finds, so a missing hook is an error.
//   - this assembly's own DllImport("joltc") calls (JoltPhysicsBackend), through SetDllImportResolver.
// The load happens once per process; every region and every backend shares it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    /// <summary>What was loaded: the platform, the file, its SHA-256 and whether the module ships a record of it.</summary>
    public sealed class JoltNativeInfo
    {
        public string Rid { get; }
        public string Path { get; }
        public string Sha256 { get; }
        public bool Recorded { get; }

        public JoltNativeInfo(string rid, string path, string sha256, bool recorded)
        {
            Rid = rid;
            Path = path;
            Sha256 = sha256;
            Recorded = recorded;
        }

        /// <summary>The one line the module logs at start.</summary>
        public string Describe() =>
            $"joltc for {Rid}: {Path} sha256 {Sha256} " +
            (Recorded ? "(the patched build this module ships)" : "(NOT a build this module ships; allowed by [Jolt] AllowUnrecordedNative)");
    }

    /// <summary>The native cannot be used: no build for this platform, the file is missing, or its hash is not recorded.</summary>
    public sealed class JoltNativeException : Exception
    {
        public JoltNativeException(string message) : base(message) { }
        public JoltNativeException(string message, Exception inner) : base(message, inner) { }
    }

    public static class JoltNative
    {
        /// <summary>
        /// The natives this module ships, by runtime identifier: file name and SHA-256 (upper-case hex). They are
        /// built by .github/workflows/joltc-native.yml; native/joltc/README.md lists the same hashes. A new
        /// platform is one more entry here plus its file under runtimes/&lt;rid&gt;/native/.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, (string File, string Sha256)> Shipped =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal)
            {
                ["win-x64"] = ("joltc.dll", "1F855744227482146708AB9AF683F4975CFC4C262030E22DAACE855F9D7479B6"),
                ["linux-x64"] = ("libjoltc.so", "EEAD7C1AA7FDFAC07132E26913E03B268DFD825CA72FFE6CEC3A181DA2EC95BB"),
            };

        public static string SupportedList => string.Join(", ", Shipped.Keys);

        private static readonly object s_gate = new object();
        private static JoltNativeInfo? s_loaded;
        private static IntPtr s_handle;

        /// <summary>
        /// The runtime identifier of a platform, in the runtimes/&lt;rid&gt;/native/ naming: win-, linux-, linux-musl-
        /// or osx-, then the process architecture (x64, x86, arm64, arm, ...). Null for an OS with no such folder.
        /// </summary>
        public static string? RidFor(OSPlatform? os, Architecture arch, bool musl = false)
        {
            string? prefix = null;
            if (os == OSPlatform.Windows) prefix = "win";
            else if (os == OSPlatform.Linux) prefix = musl ? "linux-musl" : "linux";
            else if (os == OSPlatform.OSX) prefix = "osx";
            else if (os == OSPlatform.FreeBSD) prefix = "freebsd";
            if (prefix == null)
                return null;
            return prefix + "-" + arch.ToString().ToLowerInvariant();
        }

        /// <summary>The runtime identifier of the running process.</summary>
        public static string CurrentRid()
        {
            OSPlatform? os =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OSPlatform.Linux :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX :
                RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ? OSPlatform.FreeBSD : (OSPlatform?)null;
            // A glibc build does not load on musl (Alpine); the runtime's own identifier says which libc it was built for.
            bool musl = RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase);
            return RidFor(os, RuntimeInformation.ProcessArchitecture, musl)
                ?? RuntimeInformation.OSDescription.Trim() + " " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        }

        /// <summary>The folder the runtimes/ tree sits in: the folder of this assembly, else the application's base folder.</summary>
        public static string DefaultBaseDirectory()
        {
            string location = typeof(JoltNative).Assembly.Location;
            string? dir = string.IsNullOrEmpty(location) ? null : System.IO.Path.GetDirectoryName(location);
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
        }

        /// <summary>The path of the native for <paramref name="rid"/> under <paramref name="baseDirectory"/>.</summary>
        public static string PathFor(string baseDirectory, string rid)
        {
            if (!Shipped.TryGetValue(rid, out var entry))
                throw new JoltNativeException(
                    $"Jolt physics has no native library for this platform ({rid}). Supported platforms: {SupportedList}. " +
                    "Choose another physics engine in [Startup] physics.");
            return System.IO.Path.Combine(baseDirectory, "runtimes", rid, "native", entry.File);
        }

        public static string Sha256Of(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>
        /// Finds the native for <paramref name="rid"/> and checks its hash, without loading it. Throws
        /// <see cref="JoltNativeException"/> when the platform has no native, the file is missing, or the hash is
        /// not recorded and <paramref name="allowUnrecorded"/> is false.
        /// </summary>
        public static JoltNativeInfo Check(string baseDirectory, string rid, bool allowUnrecorded)
        {
            string path = PathFor(baseDirectory, rid);
            if (!File.Exists(path))
                throw new JoltNativeException(
                    $"Jolt physics: the native library for {rid} is missing: {path}. The module ships it under " +
                    $"runtimes/{rid}/native/; copy the whole build output, including the runtimes folder.");

            string hash = Sha256Of(path);
            bool recorded = string.Equals(hash, Shipped[rid].Sha256, StringComparison.OrdinalIgnoreCase);
            if (!recorded && !allowUnrecorded)
                throw new JoltNativeException(
                    $"Jolt physics: {path} has sha256 {hash}, which is not the patched build this module ships for {rid} " +
                    $"({Shipped[rid].Sha256}). A stock joltc aborts the process when two regions step at once. Put back the " +
                    "file from the build output, or set [Jolt] AllowUnrecordedNative = true to run a build you made yourself.");
            return new JoltNativeInfo(rid, path, hash, recorded);
        }

        /// <summary>
        /// Loads the native for the running platform once per process and routes every "joltc" P/Invoke to it.
        /// Later calls return the first load's result, after checking it against <paramref name="allowUnrecorded"/>.
        /// </summary>
        public static JoltNativeInfo EnsureLoaded(bool allowUnrecorded)
        {
            lock (s_gate)
            {
                if (s_loaded != null)
                {
                    if (!s_loaded.Recorded && !allowUnrecorded)
                        throw new JoltNativeException(
                            $"Jolt physics: the loaded {s_loaded.Path} (sha256 {s_loaded.Sha256}) is not a build this module ships, " +
                            "and [Jolt] AllowUnrecordedNative is false.");
                    return s_loaded;
                }

                JoltNativeInfo info = Check(DefaultBaseDirectory(), CurrentRid(), allowUnrecorded);

                IntPtr handle;
                try
                {
                    handle = NativeLibrary.Load(info.Path);
                }
                catch (Exception e)
                {
                    throw new JoltNativeException($"Jolt physics: {info.Path} could not be loaded: {e.Message}", e);
                }

                HookBinding();
                s_handle = handle;
                NativeLibrary.SetDllImportResolver(typeof(JoltNative).Assembly, Resolve);
                s_loaded = info;
                return info;
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
            libraryName == "joltc" ? s_handle : IntPtr.Zero;

        /// <summary>The binding's resolver hook, or null when this version of JoltPhysicsSharp has none.</summary>
        public static EventInfo? BindingHook() =>
            typeof(JoltPhysicsSharp.Foundation).Assembly.GetType("JoltPhysicsSharp.JoltApi")
                ?.GetEvent("JoltDllImporterResolver", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        private static void HookBinding()
        {
            EventInfo? hook = BindingHook();
            if (hook == null || hook.EventHandlerType != typeof(DllImportResolver))
                throw new JoltNativeException(
                    "Jolt physics: JoltPhysicsSharp has no JoltApi.JoltDllImporterResolver hook, so the module cannot make it " +
                    "load the patched joltc. The binding is pinned to 2.19.1; a newer one needs this loader updated.");
            hook.AddEventHandler(null, new DllImportResolver(Resolve));
        }
    }
}
