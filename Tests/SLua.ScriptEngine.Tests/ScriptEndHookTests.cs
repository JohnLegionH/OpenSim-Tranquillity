/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A script that is stopped (the Running checkbox, llSetScriptState), held while its object crosses, or paused by the estate
/// switch stops its repeating sensor, and the sensor comes back only when the script runs again: started, the crossing failed,
/// the switch off. This is what the Phlox engine does through the hooks of LSLSystemAPI (OnScriptStopped, OnCrossingHold,
/// PauseSensorForParcel and RestoreSensorAfterParcel, and OnScriptInjected for the start), and what the Second Life wiki says a
/// stopped script does not do: llSetScriptState "does not run" a script that is off.
/// </summary>
[Collection("native-library")]
public class ScriptEndHookTests
{
    private const int Passive = 4;

    // The sensor is armed by an event the test posts (changed with 7; the region raises others itself), so a restart cannot arm it again behind the test's back.
    private static readonly string Repeating =
        "LLEvents:on('changed', function(c) if c ~= 7 then return end ll.SensorRepeat('Sensor Target', NULL_KEY, " + Passive + ", 20.0, PI, 0.3) ossl.Mark('armed') end)\n" +
        "LLEvents:on('sensor', function() ossl.Mark('s') end)\n" +
        "ossl.Mark('ready')\n";

    private static object[] Sensors(EngineRig rig, UUID item) =>
        rig.Engine.HostServices.AsyncCommands?.SensorRepeatPlugin.GetSerializationData(item) ?? Array.Empty<object>();

    private static int Fired(EngineRig rig, UUID item) => rig.MarksOf(item).Count(m => m == "s");

    private static string Diag(EngineRig rig, UUID item) =>
        "marks=[" + string.Join(",", rig.MarksOf(item)) + "] sensors=" + Sensors(rig, item).Length + " log=[" + string.Join(" | ", rig.Log.Lines) + "]";

    private static UUID ArmedRepeatingScript(EngineRig rig)
    {
        rig.AddObject("Sensor Target", rig.Object.AbsolutePosition + new Vector3(4, 0, 0), Owner);
        UUID item = rig.Rez(Slua(Repeating));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("ready")), "no 'ready': " + Diag(rig, item));
        Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { 7 }));
        Assert.True(SchedulerHarness.WaitFor(() => Fired(rig, item) >= 2), "the sensor did not repeat: " + Diag(rig, item));
        Assert.NotEmpty(Sensors(rig, item));
        return item;
    }

    private static void ComesBack(EngineRig rig, UUID item)
    {
        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length > 0), "the sensor did not come back: " + Diag(rig, item));
        int before = Fired(rig, item);
        Assert.True(SchedulerHarness.WaitFor(() => Fired(rig, item) > before + 1), "it came back but did not fire: " + Diag(rig, item));
    }

    // ---- stopped ----

    [RequiresNativeFact]
    public void AStoppedScriptsRepeatingSensorStopsAndComesBackOnlyWhenTheScriptIsStarted()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = ArmedRepeatingScript(rig);

        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);

        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length == 0), "the stop left the sensor running: " + Diag(rig, item));
        int frozen = Fired(rig, item);
        Thread.Sleep(1200);   // a real wait: it proves nothing fires while the script is stopped
        Assert.Equal(frozen, Fired(rig, item));
        Assert.Empty(Sensors(rig, item));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);

        ComesBack(rig, item);
    }

    [RequiresNativeFact]
    public void AStopOfAScriptWithNoSensorChangesNothing()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(Repeating));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("ready")));

        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item).State == ScriptRunState.Stopped));
        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.GetScriptState(item)));

        Assert.Empty(Sensors(rig, item));   // a start arms only a sensor the script had
        Assert.Equal(new[] { "ready" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void AResetAfterAStopLeavesNoSensorToComeBack()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = ArmedRepeatingScript(rig);
        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length == 0));

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);
        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Count(m => m == "ready") == 2), "main ran again: " + Diag(rig, item));
        Thread.Sleep(800);   // a real wait: the reset dropped the sensor, so none comes back
        Assert.Empty(Sensors(rig, item));
    }

    // ---- held while the object crosses ----

    [RequiresNativeFact]
    public void AHeldScriptsRepeatingSensorStopsAndComesBackWhenTheCrossingFails()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = ArmedRepeatingScript(rig);

        rig.Scene.EventManager.TriggerGroupBeginInTransit(rig.Object);

        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length == 0), "the hold left the sensor running: " + Diag(rig, item));

        rig.Scene.EventManager.TriggerGroupEndInTransit(rig.Object, false);

        ComesBack(rig, item);
    }

    [RequiresNativeFact]
    public void AScriptOfAnObjectThatLeftNeverGetsItsSensorBack()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = ArmedRepeatingScript(rig);
        rig.Scene.EventManager.TriggerGroupBeginInTransit(rig.Object);
        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length == 0));

        rig.Scene.EventManager.TriggerGroupEndInTransit(rig.Object, true);   // the object left; the region removes the script

        Thread.Sleep(800);   // a real wait: nothing starts the sensor again
        Assert.Empty(Sensors(rig, item));
    }

    // ---- paused by the estate switch ----

    [RequiresNativeFact]
    public void AScriptPausedByTheEstateSwitchStopsItsSensorAndGetsItBackWhenTheSwitchIsOff()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = ArmedRepeatingScript(rig);

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        rig.Engine.Policy.RegionSettingsChanged();

        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length == 0), "the pause left the sensor running: " + Diag(rig, item));

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = false;
        rig.Engine.Policy.RegionSettingsChanged();

        ComesBack(rig, item);
    }

    [RequiresNativeFact]
    public void ACrossingThatEndsWhileTheEstateSwitchIsOnLeavesTheSensorOff()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = ArmedRepeatingScript(rig);
        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        rig.Engine.Policy.RegionSettingsChanged();
        rig.Scene.EventManager.TriggerGroupBeginInTransit(rig.Object);
        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length == 0));

        rig.Scene.EventManager.TriggerGroupEndInTransit(rig.Object, false);

        Thread.Sleep(800);   // a real wait: the estate switch still holds the script
        Assert.Empty(Sensors(rig, item));
        Assert.Equal(PauseReason.Estate, rig.Engine.PauseReasonsOf(item));
    }
}
