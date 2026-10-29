/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;

namespace SLua.ScriptEngine
{
    public partial class SLuaEngine : IScriptEngineSelection
    {
        /// <summary>The marker that asks for this engine: the first line of an SLua script.</summary>
        private const string SluaMarker = "--!slua";

        /// <summary>The engine has no name besides its own.</summary>
        public IReadOnlyList<string> HeaderAliases => Array.Empty<string>();

        /// <summary>
        /// Asked by the region for a script whose first line names no loaded engine and which no engine holds saved
        /// state for. Yes for a script whose first line is "--!slua", and, when ClaimHeaderlessScripts is on, for every
        /// such script (it then runs as LSL unless its text begins with "--", see <see cref="EngineHeader.LanguageFor"/>).
        /// An engine that is not running, or has switched itself off, claims nothing, so the script goes where it goes
        /// without this engine. A script with no source (a crossing) is never claimed: saved state routes it.
        /// </summary>
        public bool ClaimsScript(string scriptSource)
        {
            if (!m_enabled || m_disabled || string.IsNullOrEmpty(scriptSource))
                return false;
            return m_settings.ClaimHeaderlessScripts || BeginsWithSluaMarker(scriptSource);
        }

        /// <summary>
        /// A script saved with the viewer's "Lua" or "LSL on Lua" choice is this engine's, whatever its first line says.
        /// Like <see cref="ClaimsScript"/>, an engine that is not running, or has switched itself off, accepts nothing.
        /// </summary>
        public bool AcceptsCompileTarget(ScriptCompileTarget target)
            => m_enabled && !m_disabled && target != ScriptCompileTarget.Unspecified;

        /// <summary>
        /// True when the text, after leading white space, begins with "--!slua" and the marker ends there (end of text,
        /// white space). The marker is read without regard to case, as Phlox reads its own.
        /// </summary>
        internal static bool BeginsWithSluaMarker(string source)
        {
            ReadOnlySpan<char> text = source.AsSpan().TrimStart();
            if (!text.StartsWith(SluaMarker, StringComparison.OrdinalIgnoreCase))
                return false;
            return text.Length == SluaMarker.Length || char.IsWhiteSpace(text[SluaMarker.Length]);
        }
    }
}
