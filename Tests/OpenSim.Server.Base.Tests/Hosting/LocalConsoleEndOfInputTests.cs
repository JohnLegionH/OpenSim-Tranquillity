/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// The local console (the default console type, which edits the line key by key with the cursor) with its input
/// closed: a server started detached, or with its input redirected from a file or a pipe that has ended. The
/// console must say once that its input has ended, read nothing more, and let the prompt loop stop, as the basic
/// console does at end of file.
/// Console.In, MainConsole.Instance and the ambient logger factory are process-wide, so these run in the serial
/// collection of the other console tests.
/// </summary>
[Collection("MainConsole")]
public sealed class LocalConsoleEndOfInputTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>A reader whose input has ended: every read answers null, and the reads are counted.</summary>
    private sealed class EndedInput : TextReader
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public override string? ReadLine()
        {
            Interlocked.Increment(ref _reads);
            return null;
        }

        public override int Peek() => -1;

        public override int Read() => -1;
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public (LogLevel Level, string Message)[] Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void Dispose() { }

        private sealed class Capturing : ILogger
        {
            private readonly CapturingLoggerFactory _owner;

            public Capturing(CapturingLoggerFactory owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_owner._entries)
                    _owner._entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    /// <summary>A host lifetime whose started token is cancelled by the test.</summary>
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();

        public void ReportStarted() => _started.Cancel();
    }

    /// <summary>Runs <paramref name="act"/> with Console.In ended, MainConsole.Instance a local console and the log captured.</summary>
    private static async Task WithEndedInput(Func<LocalConsole, EndedInput, CapturingLoggerFactory, Task> act)
    {
        TextReader savedIn = System.Console.In;
        ICommandConsole? savedConsole = MainConsole.Instance;
        ILoggerFactory savedFactory = LoggerProvider.LoggerFactory;
        EndedInput input = new();
        CapturingLoggerFactory log = new();

        System.Console.SetIn(input);
        LoggerProvider.LoggerFactory = log;
        LocalConsole console = new("Test");
        MainConsole.Instance = console;

        try
        {
            await act(console, input, log);
        }
        finally
        {
            LoggerProvider.LoggerFactory = savedFactory;
            MainConsole.Instance = savedConsole!;
            System.Console.SetIn(savedIn);
        }
    }

    /// <summary>Prompts on another thread, so a console that waits for a key that never comes fails the test and does not hang it.</summary>
    private static async Task<(bool Finished, Exception? Thrown)> PromptOnce(LocalConsole console)
    {
        Task prompt = Task.Run(console.Prompt);
        bool finished = await Task.WhenAny(prompt, Task.Delay(Limit)) == prompt;
        return (finished, finished ? prompt.Exception?.GetBaseException() : null);
    }

    private static int InputEndedLines(CapturingLoggerFactory log) =>
        log.Entries.Count(e => e.Level < LogLevel.Error && e.Message.Contains("input has ended", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task ThePromptWithTheInputClosedDoesNotThrowAndRemembersTheInputIsClosed()
    {
        await WithEndedInput(async (console, input, log) =>
        {
            (bool finished, Exception? thrown) = await PromptOnce(console);

            Assert.True(finished, "The prompt should return, not wait for a key that will never come.");
            Assert.Null(thrown);
            Assert.True(((ICommandConsole)console).InputClosed, "The console should know its input has ended.");
        });
    }

    [Fact]
    public async Task OnceTheInputHasEndedFurtherPromptsReadNothingMore()
    {
        await WithEndedInput(async (console, input, log) =>
        {
            await PromptOnce(console);
            int readsAfterTheFirst = input.Reads;
            (bool finished, Exception? thrown) = await PromptOnce(console);
            await PromptOnce(console);

            Assert.True(finished);
            Assert.Null(thrown);
            Assert.Equal(1, readsAfterTheFirst);
            Assert.Equal(1, input.Reads);
        });
    }

    [Fact]
    public async Task TheEndOfInputIsLoggedOnceNotOnEveryPrompt()
    {
        await WithEndedInput(async (console, input, log) =>
        {
            await PromptOnce(console);
            await PromptOnce(console);
            await PromptOnce(console);

            Assert.Equal(1, InputEndedLines(log));
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
        });
    }

    [Fact]
    public async Task TheRegionConsoleLoopWithTheLocalConsoleEndsWhenTheInputHasEnded()
    {
        await WithEndedInput(async (console, input, log) =>
        {
            TestLifetime lifetime = new();
            RegionConsoleRunnerService svc = new(NullLogger<RegionConsoleRunnerService>.Instance, lifetime);

            await svc.StartAsync(CancellationToken.None);
            lifetime.ReportStarted();
            Task finished = svc.ExecuteTask ?? Task.CompletedTask;
            bool ended = await Task.WhenAny(finished, Task.Delay(Limit)) == finished;
            await svc.StopAsync(CancellationToken.None);

            Assert.True(ended, "The console loop should stop when the input has ended.");
            Assert.Equal(1, InputEndedLines(log));
        });
    }
}
