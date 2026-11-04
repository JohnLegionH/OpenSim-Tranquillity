/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenUDP;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A touch as a viewer sends it: ObjectGrab, ObjectGrabUpdate and ObjectDeGrab packets handed to a real
/// LLClientView (ProcessInPacket), through the client's handlers, the scene and the script engine to the script.
/// TouchRoutingTests starts one hop later, at Scene.ProcessObjectGrab. Each test builds its own scene and touches
/// nothing process-wide, so the class runs in parallel.
/// </summary>
public class TouchFromClientPacketTests
{
    private readonly ITestOutputHelper _out;
    public TouchFromClientPacketTests(ITestOutputHelper o) => _out = o;

    private const string Reporter = @"
default
{
    touch_start(integer n)
    {
        llSay(0, ""S "" + (string)llDetectedKey(0) + "" "" + (string)llDetectedTouchFace(0) + "" "" + (string)llDetectedTouchST(0));
    }
    touch(integer n)
    {
        llSay(0, ""T "" + (string)llDetectedKey(0) + "" "" + (string)llDetectedTouchFace(0));
    }
    touch_end(integer n)
    {
        llSay(0, ""E "" + (string)llDetectedKey(0) + "" "" + (string)llDetectedTouchFace(0));
    }
}";

    private static int Count(SchedulerHarness h, string prefix)
        => h.Said.Count(s => s.StartsWith(prefix, StringComparison.Ordinal));

    // With the region's own scheduler thread running (threaded) the test waits on the clock for the result; otherwise
    // it drives the schedulers itself.
    private static bool Wait(SchedulerHarness h, bool threaded, Func<bool> done)
    {
        if (!threaded) return h.PumpUntil(done);
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limit)
        {
            if (done()) return true;
            System.Threading.Thread.Sleep(50);
        }
        return done();
    }

    // A window in which something must not happen again.
    private static void Settle(SchedulerHarness h, bool threaded)
    {
        if (threaded) System.Threading.Thread.Sleep(500);
        else h.PumpFor(TimeSpan.FromMilliseconds(500));
    }

    private void Drive(SchedulerHarness h, bool threaded)
    {
        h.RezScript(Reporter);
        Assert.True(Wait(h, threaded, () => (h.Prim.ScriptEvents & scriptEvents.touch_start) != 0), "touch_start never registered");

        // A real client view, built as the UDP server builds one, entered into the scene as a login does.
        const uint circuitCode = 123457;
        AgentCircuitData circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        UUID agentId = circuit.AgentID;
        h.Scene.AuthenticateHandler.AddNewCircuit(circuitCode, circuit);
        TestLLUDPServer udp = ClientStackHelpers.AddUdpServer(h.Scene);
        var udpClient = new LLUDPClient(udp, new ThrottleRates(new Nini.Config.IniConfigSource()), udp.Throttle,
            circuitCode, agentId, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 999), 0, 0);
        var response = new AuthenticateResponse
        {
            LoginInfo = new Login { First = "Touch", Last = "Tester", SecureSession = circuit.SecureSessionID }
        };
        var client = new LLClientView(h.Scene, udp, udpClient, response, agentId, circuit.SessionID, circuitCode);
        // The connection and the client are added; the move into the region is left out because it sends the
        // region handshake, and the test server has no socket. Touch does not depend on the presence being root.
        Assert.True(h.Scene.NewUserConnection(circuit, (uint)TeleportFlags.ViaLogin, null, out string reason), reason);
        h.Scene.AddNewAgent(client, PresenceType.User);
        Assert.NotNull(h.Scene.GetScenePresence(agentId));
        // The UDP server starts a client after adding it; the release packet is handled on the client's job engine.
        client.Start();

        var surface = new Vector3(0.25f, 0.5f, 0);

        var grab = new ObjectGrabPacket();
        grab.AgentData.AgentID = agentId;
        grab.ObjectData.LocalID = h.Prim.LocalId;
        grab.ObjectData.GrabOffset = Vector3.Zero;
        grab.SurfaceInfo = new[] { new ObjectGrabPacket.SurfaceInfoBlock { FaceIndex = 3, STCoord = surface, UVCoord = surface, Position = new Vector3(129f, 130f, 22f), Normal = new Vector3(0, 0, 1), Binormal = new Vector3(1, 0, 0) } };
        client.ProcessInPacket(grab);

        Assert.True(Wait(h, threaded, () => Count(h, "S ") >= 1), "touch_start never ran: " + string.Join(" | ", h.Said));

        var update = new ObjectGrabUpdatePacket();
        update.AgentData.AgentID = agentId;
        update.ObjectData.ObjectID = h.Prim.UUID;
        update.ObjectData.GrabOffsetInitial = Vector3.Zero;
        update.ObjectData.GrabPosition = h.Prim.AbsolutePosition;
        update.SurfaceInfo = new[] { new ObjectGrabUpdatePacket.SurfaceInfoBlock { FaceIndex = 3, STCoord = surface, UVCoord = surface, Position = new Vector3(129f, 130f, 22f), Normal = new Vector3(0, 0, 1), Binormal = new Vector3(1, 0, 0) } };
        client.ProcessInPacket(update);

        Assert.True(Wait(h, threaded, () => Count(h, "T ") >= 1), "touch never ran: " + string.Join(" | ", h.Said));

        var degrab = new ObjectDeGrabPacket();
        degrab.AgentData.AgentID = agentId;
        degrab.ObjectData.LocalID = h.Prim.LocalId;
        degrab.SurfaceInfo = new[] { new ObjectDeGrabPacket.SurfaceInfoBlock { FaceIndex = 3, STCoord = surface, UVCoord = surface, Position = new Vector3(129f, 130f, 22f), Normal = new Vector3(0, 0, 1), Binormal = new Vector3(1, 0, 0) } };
        client.ProcessInPacket(degrab);

        Assert.True(Wait(h, threaded, () => Count(h, "E ") >= 1), "touch_end never ran: " + string.Join(" | ", h.Said));
        Settle(h, threaded);   // a "did not run twice" window
        _out.WriteLine(string.Join(" | ", h.Said));

        Assert.Equal(1, Count(h, "S "));
        Assert.Equal(1, Count(h, "E "));
        Assert.StartsWith("S " + agentId + " 3 ", h.Said.First(s => s.StartsWith("S ", StringComparison.Ordinal)));
        Assert.All(h.Said.Where(s => s.StartsWith("T ", StringComparison.Ordinal)), s => Assert.Equal("T " + agentId + " 3", s));
        Assert.Equal("E " + agentId + " 3", h.Said.First(s => s.StartsWith("E ", StringComparison.Ordinal)));

        client.Close(false, false);
    }

    [Fact]
    public void ViewerGrabUpdateAndDeGrabPacketsRunTouchStartTouchAndTouchEndUnderPhlox()
    {
        using var h = new SchedulerHarness();
        Drive(h, false);
    }

    [Fact]
    public void ViewerGrabUpdateAndDeGrabPacketsRunTouchStartTouchAndTouchEndWithYEngineAlsoLoaded()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        Drive(h, false);
    }

    [Fact]
    public void ViewerGrabUpdateAndDeGrabPacketsRunTouchStartTouchAndTouchEndOnTheRegionsSchedulerThread()
    {
        using var h = new SchedulerHarness(keepMasterThread: true);
        Drive(h, true);
    }
}
