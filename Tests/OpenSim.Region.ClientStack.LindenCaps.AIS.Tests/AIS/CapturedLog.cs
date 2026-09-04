using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// Captures what the code under test logged, for the assertions that care about an operator being able to see a
/// fault without opening the database (A7).
///
/// <para>Swapping <see cref="LoggerProvider.LoggerFactory"/> is enough because loggers held in
/// <c>static readonly</c> fields are <c>DeferredLogger</c>s, which rebind whenever the factory reference changes
/// (<c>DeferredLogger.cs:36-51</c>). The previous factory is restored on dispose so fixtures do not leak into
/// each other.</para>
/// </summary>
public sealed class CapturedLog : IDisposable, ILoggerFactory
{
    private readonly ILoggerFactory m_previous;
    private readonly List<(LogLevel Level, string Message)> m_entries = new();

    public CapturedLog()
    {
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = this;
    }

    /// <summary>
    /// The lowest level this capture reports as enabled. Set it above <see cref="LogLevel.Debug"/> to assert that
    /// code guarded by <c>IsEnabled</c> does no work — which is how the A11 logging keeps its cost claim honest.
    /// </summary>
    public LogLevel Enabled { get; init; } = LogLevel.Trace;

    public IReadOnlyList<string> Warnings => Messages(LogLevel.Warning);

    public IReadOnlyList<string> Messages(LogLevel level)
    {
        var found = new List<string>();
        lock (m_entries)
            foreach (var (entryLevel, message) in m_entries)
                if (entryLevel == level)
                    found.Add(message);
        return found;
    }

    public void Dispose() => LoggerProvider.LoggerFactory = m_previous;

    ILogger ILoggerFactory.CreateLogger(string categoryName) => new Recorder(this);
    void ILoggerFactory.AddProvider(ILoggerProvider provider) { }

    private void Record(LogLevel level, string message)
    {
        lock (m_entries)
            m_entries.Add((level, message));
    }

    private sealed class Recorder : ILogger
    {
        private readonly CapturedLog m_owner;
        public Recorder(CapturedLog owner) { m_owner = owner; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= m_owner.Enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            m_owner.Record(logLevel, formatter(state, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
