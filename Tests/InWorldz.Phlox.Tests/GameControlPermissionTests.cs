/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using ProtoBuf;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PERMISSION_GAME_CONTROL (0x100000) is granted without a dialog to a script in an attachment its wearer asks for it
/// from, and to a script in an object its sitter asks from (the SL wiki, "game_control" and "llGetGameControlMode":
/// seats and attached objects automatically accept it). Any other script asks through the normal dialog, and the
/// answer from the viewer is cut to what was asked. The grant is kept and given back like the other bits, and ends
/// when the avatar stands up from the object.
/// </summary>
// Parallel: every test has its own harness, avatars and items; nothing process-wide is changed.
public class GameControlPermissionTests
{
    private const int GameControl = 0x100000, TakeControls = 0x4, Attach = 0x20, Debit = 0x2;
    private const string Phlox = "InWorldz.Phlox";
    private const int RegionStart = 0, NewRez = 1;

    /// <summary>A script that does nothing until it is given a grant (the restart and crossing tests).</summary>
    private const string Idle = "default { state_entry() { llSay(0, \"entry\"); } }";

    private static string Asker(UUID agent, int perm) =>
        "default { state_entry() { llRequestPermissions(\"" + agent + "\", " + perm + "); } " +
        "run_time_permissions(integer p) { llSay(0, \"rtp=\" + (string)p); " +
        "llSay(0, \"perms=\" + (string)llGetPermissions() + \" key=\" + (string)llGetPermissionsKey()); } }";

    private static TestClient Present(SchedulerHarness h, UUID id)
        => (TestClient)SceneHelpers.AddScenePresence(h.Scene, id).ControllingClient;

    private static void Wear(SchedulerHarness h, ScenePresence sp)
    {
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = 3;
        sp.AddAttachment(sog);
    }

    private static void SitOnPrim(SceneObjectPart prim, ScenePresence sp)
    {
        var add = typeof(SceneObjectPart).GetMethod("AddSittingAvatar", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        Assert.True((bool)add!.Invoke(prim, new object[] { sp })!);
    }

    private static string SaidText(SchedulerHarness h) => string.Join(" | ", h.Said);

    [Fact]
    public void AnAttachmentAskingItsWearerForGameControlIsGrantedWithoutADialog()
    {
        using var h = new SchedulerHarness();
        var wearer = Present(h, h.Prim.OwnerID);
        Wear(h, h.Scene.GetScenePresence(h.Prim.OwnerID));
        h.RezScript(Asker(h.Prim.OwnerID, GameControl));
        Assert.True(h.PumpUntil(() => h.Said.Contains("perms=" + GameControl + " key=" + h.Prim.OwnerID)), SaidText(h));
        Assert.Contains("rtp=" + GameControl, h.Said);
        Assert.Empty(wearer.ScriptQuestions);
    }

    [Fact]
    public void ASitterAskingForGameControlFromAnObjectItSitsOnIsGrantedWithoutADialog()
    {
        using var h = new SchedulerHarness();
        var sitterId = UUID.Random();
        var sitter = Present(h, sitterId);
        SitOnPrim(h.Prim, h.Scene.GetScenePresence(sitterId));
        h.RezScript(Asker(sitterId, GameControl | TakeControls));
        Assert.True(h.PumpUntil(() => h.Said.Contains("perms=" + (GameControl | TakeControls) + " key=" + sitterId)), SaidText(h));
        Assert.Contains("rtp=" + (GameControl | TakeControls), h.Said);
        Assert.Empty(sitter.ScriptQuestions);
    }

    [Fact]
    public void AnyoneElseIsAskedThroughTheDialogAndTheAnswerIsCutToTheRequest()
    {
        using var h = new SchedulerHarness();
        var other = UUID.Random();
        var client = Present(h, other);
        var item = h.RezScript(Asker(other, GameControl));
        Assert.True(h.PumpUntil(() => client.ScriptQuestions.Count >= 1), SaidText(h));
        Assert.Equal(GameControl, client.ScriptQuestions[0].Question);
        Assert.DoesNotContain(h.Said, s => s.StartsWith("rtp=", StringComparison.Ordinal));   // nothing before the answer

        client.FireScriptAnswer(h.Prim.UUID, item, GameControl | Attach | Debit);

        Assert.True(h.PumpUntil(() => h.Said.Contains("perms=" + GameControl + " key=" + other)), SaidText(h));
        Assert.Contains("rtp=" + GameControl, h.Said);
        Assert.Equal(GameControl, h.Prim.Inventory.GetInventoryItem(item).PermsMask);
    }

    [Fact]
    public void AViewerThatAnswersNoLeavesNothing()
    {
        using var h = new SchedulerHarness();
        var other = UUID.Random();
        var client = Present(h, other);
        var item = h.RezScript(Asker(other, GameControl));
        Assert.True(h.PumpUntil(() => client.ScriptQuestions.Count >= 1), SaidText(h));
        client.FireScriptAnswer(h.Prim.UUID, item, 0);
        Assert.True(h.PumpUntil(() => h.Said.Contains("rtp=0")), SaidText(h));
        Assert.Equal(0, h.Prim.Inventory.GetInventoryItem(item).PermsMask);
    }

    [Fact]
    public void ASitterAskingForGameControlAndABitASitterIsNotGivenSilentlyIsAskedThroughTheDialog()
    {
        using var h = new SchedulerHarness();
        var sitterId = UUID.Random();
        var sitter = Present(h, sitterId);
        SitOnPrim(h.Prim, h.Scene.GetScenePresence(sitterId));
        h.RezScript(Asker(sitterId, GameControl | Attach));   // a sitter's silent grants do not include ATTACH
        Assert.True(h.PumpUntil(() => sitter.ScriptQuestions.Count >= 1), SaidText(h));
        Assert.Equal(GameControl | Attach, sitter.ScriptQuestions[0].Question);
        Assert.DoesNotContain(h.Said, s => s.StartsWith("rtp=", StringComparison.Ordinal));
    }

    [Fact]
    public void StandingUpFromTheObjectEndsTheGrant()
    {
        using var h = new SchedulerHarness();
        var sitterId = UUID.Random();
        Present(h, sitterId);
        var sp = h.Scene.GetScenePresence(sitterId);
        sp.AbsolutePosition = h.Prim.ParentGroup.AbsolutePosition + new Vector3(1, 1, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, h.Prim.ParentGroup.RootPart.UUID, Vector3.Zero);
        Assert.Equal(h.Prim.ParentGroup, sp.ParentPart?.ParentGroup);
        var item = h.RezScript(Asker(sitterId, GameControl));
        Assert.True(h.PumpUntil(() => h.Said.Contains("rtp=" + GameControl)), SaidText(h));
        Assert.Equal(GameControl, h.Prim.Inventory.GetInventoryItem(item).PermsMask);

        sp.StandUp();

        Assert.Equal(0, h.Prim.Inventory.GetInventoryItem(item).PermsMask);
    }

    [Fact]
    public void ARestartGivesBackAGameControlGrantFromTheOwner()
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            foreach (SceneObjectPart p in h1.Prim.ParentGroup.Parts) p.OwnerID = owner;
            var inv = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, item, asset, "seat", Idle);
            Assert.True(h1.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
            h1.Prim.ParentGroup.ResumeScripts();
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SaidText(h1));
            inv.PermsGranter = owner;
            inv.PermsMask = GameControl;
            h1.SaveState(item);
            SavedStateRig.WaitForWrites(h1);
        }

        using var h2 = new SchedulerHarness();
        foreach (SceneObjectPart p in h2.Prim.ParentGroup.Parts) p.OwnerID = owner;
        var inv2 = TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, item, asset, "seat", Idle);
        inv2.PermsGranter = UUID.Random();   // whatever the region database held; the core zeroes it at the start
        inv2.PermsMask = Debit;
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(item) != null));
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h2.Said);   // restored, not started fresh
        Assert.Equal(GameControl, h2.Prim.Inventory.GetInventoryItem(item).PermsMask);
        Assert.Equal(owner, h2.Prim.Inventory.GetInventoryItem(item).PermsGranter);
    }

    [Fact]
    public void AGameControlGrantCarriedInAnObjectComesBackForASitter()
    {
        using var h = new SchedulerHarness();
        var sitterId = UUID.Random();
        Present(h, sitterId);
        var asset = UUID.Random();
        var source = UUID.Random();
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, source, asset, "source", Idle);
        Assert.True(h.Prim.Inventory.CreateScriptInstance(source, 0, false, Phlox, NewRez));
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        var interp = (Interpreter)h.InterpreterFor(source);
        SerializedRuntimeState st = StateManager.Decode(StateManager.CaptureBlob(interp));
        st.PermsGranter = sitterId.ToString();
        st.GrantedPermsMask = GameControl;
        st.PermsOwner = h.Prim.OwnerID.ToString();
        byte[] blob;
        using (var ms = new MemoryStream()) { Serializer.Serialize(ms, st); blob = ms.ToArray(); }
        SavedStateRig.PostRemove(h, h.Prim, source);
        h.Prim.Inventory.RemoveInventoryItem(source);
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));

        var copy = SceneHelpers.AddSceneObject(h.Scene, "Example Copy", h.Prim.OwnerID);
        SitOnPrim(copy.RootPart, h.Scene.GetScenePresence(sitterId));
        var item = UUID.Random();
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, copy.RootPart, item, asset, "copy", Idle);
        SavedStateRig.States(h).Carry(item, asset, blob);
        h.ClearSaid(item);
        Assert.Equal(1, copy.CreateScriptInstances(0, true, Phlox, NewRez));
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null), "the rezzed script did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h.Said);   // restored from the carried state, not started fresh

        Assert.Equal(GameControl, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.Equal(sitterId, copy.RootPart.Inventory.GetInventoryItem(item).PermsGranter);
        // It came back as the seat's automatic grant, so standing up from the object ends it.
        Assert.Equal(GameControl, copy.RootPart.Inventory.GetInventoryItem(item).SilentPermsMask);
    }

    // ── the kind of a grant: automatic (a seat's or an attachment's) or answered through the dialog ──────────────

    [Fact]
    public void AGrantGivenWithoutADialogIsNotedAsAutomaticOnTheItem()
    {
        using var h = new SchedulerHarness();
        Present(h, h.Prim.OwnerID);
        Wear(h, h.Scene.GetScenePresence(h.Prim.OwnerID));
        var item = h.RezScript(Asker(h.Prim.OwnerID, GameControl));
        Assert.True(h.PumpUntil(() => h.Said.Contains("rtp=" + GameControl)), SaidText(h));

        Assert.Equal(GameControl, h.Prim.Inventory.GetInventoryItem(item).SilentPermsMask);
    }

    [Fact]
    public void AGrantTheAvatarAnsweredThroughTheDialogIsNotedAsAnswered()
    {
        using var h = new SchedulerHarness();
        var other = UUID.Random();
        var client = Present(h, other);
        var item = h.RezScript(Asker(other, GameControl));
        Assert.True(h.PumpUntil(() => client.ScriptQuestions.Count >= 1), SaidText(h));
        h.Prim.Inventory.GetInventoryItem(item).SilentPermsMask = GameControl;   // a stale note from an earlier automatic grant

        client.FireScriptAnswer(h.Prim.UUID, item, GameControl);

        Assert.True(h.PumpUntil(() => h.Said.Contains("rtp=" + GameControl)), SaidText(h));
        Assert.Equal(0, h.Prim.Inventory.GetInventoryItem(item).SilentPermsMask);
    }

    private const string Listener =
        "default { state_entry() { llSay(0, \"entry\"); } game_control(key id, integer b, list a) { llSay(0, \"gc b=\" + (string)b); } }";

    /// <summary>
    /// A region stop with a script that holds a game-control grant of the given kind, then a start: the grant the new
    /// region has, and whether the script hears the owner's controller.
    /// </summary>
    private static (int Mask, UUID Granter, bool Heard) AfterARestart(int silent, bool expectHeard)
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            foreach (SceneObjectPart p in h1.Prim.ParentGroup.Parts) p.OwnerID = owner;
            var inv = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, item, asset, "seat", Listener);
            Assert.True(h1.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
            h1.Prim.ParentGroup.ResumeScripts();
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SaidText(h1));
            inv.PermsGranter = owner;
            inv.PermsMask = GameControl;
            inv.SilentPermsMask = silent;
            h1.SaveState(item);
            SavedStateRig.WaitForWrites(h1);
        }

        using var h2 = new SchedulerHarness();
        foreach (SceneObjectPart p in h2.Prim.ParentGroup.Parts) p.OwnerID = owner;
        h2.Scene.GameControlEnabled = true;
        SceneHelpers.SetupSceneModules(h2.Scene, new Nini.Config.IniConfigSource(), new OpenSim.Region.CoreModules.Avatar.GameControl.GameControlRouterModule());
        Present(h2, owner);
        var inv2 = TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, item, asset, "seat", Listener);
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(item) != null));
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h2.Said);   // restored, not started fresh

        var packet = new OpenMetaverse.Packets.GameControlDataPacket();
        packet.AgentData.AgentID = owner;
        packet.AgentData.SessionID = UUID.Random();
        packet.AgentData.Packet = 1;
        packet.Axes = new[] { new OpenMetaverse.Packets.GameControlDataPacket.AxesBlock { Value = 100 } };
        packet.Buttons = new[] { new OpenMetaverse.Packets.GameControlDataPacket.ButtonsBlock { Flags = 0x5u } };
        packet.ModeAxes = Array.Empty<OpenMetaverse.Packets.GameControlDataPacket.ModeAxesBlock>();
        packet.ModeButtons = Array.Empty<OpenMetaverse.Packets.GameControlDataPacket.ModeButtonsBlock>();
        h2.Scene.HandleGameControlData(owner, packet);
        // A state that should arrive is waited for; one that should not is given a real quiet window to arrive in.
        bool heard;
        if (expectHeard)
            heard = h2.PumpUntil(() => h2.Said.Contains("gc b=5"));
        else
        {
            h2.PumpUntilIdle(TimeSpan.FromSeconds(3));
            heard = h2.Said.Contains("gc b=5");
        }

        var now = h2.Prim.Inventory.GetInventoryItem(item);
        return (now.PermsMask, now.PermsGranter, heard);
    }

    [Fact]
    public void ARestartDropsAGameControlGrantThatWasGivenAutomatically()
    {
        var (mask, granter, heard) = AfterARestart(silent: GameControl, expectHeard: false);
        Assert.Equal(0, mask);
        Assert.Equal(UUID.Zero, granter);
        Assert.False(heard);
    }

    [Fact]
    public void ARestartKeepsAGameControlGrantTheAvatarAnsweredThroughTheDialogAndTheScriptHearsTheController()
    {
        var (mask, granter, heard) = AfterARestart(silent: 0, expectHeard: true);
        Assert.Equal(GameControl, mask);
        Assert.NotEqual(UUID.Zero, granter);
        Assert.True(heard);
    }
}
