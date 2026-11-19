/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// A GameControlData message that reaches a viewer's circuit is handed to the scene, which raises the scene event
/// for a root agent when game control is on for the region. These tests send the message through the circuit's
/// own packet dispatch, so they fail if the message has no handler registered.
/// </summary>
public class GameControlDataHandlerTests : OpenSimTestCase
{
    private static readonly MethodInfo ProcessPacketMethod =
        typeof(LLClientView).GetMethod("ProcessPacketMethod", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public GameControlDataHandlerTests()
    {
        // everything on this thread: the circuit is made when the packet is handled
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private static bool Dispatch(LLClientView client, Packet packet) =>
        (bool)ProcessPacketMethod.Invoke(client, new object[] { packet })!;

    private static GameControlDataPacket Message(UUID agentId, byte sequence, short axis0)
    {
        var p = new GameControlDataPacket();
        p.AgentData.AgentID = agentId;
        p.AgentData.SessionID = UUID.Random();
        p.AgentData.Packet = sequence;
        p.AgentData.ActionMode = 0;
        p.Axes = new[] { new GameControlDataPacket.AxesBlock { Value = axis0 } };
        p.Buttons = Array.Empty<GameControlDataPacket.ButtonsBlock>();
        p.ModeAxes = Array.Empty<GameControlDataPacket.ModeAxesBlock>();
        p.ModeButtons = Array.Empty<GameControlDataPacket.ModeButtonsBlock>();
        return p;
    }

    private static (Scene scene, LLClientView client, ScenePresence presence, List<(UUID, GameControlSnapshot)> events) Setup()
    {
        Scene scene = new SceneHelpers().SetupScene();
        TestLLUDPServer udpServer = ClientStackHelpers.AddUdpServer(scene);
        ScenePresence sp = ClientStackHelpers.AddChildClient(
            scene, udpServer, TestHelpers.ParseTail(0x1), TestHelpers.ParseTail(0x2), 123456);
        sp.IsChildAgent = false;        // the helper makes a child agent; the scene accepts input from root agents only
        LLClientView client = (LLClientView)sp.ControllingClient;
        client.IsActive = true;

        var events = new List<(UUID, GameControlSnapshot)>();
        scene.EventManager.OnGameControlData += (id, snapshot) => events.Add((id, snapshot));
        return (scene, client, sp, events);
    }

    [Fact]
    public void TheMessageHasAHandlerAndReachesTheSceneEvent()
    {
        TestHelpers.InMethod();
        var (scene, client, sp, events) = Setup();
        scene.GameControlEnabled = true;

        bool handled = Dispatch(client, Message(sp.UUID, 1, 32767));

        Assert.True(handled, "no handler is registered for GameControlData");
        var raised = Assert.Single(events);
        Assert.Equal(sp.UUID, raised.Item1);
        Assert.Equal(1f, raised.Item2.RawAxes[0]);
    }

    [Fact]
    public void WithGameControlOffTheMessageIsHandledAndNothingIsRaised()
    {
        TestHelpers.InMethod();
        var (scene, client, sp, events) = Setup();

        scene.GameControlEnabled = true;
        Assert.True(Dispatch(client, Message(sp.UUID, 1, 100)), "no handler is registered for GameControlData");
        Assert.Single(events);                              // control: it passes while on

        scene.GameControlEnabled = false;
        Assert.True(Dispatch(client, Message(sp.UUID, 2, 200)));

        Assert.Single(events);
    }
}
