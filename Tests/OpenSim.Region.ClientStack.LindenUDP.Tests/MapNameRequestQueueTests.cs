/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// A viewer asks for a region by name while the region is still busy with that viewer's login. The name search must
/// not wait behind the other requests of the same client: they run one after another on the client's own queue, and
/// a login puts requests on it that take seconds.
/// </summary>
public class MapNameRequestQueueTests : OpenSimTestCase
{
    private const uint CircuitCode = 654321;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    public MapNameRequestQueueTests()
    {
        // These tests need real worker threads: the client's queue runs its jobs through the thread pool.
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
    }

    private static LLClientView Connect(out Scene scene)
    {
        scene = new SceneHelpers().SetupScene();
        TestLLUDPServer udpServer = ClientStackHelpers.AddUdpServer(scene);
        AgentCircuitData circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        scene.AuthenticateHandler.AddNewCircuit(CircuitCode, circuit);
        LLUDPClient udpClient = new(udpServer, new ThrottleRates(new IniConfigSource()), udpServer.Throttle,
            CircuitCode, circuit.AgentID, new IPEndPoint(IPAddress.Loopback, 999), 0, 0);
        AuthenticateResponse response = new()
        {
            LoginInfo = new Login { First = "Test", Last = "Viewer", SecureSession = circuit.SecureSessionID }
        };
        LLClientView client = new(scene, udpServer, udpClient, response, circuit.AgentID, circuit.SessionID, CircuitCode);
        client.m_asyncPacketProcess.Start();
        return client;
    }

    private static MapNameRequestPacket NameRequest(string name)
    {
        return new MapNameRequestPacket
        {
            AgentData = new MapNameRequestPacket.AgentDataBlock { AgentID = UUID.Random(), SessionID = UUID.Random(), Flags = 0 },
            NameData = new MapNameRequestPacket.NameDataBlock { Name = Util.UTF8.GetBytes(name + "\0") }
        };
    }

    /// <summary>Puts a job on the client's queue that keeps the queue busy until the gate opens, as a login job does.</summary>
    private static ManualResetEventSlim BusyQueue(LLClientView client)
    {
        ManualResetEventSlim started = new(false);
        ManualResetEventSlim gate = new(false);
        client.m_asyncPacketProcess.QueueJob("CompleteAgentMovement", () => { started.Set(); gate.Wait(TimeSpan.FromSeconds(60)); });
        Assert.True(started.Wait(Generous));
        return gate;
    }

    [Fact]
    public void ANameSearchIsAnsweredWhileTheClientsQueueIsBusyWithALoginJob()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        string asked = null;
        using ManualResetEventSlim handled = new(false);
        client.OnMapNameRequest += (_, name, _) => { asked = name; handled.Set(); };
        ManualResetEventSlim gate = BusyQueue(client);
        try
        {
            client.ProcessInPacket(NameRequest("Example Region"));

            Assert.True(handled.Wait(Generous), "the name search waited behind the busy queue");
            Assert.Equal("Example Region", asked);
        }
        finally
        {
            gate.Set();
            client.Close(false, false);
        }
    }

    [Fact]
    public void AnEmptyNameMeansTheClientsOwnRegion()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene scene);
        string asked = null;
        using ManualResetEventSlim handled = new(false);
        client.OnMapNameRequest += (_, name, _) => { asked = name; handled.Set(); };
        try
        {
            // A request that carries no name bytes at all asks for the client's own region.
            MapNameRequestPacket request = NameRequest("");
            request.NameData.Name = Array.Empty<byte>();
            client.ProcessInPacket(request);

            Assert.True(handled.Wait(Generous));
            Assert.Equal(scene.RegionInfo.RegionName, asked);
        }
        finally
        {
            client.Close(false, false);
        }
    }

    [Fact]
    public void OtherRequestsOfTheClientStillWaitTheirTurnOnTheQueue()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        using ManualResetEventSlim handled = new(false);
        client.OnRequestMapBlocks += (_, _, _, _, _, _) => handled.Set();
        ManualResetEventSlim gate = BusyQueue(client);
        try
        {
            client.ProcessInPacket(new MapBlockRequestPacket
            {
                AgentData = new MapBlockRequestPacket.AgentDataBlock { AgentID = UUID.Random(), SessionID = UUID.Random() },
                PositionData = new MapBlockRequestPacket.PositionDataBlock { MinX = 1, MaxX = 2, MinY = 1, MaxY = 2 }
            });

            // Still waiting behind the busy job: a window in which something must not happen.
            Assert.False(handled.Wait(TimeSpan.FromMilliseconds(500)));
            gate.Set();
            Assert.True(handled.Wait(Generous), "the request never ran once the queue was free");
        }
        finally
        {
            gate.Set();
            client.Close(false, false);
        }
    }
}
