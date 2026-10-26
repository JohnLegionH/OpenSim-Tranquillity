/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;

namespace SLua.ScriptEngine
{
    /// <summary>One compile error: the source line (1-based, 0 when the compiler gave none) and the compiler's text.</summary>
    internal readonly record struct CompileError(int Line, string Message)
    {
        /// <summary>
        /// The text the viewer's script editor shows in its error pane, "(line,col) Error: message"
        /// (the format YEngine's compile errors use). The compiler gives no column, so it is 0.
        /// </summary>
        public string ForEditor() => $"({Line},0) Error: {Message}";
    }

    /// <summary>The outcome of one compile: bytecode, or the errors that stopped it.</summary>
    internal sealed class CompileResult
    {
        /// <summary>The compiled asset (header plus bytecode); null when the compile failed.</summary>
        public byte[] Asset { get; init; }

        public ScriptLanguage Language { get; init; }

        /// <summary>The errors, in source order; empty on success.</summary>
        public IReadOnlyList<CompileError> Errors { get; init; } = System.Array.Empty<CompileError>();

        /// <summary>LSL that calls an os*, iw* or bot* function, which the runtime's compiler refuses.</summary>
        public bool Refused { get; init; }

        /// <summary>The asset came from the compile cache and nothing was compiled.</summary>
        public bool FromCache { get; init; }

        public bool Success => Asset != null;

        public IEnumerable<string> EditorErrors()
        {
            foreach (CompileError e in Errors)
                yield return e.ForEditor();
        }
    }
}
