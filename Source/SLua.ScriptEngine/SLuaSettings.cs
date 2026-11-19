/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using Nini.Config;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The settings in the engine's configuration section, [SLua]. The section is absent from the shipped defaults, so
    /// a stock install does not load the engine. Every default is the value used when the key is not set; none of
    /// them changes how another engine behaves.
    /// </summary>
    internal sealed class SLuaSettings
    {
        /// <summary>Loads the engine in the region (Enabled). Off by default.</summary>
        public bool Enabled { get; private set; }

        /// <summary>A folder holding the native library, or the library file itself. Null: use SLUA_SHIM_PATH, then the package's runtimes folder.</summary>
        public string NativeLibraryPath { get; private set; }

        /// <summary>The base folder; the engine appends its own name to get <see cref="StateRoot"/> (ScriptEnginesPath).</summary>
        public string ScriptEnginesPath { get; private set; } = "ScriptEngines";

        /// <summary>The engine's own folder under <see cref="ScriptEnginesPath"/>: the compile cache and state database live here.</summary>
        public string StateRoot { get; private set; } = Path.Combine("ScriptEngines", EngineIdentity.EngineName);

        /// <summary>Compiled scripts are kept on disk under <see cref="StateRoot"/> and reused while the source and runtime are unchanged (CompileCache).</summary>
        public bool CompileCache { get; private set; } = true;

        /// <summary>Whether the engine keeps its scripts' state across a restart (SaveScriptState). Off, a restart starts every script fresh and no database is made.</summary>
        public bool SaveScriptState { get; private set; } = true;

        /// <summary>How many times a failed read of a script's saved state is tried again before the script is held (StateLoadRetries).</summary>
        public int StateLoadRetries { get; private set; } = 3;

        /// <summary>How long the engine waits between those tries, in milliseconds (StateLoadRetryDelayMs).</summary>
        public int StateLoadRetryDelayMs { get; private set; } = 500;

        public static SLuaSettings Read(IConfigSource source, IEngineLog log)
        {
            var s = new SLuaSettings();
            IConfig c = source?.Configs[EngineIdentity.ConfigSection];
            if (c == null)
                return s;

            s.Enabled = c.GetBoolean("Enabled", false);

            string lib = c.GetString("NativeLibraryPath", "");
            s.NativeLibraryPath = string.IsNullOrWhiteSpace(lib) ? null : lib;

            string path = c.GetString("ScriptEnginesPath", s.ScriptEnginesPath);
            s.ScriptEnginesPath = string.IsNullOrWhiteSpace(path) ? "ScriptEngines" : path;
            s.StateRoot = Path.Combine(s.ScriptEnginesPath, EngineIdentity.EngineName);
            s.CompileCache = c.GetBoolean("CompileCache", s.CompileCache);
            s.SaveScriptState = c.GetBoolean("SaveScriptState", s.SaveScriptState);
            s.StateLoadRetries = Math.Max(0, c.GetInt("StateLoadRetries", s.StateLoadRetries));
            s.StateLoadRetryDelayMs = Math.Max(0, c.GetInt("StateLoadRetryDelayMs", s.StateLoadRetryDelayMs));
            return s;
        }
    }
}
