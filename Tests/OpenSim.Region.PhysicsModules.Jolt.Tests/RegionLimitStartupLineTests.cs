/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The "backend initialised" start-up line names where each of MaxBodies, MaxBodyPairs and MaxContactConstraints
/// came from: this region's own override, the process-wide [Jolt] setting, or automatic sizing.
/// Serial: it swaps LoggerProvider.LoggerFactory, which is process-wide.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RegionLimitStartupLineTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;

    private sealed class Capture : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Message)> Lines = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly Capture _c;
            public Logger(Capture c) { _c = c; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (_c.Lines)
                    _c.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static string InitialisedLine(string regionName, (string key, string value)[] joltKeys, (string key, string value)[] regionKeys)
    {
        var src = new IniConfigSource();
        IConfig startup = src.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = src.AddConfig("Jolt");
        foreach (var (k, v) in joltKeys)
            jolt.Set(k, v);
        if (regionKeys.Length > 0)
        {
            IConfig region = src.AddConfig(regionName);
            foreach (var (k, v) in regionKeys)
                region.Set(k, v);
        }

        var capture = new Capture();
        ILoggerFactory saved = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try
        {
            var scene = new JoltScene();
            scene.Initialise(src);
            var heights = new float[Size * Size];
            Array.Fill(heights, 25f);
            scene.InitialiseWithoutScene(regionName, Size, Size, heights, 20f, Heartbeat);
            scene.Dispose();
        }
        finally { LoggerProvider.LoggerFactory = saved; }
        lock (capture.Lines)
            return capture.Lines.Single(l => l.Message.Contains("backend initialised", StringComparison.Ordinal)).Message;
    }

    [Fact]
    public void A_region_with_no_override_and_no_process_setting_shows_automatic_for_all_three()
    {
        string m = InitialisedLine("Example Region", Array.Empty<(string, string)>(), Array.Empty<(string, string)>());
        Assert.Contains("MaxBodies=65536 (automatic)", m);
        Assert.Contains("MaxBodyPairs=65536 (automatic)", m);
        Assert.Contains("MaxContactConstraints=16384 (automatic)", m);
    }

    [Fact]
    public void A_process_setting_with_no_region_override_is_named()
    {
        string m = InitialisedLine("Example Region",
            new[] { ("MaxBodies", "9000"), ("MaxBodyPairs", "9100"), ("MaxContactConstraints", "9200") },
            Array.Empty<(string, string)>());
        Assert.Contains("MaxBodies=9000 (process setting, [Jolt] MaxBodies = 9000)", m);
        Assert.Contains("MaxBodyPairs=9100 (process setting, [Jolt] MaxBodyPairs = 9100)", m);
        Assert.Contains("MaxContactConstraints=9200 (process setting, [Jolt] MaxContactConstraints = 9200)", m);
    }

    [Fact]
    public void A_region_override_is_named_over_an_explicit_process_setting()
    {
        string m = InitialisedLine("Example Region",
            new[] { ("MaxBodies", "9000") },
            new[] { ("MaxBodies", "1234") });
        Assert.Contains("MaxBodies=1234 (region override, [Example Region] MaxBodies = 1234)", m);
        // Not overridden for this region: still the process setting.
        Assert.Contains("MaxBodyPairs=65536 (automatic)", m);
    }
}
