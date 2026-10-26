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
    /// Compiles script source to the runtime's asset format: SLua with Luau's compiler, LSL with the runtime's LSL
    /// compiler, both through the native library's compile export.
    /// </summary>
    internal sealed class ScriptCompiler
    {
        /// <summary>Compile source text as the language its first line (or, without one, its content) selects. Never throws for bad input.</summary>
        public CompileResult Compile(string source) => throw new NotImplementedException();
    }
}
