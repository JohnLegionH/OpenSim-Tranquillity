/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The remaining policy hooks (bots, owner change, controls released, presence removed, arrival, Experience land,
/// Experience grant revoked, permissions removed) reach the engine's scripts' grants the way Phlox's handlers reach theirs.
/// The ll functions that make grants are bound in a later step; until then the grants are a recorder.
/// </summary>
[Collection("native-library")]
public class PolicyHooksTests
{
    private static readonly UUID Resident = new UUID("00000000-0000-0000-0000-930000000004");

    private static EngineRig RigWith(RecordingGrants grants)
    {
        var rig = new EngineRig();
        rig.Engine.Grants = grants;
        return rig;
    }

    // ---- hook 32: bots ----

    [RequiresNativeFact]
    public void AnObjectLeavingTheSceneTellsTheGrantsSoItsBotsAreCleanedUp()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);

        rig.Scene.EventManager.TriggerObjectBeingRemovedFromScene(rig.Object);

        Assert.Equal(new[] { "removed:" + rig.Object.UUID }, grants.Calls);
    }

    // ---- hook 36: owner or group change ----

    [RequiresNativeFact]
    public void ANewOwnerEndsTheGrantsButANewGroupAloneDoesNot()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);

        rig.Scene.EventManager.TriggerObjectOwnerOrGroupChanged(rig.Object, EngineRig.Owner, EngineRig.Owner, UUID.Zero, UUID.Random());
        Assert.Empty(grants.Calls);

        rig.Scene.EventManager.TriggerObjectOwnerOrGroupChanged(rig.Object, EngineRig.Owner, Resident, UUID.Zero, UUID.Zero);
        Assert.Equal(new[] { "owner:" + rig.Object.UUID }, grants.Calls);
    }

    // ---- hook 38: controls released ----

    [RequiresNativeFact]
    public void AControlsReleaseByTheRegionIsToldAsTheRegionsAndTheScriptsOwnIsNot()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, Resident);
        UUID[] items = { UUID.Random(), UUID.Random() };
        grants.Clear();

        rig.Scene.EventManager.TriggerScriptControlsReleased(sp.UUID, items);
        using (SceneHooksB.OwnControlChange())
            rig.Scene.EventManager.TriggerScriptControlsReleased(sp.UUID, items);
        rig.Scene.EventManager.TriggerScriptControlsReleased(sp.UUID, items); // the scope has ended

        Assert.Equal(new[] { $"controls:{sp.UUID}:2:region", $"controls:{sp.UUID}:2:script", $"controls:{sp.UUID}:2:region" }, grants.Calls);
    }

    [RequiresNativeFact]
    public void AReleaseForAnAvatarWhoIsNotHereOrIsOnlyAChildAgentIsNotTheRegionsToAct()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);
        UUID stranger = UUID.Random();
        ScenePresence child = SceneHelpers.AddChildScenePresence(rig.Scene, UUID.Random());
        grants.Clear();

        rig.Scene.EventManager.TriggerScriptControlsReleased(stranger, new[] { UUID.Random() });
        rig.Scene.EventManager.TriggerScriptControlsReleased(child.UUID, new[] { UUID.Random() });

        Assert.Equal(new[] { $"controls:{stranger}:1:script", $"controls:{child.UUID}:1:script" }, grants.Calls);
    }

    // ---- hook 39: a presence is removed ----

    [RequiresNativeFact]
    public void ARemovedPresenceMakesTheScriptsThatRunOnlyByControlsBeAskedAgain()
    {
        using var rig = new EngineRig();
        rig.InstallLand();
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, Resident);
        rig.Object.AbsolutePosition = EngineRig.East;
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        rig.ItemOf(item).PermsGranter = sp.UUID;
        sp.RegisterControlEventsToScript(1, 1, 0, rig.LocalId, item);
        rig.Object.AbsolutePosition = EngineRig.West;
        rig.Scene.EventManager.TriggerGroupCrossedToNewParcel(rig.Object, rig.EastParcel, rig.WestParcel);
        Assert.Equal(PauseReason.None, rig.Engine.PauseReasonsOf(item)); // runs only by its controls

        rig.ItemOf(item).PermsGranter = UUID.Zero; // the grant is gone and no release event came
        rig.Scene.EventManager.TriggerOnRemovePresence(sp.UUID);

        Assert.Equal(PauseReason.Parcel, rig.Engine.PauseReasonsOf(item));
    }

    // ---- hook 40: an avatar arrives ----

    [RequiresNativeFact]
    public void AnAvatarBecomingARootAgentIsToldToTheGrants()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, Resident);

        rig.Scene.EventManager.TriggerOnMakeRootAgent(sp);

        Assert.Contains("arrived:" + sp.UUID, grants.Calls);
    }

    // ---- hook 41: Experience land check ----

    [RequiresNativeFact]
    public void ARootAvatarEnteringAParcelStartsTheExperienceLandCheckAndAChildAgentDoesNot()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);
        ScenePresence root = SceneHelpers.AddScenePresence(rig.Scene, Resident);
        ScenePresence child = SceneHelpers.AddChildScenePresence(rig.Scene, UUID.Random());
        grants.Clear();

        rig.Scene.EventManager.TriggerAvatarEnteringNewParcel(root, 1, rig.Scene.RegionInfo.RegionID);
        rig.Scene.EventManager.TriggerAvatarEnteringNewParcel(child, 1, rig.Scene.RegionInfo.RegionID);

        Assert.Equal(new[] { "expland:" + root.UUID }, grants.Calls);
    }

    // ---- hooks 42 and 43: an Experience grant revoked, permissions removed ----

    [RequiresNativeFact]
    public void ARevokedExperienceGrantReachesTheGrantsOfAScriptTheEngineRunsOnly()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);
        UUID granter = UUID.Random(), experience = UUID.Random();

        rig.Scene.EventManager.TriggerExperiencePermissionsRevoked(rig.Object.UUID, item, granter, experience, 4, 1);
        rig.Scene.EventManager.TriggerExperiencePermissionsRevoked(rig.Object.UUID, UUID.Random(), granter, experience, 4, 1); // another engine's script

        Assert.Equal(new[] { $"expend:{item}:{granter}:{experience}" }, grants.Calls);
    }

    [RequiresNativeFact]
    public void PermissionsTheRegionTookFromAScriptReachTheGrantsOfAScriptTheEngineRunsOnly()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);
        UUID item = rig.Rez(EngineRig.Lsl(EngineRig.Probe));
        rig.WaitScheduled(item);

        rig.Scene.EventManager.TriggerScriptPermissionsRemoved(rig.Object.UUID, item, UUID.Random(), 4);
        rig.Scene.EventManager.TriggerScriptPermissionsRemoved(rig.Object.UUID, UUID.Random(), UUID.Random(), 4);

        Assert.Equal(new[] { "perms:" + item }, grants.Calls);
    }

    // ---- subscription ----

    [RequiresNativeFact]
    public void TheHooksFollowTheRegionFromAddToRemove()
    {
        var grants = new RecordingGrants();
        using var rig = RigWith(grants);
        Assert.True(rig.Engine.Policy.IsSubscribed);

        rig.Engine.RemoveRegion(rig.Scene);
        rig.Scene.EventManager.TriggerObjectBeingRemovedFromScene(rig.Object);

        Assert.Null(rig.Engine.Policy);
        Assert.Empty(grants.Calls);
    }
}
