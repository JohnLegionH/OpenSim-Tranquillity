/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

#nullable disable

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;

namespace OpenSim.Region.ScriptEngine.Shared.Tests;

/// <summary>
/// llGetGameControlMode, llGetGameControlModeButtons and llGetGameControlModeAxes, and the permission rules of
/// PERMISSION_GAME_CONTROL in llRequestPermissions (Second Life wiki: "game_control", "llGetGameControlMode",
/// "llRequestPermissions"). The three functions answer from the state of the game_control event the script is running
/// and only for the avatar that sent it; outside such an event, or for another key, they give the "no data" answers
/// the wiki documents: -1, 0 and an empty list.
/// Each test builds its own scene and mock engine, so the class runs in parallel with the others.
/// </summary>
public class GameControlApiTests
{
    private const int GameControl = 0x100000;   // PERMISSION_GAME_CONTROL, Linden Lab's value

    private sealed class Rig
    {
        public Scene Scene;
        public MockScriptEngine Engine;
        public SceneObjectGroup Group;
        public TaskInventoryItem Item;
        public LSL_Api Api;
        public ScenePresence Avatar;
        public TestClient Client;
        public List<(UUID item, UUID granter, int mask)> Reported = new();
    }

    private static Rig MakeRig()
    {
        Rig rig = new();
        rig.Engine = new MockScriptEngine();
        rig.Scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(rig.Scene, new IniConfigSource(), rig.Engine);
        rig.Avatar = SceneHelpers.AddScenePresence(rig.Scene, UUID.Random());
        rig.Client = (TestClient)rig.Avatar.ControllingClient;
        rig.Group = SceneHelpers.AddSceneObject(rig.Scene);
        rig.Item = TaskInventoryHelpers.AddScript(rig.Scene.AssetService, rig.Group.RootPart);
        rig.Api = new LSL_Api();
        rig.Api.Initialize(rig.Engine, rig.Group.RootPart, rig.Item);
        rig.Scene.EventManager.OnScriptPermissionsGranted += (part, item, granter, mask) =>
            rig.Reported.Add((item, granter, mask));
        return rig;
    }

    private static GameControlSnapshot Snapshot(int mode, uint semanticButtons, params float[] semanticAxes)
    {
        Assert.True(GameControlSnapshot.TryCreate(5u, new float[] { 0.5f, 0, 0, 0, 0, 0 }, mode, semanticButtons,
            semanticAxes, out GameControlSnapshot s));
        return s;
    }

    private static int PermsMask(Rig rig)
        => rig.Group.RootPart.Inventory.GetInventoryItem(rig.Item.ItemID).PermsMask;

    private static int SilentMask(Rig rig)
        => rig.Group.RootPart.Inventory.GetInventoryItem(rig.Item.ItemID).SilentPermsMask;

    private static void Wear(Rig rig)
    {
        rig.Group.AttachedAvatar = rig.Avatar.UUID;
        rig.Group.IsAttachment = true;
        rig.Group.AttachmentPoint = 3;
    }

    private static void Sit(Rig rig)
    {
        rig.Avatar.AbsolutePosition = rig.Group.AbsolutePosition + new Vector3(1, 1, 0);
        rig.Avatar.HandleAgentRequestSit(rig.Client, rig.Avatar.UUID, rig.Group.RootPart.UUID, Vector3.Zero);
        Assert.True(rig.Group.HasSittingAvatar(rig.Avatar.UUID));
    }

    // ── the three functions ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModeAnswersAreTakenFromTheRunningEvent()
    {
        Rig rig = MakeRig();
        rig.Item.PermsGranter = rig.Avatar.UUID;
        rig.Item.PermsMask = GameControl;
        rig.Engine.EventData[rig.Item.ItemID] = Snapshot(GameControlSnapshot.ModeFlycam, 0x0Au,
            0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f);
        string agent = rig.Avatar.UUID.ToString();

        Assert.Equal(GameControlSnapshot.ModeFlycam, (int)rig.Api.llGetGameControlMode(agent));
        Assert.Equal(0x0A, (int)rig.Api.llGetGameControlModeButtons(agent));

        LSL_List axes = rig.Api.llGetGameControlModeAxes(agent);
        Assert.Equal(7, axes.Length);
        Assert.Equal(0.1, (double)(LSL_Float)axes.Data[0], 5);
        Assert.Equal(0.7, (double)(LSL_Float)axes.Data[6], 5);
    }

    [Fact]
    public void OutsideAGameControlEventTheNoDataAnswersAreGiven()
    {
        Rig rig = MakeRig();
        rig.Item.PermsGranter = rig.Avatar.UUID;
        rig.Item.PermsMask = GameControl;
        string agent = rig.Avatar.UUID.ToString();

        Assert.Equal(-1, (int)rig.Api.llGetGameControlMode(agent));
        Assert.Equal(0, (int)rig.Api.llGetGameControlModeButtons(agent));
        Assert.Equal(0, rig.Api.llGetGameControlModeAxes(agent).Length);
    }

    [Fact]
    public void AnotherKeyGetsTheNoDataAnswersEvenInsideTheEvent()
    {
        Rig rig = MakeRig();
        rig.Item.PermsGranter = rig.Avatar.UUID;
        rig.Item.PermsMask = GameControl;
        rig.Engine.EventData[rig.Item.ItemID] = Snapshot(GameControlSnapshot.ModeAvatar, 3u, 1, 0, 0, 0, 0);
        string other = UUID.Random().ToString();

        Assert.Equal(-1, (int)rig.Api.llGetGameControlMode(other));
        Assert.Equal(0, (int)rig.Api.llGetGameControlModeButtons(other));
        Assert.Equal(0, rig.Api.llGetGameControlModeAxes(other).Length);
        Assert.Equal(-1, (int)rig.Api.llGetGameControlMode("not a key"));
    }

    [Fact]
    public void AnUnknownModeAnswersMinusOneAndNoSemanticData()
    {
        Rig rig = MakeRig();
        rig.Item.PermsGranter = rig.Avatar.UUID;
        rig.Item.PermsMask = GameControl;
        rig.Engine.EventData[rig.Item.ItemID] = Snapshot(99, 7u);
        string agent = rig.Avatar.UUID.ToString();

        Assert.Equal(-1, (int)rig.Api.llGetGameControlMode(agent));
        Assert.Equal(0, (int)rig.Api.llGetGameControlModeButtons(agent));
        Assert.Equal(0, rig.Api.llGetGameControlModeAxes(agent).Length);
    }

    // ── PERMISSION_GAME_CONTROL in llRequestPermissions ───────────────────────────────────────────────────────

    [Fact]
    public void AWornAttachmentGetsTheGrantWithoutADialogAndItIsNotedAsAutomatic()
    {
        Rig rig = MakeRig();
        Wear(rig);

        rig.Api.llRequestPermissions(rig.Avatar.UUID.ToString(), GameControl);

        Assert.Equal(GameControl, PermsMask(rig));
        Assert.Equal(GameControl, SilentMask(rig));
        Assert.Empty(rig.Client.ScriptQuestions);
        Assert.Contains((rig.Item.ItemID, rig.Avatar.UUID, GameControl), rig.Reported);
    }

    [Fact]
    public void ASeatedAvatarGetsTheGrantWithoutADialogAndItIsNotedAsAutomatic()
    {
        Rig rig = MakeRig();
        Sit(rig);

        rig.Api.llRequestPermissions(rig.Avatar.UUID.ToString(), GameControl);

        Assert.Equal(GameControl, PermsMask(rig));
        Assert.Equal(GameControl, SilentMask(rig));
        Assert.Empty(rig.Client.ScriptQuestions);
        Assert.Contains((rig.Item.ItemID, rig.Avatar.UUID, GameControl), rig.Reported);
    }

    [Fact]
    public void AScriptThatIsNeitherWornNorSeatedAsksTheAvatarAndTheGrantIsNotAutomatic()
    {
        Rig rig = MakeRig();

        rig.Api.llRequestPermissions(rig.Avatar.UUID.ToString(), GameControl);

        Assert.Equal(0, PermsMask(rig));
        Assert.Single(rig.Client.ScriptQuestions);
        Assert.Equal(GameControl, rig.Client.ScriptQuestions[0].Question);

        rig.Client.FireScriptAnswer(rig.Group.RootPart.UUID, rig.Item.ItemID, GameControl);

        Assert.Equal(GameControl, PermsMask(rig));
        Assert.Equal(0, SilentMask(rig));
        Assert.Contains((rig.Item.ItemID, rig.Avatar.UUID, GameControl), rig.Reported);
    }

    [Fact]
    public void AViewerAnswerCannotAddGameControlToAGrantThatDidNotAskForIt()
    {
        Rig rig = MakeRig();

        rig.Api.llRequestPermissions(rig.Avatar.UUID.ToString(), ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION);
        rig.Client.FireScriptAnswer(rig.Group.RootPart.UUID, rig.Item.ItemID,
            ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION | GameControl);

        Assert.Equal(ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION, PermsMask(rig));
    }

    [Fact]
    public void ReleasingPermissionsClearsTheAutomaticNoteAndTellsTheIndex()
    {
        Rig rig = MakeRig();
        Wear(rig);
        rig.Api.llRequestPermissions(rig.Avatar.UUID.ToString(), GameControl);
        rig.Reported.Clear();

        rig.Api.llRequestPermissions(rig.Avatar.UUID.ToString(), 0);

        Assert.Equal(0, PermsMask(rig));
        Assert.Equal(0, SilentMask(rig));
        Assert.Contains((rig.Item.ItemID, UUID.Zero, 0), rig.Reported);
    }

    [Fact]
    public void ARequestForOtherBitsOnlyNotesNothingAsGameControl()
    {
        Rig rig = MakeRig();
        Wear(rig);

        rig.Api.llRequestPermissions(rig.Avatar.UUID.ToString(), ScriptBaseClass.PERMISSION_TAKE_CONTROLS);

        Assert.Equal(ScriptBaseClass.PERMISSION_TAKE_CONTROLS, PermsMask(rig));
        Assert.Equal(0, SilentMask(rig) & GameControl);
    }
}
