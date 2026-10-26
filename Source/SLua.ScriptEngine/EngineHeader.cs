/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;

namespace SLua.ScriptEngine
{
    /// <summary>The language a script owned by this engine is compiled as.</summary>
    public enum ScriptLanguage
    {
        Lsl,
        Slua,

        /// <summary>The header names a language this engine does not run.</summary>
        Unsupported,
    }

    /// <summary>
    /// Which engine a script belongs to, by its first line ("//&lt;engine&gt;:&lt;language&gt;"), as YEngine reads
    /// its own (XMREngine.OnRezScript) and as the region's engine selection reads it (Scene.ResolveScriptEngine).
    /// Every engine receives every rez; each one runs this test first and lets go of a script that is not its own.
    /// </summary>
    internal static class EngineHeader
    {
        /// <summary>
        /// The engine the script's first line names, or null: "//" at the very start, a first line ending at a '\n'
        /// past index 5, trimmed; the first ':' at index 3 or more; the name before it with trailing blanks trimmed.
        /// The comparison with engine names is ordinal (case matters).
        /// </summary>
        internal static string NamedEngine(string script)
        {
            if (script is null || !script.StartsWith("//"))
                return null;
            int lineEnd = script.IndexOf('\n');
            if (lineEnd <= 5)
                return null;
            string firstline = script[2..lineEnd].Trim();
            int colon = firstline.IndexOf(':');
            if (colon < 3)
                return null;
            string name = firstline[..colon].TrimEnd();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>
        /// The language part of the first line, or "" for none: the text after the first ':' when at least two
        /// characters follow it, trimmed and lowercased; a shorter one is ignored.
        /// </summary>
        internal static string Language(string script)
        {
            if (script is null || !script.StartsWith("//"))
                return "";
            int lineEnd = script.IndexOf('\n');
            if (lineEnd <= 5)
                return "";
            string firstline = script[2..lineEnd].Trim();
            int colon = firstline.IndexOf(':');
            if (colon > 0 && colon < firstline.Length - 2)
                return firstline[(colon + 1)..].Trim().ToLowerInvariant();
            return "";
        }

        /// <summary>
        /// The engine that runs the script: the engine its header names when that engine is loaded in the region,
        /// otherwise the engine the scene passed (the saved-state owner, or the default for a fresh compile).
        /// </summary>
        internal static string Owner(string script, string defaultEngine, IEnumerable<string> loadedEngines)
        {
            string named = NamedEngine(script);
            if (named is not null)
            {
                foreach (string loaded in loadedEngines)
                {
                    if (loaded == named)
                        return named;
                }
            }
            return defaultEngine;
        }

        /// <summary>
        /// The language of a script this engine owns. A first line naming this engine decides by its language part:
        /// "" or "lsl" is LSL, "slua" is SLua, anything else is unsupported. Without such a header the source decides:
        /// text that begins, after leading white space, with "--" is SLua, anything else is LSL.
        /// </summary>
        internal static ScriptLanguage LanguageFor(string script)
        {
            if (NamedEngine(script) == EngineIdentity.EngineName)
            {
                string language = Language(script);
                if (language == "slua")
                    return ScriptLanguage.Slua;
                return language.Length == 0 || language == "lsl" ? ScriptLanguage.Lsl : ScriptLanguage.Unsupported;
            }
            return !string.IsNullOrEmpty(script) && script.TrimStart().StartsWith("--") ? ScriptLanguage.Slua : ScriptLanguage.Lsl;
        }
    }
}
