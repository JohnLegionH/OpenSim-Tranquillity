/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>What the engine needs from the host to find and check the native library; tests replace the parts.</summary>
    internal sealed class NativeRuntimeOptions
    {
        /// <summary>[SLua] NativeLibraryPath: a folder holding the library, or the library file. Null: none configured.</summary>
        public string ConfiguredPath { get; set; }

        /// <summary>Reads an environment variable; the engine reads SLUA_SHIM_PATH through it.</summary>
        public Func<string, string> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

        /// <summary>The folder the standard package layout (runtimes/&lt;rid&gt;/native) is probed under.</summary>
        public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

        public Func<string, bool> FileExists { get; set; } = File.Exists;

        /// <summary>
        /// Loads the library file at a path into the process; IntPtr.Zero when it cannot be loaded. Null means the
        /// real loader, and only then does the loaded library become the one the bindings call.
        /// </summary>
        public Func<string, IntPtr> TryLoad { get; set; }

        /// <summary>Asks the loaded library for its API version. Null asks the library; a test replaces it to report another version.</summary>
        public Func<int> ApiVersion { get; set; }
    }

    internal sealed class RuntimeLoadResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string LibraryPath { get; set; }
        public int ApiVersion { get; set; }
    }

    /// <summary>
    /// Finds the native Luau runtime library, loads it and checks its API version. A library that cannot be found or
    /// loaded, or that reports another API version, is reported in the result and the engine does not register.
    /// </summary>
    internal static class NativeRuntime
    {
        /// <summary>The C API version this engine is written for; a library reporting any other is not used.</summary>
        public const int RequiredApiVersion = 3;

        private const string ImportName = "slua_shim";
        private static readonly object s_lock = new object();
        private static IntPtr s_handle;
        private static bool s_resolverSet;

        /// <summary>The runtime identifier of the folder under runtimes/ that a NuGet package puts native files in.</summary>
        internal static string Rid
        {
            get
            {
                string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
                string arch = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.Arm64 => "arm64",
                    Architecture.Arm => "arm",
                    Architecture.X86 => "x86",
                    _ => "x64",
                };
                return os + "-" + arch;
            }
        }

        internal static string LibraryFileName => OperatingSystem.IsWindows() ? "slua_shim.dll" : "libslua_shim.so";

        /// <summary>
        /// The library files to try, in order: the configured path, the SLUA_SHIM_PATH environment variable, then the
        /// standard package layout under the application folder (runtimes/&lt;rid&gt;/native, then the folder itself).
        /// A path that names the library file is used as it is; any other is taken as the folder holding it.
        /// </summary>
        internal static IReadOnlyList<string> Candidates(NativeRuntimeOptions options)
        {
            var list = new List<string>();
            void Add(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string file = string.Equals(Path.GetFileName(path), LibraryFileName, StringComparison.OrdinalIgnoreCase)
                    ? path
                    : Path.Combine(path, LibraryFileName);
                if (!list.Contains(file)) list.Add(file);
            }

            Add(options.ConfiguredPath);
            Add(options.GetEnvironmentVariable?.Invoke("SLUA_SHIM_PATH"));
            if (!string.IsNullOrWhiteSpace(options.BaseDirectory))
            {
                Add(Path.Combine(options.BaseDirectory, "runtimes", Rid, "native"));
                Add(options.BaseDirectory);
            }
            return list;
        }

        internal static RuntimeLoadResult Load(NativeRuntimeOptions options)
        {
            Func<string, bool> exists = options.FileExists ?? File.Exists;
            bool real = options.TryLoad == null;
            Func<string, IntPtr> tryLoad = options.TryLoad ?? (path => NativeLibrary.TryLoad(path, out IntPtr h) ? h : IntPtr.Zero);

            string loadedPath = null;
            IntPtr handle = IntPtr.Zero;
            IReadOnlyList<string> candidates = Candidates(options);
            foreach (string candidate in candidates)
            {
                if (!exists(candidate)) continue;
                handle = tryLoad(candidate);
                if (handle != IntPtr.Zero)
                {
                    loadedPath = candidate;
                    break;
                }
            }

            if (loadedPath == null)
            {
                return new RuntimeLoadResult
                {
                    Message = $"The native runtime library {LibraryFileName} was not found or could not be loaded; the engine is not registered. " +
                              $"Looked for: {string.Join(", ", candidates)}. " +
                              "Set NativeLibraryPath in the engine configuration section, or the SLUA_SHIM_PATH environment variable, to the folder holding it.",
                };
            }

            if (real) Bind(handle);

            int version;
            try
            {
                version = options.ApiVersion != null ? options.ApiVersion() : Host.ApiVersion();
            }
            catch (Exception e) when (e is EntryPointNotFoundException || e is DllNotFoundException || e is BadImageFormatException)
            {
                return new RuntimeLoadResult
                {
                    LibraryPath = loadedPath,
                    Message = $"The native runtime library {loadedPath} does not export slua_api_version ({e.GetType().Name}: {e.Message}); " +
                              "it is not a library this engine can use, and the engine is not registered.",
                };
            }

            if (version != RequiredApiVersion)
            {
                return new RuntimeLoadResult
                {
                    LibraryPath = loadedPath,
                    ApiVersion = version,
                    Message = $"The native runtime library {loadedPath} reports API version {version}; this engine requires {RequiredApiVersion}. The engine is not registered.",
                };
            }

            return new RuntimeLoadResult
            {
                Ok = true,
                LibraryPath = loadedPath,
                ApiVersion = version,
                Message = $"Native runtime {loadedPath}, API version {version}",
            };
        }

        /// <summary>Loads and checks the library and writes one log line: the error that keeps the engine out, or the library in use.</summary>
        internal static bool Gate(NativeRuntimeOptions options, IEngineLog log)
        {
            RuntimeLoadResult result = Load(options);
            if (result.Ok)
                log.Info(result.Message);
            else
                log.Error(result.Message);
            return result.Ok;
        }

        /// <summary>The loader the engine uses in a region: the library is searched for with the configured path.</summary>
        internal static bool GateForEngine(SLuaSettings settings, IEngineLog log) =>
            Gate(new NativeRuntimeOptions { ConfiguredPath = settings.NativeLibraryPath }, log);

        private static void Bind(IntPtr handle)
        {
            lock (s_lock)
            {
                s_handle = handle;
                if (s_resolverSet) return;
                NativeLibrary.SetDllImportResolver(typeof(Host).Assembly, Resolve);
                s_resolverSet = true;
            }
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
            name == ImportName ? s_handle : IntPtr.Zero;
    }
}
