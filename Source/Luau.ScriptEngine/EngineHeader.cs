/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;

namespace Luau.ScriptEngine
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
    /// Which engine a script belongs to, by its first line ("//&lt;engine&gt;:&lt;language&gt;"). Not implemented yet.
    /// </summary>
    internal static class EngineHeader
    {
        internal static string NamedEngine(string script) => null;
        internal static string Language(string script) => "";
        internal static string Owner(string script, string defaultEngine, IEnumerable<string> loadedEngines) => defaultEngine;
        internal static ScriptLanguage LanguageFor(string script) => ScriptLanguage.Lsl;
    }
}
