/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using SLua.ScriptEngine.Bindings;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Follow-ups to the faults-and-lifecycle work: a counter for events dropped while a script is stopped, the test
/// seams of the scheduler options kept out of the public surface, and the engine switching itself off when every
/// worker has failed. The tests load the native library, whose handle and counters are process-wide, so they run one
/// at a time with the other native tests.
/// </summary>
[Collection("native-library")]
public class E6RulingsTests
{
    private const string Listener = "LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"data\", d) end)";

    [RequiresNativeFact]
    public void EventsPostedToAStoppedScriptAreCounted()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener);
        h.Pump();
        h.Scheduler.SetRunning(s, false);
        h.Pump();

        for (int i = 0; i < 3; i++) h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str(i.ToString()));
        h.Pump();

        Assert.Equal(3, s.DroppedWhileStopped);
        Assert.Equal(3, h.Scheduler.GetStats().DroppedWhileStopped);
        Assert.Empty(h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void EventsAlreadyQueuedWhenAScriptIsStoppedAreCountedAsDropped()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener + " ossl.Sleep(5.0)");
        h.Pump();
        for (int i = 0; i < 4; i++) h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str(i.ToString()));
        h.Pump();

        h.Scheduler.SetRunning(s, false);
        h.Pump();

        Assert.Equal(4, s.DroppedWhileStopped);
    }

    [RequiresNativeFact]
    public void AnEventForARunningScriptIsNotCountedAsDroppedWhileStopped()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener);
        h.Pump();
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Pump();

        Assert.Equal(0, s.DroppedWhileStopped);
        Assert.Equal(new[] { "data:1" }, h.MarksOf(s));
    }

    [Theory]
    [InlineData("BeforePass")]
    [InlineData("StepOverride")]
    public void TheTestSeamsOfTheSchedulerOptionsAreNotPublic(string name)
    {
        PropertyInfo publicProperty = typeof(SchedulerOptions).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo internalProperty = typeof(SchedulerOptions).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Null(publicProperty);
        Assert.NotNull(internalProperty);
        Assert.Contains(typeof(SLuaEngine).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>(),
            a => a.AssemblyName == "SLua.ScriptEngine.Tests");
    }

    [RequiresNativeFact]
    public void WhenEveryWorkerHasFailedTheEngineDisablesItselfWithOneLogLineAndTheRegionCarriesOn()
    {
        int armed = 0;
        using var rig = new EngineRig(tweak: o =>
        {
            o.MaxWorkerCrashes = 1;
            o.WorkerRestartDelayMs = 0;
            o.BeforePass = (_, _) => { if (Volatile.Read(ref armed) != 0) throw new InvalidOperationException("injected pass failure"); };
        });
        Volatile.Write(ref armed, 1);
        rig.Rez(EngineRig.Slua("ossl.Count()"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.IsDisabled), "the engine disabled itself");
        // Scripts given to the disabled engine after that cost no more log lines.
        for (int i = 0; i < 3; i++) rig.Rez(EngineRig.Slua("ossl.Count()"));
        Thread.Sleep(300); // proves a line does NOT appear: a real wait

        Assert.Single(rig.Log.Lines, l => l.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("could not be given to a worker"));
        Assert.Same(rig.Engine, rig.Scene.RequestModuleInterface<IScriptModule>());
        Assert.False(rig.Engine.PostScriptEvent(OpenMetaverse.UUID.Random(), "dataserver", new object[] { "k", "v" }));
    }

    [RequiresNativeFact]
    public void AWorkerThatFailedWhileAnotherStillRunsDoesNotDisableTheEngine()
    {
        int armed = 0;
        using var rig = new EngineRig(settings: new Dictionary<string, string> { ["WorkerThreads"] = "2" }, tweak: o =>
        {
            o.MaxWorkerCrashes = 1;
            o.WorkerRestartDelayMs = 0;
            o.BeforePass = (worker, _) => { if (worker == 0 && Volatile.Read(ref armed) != 0) throw new InvalidOperationException("injected pass failure"); };
        });
        Volatile.Write(ref armed, 1);
        rig.Rez(EngineRig.Slua("ossl.Count()"));
        rig.Rez(EngineRig.Slua("ossl.Count()"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.Scheduler.GetStats().FailedWorkers == 1), "worker 0 failed");
        Thread.Sleep(200); // proves the engine does NOT disable: a real wait
        Assert.False(rig.Engine.IsDisabled);
    }
}
