/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The script API is compiled once per process on a background thread after the engine starts. The warm-up state is
/// process-wide (a static), so these run one at a time with the other native-library tests.
/// </summary>
[Collection("native-library")]
public class WarmUpTests : IDisposable
{
    private readonly Action<IEngineLog> m_work = ApiWarmUp.Work;

    public WarmUpTests() => ApiWarmUp.ResetForTest();

    public void Dispose()
    {
        ApiWarmUp.Work = m_work;
        ApiWarmUp.ResetForTest();
    }

    [Fact]
    public void TheWarmUpIsOnByDefaultAndASettingTurnsItOff()
    {
        var source = new IniConfigSource();
        source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", "true");
        Assert.True(SLuaSettings.Read(source, new RecordingLog()).WarmUp);

        source.Configs[EngineIdentity.ConfigSection].Set("WarmUp", "false");
        Assert.False(SLuaSettings.Read(source, new RecordingLog()).WarmUp);
    }

    [RequiresNativeFact]
    public void TheRegionStartsWhileTheWarmUpIsStillRunning()
    {
        var started = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        ApiWarmUp.Work = log => { started.Set(); gate.Wait(TimeSpan.FromSeconds(60)); };
        try
        {
            using var rig = new EngineRig(); // the region is added in the constructor and returns with the warm-up held
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "the warm-up started");
            Assert.False(ApiWarmUp.IsDone);
        }
        finally { gate.Set(); }
        Assert.True(SchedulerHarness.WaitFor(() => ApiWarmUp.IsDone));
    }

    [RequiresNativeFact]
    public void AScriptWhoseFirstCallComesBeforeTheWarmUpEndsWaitsForIt()
    {
        var started = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        ApiWarmUp.Work = log => { started.Set(); gate.Wait(TimeSpan.FromSeconds(60)); };
        try
        {
            using var rig = new EngineRig(productionFunctions: true);
            UUID item = rig.Rez(Slua("local name = ll.GetObjectName()\nossl.Mark('name=' .. name)"));
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));

            Thread.Sleep(500); // a window in which the call, had it not waited, would have come back
            Assert.Empty(rig.MarksOf(item));
            Assert.False(ApiWarmUp.IsDone);

            gate.Set();
            Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("name=Example Object")), "the call went through once the warm-up ended");
            Assert.True(ApiWarmUp.IsDone);
        }
        finally { gate.Set(); }
    }

    [RequiresNativeFact]
    public void TheWarmUpRunsOncePerProcessNotOncePerRegion()
    {
        int runs = 0;
        ApiWarmUp.Work = log => Interlocked.Increment(ref runs);
        using (var first = new EngineRig())
        using (var second = new EngineRig())
        {
            Assert.True(SchedulerHarness.WaitFor(() => ApiWarmUp.IsDone));
        }
        Assert.Equal(1, runs);
        Assert.Equal(1, ApiWarmUp.Runs);
    }

    [RequiresNativeFact]
    public void ASettingTurnsTheWarmUpOffAndScriptsStillRun()
    {
        int runs = 0;
        ApiWarmUp.Work = log => Interlocked.Increment(ref runs);
        using var rig = new EngineRig(new Dictionary<string, string> { ["WarmUp"] = "false" }, productionFunctions: true);
        UUID item = rig.Rez(Slua("ossl.Mark('name=' .. ll.GetObjectName())"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("name=Example Object")));
        Assert.Equal(0, runs);
        Assert.Equal(0, ApiWarmUp.Runs);
    }

    [RequiresNativeFact]
    public void AWarmUpThatFailsIsLoggedOnceAndScriptsStillRun()
    {
        ApiWarmUp.Work = log => throw new InvalidOperationException("example failure");
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("ossl.Mark('name=' .. ll.GetObjectName())"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("name=Example Object")));
        Assert.True(ApiWarmUp.IsDone);
        string[] lines = rig.Log.Warnings.Where(l => l.Contains("warm-up")).ToArray();
        Assert.Single(lines);
        Assert.Contains("example failure", lines[0]);
    }

    [RequiresNativeFact]
    public void TheDefaultWarmUpCompilesTheApiMethods()
    {
        using var rig = new EngineRig();
        Assert.True(SchedulerHarness.WaitFor(() => ApiWarmUp.IsDone, 120000), "the warm-up ended");
        Assert.True(ApiWarmUp.Prepared > 500, "compiled " + ApiWarmUp.Prepared + " methods");
        Assert.Empty(rig.Log.Warnings);
    }
}
