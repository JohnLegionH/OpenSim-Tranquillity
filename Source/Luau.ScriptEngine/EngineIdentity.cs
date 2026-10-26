/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Luau.ScriptEngine.Tests")]

namespace Luau.ScriptEngine
{
    /// <summary>
    /// The one place the engine's name is written. The name is the script header token
    /// ("//NGC.Luau:"), the configuration section and the value of <c>ScriptEngineName</c>, so renaming the engine
    /// is a change to <see cref="EngineName"/> alone. The name is provisional.
    /// </summary>
    internal static class EngineIdentity
    {
        /// <summary>The engine's name, as the region compares it with the name a script's first line gives.</summary>
        public const string EngineName = "NGC.Luau";

        /// <summary>The first-line prefix that routes a script to this engine: "//" + name + ":".</summary>
        public const string HeaderToken = "//" + EngineName + ":";

        /// <summary>The configuration section that switches the engine on and holds its settings.</summary>
        public const string ConfigSection = EngineName;
    }
}
