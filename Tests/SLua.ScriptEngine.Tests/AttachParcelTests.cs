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
/// The parcel check after attach, as Phlox does it: worn, an object's scripts always run, wherever the avatar stands; dropped,
/// the parcel under the object decides. An attachment that enters a no-scripts parcel keeps running, and an object
/// rezzed there is paused.
/// </summary>
[Collection("native-library")]
public class AttachParcelTests
{
    private static readonly UUID Wearer = new UUID("00000000-0000-0000-0000-930000000003");

    private const string AttachCounter =
        "default {\n" +
        "  attach(key id) { llOwnerSay(\"attach\"); }\n" +
        "  changed(integer c) { llOwnerSay(\"c\" + (string)c); }\n" +
        "}";

    private static void Wear(EngineRig rig)
    {
        rig.Object.IsAttachment = true;
        rig.Object.AttachedAvatar = Wearer;
        rig.Scene.EventManager.TriggerOnAttach(rig.LocalId, UUID.Zero, Wearer);
    }

    private static void Drop(EngineRig rig)
    {
        rig.Object.IsAttachment = false;
        rig.Object.AttachedAvatar = UUID.Zero;
        rig.Scene.EventManager.TriggerOnAttach(rig.LocalId, UUID.Zero, UUID.Zero);
    }

    [RequiresNativeFact]
    public void AnAttachmentThatEntersANoScriptsParcelKeepsRunning()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        rig.Object.IsAttachment = true;
        rig.Object.AttachedAvatar = Wearer;
        UUID item = rig.Rez(EngineRig.Lsl(AttachCounter), stateSource: (int)StateSource.AttachedRez);
        rig.WaitScheduled(item);

        rig.Scene.EventManager.TriggerOnAttach(rig.LocalId, item, Wearer);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 1);
        Assert.True(rig.Heard(item, 1));
    }

    [RequiresNativeFact]
    public void AnObjectDroppedOnANoScriptsParcelIsPausedAtOnce()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        rig.Object.IsAttachment = true;
        rig.Object.AttachedAvatar = Wearer;
        UUID item = rig.Rez(EngineRig.Lsl(AttachCounter), stateSource: (int)StateSource.AttachedRez);
        rig.WaitScheduled(item);
        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));

        Drop(rig);

        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 2);
        Assert.True(rig.StaysSilent(item, 2));
    }

    [RequiresNativeFact]
    public void AnObjectDroppedOnAParcelThatAllowsScriptsKeepsRunning()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.East;
        rig.Object.IsAttachment = true;
        rig.Object.AttachedAvatar = Wearer;
        UUID item = rig.Rez(EngineRig.Lsl(AttachCounter), stateSource: (int)StateSource.AttachedRez);
        rig.WaitScheduled(item);

        Drop(rig);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void AnObjectPausedOnTheGroundRunsAgainWhenItIsWorn()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(AttachCounter));
        rig.WaitScheduled(item);
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 3);
        Assert.True(rig.StaysSilent(item, 3));

        Wear(rig);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
        Assert.True(rig.Heard(item, 3));
    }

    [RequiresNativeFact]
    public void WearingAnObjectDoesNotLiftTheEstateSwitch()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(AttachCounter));
        rig.WaitScheduled(item);
        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        rig.Engine.Policy.RegionSettingsChanged();
        Assert.Equal(PauseReason.Parcel | PauseReason.Estate, rig.Engine.PauseReasonsOf(item));

        Wear(rig);

        Assert.Equal(PauseReason.Estate, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void TheAttachEventIsPostedOnceAndTheParcelCheckDoesNotPostAnother()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.East;
        UUID item = rig.Rez(EngineRig.Lsl(AttachCounter));
        rig.WaitScheduled(item);

        Wear(rig);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("attach")));
        Thread.Sleep(300); // a real wait: proves no second one arrives
        Assert.Single(rig.MarksOf(item).Where(m => m == "attach"));
    }

    [RequiresNativeFact]
    public void AnObjectRezzedOnANoScriptsParcelIsPausedBeforeItRunsAnything()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(AttachCounter), postOnRez: true, stateSource: (int)StateSource.NewRez);
        rig.WaitScheduled(item);

        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
        Assert.True(rig.StaysSilent(item, 99));
        Assert.Empty(rig.MarksOf(item)); // not even its start-up events
    }
}
