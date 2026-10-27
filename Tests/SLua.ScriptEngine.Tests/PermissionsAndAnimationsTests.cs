/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Permissions and animations run on the real LSLSystemAPI, with the engine providing the state tracker the API tells when a
/// script's grant changes. The grants are Phlox's: an avatar wearing the object is granted what it asks of the implicit set
/// (taking controls, triggering animations, attaching, tracking and controlling the camera, overriding animations) with no
/// dialog; an avatar who is not in the region ends the request with run_time_permissions(0); anyone else present is asked in a
/// dialog and nothing is granted until the viewer answers. A reset ends the grant (SL wiki LlRequestPermissions and
/// LlResetScript).
/// </summary>
[Collection("native-library")]
public class PermissionsAndAnimationsTests
{
    // The permission bits (SL wiki LlRequestPermissions): take controls 0x4, trigger animation 0x10, attach 0x20, track camera 0x400,
    // control camera 0x800, override animations 0x8000.
    private const int TakeControls = 0x4;
    private const int TriggerAnimation = 0x10;
    private const int Attach = 0x20;
    private const int WearerImplicit = TakeControls | TriggerAnimation | Attach | 0x400 | 0x800 | 0x8000;

    private static string Diag(EngineRig rig, UUID item) =>
        "marks=[" + string.Join(",", rig.MarksOf(item)) + "] shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "]";

    private static void Heard(EngineRig rig, UUID item, string mark) =>
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark)), "no '" + mark + "': " + Diag(rig, item));

    private const string PermHandler = "LLEvents:on('run_time_permissions', function(p) ossl.Mark('perm=' .. p .. '|' .. ll.GetPermissions() .. '|' .. tostring(ll.GetPermissionsKey())) end)\n";

    private static ScenePresence Wear(EngineRig rig)
    {
        ScenePresence wearer = SceneHelpers.AddScenePresence(rig.Scene, UUID.Random());
        rig.Object.IsAttachment = true;
        rig.Object.AttachedAvatar = wearer.UUID;
        return wearer;
    }

    [Fact]
    public void PermissionsAndAnimationsRunOnTheRealApi()
    {
        Assert.All(new[] { "llRequestPermissions", "llReleaseControls", "llStartAnimation", "llStopAnimation", "llAttachToAvatarTemp" },
            n => Assert.Contains(n, LlServed.Served));
        // the experience requests also need the host's experience callback, which the engine does not provide
        Assert.Contains(LlServed.NotServed, r => r.StartsWith("llRequestExperiencePermissions:"));
        Assert.DoesNotContain(LlServed.NotServed, r => r.StartsWith("llRequestPermissions:") || r.StartsWith("llStartAnimation:"));
    }

    [RequiresNativeFact]
    public void TheEngineProvidesTheStateTrackerAndAGrantTellsIt()
    {
        using var rig = new EngineRig(productionFunctions: true);
        ScenePresence wearer = Wear(rig);
        UUID item = rig.Rez(Slua(PermHandler + "ll.RequestPermissions('" + wearer.UUID + "', " + TriggerAnimation + ")\n"));

        Heard(rig, item, "perm=" + TriggerAnimation + "|" + TriggerAnimation + "|" + wearer.UUID);
        Assert.NotNull(rig.Engine.HostServices.StateManager);
        Assert.True(((EngineStateTracker)rig.Engine.HostServices.StateManager).Changes > 0);
    }

    [RequiresNativeFact]
    public void AnAgentWhoIsNotInTheRegionEndsTheRequestWithNoPermissions()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(PermHandler + "ll.RequestPermissions('" + UUID.Random() + "', " + TriggerAnimation + ")\n"));

        Heard(rig, item, "perm=0|0|" + UUID.Zero);
    }

    [RequiresNativeFact]
    public void TheWearerIsGrantedTheImplicitSetWithNoDialogAndAnLslScriptIsToo()
    {
        using var rig = new EngineRig(productionFunctions: true);
        ScenePresence wearer = Wear(rig);
        UUID slua = rig.Rez(Slua(PermHandler + "ll.RequestPermissions('" + wearer.UUID + "', " + WearerImplicit + ")\n"));
        UUID lsl = rig.Rez(Lsl("default { state_entry() { llRequestPermissions(\"" + wearer.UUID + "\", " + WearerImplicit + "); }\n" +
            "run_time_permissions(integer p) { llOwnerSay(\"perm=\" + (string)p + \"|\" + (string)llGetPermissions() + \"|\" + (string)llGetPermissionsKey()); } }"));

        Heard(rig, slua, "perm=" + WearerImplicit + "|" + WearerImplicit + "|" + wearer.UUID);
        Heard(rig, lsl, "perm=" + WearerImplicit + "|" + WearerImplicit + "|" + wearer.UUID);
    }

    [RequiresNativeFact]
    public void AnAvatarWhoIsHereButNeitherWearsNorSitsIsAskedAndNothingIsGranted()
    {
        using var rig = new EngineRig(productionFunctions: true);
        ScenePresence bystander = SceneHelpers.AddScenePresence(rig.Scene, UUID.Random());
        UUID item = rig.Rez(Slua(PermHandler + "ll.RequestPermissions('" + bystander.UUID + "', " + TriggerAnimation + ")\nossl.Mark('asked')\n"));

        Heard(rig, item, "asked");
        Thread.Sleep(600);   // a real wait: it proves no grant arrives without the viewer's answer
        Assert.DoesNotContain(rig.MarksOf(item), m => m.StartsWith("perm="));
        Assert.Equal(0, rig.ItemOf(item).PermsMask);
    }

    [RequiresNativeFact]
    public void AGrantedScriptStartsAndStopsAnAnimationOnTheWearer()
    {
        using var rig = new EngineRig(productionFunctions: true);
        ScenePresence wearer = Wear(rig);
        UUID animation = UUID.Random();
        rig.Object.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = animation, Name = "wave", OwnerID = Owner,
            Type = (int)OpenMetaverse.AssetType.Animation, InvType = (int)OpenMetaverse.InventoryType.Animation,
        }, false);
        UUID item = rig.Rez(Slua(
            "LLEvents:on('run_time_permissions', function(p) ll.StartAnimation('wave') ossl.Mark('started') end)\n" +
            "LLEvents:on('touch_start', function() ll.StopAnimation('wave') ossl.Mark('stopped') end)\n" +
            "ll.RequestPermissions('" + wearer.UUID + "', " + TriggerAnimation + ")\n"));

        Heard(rig, item, "started");
        Assert.True(SchedulerHarness.WaitFor(() => wearer.Animator.Animations.HasAnimation(animation)), "the animation never played: " + Diag(rig, item));

        Assert.True(rig.Engine.PostObjectEvent(rig.LocalId, new OpenSim.Region.ScriptEngine.Shared.EventParams("touch_start", new object[] { 0 }, new OpenSim.Region.ScriptEngine.Shared.DetectParams[0])));
        Heard(rig, item, "stopped");
        Assert.True(SchedulerHarness.WaitFor(() => !wearer.Animator.Animations.HasAnimation(animation)), "the animation never stopped");
    }

    [RequiresNativeFact]
    public void AResetEndsTheGrant()
    {
        using var rig = new EngineRig(productionFunctions: true);
        ScenePresence wearer = Wear(rig);
        UUID item = rig.Rez(Slua(PermHandler + "ll.RequestPermissions('" + wearer.UUID + "', " + TriggerAnimation + ")\n"));
        Heard(rig, item, "perm=" + TriggerAnimation + "|" + TriggerAnimation + "|" + wearer.UUID);
        Assert.Equal(TriggerAnimation, rig.ItemOf(item).PermsMask);

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);

        Assert.True(SchedulerHarness.WaitFor(() => rig.ItemOf(item).PermsMask == 0), "the grant outlived the reset");
        Assert.Equal(UUID.Zero, rig.ItemOf(item).PermsGranter);
    }
}
