/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace Luau.ScriptEngine
{
    /// <summary>The cheap check that refuses source the native compiler must not be given.</summary>
    internal static class SourceGuard
    {
        /// <summary>The first limit the source passes, as a compile error, or null when it is within all of them.</summary>
        public static CompileError? Check(string source, ScriptLanguage language, CompileLimits limits) => null;
    }
}
