/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Luau.ScriptEngine.Bindings;

namespace Luau.ScriptEngine
{
    /// <summary>
    /// Compiles script source to the runtime's asset format: SLua with Luau's compiler, LSL with the runtime's LSL
    /// compiler, both through the native library's compile export.
    /// </summary>
    internal sealed class ScriptCompiler
    {
        // The runtime's result code for LSL that calls an os*, iw* or bot* function (SLUA_ERR_UNSUPPORTED_FUNCTION).
        private const int UnsupportedFunctionCode = -10;

        // The runtime reports an LSL error with the compiler's 1-based line plus one more. SLua lines are exact.
        private const int LslLineOffset = 1;

        private static readonly Regex ErrorLine = new Regex(@"^(\d+): (.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>Compile source text as the language its first line (or, without one, its content) selects. Never throws for bad input.</summary>
        public CompileResult Compile(string source)
        {
            source ??= string.Empty;
            ScriptLanguage language = EngineHeader.LanguageFor(source);
            if (language == ScriptLanguage.Unsupported)
                return Failed(language, new CompileError(1, UnsupportedLanguageMessage(source)));

            // A header line is blanked for SLua, its newline kept, so the compiler's line numbers match the source.
            string text = language == ScriptLanguage.Slua && EngineHeader.NamedEngine(source) == EngineIdentity.EngineName
                ? source[source.IndexOf('\n')..]
                : source;
            if (text.Length == 0)
                return Failed(language, new CompileError(1, "the script is empty"));

            try
            {
                byte[] asset = Host.Compile(text, language == ScriptLanguage.Lsl);
                return new CompileResult { Asset = asset, Language = language };
            }
            catch (SluaException e)
            {
                return new CompileResult
                {
                    Language = language,
                    Errors = ParseErrors(DetailOf(e), language),
                    Refused = e.Code == UnsupportedFunctionCode,
                };
            }
        }

        private static CompileResult Failed(ScriptLanguage language, CompileError error)
            => new CompileResult { Language = language, Errors = new[] { error } };

        private static string UnsupportedLanguageMessage(string source)
        {
            string header = source[..source.IndexOf('\n')].TrimEnd('\r');
            return $"the first line {header} names the language \"{EngineHeader.Language(source)}\"; {EngineIdentity.EngineName} runs lsl or slua";
        }

        /// <summary>The compiler's own text: a SluaException's message without the "slua_compile failed with code N:" prefix.</summary>
        private static string DetailOf(SluaException e)
        {
            string prefix = $"slua_compile failed with code {e.Code}";
            string message = e.Message;
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                message = message[prefix.Length..].TrimStart(':', ' ');
            return message;
        }

        /// <summary>
        /// The compiler gives one "line: message" per error, one per text line. A line that does not begin that way
        /// continues the error before it, or is an error of its own with no line when it comes first.
        /// </summary>
        private static List<CompileError> ParseErrors(string detail, ScriptLanguage language)
        {
            var errors = new List<CompileError>();
            int offset = language == ScriptLanguage.Lsl ? LslLineOffset : 0;
            foreach (string raw in detail.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;
                Match m = ErrorLine.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int number))
                    errors.Add(new CompileError(Math.Max(number - offset, 0), m.Groups[2].Value));
                else if (errors.Count > 0)
                    errors[^1] = errors[^1] with { Message = errors[^1].Message + " " + line };
                else
                    errors.Add(new CompileError(0, line));
            }
            if (errors.Count == 0)
                errors.Add(new CompileError(0, "the compiler rejected the script"));
            return errors;
        }
    }
}
