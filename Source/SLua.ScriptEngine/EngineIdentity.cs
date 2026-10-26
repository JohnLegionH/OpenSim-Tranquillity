/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SLua.ScriptEngine.Tests")]

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The one place the engine's name is written. The name is the script header token
    /// ("//SLua:"), the configuration section and the value of <c>ScriptEngineName</c>, so renaming the engine
    /// is a change to <see cref="EngineName"/> alone.
    /// </summary>
    internal static class EngineIdentity
    {
        /// <summary>The engine's name, as the region compares it with the name a script's first line gives.</summary>
        public const string EngineName = "SLua";

        /// <summary>The first-line prefix that routes a script to this engine: "//" + name + ":".</summary>
        public const string HeaderToken = "//" + EngineName + ":";

        /// <summary>The configuration section that switches the engine on and holds its settings.</summary>
        public const string ConfigSection = EngineName;

        /// <summary>
        /// The engine that runs LSL scripts calling os*, iw* or bot* functions, as the library's refusal names it. Phlox is
        /// selected by a header naming its full engine name (<c>//InWorldz.Phlox:</c>), not by <c>//Phlox:</c>, so the
        /// library is given no header for it; <see cref="RefusalHint"/> tells the scripter to remove ours instead.
        /// </summary>
        public const string OtherEngineName = "Phlox";

        /// <summary>Added to the library's refusal of an os*, iw* or bot* call in a script whose first line names this engine.</summary>
        public static readonly string RefusalHint = "Remove the " + HeaderToken + " header from the first line.";

        private static readonly object s_lock = new object();
        private static bool s_applied;

        /// <summary>
        /// Names this engine and the other one to the library, once, before the first compile. The library has no built-in
        /// names and its refusal and error text use these.
        /// </summary>
        internal static void Apply()
        {
            lock (s_lock)
            {
                if (s_applied) return;
                Bindings.Host.SetEngineIdentity(EngineName, OtherEngineName, null);
                s_applied = true;
            }
        }
    }
}
