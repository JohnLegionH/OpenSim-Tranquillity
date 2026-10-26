/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Luau.ScriptEngine.Bindings;

namespace Luau.ScriptEngine
{
    /// <summary>What the engine needs from the host to find and check the native library; tests replace the parts.</summary>
    internal sealed class NativeRuntimeOptions
    {
        /// <summary>[NGC.Luau] NativeLibraryPath: a folder holding the library, or the library file. Null: none configured.</summary>
        public string ConfiguredPath { get; set; }

        /// <summary>Reads an environment variable; the engine reads SLUA_SHIM_PATH through it.</summary>
        public Func<string, string> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

        /// <summary>The folder the standard package layout (runtimes/&lt;rid&gt;/native) is probed under.</summary>
        public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

        public Func<string, bool> FileExists { get; set; } = File.Exists;

        /// <summary>Loads the library file at a path into the process; IntPtr.Zero when it cannot be loaded.</summary>
        public Func<string, IntPtr> TryLoad { get; set; } = path => NativeLibrary.TryLoad(path, out IntPtr handle) ? handle : IntPtr.Zero;

        /// <summary>Asks the loaded library for its API version. A test replaces it to report another version.</summary>
        public Func<int> ApiVersion { get; set; } = Host.ApiVersion;
    }

    internal sealed class RuntimeLoadResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string LibraryPath { get; set; }
        public int ApiVersion { get; set; }
    }

    /// <summary>Finds the native Luau runtime library, loads it and checks its API version. Not implemented yet.</summary>
    internal static class NativeRuntime
    {
        /// <summary>The C API version this engine is written for; a library reporting any other is not used.</summary>
        public const int RequiredApiVersion = 3;

        internal static string Rid => "";
        internal static string LibraryFileName => OperatingSystem.IsWindows() ? "slua_shim.dll" : "libslua_shim.so";
        internal static IReadOnlyList<string> Candidates(NativeRuntimeOptions options) => Array.Empty<string>();
        internal static RuntimeLoadResult Load(NativeRuntimeOptions options) => new RuntimeLoadResult { Ok = false, Message = "not implemented" };
        internal static bool Gate(NativeRuntimeOptions options, IEngineLog log) => false;
    }
}
