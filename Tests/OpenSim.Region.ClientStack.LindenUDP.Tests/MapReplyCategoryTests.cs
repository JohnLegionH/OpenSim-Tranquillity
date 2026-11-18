/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// A login queues the region's terrain on the client's Land category, which a viewer usually gives a low rate. The
/// small replies to a viewer's map requests (a region name search, map blocks, map items, the map layer) used to be
/// queued on Land as well, so the viewer saw them only after the terrain queued ahead of them. They go on the Task
/// category instead: it is shared with the other small replies, and the entity updates only fill it with what its
/// rate can send in 30 ms. These tests read the datagrams a client's queues really send.
/// </summary>
/// <remarks>
/// The test assembly runs its classes one at a time, so this class needs no serial group of its own. The token buckets
/// read the real clock, so a test waits for the packets to arrive (up to a generous limit) and never for a fixed time.
/// </remarks>
public class MapReplyCategoryTests : OpenSimTestCase
{
    private const uint CircuitCode = 777201;
    private const int TerrainPackets = 12;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private const byte LayerDataId = 11; // high frequency message id of LayerData
    private const int MapLayerReplyId = 406;
    private const int MapBlockReplyId = 409;
    private const int MapItemReplyId = 411;

    /// <summary>A UDP socket on the loopback address that stands in for the viewer.</summary>
    private sealed class Viewer : IDisposable
    {
        private readonly Socket m_socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly byte[] m_buffer = new byte[2048];
        public readonly List<byte[]> Received = new();

        public Viewer()
        {
            m_socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }

        public IPEndPoint EndPoint => (IPEndPoint)m_socket.LocalEndPoint;

        /// <summary>Takes every datagram that has arrived so far.</summary>
        public void Collect()
        {
            while (m_socket.Poll(1000, SelectMode.SelectRead))
            {
                int n = m_socket.Receive(m_buffer);
                byte[] copy = new byte[n];
                Buffer.BlockCopy(m_buffer, 0, copy, 0, n);
                Received.Add(copy);
            }
        }

        public void Dispose() => m_socket.Dispose();
    }

    private sealed class Rig : IDisposable
    {
        public Viewer Viewer = new();
        public LLClientView Client;
        public LLUDPClient Udp;
        public TestLLUDPServer Server;

        public void Dispose()
        {
            try { Client?.Close(false, false); } catch { }
            try { Server?.StopInbound(); } catch { }
            Viewer.Dispose();
        }
    }

    private static Rig Connect()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        Rig rig = new();
        Scene scene = new SceneHelpers().SetupScene("Map reply test region", UUID.Random(), 1000, 1000, 256, 256,
            new IniConfigSource());
        // flat terrain: a patch of it is coded without coefficients, so its coordinates can be read back
        TerrainData terrain = scene.Heightmap.GetTerrainData();
        for (int x = 0; x < terrain.SizeX; x++)
            for (int y = 0; y < terrain.SizeY; y++)
                terrain[x, y] = 25f;
        rig.Server = ClientStackHelpers.AddUdpServer(scene);
        rig.Server.StartInbound(0); // opens the socket the queues send on; the test pumps the queues itself
        AgentCircuitData circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        circuit.Channel = "Firestorm-Releasex64";
        scene.AuthenticateHandler.AddNewCircuit(CircuitCode, circuit);
        rig.Udp = new LLUDPClient(rig.Server, new ThrottleRates(new IniConfigSource()), rig.Server.Throttle,
            CircuitCode, circuit.AgentID, rig.Viewer.EndPoint, 0, 0);
        AuthenticateResponse response = new()
        {
            LoginInfo = new Login { First = "Test", Last = "Viewer", SecureSession = circuit.SecureSessionID }
        };
        rig.Client = new LLClientView(scene, rig.Server, rig.Udp, response, circuit.AgentID, circuit.SessionID, CircuitCode);
        return rig;
    }

    /// <summary>The viewer's throttle message: Land at the floor the region accepts (1000 bytes/s), the rest roomy.</summary>
    private static void SetLowLandRate(LLUDPClient udp)
    {
        float[] bitsPerSecond = { 160000, 8000, 160000, 160000, 160000, 160000, 160000 }; // resend, land, wind, cloud, task, texture, asset
        byte[] data = new byte[28];
        for (int i = 0; i < bitsPerSecond.Length; i++)
            Utils.FloatToBytes(bitsPerSecond[i], data, i * 4);
        udp.SetThrottles(data);
    }

    private static void QueueTerrain(LLClientView client)
    {
        for (int i = 0; i < TerrainPackets; i++)
            client.SendLayerData(new[] { i, 0 });
    }

    /// <summary>Runs the client's queues the way the region's send loop does until the wanted number of datagrams has arrived.</summary>
    private static void Pump(Rig rig, int wanted)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (rig.Viewer.Received.Count < wanted && watch.Elapsed < Generous)
        {
            rig.Udp.DequeueOutgoing();
            rig.Viewer.Collect();
        }
        Assert.Equal(wanted, rig.Viewer.Received.Count);
    }

    private static bool IsTerrain(byte[] packet) => packet[6] == LayerDataId;

    private static int LowFrequencyId(byte[] packet) =>
        packet[6] == 0xFF && packet[7] == 0xFF ? (packet[8] << 8) | packet[9] : -1;

    private static (int X, int Y) PatchOf(byte[] packet)
    {
        BitPack bits = new(packet, 10);
        bits.UnpackBits(16); // stride
        bits.UnpackBits(8);  // patch size
        bits.UnpackBits(8);  // layer
        Assert.Equal(0, bits.UnpackBits(8)); // a flat patch: quantisation byte 0
        bits.UnpackBits(32);
        bits.UnpackBits(8);
        bits.UnpackBits(8);
        int id = bits.UnpackBits(10);
        return (id >> 5, id & 0x1F);
    }

    private static void SendMapBlockReply(LLClientView client)
    {
        client.SendMapBlock(new List<MapBlockData>
        {
            new() { X = 1000, Y = 1000, Name = "Example Region", Access = 21, MapImageId = UUID.Random(), SizeX = 256, SizeY = 256 }
        }, 2);
    }

    private static void SendMapItemReplyPacket(LLClientView client)
    {
        client.SendMapItemReply(new[] { new mapItemReply(1000 * 256, 1000 * 256, UUID.Zero, "Example Item", 1, 0) }, 6, 0);
    }

    public static IEnumerable<object[]> Replies() => new[]
    {
        new object[] { "MapBlockReply", MapBlockReplyId },
        new object[] { "MapItemReply", MapItemReplyId },
    };

    private static void SendReply(string kind, LLClientView client)
    {
        if (kind == "MapBlockReply")
            SendMapBlockReply(client);
        else
            SendMapItemReplyPacket(client);
    }

    [Theory]
    [MemberData(nameof(Replies))]
    public void AMapReplyQueuedBehindTerrainLeavesBeforeTheTerrain(string kind, int messageId)
    {
        TestHelpers.InMethod();
        using Rig rig = Connect();
        SetLowLandRate(rig.Udp);
        QueueTerrain(rig.Client);
        Assert.Equal(TerrainPackets, rig.Udp.GetPacketsQueuedCount(ThrottleOutPacketType.Land));

        SendReply(kind, rig.Client);
        Pump(rig, TerrainPackets + 1);

        int at = rig.Viewer.Received.FindIndex(p => LowFrequencyId(p) == messageId);
        Assert.True(at >= 0, $"the {kind} never reached the viewer");
        // The queues are visited in turn and send one packet each, so the terrain packet at the head of the Land
        // queue may leave in the same pass; no more than that may leave first.
        Assert.True(at <= 1, $"the {kind} left as packet {at + 1} of {TerrainPackets + 1}, behind the terrain");
    }

    [Theory]
    [MemberData(nameof(Replies))]
    public void TheTerrainKeepsItsOrderWhenAMapReplyIsQueuedAfterIt(string kind, int messageId)
    {
        TestHelpers.InMethod();
        using Rig rig = Connect();
        SetLowLandRate(rig.Udp);
        QueueTerrain(rig.Client);

        SendReply(kind, rig.Client);
        Pump(rig, TerrainPackets + 1);

        List<(int X, int Y)> patches = rig.Viewer.Received.Where(IsTerrain).Select(PatchOf).ToList();
        Assert.Equal(Enumerable.Range(0, TerrainPackets).Select(i => (i, 0)).ToList(), patches);
        Assert.Single(rig.Viewer.Received, p => LowFrequencyId(p) == messageId);
    }

    /// <summary>Records the category of each packet the client's packet path hands to the UDP server.</summary>
    private sealed class CategoryRecorder : TestLLUDPServer
    {
        public readonly List<(PacketType Type, ThrottleOutPacketType Category)> Sent = new();

        public CategoryRecorder(IPAddress ip, uint port, int offset, IConfigSource config, AgentCircuitManager acm)
            : base(ip, port, offset, config, acm) { }

        public override void SendPacket(LLUDPClient udpClient, Packet packet, ThrottleOutPacketType category,
            bool allowSplitting, UnackedPacketMethod method)
        {
            Sent.Add((packet.Type, category));
            base.SendPacket(udpClient, packet, category, allowSplitting, method);
        }
    }

    [Fact]
    public void TheMapLayerReplyIsNotQueuedOnTheTerrainCategory()
    {
        TestHelpers.InMethod();
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        Scene scene = new SceneHelpers().SetupScene();
        CategoryRecorder server = new(IPAddress.Any, 0, 0, new IniConfigSource(), scene.AuthenticateHandler);
        server.AddScene(scene);
        AgentCircuitData circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        scene.AuthenticateHandler.AddNewCircuit(CircuitCode, circuit);
        LLUDPClient udp = new(server, new ThrottleRates(new IniConfigSource()), server.Throttle, CircuitCode,
            circuit.AgentID, new IPEndPoint(IPAddress.Loopback, 999), 0, 0);
        AuthenticateResponse response = new()
        {
            LoginInfo = new Login { First = "Test", Last = "Viewer", SecureSession = circuit.SecureSessionID }
        };
        LLClientView client = new(scene, server, udp, response, circuit.AgentID, circuit.SessionID, CircuitCode);
        try
        {
            client.RequestMapLayer();

            var sent = Assert.Single(server.Sent, s => s.Type == PacketType.MapLayerReply);
            Assert.Equal(ThrottleOutPacketType.Task, sent.Category);
        }
        finally { client.Close(false, false); }
    }
}
