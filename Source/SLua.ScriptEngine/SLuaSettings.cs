/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

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

        public static SLuaSettings Read(IConfigSource source, IEngineLog log)
        {
            var s = new SLuaSettings();
            IConfig c = source?.Configs[EngineIdentity.ConfigSection];
            if (c == null)
                return s;

            s.Enabled = c.GetBoolean("Enabled", false);

            string lib = c.GetString("NativeLibraryPath", "");
            s.NativeLibraryPath = string.IsNullOrWhiteSpace(lib) ? null : lib;
            return s;
        }
    }
}
