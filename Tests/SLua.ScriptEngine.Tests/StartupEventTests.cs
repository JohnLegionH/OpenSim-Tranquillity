/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Interfaces;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The events a script's start brings, in Second Life's order as Phlox posts them: state_entry (the runtime runs it
/// when the script starts), then on_rez when the rez asks for it, then attach for a worn object, then changed with
/// the region-start or arrival bit. CHANGED_REGION_START is 0x400 (https://wiki.secondlife.com/wiki/CHANGED_REGION_START).
/// </summary>
[Collection("native-library")]
public class StartupEventTests
{
    private const string AllStartup =
        "default {\n" +
        "  state_entry() { llOwnerSay(\"entry\"); }\n" +
        "  on_rez(integer p) { llOwnerSay(\"rez\" + (string)p); }\n" +
        "  attach(key id) { llOwnerSay(\"attach\"); }\n" +
        "  changed(integer c) { llOwnerSay(\"changed\" + (string)c); }\n" +
        "}";

    private static string[] Settled(EngineRig rig, UUID item, int count)
    {
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length >= count), "the expected marks arrived");
        Thread.Sleep(300); // proves nothing more arrives: a real wait
        return rig.MarksOf(item);
    }

    [RequiresNativeFact]
    public void ANewRezThatAsksForOnRezGetsStateEntryThenOnRezWithItsParameter()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(AllStartup), startParam: 42, postOnRez: true, stateSource: (int)StateSource.NewRez);

        Assert.Equal(new[] { "entry", "rez42" }, Settled(rig, item, 2));
    }

    [RequiresNativeFact]
    public void ARezThatDoesNotAskForOnRezGetsOnlyStateEntry()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(AllStartup), startParam: 42, postOnRez: false, stateSource: (int)StateSource.NewRez);

        Assert.Equal(new[] { "entry" }, Settled(rig, item, 1));
    }

    [RequiresNativeFact]
    public void ARegionStartGetsChangedWithTheRegionStartBitAfterOnRez()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(AllStartup), postOnRez: true, stateSource: (int)StateSource.RegionStart);

        Assert.Equal(new[] { "entry", "rez0", "changed1024" }, Settled(rig, item, 3));
    }

    [RequiresNativeFact]
    public void ACrossingGetsChangedRegionForTheRootPrimOnly()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(AllStartup), stateSource: (int)StateSource.PrimCrossing);

        Assert.Equal(new[] { "entry", "changed256" }, Settled(rig, item, 2));
    }

    [RequiresNativeFact]
    public void ATeleportOfAWornObjectGetsRegionAndTeleportBits()
    {
        using var rig = new EngineRig();
        rig.Object.AttachedAvatar = UUID.Random();
        rig.Object.AttachmentPoint = 1;
        UUID item = rig.Rez(EngineRig.Lsl(AllStartup), stateSource: (int)StateSource.Teleporting);

        Assert.Equal(new[] { "entry", "changed768" }, Settled(rig, item, 2));
    }

    [RequiresNativeFact]
    public void AWornObjectsScriptGetsAttachAfterOnRez()
    {
        using var rig = new EngineRig();
        rig.Object.AttachedAvatar = UUID.Random();
        UUID item = rig.Rez(EngineRig.Lsl(AllStartup), postOnRez: true, stateSource: (int)StateSource.AttachedRez);

        Assert.Equal(new[] { "entry", "rez0", "attach" }, Settled(rig, item, 3));
    }

    [RequiresNativeFact]
    public void AScriptWithoutTheHandlersGetsNoneOfTheStartupEventsQueued()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl("default { state_entry() { llOwnerSay(\"entry\"); } }"), postOnRez: true, stateSource: (int)StateSource.RegionStart);

        Assert.Equal(new[] { "entry" }, Settled(rig, item, 1));
        Assert.Equal(2, rig.Engine.SchedulerScriptFor(item).DroppedUnhandled); // on_rez and changed
    }

    [RequiresNativeFact]
    public void ASluaScriptGetsOnRezAfterItsMainFunctionHasRun()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(
            "ossl.Mark(\"main\")\nLLEvents:on(\"on_rez\", function(p) ossl.Mark(\"rez\" .. p) end)"),
            startParam: 7, postOnRez: true, stateSource: (int)StateSource.NewRez);

        Assert.Equal(new[] { "main", "rez7" }, Settled(rig, item, 2));
    }

    [RequiresNativeFact]
    public void AScriptThatStartsStoppedGetsItsStartupEventsWhenItIsStarted()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(AllStartup), running: false, startParam: 3, postOnRez: true, stateSource: (int)StateSource.NewRez);
        rig.WaitScheduled(item);
        Thread.Sleep(300); // proves nothing runs while it is stopped: a real wait
        Assert.Empty(rig.MarksOf(item));

        rig.Scene.EventManager.TriggerStartScript(rig.LocalId, item);

        Assert.Equal(new[] { "entry", "rez3" }, Settled(rig, item, 2));
    }

    [RequiresNativeFact]
    public void AResetRunsStateEntryAgainAndPostsNoOnRez()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(AllStartup), postOnRez: true, stateSource: (int)StateSource.NewRez);
        Settled(rig, item, 2);

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);

        Assert.Equal(new[] { "entry", "rez0", "entry" }, Settled(rig, item, 3));
    }
}
