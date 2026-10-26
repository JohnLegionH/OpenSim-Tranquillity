/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;

namespace Luau.ScriptEngine
{
    /// <summary>
    /// Compiled assets kept on disk, one file per (source, language, runtime stamp). A stamp change gives every
    /// source a new file name, so entries from an older runtime are never read; the code does not delete them.
    /// </summary>
    internal sealed class CompileCache
    {
        public CompileCache(string directory, string stamp, IEngineLog log = null) => throw new NotImplementedException();

        public string Directory => throw new NotImplementedException();

        /// <summary>The runtime's version text (runtime=&lt;pin&gt; api=&lt;n&gt;) plus a hash of the bound function list.</summary>
        public static string MakeStamp(string runtimeVersion, string boundFunctionHash) => throw new NotImplementedException();

        /// <summary>The engine's cache folder under its own state folder.</summary>
        public static string DirectoryFor(LuauSettings settings) => throw new NotImplementedException();

        public string PathFor(string text, ScriptLanguage language) => throw new NotImplementedException();

        public bool TryGet(string text, ScriptLanguage language, out byte[] asset) => throw new NotImplementedException();

        public void Put(string text, ScriptLanguage language, byte[] asset) => throw new NotImplementedException();
    }
}
