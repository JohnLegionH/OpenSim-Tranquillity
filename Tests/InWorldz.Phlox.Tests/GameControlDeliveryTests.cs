/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InWorldz.Phlox.Compiler;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using ProtoBuf;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// game_control(key id, integer button_levels, list axes) in Phlox, with llGetGameControlMode,
/// llGetGameControlModeAxes and llGetGameControlModeButtons (SL function ids 804, 805, 806; the SL wiki: -1 when the
/// script has no game-control snapshot for the id). The answers come from the snapshot that rides with the event being
/// run. A newer state from an agent replaces that agent's queued, not yet run game_control in the same script when
/// the buttons are the same; a change of buttons is always its own event.
/// </summary>
// Parallel: every test has its own harness; the serialization tests use their own objects.
public class GameControlDeliveryTests
{
    private const string Script = @"
        default {
            game_control(key id, integer b, list axes) {
                llSay(0, ""gc b="" + (string)b + "" a0="" + (string)llList2Float(axes, 0)
                    + "" mode="" + (string)llGetGameControlMode(id)
                    + "" mb="" + (string)llGetGameControlModeButtons(id)
                    + "" n="" + (string)llGetListLength(llGetGameControlModeAxes(id))
                    + "" ax0="" + (string)llList2Float(llGetGameControlModeAxes(id), 0)
                    + "" other="" + (string)llGetGameControlMode(NULL_KEY));
            }
            touch_start(integer n) {
                llSay(0, ""touch mode="" + (string)llGetGameControlMode(llGetOwner())
                    + "" mb="" + (string)llGetGameControlModeButtons(llGetOwner())
                    + "" n="" + (string)llGetListLength(llGetGameControlModeAxes(llGetOwner())));
            }
        }";

    private static GameControlSnapshot Snapshot(uint rawButtons, float axis0, int mode = GameControlSnapshot.ModeMouselook,
                                                uint semanticButtons = 0x800, float semantic0 = 0.5f)
    {
        float[] raw = { axis0, 0, 0, 0, 0, 0 };
        float[] sem = new float[GameControlSnapshot.SemanticAxisCount(mode)];
        if (sem.Length > 0) sem[0] = semantic0;
        Assert.True(GameControlSnapshot.TryCreate(rawButtons, raw, mode, semanticButtons, sem, out var s));
        return s;
    }

    private static bool Post(SchedulerHarness h, UUID item, UUID agent, GameControlSnapshot s)
        => ((IScriptModule)h.Engine).PostScriptEvent(item, "game_control",
            new object[] { agent.ToString(), (int)s.RawButtons, s.RawAxes.Select(a => (object)a).ToArray() }, s);

    private static List<string> Lines(SchedulerHarness h) => h.Said.Where(s => s.StartsWith("gc ", StringComparison.Ordinal)).ToList();

    private static string SaidText(SchedulerHarness h) => string.Join(" | ", h.Said);

    [Fact]
    public void TheEventCarriesTheStateAndTheFunctionsAnswerFromItsSnapshot()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(Script);
        var agent = UUID.Random();

        Post(h, item, agent, Snapshot(0x5, 0.5f));

        Assert.True(h.PumpUntil(() => Lines(h).Count == 1), SaidText(h));
        Assert.Equal("gc b=5 a0=0.500000 mode=1 mb=2048 n=5 ax0=0.500000 other=-1", Lines(h)[0]);
    }

    [Fact]
    public void ASnapshotOfAnUnknownModeAnswersMinusOneNoAxesAndNoButtons()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(Script);

        Post(h, item, UUID.Random(), Snapshot(0x1, 0.25f, mode: 99, semanticButtons: 0x800));

        Assert.True(h.PumpUntil(() => Lines(h).Count == 1), SaidText(h));
        Assert.Equal("gc b=1 a0=0.250000 mode=-1 mb=0 n=0 ax0=0.000000 other=-1", Lines(h)[0]);
    }

    [Fact]
    public void OutsideAGameControlEventTheFunctionsAnswerMinusOneNothingAndZero()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(Script);
        var agent = UUID.Random();
        Post(h, item, agent, Snapshot(0x5, 0.5f));
        Assert.True(h.PumpUntil(() => Lines(h).Count == 1), SaidText(h));

        h.PostTouch(item);

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("touch ", StringComparison.Ordinal))), SaidText(h));
        Assert.Equal("touch mode=-1 mb=0 n=0", h.Said.First(s => s.StartsWith("touch ", StringComparison.Ordinal)));
    }

    [Fact]
    public void AxesOnlyUpdatesFoldIntoTheQueuedEventAndAPressAndReleaseAreEachDelivered()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(Script);
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));   // loaded and waiting: the first state starts at once
        var agent = UUID.Random();

        // All posted before the script runs once: the first starts, the rest meet a busy script.
        Post(h, item, agent, Snapshot(0x0, 0.01f));
        for (int i = 2; i <= 60; i++) Post(h, item, agent, Snapshot(0x0, i / 100f));        // axes only
        Post(h, item, agent, Snapshot(0x1, 0.61f));                                         // press
        for (int i = 62; i <= 120; i++) Post(h, item, agent, Snapshot(0x1, i / 200f));     // axes only
        Post(h, item, agent, Snapshot(0x0, 1.0f));                                          // release
        for (int i = 2; i <= 40; i++) Post(h, item, agent, Snapshot(0x0, -i / 100f));      // axes only

        Assert.True(h.PumpUntil(() => Lines(h).Any(l => l.Contains("a0=-0.400000"))), SaidText(h) + " // " + h.Diagnose(item));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        var lines = Lines(h);

        Assert.True(lines.Count <= 6, "expected a handful of events, got " + lines.Count + ": " + string.Join(" | ", lines));
        var buttons = lines.Select(l => l.Split(' ')[1]).ToList();
        int press = buttons.IndexOf("b=1");
        Assert.True(press > 0, "no press: " + string.Join(" | ", lines));
        Assert.Contains("b=0", buttons.Skip(press));                                        // and the release after it
        Assert.Contains("gc b=1 a0=0.600000", string.Join(" | ", lines));                // the press, with the newest axes sent while it was held
        Assert.Contains("a0=-0.400000", lines.Last());                                      // the newest axes ended it
    }

    [Fact]
    public void ButtonChangesAreNeverFoldedAway()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(Script);
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));   // loaded and waiting: the first state starts at once
        var agent = UUID.Random();

        for (int i = 0; i < 70; i++) Post(h, item, agent, Snapshot((uint)(i % 2), 0.1f));   // press, release ... more than the queue holds

        Assert.True(h.PumpUntil(() => Lines(h).Count == 70, TimeSpan.FromSeconds(30)), "got " + Lines(h).Count + ": " + SaidText(h));
        Assert.Equal(Enumerable.Range(0, 70).Select(i => "b=" + (i % 2)), Lines(h).Select(l => l.Split(' ')[1]));
    }

    [Fact]
    public void TwoAgentsInputsAreKeptApart()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(Script);
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));   // loaded and waiting: the first state starts at once
        var a = UUID.Random();
        var b = UUID.Random();

        Post(h, item, a, Snapshot(0x0, 0.01f));                       // starts at once
        Post(h, item, a, Snapshot(0x0, 0.02f));
        Post(h, item, b, Snapshot(0x0, 0.03f));
        Post(h, item, a, Snapshot(0x0, 0.04f));                       // replaces a's queued 0.02, not b's
        Post(h, item, b, Snapshot(0x0, 0.05f));                       // replaces b's queued 0.03

        Assert.True(h.PumpUntil(() => Lines(h).Count >= 3), SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "a0=0.010000", "a0=0.040000", "a0=0.050000" }, Lines(h).Select(l => l.Split(' ')[2]));
    }

    [Fact]
    public void TheSnapshotIsSavedWithAQueuedEventAndAnOldRowLoadsWithNone()
    {
        var state = new RuntimeState(0);
        var snapshot = Snapshot(0x5, 0.5f, semanticButtons: 0x800, semantic0: -0.25f);
        state.QueueEvent(new PostedEvent
        {
            EventType = InWorldz.Phlox.Types.SupportedEventList.Events.GAME_CONTROL,
            Args = new object[] { UUID.Zero.ToString(), 5, new object[] { 0.5f } },
            GameControl = snapshot,
        });

        var saved = SerializedRuntimeState.FromRuntimeState(state);
        byte[] bytes;
        using (var ms = new MemoryStream()) { Serializer.Serialize(ms, saved); bytes = ms.ToArray(); }
        SerializedRuntimeState loaded;
        using (var ms = new MemoryStream(bytes)) loaded = Serializer.Deserialize<SerializedRuntimeState>(ms);
        var back = loaded.ToRuntimeState().EventQueue.First();

        Assert.NotNull(back.GameControl);
        Assert.Equal(snapshot, back.GameControl);

        // A row written before the snapshot existed holds tags 1 to 4 only.
        byte[] oldRow;
        using (var ms = new MemoryStream())
        {
            Serializer.Serialize(ms, new LegacyPostedEvent { EventType = back.EventType, TransitionToState = -1 });
            oldRow = ms.ToArray();
        }
        SerializedPostedEvent old;
        using (var ms = new MemoryStream(oldRow)) old = Serializer.Deserialize<SerializedPostedEvent>(ms);
        Assert.Equal(InWorldz.Phlox.Types.SupportedEventList.Events.GAME_CONTROL, old.EventType);
        Assert.Null(old.GameControl);
    }

    // ── the whole way: the viewer's message, the router, the engine ─────────────

    private static int s_sequence;

    private static void FromViewer(Scene scene, UUID agent, short axis0, uint buttons)
    {
        var p = new GameControlDataPacket();
        p.AgentData.AgentID = agent;
        p.AgentData.SessionID = UUID.Random();
        p.AgentData.Packet = (byte)(++s_sequence % 120);
        p.AgentData.ActionMode = 1;
        p.Axes = new[] { new GameControlDataPacket.AxesBlock { Value = axis0 } };
        p.Buttons = new[] { new GameControlDataPacket.ButtonsBlock { Flags = buttons } };
        p.ModeAxes = new[] { new GameControlDataPacket.ModeAxesBlock { Value = 16384 } };
        p.ModeButtons = new[] { new GameControlDataPacket.ModeButtonsBlock { Flags = 0x800u } };
        scene.HandleGameControlData(agent, p);
    }

    private static string Requester(UUID agent) =>
        "default { state_entry() { llRequestPermissions(\"" + agent + "\", PERMISSION_GAME_CONTROL); } "
        + "run_time_permissions(integer p) { llSay(0, \"rtp=\" + (string)p); } "
        + "game_control(key id, integer b, list axes) { llSay(0, \"gc b=\" + (string)b + \" a0=\" + (string)llList2Float(axes, 0)"
        + " + \" mode=\" + (string)llGetGameControlMode(id) + \" mb=\" + (string)llGetGameControlModeButtons(id)"
        + " + \" n=\" + (string)llGetListLength(llGetGameControlModeAxes(id)) + \" ax0=\" + (string)llList2Float(llGetGameControlModeAxes(id), 0)"
        + " + \" other=\" + (string)llGetGameControlMode(NULL_KEY)); } }";

    [Fact]
    public void AWornScriptGetsItsWearersControllerInputThroughTheRegion()
    {
        using var h = new SchedulerHarness();
        h.Scene.GameControlEnabled = true;
        var wearerId = h.Prim.OwnerID;
        SceneHelpers.AddScenePresence(h.Scene, wearerId);
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = wearerId; sog.IsAttachment = true; sog.AttachmentPoint = 3;
        h.Scene.GetScenePresence(wearerId).AddAttachment(sog);
        h.RezScript(Requester(wearerId));
        Assert.True(h.PumpUntil(() => h.Said.Contains("rtp=1048576")), SaidText(h));

        FromViewer(h.Scene, wearerId, 32767, 0x5);

        Assert.True(h.PumpUntil(() => Lines(h).Count == 1), SaidText(h));
        Assert.Equal("gc b=5 a0=1.000000 mode=1 mb=2048 n=5 ax0=0.500015 other=-1", Lines(h)[0]);
    }

    [Fact]
    public void AScriptAnotherAvatarGrantedGetsThatAvatarsInputAndNobodyElses()
    {
        using var h = new SchedulerHarness();
        h.Scene.GameControlEnabled = true;
        var granter = UUID.Random();
        var other = UUID.Random();
        var client = (TestClient)SceneHelpers.AddScenePresence(h.Scene, granter).ControllingClient;
        SceneHelpers.AddScenePresence(h.Scene, other);
        var item = h.RezScript(Requester(granter));
        Assert.True(h.PumpUntil(() => client.ScriptQuestions.Count >= 1), SaidText(h));
        client.FireScriptAnswer(h.Prim.UUID, item, 0x100000);
        Assert.True(h.PumpUntil(() => h.Said.Contains("rtp=1048576")), SaidText(h));

        FromViewer(h.Scene, other, 100, 0x1);
        FromViewer(h.Scene, granter, 200, 0x2);

        Assert.True(h.PumpUntil(() => Lines(h).Count >= 1), SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Single(Lines(h));
        Assert.StartsWith("gc b=2 ", Lines(h)[0]);
    }

    /// <summary>SerializedPostedEvent as it was before the game-control snapshot: tags 1 to 4.</summary>
    [ProtoContract]
    private class LegacyPostedEvent
    {
        [ProtoMember(1, IsRequired = true)] public InWorldz.Phlox.Types.SupportedEventList.Events EventType;
        [ProtoMember(4)] public int TransitionToState;
    }

    private static readonly (string Name, int Value)[] Constants =
    {
        ("PERMISSION_GAME_CONTROL", 0x100000),
        ("GAME_CONTROL_MODE_AVATAR", 0), ("GAME_CONTROL_MODE_MOUSELOOK", 1), ("GAME_CONTROL_MODE_FLYCAM", 2),
        ("GAME_CONTROL_MODE_CAPTIVE", 3), ("GAME_CONTROL_MODE_CURSOR", 4),
        ("GAME_CONTROL_BUTTON_SOUTH", 0x1), ("GAME_CONTROL_BUTTON_EAST", 0x2), ("GAME_CONTROL_BUTTON_WEST", 0x4),
        ("GAME_CONTROL_BUTTON_NORTH", 0x8), ("GAME_CONTROL_BUTTON_SELECT", 0x10), ("GAME_CONTROL_BUTTON_HOME", 0x20),
        ("GAME_CONTROL_BUTTON_JUMP", 0x1), ("GAME_CONTROL_BUTTON_CROUCH", 0x2), ("GAME_CONTROL_BUTTON_SIT", 0x4),
        ("GAME_CONTROL_BUTTON_TOGGLE_CURSOR", 0x10), ("GAME_CONTROL_BUTTON_TOGGLE_SPEAK", 0x20),
        ("GAME_CONTROL_BUTTON_TOGGLE_MOUSELOOK", 0x40), ("GAME_CONTROL_BUTTON_RESET", 0x80),
        ("GAME_CONTROL_BUTTON_TOGGLE_FLYCAM", 0x100), ("GAME_CONTROL_BUTTON_CURSOR_BUTTON_LEFT", 0x200),
        ("GAME_CONTROL_BUTTON_CURSOR_BUTTON_RIGHT", 0x400), ("GAME_CONTROL_BUTTON_ADVANCE_FORWARD", 0x800),
        ("GAME_CONTROL_BUTTON_ADVANCE_BACK", 0x1000), ("GAME_CONTROL_BUTTON_STRAFE_LEFT", 0x2000),
        ("GAME_CONTROL_BUTTON_STRAFE_RIGHT", 0x4000), ("GAME_CONTROL_BUTTON_TOUCHPAD", 0x100000),
        ("GAME_CONTROL_AXIS_LEFT_TRIGGER", 4), ("GAME_CONTROL_AXIS_RIGHT_TRIGGER", 5),
        ("GAME_CONTROL_AXIS_STRAFE", 0), ("GAME_CONTROL_AXIS_ADVANCE", 1), ("GAME_CONTROL_AXIS_TURN", 2),
        ("GAME_CONTROL_AXIS_LOOK", 3), ("GAME_CONTROL_AXIS_RISE", 4), ("GAME_CONTROL_AXIS_TRUCK", 0),
        ("GAME_CONTROL_AXIS_DOLLY", 1), ("GAME_CONTROL_AXIS_PAN", 2), ("GAME_CONTROL_AXIS_TILT", 3),
        ("GAME_CONTROL_AXIS_BOOM", 4), ("GAME_CONTROL_AXIS_ROLL", 5), ("GAME_CONTROL_AXIS_ZOOM", 6),
        ("GAME_CONTROL_BUTTON_DOLLY_FORWARD", 0x800), ("GAME_CONTROL_BUTTON_ZOOM_OUT", 0x1),
        ("GAME_CONTROL_BUTTON_ROLL_CW", 0x400), ("GAME_CONTROL_BUTTON_TRUCK_RIGHT", 0x4000),
        // the names Phlox already had stay
        ("GAME_CONTROL_BUTTON_A", 0x1), ("GAME_CONTROL_AXIS_TRIGGERLEFT", 4),
    };

    [Fact]
    public void TheGameControlConstantsHaveTheValuesOfTheLslDefinitions()
    {
        var wrong = new List<string>();
        foreach (var (name, value) in Constants)
        {
            if (!DefaultConstants.Constants.TryGetValue(name, out var c)) { wrong.Add(name + " missing"); continue; }
            if (!SlConstTests.TryParseInt(c.ConstValue, out var v) || v != value) wrong.Add(name + " = " + c.ConstValue + ", wanted " + value);
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }
}
