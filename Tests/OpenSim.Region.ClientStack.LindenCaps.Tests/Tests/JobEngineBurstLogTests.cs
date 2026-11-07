/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests;

/// <summary>
/// A viewer that selects every object it can see (an object search) queues thousands of packet jobs a second on its
/// own job engine. A full queue and a job that throws are counted, and logged at debug level only on an engine marked
/// quiet; an ordinary engine keeps logging both as before.
/// </summary>
// The log is captured through LoggerProvider.LoggerFactory, which is process-wide, hence the collection.
[Collection("logger-factory")]
public class JobEngineBurstLogTests : IDisposable
{
    private const int QueueCapacity = 5000;
    private readonly Capture m_log = new();
    private readonly ILoggerFactory m_previous;
    private readonly List<JobEngine> m_engines = new();

    public JobEngineBurstLogTests()
    {
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = m_log;
    }

    public void Dispose()
    {
        foreach (var e in m_engines) e.Stop();
        LoggerProvider.LoggerFactory = m_previous;
    }

    private sealed class Capture : ILoggerFactory
    {
        private readonly List<(LogLevel level, string text)> m_lines = new();
        // only what the engine under test wrote; other static initialisers in the process log too
        public (LogLevel level, string text)[] Lines { get { lock (m_lines) return m_lines.Where(l => l.text.Contains("[burst-test]")).ToArray(); } }
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L(Capture c) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (c.m_lines) c.m_lines.Add((logLevel, formatter(state, exception))); }
        }
    }

    private JobEngine Engine(bool quiet)
    {
        var e = new JobEngine("burst-test", "burst-test") { QuietLogging = quiet };
        e.Start();
        m_engines.Add(e);
        return e;
    }

    private static bool WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>Holds the engine's one worker on a job, then queues more than the queue holds.</summary>
    private static void Overflow(JobEngine e, ManualResetEventSlim release, int extra)
    {
        var started = new ManualResetEventSlim();
        e.QueueJob("hold", () => { started.Set(); release.Wait(TimeSpan.FromSeconds(30)); });
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        for (int i = 0; i < QueueCapacity + extra; i++)
            e.QueueJob("Select", () => { });
    }

    [Fact]
    public void QuietEngine_FullQueue_IsCountedAndLogsNothingAboveDebug()
    {
        var e = Engine(quiet: true);
        using var release = new ManualResetEventSlim();
        Overflow(e, release, 300);
        release.Set();

        Assert.Equal(300, e.DroppedJobs);
        Assert.DoesNotContain(m_log.Lines, l => l.level >= LogLevel.Information);
    }

    [Fact]
    public void QuietEngine_FailingJobs_AreCountedAndLogNothingAboveDebug()
    {
        var e = Engine(quiet: true);
        for (int i = 0; i < 40; i++)
            e.QueueJob("ObjectBuy", () => throw new InvalidOperationException("boom"));

        Assert.True(WaitFor(() => e.FailedJobs == 40));
        Assert.DoesNotContain(m_log.Lines, l => l.level >= LogLevel.Information);
    }

    [Fact]
    public void OrdinaryEngine_FullQueue_StillWarnsOnce()
    {
        var e = Engine(quiet: false);
        using var release = new ManualResetEventSlim();
        Overflow(e, release, 300);
        release.Set();

        Assert.Equal(300, e.DroppedJobs);
        Assert.Single(m_log.Lines, l => l.level == LogLevel.Warning && l.text.Contains("Job queue at maximum capacity"));
    }

    [Fact]
    public void OrdinaryEngine_FailingJob_StillLogsAnError()
    {
        var e = Engine(quiet: false);
        e.QueueJob("ObjectBuy", () => throw new InvalidOperationException("boom"));

        Assert.True(WaitFor(() => e.FailedJobs == 1));
        Assert.Contains(m_log.Lines, l => l.level == LogLevel.Error && l.text.Contains("ObjectBuy"));
    }
}
