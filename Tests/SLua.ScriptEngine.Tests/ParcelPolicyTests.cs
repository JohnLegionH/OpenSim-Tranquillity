/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>An estate module that only raises the region-settings event, as the estate tools do after "disable scripts" is changed.</summary>
internal sealed class EstateStub : IEstateModule
{
    public event ChangeDelegate OnRegionInfoChange;
    public event ChangeDelegate OnEstateInfoChange { add { } remove { } }
    public event MessageDelegate OnEstateMessage { add { } remove { } }
    public event EstateTeleportOneUserHomeRequest OnEstateTeleportOneUserHomeRequest { add { } remove { } }
    public event EstateTeleportAllUsersHomeRequest OnEstateTeleportAllUsersHomeRequest { add { } remove { } }

    public int Subscribers => OnRegionInfoChange?.GetInvocationList().Length ?? 0;

    public void TriggerRegionInfoChange() => OnRegionInfoChange?.Invoke(UUID.Zero);

    public uint GetRegionFlags() => 0;
    public bool IsManager(UUID avatarID) => false;
    public string SetEstateOwner(int estateID, UserAccount account) => null;
    public string SetEstateName(int estateID, string newName) => null;
    public string SetRegionEstate(RegionInfo regionInfo, int estateID) => null;
    public string CreateEstate(string estateName, UUID ownerID) => null;
    public void sendRegionHandshakeToAll() { }
    public void TriggerEstateInfoChange() { }
    public void setEstateTerrainBaseTexture(int level, UUID texture) { }
    public void SetEstateTerrainTextures(List<UUID> textureIDs, int types) { }
    public void setEstateTerrainTextureHeights(int corner, float lowValue, float highValue) { }
    public bool IsTerrainXfer(ulong xferID) => false;
    public bool handleEstateChangeInfoCap(string estateName, UUID invoice, bool? externallyVisible, bool? allowDirectTeleport, bool? denyAnonymous,
        bool? denyAgeUnverified, bool? alloVoiceChat, bool? overridePublicAccess, bool? allowEnvironmentOverride) => false;
    public void HandleRegionInfoRequest(IClientAPI remote_client) { }
    public bool SetRegionInfobyCap(OSDMap map) => false;
}

/// <summary>
/// No Scripts parcels are enforced live, as Phlox does it: a script on a parcel that does not allow scripts is paused (never
/// stopped or reset) and runs on, with what was queued for it, when the parcel allows it. Attachments and scripts holding
/// taken controls are exempt. The pause goes through the scheduler's suspend, so queued events and the prim's event flags
/// are kept and the pause is counted. The estate's "disable scripts" pauses every script of the engine the same way.
/// </summary>
[Collection("native-library")]
public class ParcelPolicyTests
{
    private static readonly UUID Resident = new UUID("00000000-0000-0000-0000-930000000002");
    private static readonly UUID GroupA = new UUID("00000000-0000-0000-8888-930000000001");
    private static readonly UUID GroupB = new UUID("00000000-0000-0000-8888-930000000002");

    private static bool Allows(LandData land, UUID owner, UUID group) => SceneHooksB.ParcelAllowsScripts(land, owner, group);

    [Fact]
    public void TheParcelRuleIsHalcyons()
    {
        var land = new LandData { OwnerID = EngineRig.ParcelOwner, GroupID = GroupA, Flags = 0 };
        Assert.True(Allows(land, EngineRig.ParcelOwner, UUID.Zero));  // the object's owner owns the parcel
        Assert.False(Allows(land, Resident, GroupA));                  // group matches, flag off
        land.Flags = (uint)ParcelFlags.AllowGroupScripts;
        Assert.True(Allows(land, Resident, GroupA));
        Assert.False(Allows(land, Resident, GroupB));
        land.Flags = (uint)ParcelFlags.AllowOtherScripts;
        Assert.True(Allows(land, Resident, GroupB));
        // a parcel with no group has no group to match
        var noGroup = new LandData { OwnerID = EngineRig.ParcelOwner, GroupID = UUID.Zero, Flags = (uint)ParcelFlags.AllowGroupScripts };
        Assert.False(Allows(noGroup, Resident, UUID.Zero));
        Assert.False(Allows(null, Resident, GroupA));                  // no parcel, not allowed
    }

    [RequiresNativeFact]
    public void AScriptOnANoScriptsParcelStartsPausedAndIsToldSo()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
        Assert.False(rig.Engine.SchedulerScriptFor(item).IsRunning, "a paused script does not run");
    }

    [RequiresNativeFact]
    public void AScriptOnAParcelThatAllowsScriptsRuns()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.East;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 1);
        Assert.True(rig.Heard(item, 1));
    }

    [RequiresNativeFact]
    public void TheParcelOwnersOwnScriptsRunOnANoScriptsParcel()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        SceneObjectGroup mine = rig.AddObject("Example Owned", EngineRig.West, EngineRig.ParcelOwner);
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe), part: mine.RootPart);
        rig.WaitScheduled(item);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void APausedScriptDropsWhatReachesItAndCarriesOnWithNewEventsWhenTheParcelAllowsScripts()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        rig.PostProbe(item, 7);
        Assert.True(rig.StaysSilent(item, 7));

        rig.SetParcelFlags(rig.WestParcel, otherScripts: true, groupScripts: false);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 8);
        Assert.True(rig.Heard(item, 8), "the script runs new events after the resume");
        Assert.False(rig.MarksOf(item).Contains("c7"), "the event that arrived while paused was dropped, as Phlox drops it");
    }

    [RequiresNativeFact]
    public void ChangingTheParcelToNoScriptsPausesARunningScriptWithoutResettingIt()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.East;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        rig.PostProbe(item, 1);
        Assert.True(rig.Heard(item, 1));

        rig.SetParcelFlags(rig.EastParcel, otherScripts: false, groupScripts: false);
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 2);
        Assert.True(rig.StaysSilent(item, 2));

        rig.SetParcelFlags(rig.EastParcel, otherScripts: true, groupScripts: false);
        rig.PostProbe(item, 3);
        Assert.True(rig.Heard(item, 3));
        Assert.False(rig.MarksOf(item).Contains("c2")); // dropped while paused
        Assert.Single(rig.MarksOf(item).Where(m => m == "c1"));  // nothing was replayed: it was never reset
    }

    [RequiresNativeFact]
    public void AnObjectThatMovesToAnotherParcelIsCheckedAgain()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.East;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        rig.Object.AbsolutePosition = EngineRig.West;
        rig.Scene.EventManager.TriggerGroupCrossedToNewParcel(rig.Object, rig.EastParcel, rig.WestParcel);
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));

        rig.Object.AbsolutePosition = EngineRig.East;
        rig.Scene.EventManager.TriggerGroupCrossedToNewParcel(rig.Object, rig.WestParcel, rig.EastParcel);
        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void ANewOwnerOfTheObjectDecidesWhetherTheParcelAllowsItsScripts()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));

        foreach (SceneObjectPart part in rig.Object.Parts) part.OwnerID = EngineRig.ParcelOwner;
        rig.Scene.EventManager.TriggerObjectOwnerOrGroupChanged(rig.Object, EngineRig.Owner, EngineRig.ParcelOwner, UUID.Zero, UUID.Zero);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void AnAttachmentIsNeverPausedByAParcel()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        rig.Object.IsAttachment = true;
        rig.Object.AttachedAvatar = UUID.Random();
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe), stateSource: (int)StateSource.AttachedRez);
        rig.WaitScheduled(item);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void AScriptHoldingTakenControlsKeepsRunningOnANoScriptsParcelUntilTheRegionReleasesThem()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, Resident);
        rig.Object.AbsolutePosition = EngineRig.East;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        rig.ItemOf(item).PermsGranter = sp.UUID;
        rig.ItemOf(item).PermsMask = 4; // PERMISSION_TAKE_CONTROLS
        sp.RegisterControlEventsToScript(1, 1, 0, rig.LocalId, item);

        rig.Object.AbsolutePosition = EngineRig.West;
        rig.Scene.EventManager.TriggerGroupCrossedToNewParcel(rig.Object, rig.EastParcel, rig.WestParcel);
        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));

        sp.ClearControls(); // a stand-up or Release Keys: the region releases them
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void AScriptThatTakesControlsIsCheckedWhenItAsksForTheCheck()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, Resident);
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));

        rig.ItemOf(item).PermsGranter = sp.UUID;
        sp.RegisterControlEventsToScript(1, 1, 0, rig.LocalId, item);
        rig.Engine.RecheckParcel(item); // what llTakeControls will call

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void AParcelPauseIsNotTheOwnersAndLeavesTheRunningBoxTicked()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
        Assert.True(rig.ItemOf(item).ScriptRunning);
        Assert.True(rig.Engine.GetScriptState(item));
    }

    [RequiresNativeFact]
    public void AScriptTheOwnerStoppedStaysStoppedWhenTheParcelResumes()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe), running: false);
        rig.WaitScheduled(item);
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));

        rig.SetParcelFlags(rig.WestParcel, otherScripts: true, groupScripts: false);

        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item));
        Assert.False(rig.Engine.SchedulerScriptFor(item).Running);
    }

    [RequiresNativeFact]
    public void AConsoleSuspendIsNotLiftedByTheParcelAndTheParcelNotByTheResume()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        Assert.True(rig.Engine.SuspendScript(item));
        Assert.Equal(PauseReason.Parcel | PauseReason.Core, rig.Engine.PauseReasonsOf(item));

        rig.SetParcelFlags(rig.WestParcel, otherScripts: true, groupScripts: false);
        Assert.Equal(PauseReason.Core, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 3);
        Assert.True(rig.StaysSilent(item, 3));

        Assert.True(rig.Engine.ResumeScript(item));
        Assert.True(rig.Heard(item, 3));

        rig.SetParcelFlags(rig.WestParcel, otherScripts: false, groupScripts: false);
        Assert.True(rig.Engine.ResumeScript(item)); // the region's resume does not undo the parcel
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void ThePausedScriptsEventFlagsAreKeptAndThePauseIsCounted()
    {
        var flags = new RecordingPrimFlags();
        using var rig = new EngineRig(primFlags: flags);
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.East;
        UUID item = rig.Rez(EngineRig.Lsl("default { touch_start(integer n) { } }"));
        rig.WaitScheduled(item);
        Assert.True(SchedulerHarness.WaitFor(() => flags.History(item).Length >= 1));
        ulong[] before = flags.History(item);

        rig.SetParcelFlags(rig.EastParcel, otherScripts: false, groupScripts: false);

        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.GetStats().Suspended == 1), "the pause is counted by the scheduler");
        Assert.Equal(before, flags.History(item)); // no flag call: the prim still asks for touches
        Assert.Equal(1, rig.Engine.PolicyStats.ParcelPaused);

        rig.SetParcelFlags(rig.EastParcel, otherScripts: true, groupScripts: false);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.GetStats().Suspended == 0));
        Assert.Equal(1, rig.Engine.PolicyStats.ParcelResumed);
    }

    // ---- the estate's "disable scripts" ----

    [RequiresNativeFact]
    public void TheEstateSwitchPausesEveryScriptAndLiftingItResumesThem()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.East;
        UUID a = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        SceneObjectGroup other = rig.AddObject("Example Other", EngineRig.East, Resident);
        UUID b = rig.Rez(EngineRig.Lsl(EngineRig.Probe), part: other.RootPart);
        rig.WaitScheduled(a);
        rig.WaitScheduled(b);

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        rig.Engine.Policy.RegionSettingsChanged();
        Assert.Equal(PauseReason.Estate, rig.Engine.PauseReasonsOf(a));
        Assert.Equal(PauseReason.Estate, rig.Engine.PauseReasonsOf(b));
        rig.PostProbe(a, 5);
        Assert.True(rig.StaysSilent(a, 5));

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = false;
        rig.Engine.Policy.RegionSettingsChanged();
        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(a));
        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(b));
        rig.PostProbe(a, 6);
        Assert.True(rig.Heard(a, 6));
        Assert.False(rig.MarksOf(a).Contains("c5")); // dropped while paused
        Assert.Equal(2, rig.Engine.PolicyStats.EstatePaused);
        Assert.Equal(2, rig.Engine.PolicyStats.EstateResumed);
    }

    [RequiresNativeFact]
    public void AScriptMadeWhileTheEstateSwitchIsOnStartsPaused()
    {
        using var rig = new EngineRig();
        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        Assert.Equal(PauseReason.Estate, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 1);
        Assert.True(rig.StaysSilent(item, 1));
    }

    [RequiresNativeFact]
    public void AScriptPausedByTheParcelAndTheEstateRunsOnlyWhenBothAreLifted()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        rig.Object.AbsolutePosition = EngineRig.West;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        rig.Engine.Policy.RegionSettingsChanged();
        Assert.Equal(PauseReason.Parcel | PauseReason.Estate, rig.Engine.PauseReasonsOf(item));

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = false;
        rig.Engine.Policy.RegionSettingsChanged();
        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
        rig.PostProbe(item, 4);
        Assert.True(rig.StaysSilent(item, 4));

        rig.SetParcelFlags(rig.WestParcel, otherScripts: true, groupScripts: false);
        rig.PostProbe(item, 5);
        Assert.True(rig.Heard(item, 5));
        Assert.False(rig.MarksOf(item).Contains("c4")); // dropped while paused
    }

    [RequiresNativeFact]
    public void AnAttachmentIsPausedByTheEstateSwitchLikeAnyOtherScript()
    {
        using var rig = new EngineRig();
        rig.Object.IsAttachment = true;
        rig.Object.AttachedAvatar = UUID.Random();
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe), stateSource: (int)StateSource.AttachedRez);
        rig.WaitScheduled(item);

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        rig.Engine.Policy.RegionSettingsChanged();

        Assert.Equal(PauseReason.Estate, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void TheEstateModulesRegionInfoEventTellsTheEngineOnceTheRegionHasLoaded()
    {
        using var rig = new EngineRig();
        var estate = new EstateStub();
        rig.Scene.RegisterModuleInterface<IEstateModule>(estate);
        rig.Scene.RegisterModuleInterface<OpenSim.Region.Framework.Interfaces.IWorldComm>(new ChatAndListenTests.StandInWorldComm());   // without one the engine disables itself
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        rig.Engine.RegionLoaded(rig.Scene);
        Assert.Equal(1, estate.Subscribers);

        rig.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        estate.TriggerRegionInfoChange();

        Assert.Equal(PauseReason.Estate, rig.Engine.PauseReasonsOf(item));
    }

    [RequiresNativeFact]
    public void TheEngineLetsTheRegionLeaveTheParcelRuleToIt()
    {
        using var rig = new EngineRig();

        Assert.True(rig.Engine.EnforcesParcelScriptRules);
    }
}
