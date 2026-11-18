/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Server.GridServer;
using OpenSim.Server.MoneyServer;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// When the console input reaches end of file (the process was started with its input closed, or the
/// program feeding it went away), the servers log it once and carry on without reading the console.
/// Before, the basic console passed the null from Console.ReadLine to the command parser, which threw, and the
/// prompt loop called it again at once, writing an error to the log on every pass.
/// Console.In, MainConsole.Instance and the ambient logger factory are process-wide, so these run in the serial
/// collection.
/// </summary>
[Collection("MainConsole")]
public sealed class ConsoleEndOfInputTests
{
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
        private readonly List<(string Category, LogLevel Level, string Message)> _entries = new();

        public (string Category, LogLevel Level, string Message)[] Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public void Dispose() { }

        private sealed class Capturing : ILogger
        {
            private readonly CapturingLoggerFactory _owner;
            private readonly string _category;

            public Capturing(CapturingLoggerFactory owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_owner._entries)
                    _owner._entries.Add((_category, logLevel, formatter(state, exception)));
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

    /// <summary>
    /// An <see cref="IServerBase"/> that answers only its console; every other member returns its default.
    /// The loops under test read nothing else.
    /// </summary>
    private class ConsoleOnlyServerBase : DispatchProxy
    {
        public ICommandConsole? ConsoleToReturn { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Console")
                return ConsoleToReturn;

            Type? returnType = targetMethod?.ReturnType;
            return returnType is { IsValueType: true } && returnType != typeof(void) ? Activator.CreateInstance(returnType) : null;
        }
    }

    private static IServerBase ServerBaseWith(ICommandConsole console)
    {
        IServerBase proxy = DispatchProxy.Create<IServerBase, ConsoleOnlyServerBase>();
        ((ConsoleOnlyServerBase)(object)proxy).ConsoleToReturn = console;
        return proxy;
    }

    /// <summary>Runs <paramref name="act"/> with Console.In ended, MainConsole.Instance a basic console and the log captured.</summary>
    private static async Task WithEndedInput(Func<CommandConsole, EndedInput, CapturingLoggerFactory, Task> act)
    {
        TextReader savedIn = System.Console.In;
        ICommandConsole? savedConsole = MainConsole.Instance;
        ILoggerFactory savedFactory = LoggerProvider.LoggerFactory;
        EndedInput input = new();
        CapturingLoggerFactory log = new();
        CommandConsole console = new("Test");

        System.Console.SetIn(input);
        MainConsole.Instance = console;
        LoggerProvider.LoggerFactory = log;

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

    private static int InputEndedLines(CapturingLoggerFactory log) =>
        log.Entries.Count(e => e.Level < LogLevel.Error && e.Message.Contains("input", StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts the runner, lets the host report started, and waits (up to 10 s) for the loop to end by itself.</summary>
    private static async Task<(bool Ended, int Reads)> RunUntilItEnds(BackgroundService svc, TestLifetime lifetime, EndedInput input)
    {
        await svc.StartAsync(CancellationToken.None);
        lifetime.ReportStarted();

        Task finished = svc.ExecuteTask ?? Task.CompletedTask;
        bool ended = await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(10))) == finished;
        int reads = input.Reads;

        await svc.StopAsync(CancellationToken.None);
        return (ended, reads);
    }

    [Fact]
    public async Task ThePromptAtEndOfInputDoesNotThrowAndRemembersTheInputIsClosed()
    {
        await WithEndedInput((console, input, log) =>
        {
            Exception? thrown = Record.Exception(() => console.Prompt());

            Assert.Null(thrown);
            Assert.True(((ICommandConsole)console).InputClosed, "The console should know its input has ended.");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OnceTheInputHasEndedFurtherPromptsReadNothing()
    {
        await WithEndedInput((console, input, log) =>
        {
            Record.Exception(() => console.Prompt());
            Record.Exception(() => console.Prompt());
            Record.Exception(() => console.Prompt());

            Assert.Equal(1, input.Reads);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheEndOfInputIsLoggedOnceNotOnEveryPrompt()
    {
        await WithEndedInput((console, input, log) =>
        {
            Record.Exception(() => console.Prompt());
            Record.Exception(() => console.Prompt());
            Record.Exception(() => console.Prompt());

            Assert.Equal(1, InputEndedLines(log));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheRegionConsoleLoopEndsAtEndOfInputAfterOneRead()
    {
        await WithEndedInput(async (console, input, log) =>
        {
            TestLifetime lifetime = new();
            RegionConsoleRunnerService svc = new(NullLogger<RegionConsoleRunnerService>.Instance, lifetime);

            (bool ended, int reads) = await RunUntilItEnds(svc, lifetime, input);

            Assert.True(ended, "The console loop should stop when the input has ended.");
            Assert.Equal(1, reads);
            Assert.Equal(1, InputEndedLines(log));
        });
    }

    [Fact]
    public async Task TheGridConsoleLoopEndsAtEndOfInputAfterOneRead()
    {
        await WithEndedInput(async (console, input, log) =>
        {
            TestLifetime lifetime = new();
            GridConsoleRunnerService svc = new(
                ServerBaseWith(console), NullLogger<GridConsoleRunnerService>.Instance, lifetime);

            (bool ended, int reads) = await RunUntilItEnds(svc, lifetime, input);

            Assert.True(ended, "The console loop should stop when the input has ended.");
            Assert.Equal(1, reads);
        });
    }

    [Fact]
    public async Task TheMoneyConsoleLoopEndsAtEndOfInputAfterOneRead()
    {
        await WithEndedInput(async (console, input, log) =>
        {
            TestLifetime lifetime = new();
            MoneyConsoleRunnerService svc = new(
                ServerBaseWith(console), NullLogger<MoneyConsoleRunnerService>.Instance, lifetime);

            (bool ended, int reads) = await RunUntilItEnds(svc, lifetime, input);

            Assert.True(ended, "The console loop should stop when the input has ended.");
            Assert.Equal(1, reads);
        });
    }
}
