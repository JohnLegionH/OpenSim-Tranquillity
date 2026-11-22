/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// The log lines of the wind-volume store, formatted the way a running region formats them: a create, a remove by a
/// script, and the one line an object gets when it reaches the create and remove throttle. Formatting a line must
/// never throw, since the throw would reach the script that made the call.
/// </summary>
// Swaps LoggerProvider.LoggerFactory, a process-wide static, for the length of each test; the assembly runs its tests
// one at a time (AssemblyInfo.cs), so no other test logs while it is swapped.
public class WindVolumeLogTests
{
    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        public readonly List<string> Lines = new();
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                // A running region's console logger formats every message; this does the same.
                string text = formatter(state, exception);
                lock (m_c.Lines) m_c.Lines.Add(text);
            }
        }
    }

    private static readonly UUID Obj = UUID.Random();
    private static readonly UUID Owner = UUID.Random();

    private static WindVolumeSpec Spec(Vector3 drift) => new WindVolumeSpec
    {
        Shape = WindVolumeShape.Sphere, Center = new Vector3(100, 100, 50), Radius = 10f, FalloffStart = 0.5f,
        LifetimeSeconds = 60f, Velocity = new Vector3(5, 0, 0), Drift = drift,
    };

    private static List<string> Logged(Action<WindVolumeStore> act)
    {
        var capture = new Capture();
        ILoggerFactory before = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try
        {
            long now = 1000;
            var store = new WindVolumeStore(true, new WindVolumeLimits { CreateThrottlePerMinute = 2 }, () => now) { RegionName = "Test Region" };
            act(store);
        }
        finally { LoggerProvider.LoggerFactory = before; }
        return capture.Lines;
    }

    [Fact]
    public void ACreateAndAScriptsRemoveAreLoggedWithTheObjectOwnerRegionAndDrift()
    {
        int handle = 0;
        List<string> lines = Logged(store =>
        {
            handle = store.Create(Obj, Owner, Spec(new Vector3(3, 0, 0)));
            Assert.Equal(WindVolumeError.Ok, store.Remove(handle, Obj));
        });

        Assert.Contains(lines, l => l.Contains("Test Region") && l.Contains(Obj.ToString()) && l.Contains(Owner.ToString())
            && l.Contains($"made wind volume {handle}") && l.Contains("drift <3, 0, 0>"));
        Assert.Contains(lines, l => l.Contains($"removed wind volume {handle}") && l.Contains(Obj.ToString()));
    }

    [Fact]
    public void AnObjectThatReachesTheThrottleIsLoggedOnceAndItsScriptGetsTheCode()
    {
        var results = new List<int>();
        List<string> lines = new();
        Exception? thrown = Record.Exception(() => lines = Logged(store =>
        {
            results.Add(store.Create(Obj, Owner, Spec(Vector3.Zero)));
            results.Add(store.Create(Obj, Owner, Spec(Vector3.Zero)));
            results.Add(store.Create(Obj, Owner, Spec(Vector3.Zero)));
            results.Add(store.Remove(1, Obj));
            results.Add(store.Create(Obj, Owner, Spec(Vector3.Zero)));
        }));

        Assert.Null(thrown);
        Assert.Equal(new[] { WindVolumeError.Throttled, WindVolumeError.Throttled, WindVolumeError.Throttled }, results.Skip(2).ToArray());
        string[] throttled = lines.Where(l => l.Contains("further calls are refused")).ToArray();
        string only = Assert.Single(throttled);
        Assert.Contains(Obj.ToString(), only);
        Assert.Contains("2 wind volume create or remove calls in the last 60 s", only);
        Assert.Contains("until the oldest is 60 s old", only);
    }
}
