/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Net;
using System.Security.Cryptography;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// A viewer that only takes 256 m regions knows one land layer, the standard one ('L', 5 bit patch coordinates, at
/// most 16 patches along each edge). Sent to it, a larger region's terrain is the south-west 256 m in that layer:
/// patches 0 to 15 on each axis. Every other viewer gets the layer it got before.
/// </summary>
public class LegacyTerrainTests : OpenSimTestCase
{
    private const uint CircuitCode = 777001;

    // Captures the land layer packets instead of queueing them on a socket.
    private sealed class CapturingClient : LLClientView
    {
        public readonly List<byte[]> Packets = new();

        public CapturingClient(Scene scene, LLUDPServer server, LLUDPClient udp, AuthenticateResponse response,
            UUID agent, UUID session, uint circuit) : base(scene, server, udp, response, agent, session, circuit) { }

        protected override void SendLandLayerBuffer(UDPPacketBuffer buf)
        {
            byte[] copy = new byte[buf.DataLength];
            Buffer.BlockCopy(buf.Data, 0, copy, 0, buf.DataLength);
            Packets.Add(copy);
        }
    }

    // One flat patch is coded as: quantisation byte 0, 32 bit offset, range 1 and 0, the patch coordinates, and a
    // 2 bit end-of-block, so a packet of flat patches can be read back without decoding any coefficients.
    private sealed record Decoded(byte Layer, List<(int X, int Y)> Patches);

    private static Decoded Decode(byte[] packet, bool wide)
    {
        // read the layer first: the patch coordinates are coded differently in the two layers
        Assert.Equal(wide ? LandExtendedLayer : LandLayer, packet[7]);
        return DecodePatches(packet, wide);
    }

    private static Decoded DecodePatches(byte[] packet, bool wide)
    {
        BitPack bits = new(packet, 10);
        bits.UnpackBits(16); // stride
        bits.UnpackBits(8);  // patch size
        byte layer = (byte)bits.UnpackBits(8);
        List<(int, int)> patches = new();
        while (true)
        {
            int quant = bits.UnpackBits(8);
            if (quant == 97) // end of patches
                break;
            Assert.Equal(0, quant); // a flat patch
            bits.UnpackBits(32);
            bits.UnpackBits(8);
            bits.UnpackBits(8);
            if (wide)
            {
                int id = bits.UnpackBits(32);
                patches.Add((id >> 16, id & 0xFFFF));
            }
            else
            {
                int id = bits.UnpackBits(10);
                patches.Add((id >> 5, id & 0x1F));
            }
            bits.UnpackBits(2);
        }
        return new Decoded(layer, patches);
    }

    private static void Flatten(Scene scene)
    {
        TerrainData terrain = scene.Heightmap.GetTerrainData();
        for (int x = 0; x < terrain.SizeX; x++)
            for (int y = 0; y < terrain.SizeY; y++)
                terrain[x, y] = 25f;
    }

    private static CapturingClient Connect(uint regionSize, string channel, bool flatten = false)
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        Scene scene = new SceneHelpers().SetupScene("Terrain test region", UUID.Random(), 1000, 1000, regionSize,
            regionSize, new IniConfigSource());
        if (flatten)
            Flatten(scene);
        TestLLUDPServer udpServer = ClientStackHelpers.AddUdpServer(scene);
        AgentCircuitData circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        circuit.Channel = channel;
        scene.AuthenticateHandler.AddNewCircuit(CircuitCode, circuit);
        LLUDPClient udpClient = new(udpServer, new ThrottleRates(new IniConfigSource()), udpServer.Throttle,
            CircuitCode, circuit.AgentID, new IPEndPoint(IPAddress.Loopback, 999), 0, 0);
        AuthenticateResponse response = new()
        {
            LoginInfo = new Login { First = "Test", Last = "Viewer", SecureSession = circuit.SecureSessionID }
        };
        return new CapturingClient(scene, udpServer, udpClient, response, circuit.AgentID, circuit.SessionID, CircuitCode);
    }

    private const string Legacy = "Second Life Release";
    private const string Varregion = "Firestorm-Releasex64";
    private const byte LandLayer = (byte)TerrainPatch.LayerType.Land;
    private const byte LandExtendedLayer = (byte)TerrainPatch.LayerType.LandExtended;

    [Fact]
    public void LegacyViewerGetsTheStandardLayerOfALargeRegion()
    {
        TestHelpers.InMethod();
        CapturingClient c = Connect(1024, Legacy);
        try
        {
            c.SendLayerData(new[] { 3, 5 });

            Assert.Single(c.Packets);
            Decoded d = Decode(c.Packets[0], wide: false);
            Assert.Equal(LandLayer, d.Layer);
            Assert.Equal(new[] { (3, 5) }, d.Patches.ToArray());
            Assert.Equal(LandLayer, c.Packets[0][7]);
        }
        finally { c.Close(false, false); }
    }

    [Fact]
    public void LegacyViewerGetsOnlyPatchesZeroToFifteenOnEachAxis()
    {
        TestHelpers.InMethod();
        CapturingClient c = Connect(1024, Legacy);
        try
        {
            c.SendLayerData(new[] { 15, 15, 16, 0, 0, 16, 63, 63, 20, 20, 2, 7 });

            Assert.Single(c.Packets);
            Decoded d = Decode(c.Packets[0], wide: false);
            Assert.Equal(new[] { (15, 15), (2, 7) }, d.Patches.ToArray());
        }
        finally { c.Close(false, false); }
    }

    [Fact]
    public void LegacyViewerIsSentNothingForPatchesBeyondTheSouthWestCorner()
    {
        TestHelpers.InMethod();
        CapturingClient c = Connect(1024, Legacy);
        try
        {
            c.SendLayerData(new[] { 16, 0, 0, 16, 63, 63, 20, 20 });

            Assert.Empty(c.Packets);
        }
        finally { c.Close(false, false); }
    }

    [Fact]
    public void LegacyViewerGetsTheWholeSouthWestCornerAndNothingElseWhenTheRegionIsSent()
    {
        TestHelpers.InMethod();
        CapturingClient c = Connect(1024, Legacy);
        try
        {
            c.SendLayerData();

            List<(int X, int Y)> all = new();
            foreach (byte[] packet in c.Packets)
            {
                Decoded d = Decode(packet, wide: false);
                Assert.Equal(LandLayer, d.Layer);
                all.AddRange(d.Patches);
            }
            Assert.Equal(256, all.Count);
            Assert.Equal(256, all.Distinct().Count());
            Assert.All(all, p => Assert.True(p.X is >= 0 and <= 15 && p.Y is >= 0 and <= 15, $"patch {p}"));
        }
        finally { c.Close(false, false); }
    }

    [Fact]
    public void LegacyViewerTerrainOfALargeRegionEqualsTheTerrainOfA256mRegion()
    {
        TestHelpers.InMethod();
        CapturingClient large = Connect(1024, Legacy, flatten: true);
        CapturingClient small = Connect(256, Varregion, flatten: true);
        try
        {
            int[] map = { 0, 0, 5, 9, 15, 15 };
            large.SendLayerData(map);
            small.SendLayerData(map);

            Assert.Single(large.Packets);
            Assert.Single(small.Packets);
            Assert.Equal(small.Packets[0], large.Packets[0]);
        }
        finally { large.Close(false, false); small.Close(false, false); }
    }

    [Fact]
    public void OtherViewersStillGetTheExtendedLayerOfALargeRegion()
    {
        TestHelpers.InMethod();
        CapturingClient c = Connect(1024, Varregion);
        try
        {
            c.SendLayerData(new[] { 3, 5, 40, 41 });

            Assert.Single(c.Packets);
            Decoded d = Decode(c.Packets[0], wide: true);
            Assert.Equal(LandExtendedLayer, d.Layer);
            Assert.Equal(new[] { (3, 5), (40, 41) }, d.Patches.ToArray());
        }
        finally { c.Close(false, false); }
    }

    // The bytes sent to a viewer that is not on the list are the bytes sent before the legacy-viewer handling
    // existed. The hash was taken from that code with this fixture.
    [Fact]
    public void OtherViewersTerrainOfALargeRegionIsByteForByteWhatItWas()
    {
        TestHelpers.InMethod();
        CapturingClient c = Connect(1024, Varregion);
        try
        {
            c.SendLayerData(new[] { 3, 5, 40, 41, 63, 0 });

            byte[] all = c.Packets.SelectMany(p => p).ToArray();
            Assert.Equal("C3ED09D9B532D21A0787850E14AA0CA0061A0AFC6E17E73A8F3C0B7715C6AE13", Convert.ToHexString(SHA256.HashData(all)));
        }
        finally { c.Close(false, false); }
    }

    [Fact]
    public void OtherViewersTerrainOfA256mRegionIsByteForByteWhatItWas()
    {
        TestHelpers.InMethod();
        CapturingClient c = Connect(256, Varregion);
        try
        {
            c.SendLayerData(new[] { 3, 5, 15, 15, 0, 0 });

            byte[] all = c.Packets.SelectMany(p => p).ToArray();
            Assert.Equal(LandLayer, c.Packets[0][7]);
            Assert.Equal("D910E911ED8C940C6944EA0509613947A4F022EE596083F098B2544C1936399D", Convert.ToHexString(SHA256.HashData(all)));
        }
        finally { c.Close(false, false); }
    }
}
