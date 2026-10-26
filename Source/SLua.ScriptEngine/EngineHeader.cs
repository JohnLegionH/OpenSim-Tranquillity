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
        /// The name is returned as written; comparing it with an engine's name is the caller's business.
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
        /// The engine that runs the script: the engine its header names when that engine is loaded in the region
        /// (this engine's name without regard to case, every other name as written), otherwise the engine
        /// the scene passed (the saved-state owner, or the default for a fresh compile). The region itself compares
        /// names as written (Scene.ResolveScriptEngine), so a lower-case header reaches this test only when the
        /// scene already chose this engine.
        /// </summary>
        internal static string Owner(string script, string defaultEngine, IEnumerable<string> loadedEngines)
        {
            string named = NamedEngine(script);
            if (named is not null)
            {
                foreach (string loaded in loadedEngines)
                {
                    // Only this engine's own name is read without regard to case. Another engine named in a
                    // different case is not its script as far as the region is concerned, and letting go of it
                    // here would leave a script the region gave to this engine running nowhere.
                    if (loaded == named
                        || (loaded == EngineIdentity.EngineName
                            && string.Equals(loaded, named, System.StringComparison.OrdinalIgnoreCase)))
                        return loaded;
                }
            }
            return defaultEngine;
        }

        /// <summary>
        /// The language of a script this engine owns. A first line naming this engine decides by its language part:
        /// "" or "slua" is SLua, "lsl" is LSL, anything else is unsupported (the engine name and the language are read
        /// without regard to case). Without such a header the source decides:
        /// text that begins, after leading white space, with "--" is SLua, anything else is LSL.
        /// </summary>
        internal static ScriptLanguage LanguageFor(string script)
        {
            if (string.Equals(NamedEngine(script), EngineIdentity.EngineName, System.StringComparison.OrdinalIgnoreCase))
            {
                string language = Language(script);
                if (language.Length == 0 || language == "slua")
                    return ScriptLanguage.Slua;
                return language == "lsl" ? ScriptLanguage.Lsl : ScriptLanguage.Unsupported;
            }
            return !string.IsNullOrEmpty(script) && script.TrimStart().StartsWith("--") ? ScriptLanguage.Slua : ScriptLanguage.Lsl;
        }
    }
}
