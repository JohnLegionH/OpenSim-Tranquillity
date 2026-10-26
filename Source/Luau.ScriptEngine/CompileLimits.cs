/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace Luau.ScriptEngine
{
    /// <summary>
    /// What the engine accepts before it hands source to the runtime's compiler, which is native: a native stack
    /// overflow ends the process, and its error handling can take quadratic time. Each default sits far under the
    /// size at which the compiler was seen to misbehave when probed in a child process, and far over real scripts.
    /// </summary>
    internal sealed class CompileLimits
    {
        /// <summary>Longest source, in UTF-8 bytes, either language.</summary>
        public int MaxSourceBytes { get; init; } = 256 * 1024;

        /// <summary>LSL: most parentheses and brackets open at once.</summary>
        public int MaxExpressionDepth { get; init; } = 1000;

        /// <summary>LSL: most braces open at once.</summary>
        public int MaxBlockDepth { get; init; } = 500;

        /// <summary>LSL: most operator characters between two statement delimiters (';', '{', '}').</summary>
        public int MaxStatementOperators { get; init; } = 1000;

        /// <summary>Most errors kept from one compile; the rest are counted in one closing entry.</summary>
        public int MaxErrors { get; init; } = 20;

        /// <summary>Stack of the thread the native compiler runs on.</summary>
        public int StackSize { get; init; } = 16 * 1024 * 1024;
    }
}
