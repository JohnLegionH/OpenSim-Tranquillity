/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>The native compile call: source text to an asset; throws <see cref="SluaException"/> with the compiler's message on failure.</summary>
    internal delegate byte[] NativeCompile(string text, bool lsl);

    /// <summary>
    /// Compiles script source to the runtime's asset format: SLua with Luau's compiler, LSL with the runtime's LSL
    /// compiler, both through the native library's compile export.
    /// </summary>
    internal sealed class ScriptCompiler : IDisposable
    {
        // The runtime's result code for LSL that calls an os*, iw* or bot* function (SLUA_ERR_UNSUPPORTED_FUNCTION).
        private const int UnsupportedFunctionCode = -10;

        private static readonly Regex ErrorLine = new Regex(@"^(\d+): (.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

        private readonly CompileLimits m_limits;
        private readonly NativeCompile m_native;
        private readonly IEngineLog m_log;
        private readonly CompileCache m_cache;

        // The native compiler runs on one dedicated thread with a large fixed stack, never on the caller's thread.
        private readonly BlockingCollection<Job> m_jobs = new BlockingCollection<Job>();
        private Thread m_thread;
        private readonly object m_threadLock = new object();
        private bool m_disposed;

        private sealed class Job
        {
            public string Text;
            public bool Lsl;
            public readonly TaskCompletionSource<byte[]> Done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ScriptCompiler(CompileLimits limits = null, NativeCompile native = null, IEngineLog log = null, CompileCache cache = null)
        {
            m_limits = limits ?? new CompileLimits();
            m_native = native ?? Host.Compile;
            m_log = log;
            m_cache = cache;
        }

        /// <summary>Stops the compile thread once the jobs already queued have been answered.</summary>
        public void Dispose()
        {
            Thread thread;
            lock (m_threadLock)
            {
                if (m_disposed) return;
                m_disposed = true;
                thread = m_thread;
            }
            m_jobs.CompleteAdding();
            thread?.Join(TimeSpan.FromSeconds(10));
        }

        /// <summary>Compile source text as the language its first line (or, without one, its content) selects. Never throws for bad input.</summary>
        public CompileResult Compile(string source)
        {
            source ??= string.Empty;
            ScriptLanguage language = EngineHeader.LanguageFor(source);
            if (language == ScriptLanguage.Unsupported)
                return Failed(language, new CompileError(1, UnsupportedLanguageMessage(source)));

            // The pre-check runs before the native compiler is given a byte.
            if (SourceGuard.Check(source, language, m_limits) is CompileError refused)
                return Failed(language, refused);

            // A header line is blanked for SLua, its newline kept, so the compiler's line numbers match the source.
            string text = language == ScriptLanguage.Slua && string.Equals(EngineHeader.NamedEngine(source), EngineIdentity.EngineName, System.StringComparison.OrdinalIgnoreCase)
                ? source[source.IndexOf('\n')..]
                : source;
            if (text.Length == 0)
                return Failed(language, new CompileError(1, "the script is empty"));

            if (m_cache != null && m_cache.TryGet(text, language, out byte[] cached))
                return new CompileResult { Asset = cached, Language = language, FromCache = true };

            try
            {
                byte[] asset = RunOnCompileThread(text, language == ScriptLanguage.Lsl);
                m_cache?.Put(text, language, asset);
                return new CompileResult { Asset = asset, Language = language };
            }
            catch (SluaException e)
            {
                bool isRefusal = e.Code == UnsupportedFunctionCode;
                List<CompileError> errors = ParseErrors(DetailOf(e));
                if (isRefusal && EngineHeader.NamedEngine(source) is string named
                    && string.Equals(named, EngineIdentity.EngineName, StringComparison.OrdinalIgnoreCase))
                    errors[^1] = errors[^1] with { Message = errors[^1].Message + " " + EngineIdentity.RefusalHint };
                return new CompileResult
                {
                    Language = language,
                    Errors = CapErrors(errors),
                    Refused = isRefusal,
                };
            }
            catch (Exception e) when (e is not ObjectDisposedException)
            {
                // Not a mistake in the script. The resident is told the type only; the detail goes to the log.
                m_log?.Error("the compiler failed on a script: " + e);
                return Failed(language, new CompileError(0,
                    $"compiler error (not a script error): {e.GetType().Name}; reported to the grid operator"));
            }
        }

        private byte[] RunOnCompileThread(string text, bool lsl)
        {
            var job = new Job { Text = text, Lsl = lsl };
            lock (m_threadLock)
            {
                if (m_disposed) throw new ObjectDisposedException(nameof(ScriptCompiler));
                if (m_thread == null)
                {
                    m_thread = new Thread(CompileLoop, m_limits.StackSize) { IsBackground = true, Name = EngineIdentity.EngineName + " compile" };
                    m_thread.Start();
                }
                m_jobs.Add(job);
            }
            return job.Done.Task.GetAwaiter().GetResult();
        }

        private void CompileLoop()
        {
            foreach (Job job in m_jobs.GetConsumingEnumerable())
            {
                try { job.Done.SetResult(m_native(job.Text, job.Lsl)); }
                catch (Exception e) { job.Done.SetException(e); }
            }
        }

        /// <summary>Keeps the first <see cref="CompileLimits.MaxErrors"/> errors and counts the rest in one closing entry.</summary>
        private List<CompileError> CapErrors(List<CompileError> errors)
        {
            if (errors.Count <= m_limits.MaxErrors)
                return errors;
            int more = errors.Count - m_limits.MaxErrors;
            errors.RemoveRange(m_limits.MaxErrors, more);
            errors.Add(new CompileError(0, $"{more} more errors not shown"));
            return errors;
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
        private static List<CompileError> ParseErrors(string detail)
        {
            var errors = new List<CompileError>();
            foreach (string raw in detail.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;
                Match m = ErrorLine.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int number))
                    errors.Add(new CompileError(Math.Max(number, 0), m.Groups[2].Value));
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
