/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The cheap check that refuses source the native compiler must not be given. It reads the text once, skipping
    /// strings and comments, and counts what made the LSL compiler overflow a native stack or run in quadratic time
    /// when probed: nested brackets, nested braces, and a long run of operators in one statement.
    /// SLua is only held to the size limit; Luau limits its own nesting and was probed safe at 100,000 levels.
    /// </summary>
    internal static class SourceGuard
    {
        private const string OperatorChars = "+-*/%&|^<>=!~";

        /// <summary>The first limit the source passes, as a compile error, or null when it is within all of them.</summary>
        public static CompileError? Check(string source, ScriptLanguage language, CompileLimits limits)
        {
            if (Encoding.UTF8.GetByteCount(source) > limits.MaxSourceBytes)
                return new CompileError(1, $"the script is too large: the limit is {limits.MaxSourceBytes} bytes of source");
            return language == ScriptLanguage.Lsl ? CheckLsl(source, limits) : null;
        }

        private static CompileError? CheckLsl(string s, CompileLimits limits)
        {
            int line = 1, expression = 0, block = 0, operators = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\n':
                        line++;
                        break;
                    case '"':
                        for (i++; i < s.Length && s[i] != '"'; i++)
                        {
                            if (s[i] == '\\' && i + 1 < s.Length)
                                i++;
                            if (s[i] == '\n')
                                line++;
                        }
                        break;
                    case '/' when i + 1 < s.Length && s[i + 1] == '/':
                        while (i + 1 < s.Length && s[i + 1] != '\n')
                            i++;
                        break;
                    case '/' when i + 1 < s.Length && s[i + 1] == '*':
                        for (i += 2; i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'); i++)
                        {
                            if (s[i] == '\n')
                                line++;
                        }
                        i++;
                        break;
                    case '(':
                    case '[':
                        if (++expression > limits.MaxExpressionDepth)
                            return new CompileError(line, $"expressions nested too deeply (limit {limits.MaxExpressionDepth})");
                        break;
                    case ')':
                    case ']':
                        if (expression > 0) expression--;
                        break;
                    case '{':
                        operators = 0;
                        if (++block > limits.MaxBlockDepth)
                            return new CompileError(line, $"blocks nested too deeply (limit {limits.MaxBlockDepth})");
                        break;
                    case '}':
                        operators = 0;
                        if (block > 0) block--;
                        break;
                    case ';':
                        operators = 0;
                        break;
                    default:
                        if (IsOperator(s, i) && ++operators > limits.MaxStatementOperators)
                            return new CompileError(line, $"a statement has more than {limits.MaxStatementOperators} operators; split it into several statements");
                        break;
                }
            }
            return null;
        }

        /// <summary>An operator character, or a '.' that is a member access (followed by a name) rather than part of a number.</summary>
        private static bool IsOperator(string s, int i)
        {
            char c = s[i];
            if (OperatorChars.IndexOf(c) >= 0)
                return true;
            return c == '.' && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '_');
        }
    }
}
