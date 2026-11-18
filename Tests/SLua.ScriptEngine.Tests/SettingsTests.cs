/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Threading;
using Nini.Config;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>The section's keys and their defaults.</summary>
public class SettingsTests
{
    private static SLuaSettings Read(Action<IConfig> fill, out RecordingLog log, Action<IConfigSource> other = null)
    {
        var source = new IniConfigSource();
        IConfig c = source.AddConfig(EngineIdentity.ConfigSection);
        fill?.Invoke(c);
        other?.Invoke(source);
        log = new RecordingLog();
        return SLuaSettings.Read(source, log);
    }

    [Fact]
    public void DefaultsAreTheDocumentedOnes()
    {
        SLuaSettings s = Read(null, out RecordingLog log);
        Assert.False(s.Enabled);
        Assert.Equal("ScriptEngines", s.ScriptEnginesPath);
        Assert.Equal(1, s.WorkerThreads);
        Assert.Equal(ThreadPriority.Lowest, s.SchedulerThreadPriority);
        Assert.Equal(2.0, s.QuantaMs);
        Assert.Equal("default", s.Watchdog);
        Assert.Equal(128, s.MemoryLimitKiB);
        Assert.Equal(0.1, s.MinTimerInterval, 6);
        Assert.Equal(64, s.MaxEventQueue);
        Assert.Equal(2500, s.StateSaveIntervalMs);
        Assert.True(s.CompileCache);
        Assert.Equal("auto", s.ServiceCallDeferral);
        Assert.Equal(4, s.ServiceCallThreads);
        Assert.Equal(35000, s.ServiceCallTimeoutMs);
        Assert.Equal("warn", s.NativeLogLevel);
        Assert.True(s.ResetThrottle && s.ChatThrottle && s.BotThrottle && s.PhysicsThrottle && s.LinkMessageThrottle
            && s.NotecardThrottle && s.NotecardCache && s.FormatStringThrottle && s.HttpInFlightThrottle);
        Assert.Equal("sl", s.NotecardPause);
        Assert.False(s.AllowGodFunctions);
        Assert.Null(s.NativeLibraryPath);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void ConfiguredValuesAreRead()
    {
        SLuaSettings s = Read(c =>
        {
            c.Set("Enabled", "true");
            c.Set("ScriptEnginesPath", "/srv/engines");
            c.Set("WorkerThreads", "3");
            c.Set("SchedulerThreadPriority", "Normal");
            c.Set("QuantaMs", "4");
            c.Set("Watchdog", "threaded");
            c.Set("MemoryLimitKiB", "64");
            c.Set("NativeLibraryPath", "/opt/slua");
            c.Set("ChatThrottle", "false");
        }, out RecordingLog log);
        Assert.True(s.Enabled);
        Assert.Equal("/srv/engines", s.ScriptEnginesPath);
        Assert.Equal(3, s.WorkerThreads);
        Assert.Equal(ThreadPriority.Normal, s.SchedulerThreadPriority);
        Assert.Equal(4.0, s.QuantaMs);
        Assert.Equal("threaded", s.Watchdog);
        Assert.Equal(64, s.MemoryLimitKiB);
        Assert.Equal("/opt/slua", s.NativeLibraryPath);
        Assert.False(s.ChatThrottle);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void AMemoryLimitAboveTheCeilingIsLoggedAndReplacedByTheCeiling()
    {
        SLuaSettings s = Read(c => c.Set("MemoryLimitKiB", "512"), out RecordingLog log);
        Assert.Equal(128, s.MemoryLimitKiB);
        Assert.Single(log.Warnings);
    }

    [Fact]
    public void ANameThatIsNotAWatchdogModeIsLoggedAndTheDefaultUsed()
    {
        SLuaSettings s = Read(c => c.Set("Watchdog", "signal"), out RecordingLog log);
        Assert.Equal("default", s.Watchdog);
        Assert.Single(log.Warnings);
    }

    [Fact]
    public void WorkerThreadsBelowOneBecomeOne()
    {
        Assert.Equal(1, Read(c => c.Set("WorkerThreads", "0"), out _).WorkerThreads);
    }

    [Fact]
    public void GodFunctionsFallBackToYEnginesSettingAndTheEnginesOwnWins()
    {
        Assert.True(Read(null, out _, src => src.AddConfig("YEngine").Set("AllowGodFunctions", "true")).AllowGodFunctions);
        Assert.False(Read(c => c.Set("AllowGodFunctions", "false"), out _, src => src.AddConfig("YEngine").Set("AllowGodFunctions", "true")).AllowGodFunctions);
    }

    [Fact]
    public void ScriptEnginesPathGainsTheEnginesOwnFolder()
    {
        Assert.Equal(Path.Combine("ScriptEngines", "SLua"), Read(null, out _).StateRoot);
        Assert.Equal(Path.Combine("/srv/engines", "SLua"), Read(c => c.Set("ScriptEnginesPath", "/srv/engines"), out _).StateRoot);
    }
}
