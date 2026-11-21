/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Land.Tests;

/// <summary>
/// The parcel overlay is 4096 cells (64 x 64 cells of 4 m) in 4 packets of 1024, sequence ids 0 to 3, for a 256 m
/// region. A viewer that only takes 256 m regions keeps the overlay in a buffer of exactly that size and copies each
/// packet in at sequence id x 1024 without checking the id, so it must be sent those 4 packets and no others, even
/// when the region is larger. Every other viewer gets the whole region's overlay, as before.
/// </summary>
public class ParcelOverlayLegacyViewerTests : OpenSimTestCase
{
    private const int CellsPerPacket = 1024;
    private const int WestParcelWidthMetres = 128;

    private static readonly UUID ViewerAgent = TestHelpers.ParseTail(0x11);
    private static readonly UUID OtherOwner = TestHelpers.ParseTail(0x12);

    // A region of the given size cut into two parcels at 128 m from the west edge: the viewer's own land on the
    // west, someone else's on the east.
    private sealed class Fixture
    {
        public readonly Scene Scene;
        public readonly LandManagementModule Module = new();
        public readonly TestClient Client;

        public Fixture(uint size, bool only256m)
        {
            Scene = new SceneHelpers().SetupScene("Overlay test region", UUID.Random(), 1000, 1000, size, size,
                new IniConfigSource());
            SceneHelpers.SetupSceneModules(Scene, Module);

            AddParcel(ViewerAgent, 0, 0, WestParcelWidthMetres, (int)size);
            AddParcel(OtherOwner, WestParcelWidthMetres, 0, (int)size, (int)size);

            AgentCircuitData agent = SceneHelpers.GenerateAgentData(ViewerAgent);
            Client = new TestClient(agent, Scene) { Only256mRegions = only256m };
            SceneHelpers.AddScenePresence(Scene, Client, agent);
            Client.ParcelOverlayPackets.Clear();
        }

        private void AddParcel(UUID owner, int x0, int y0, int x1, int y1)
        {
            LandObject lo = new(owner, false, Scene);
            lo.SetLandBitmap(lo.GetSquareLandBitmap(x0, y0, x1, y1));
            Module.AddLandObject(lo);
        }

        public List<(int Sequence, byte[] Data)> Overlay()
        {
            Client.ParcelOverlayPackets.Clear();
            Module.SendParcelOverlay(Client);
            return Client.ParcelOverlayPackets;
        }
    }

    private static byte[] Flatten(List<(int Sequence, byte[] Data)> packets)
    {
        List<byte> all = new();
        foreach (var p in packets)
            all.AddRange(p.Data);
        return all.ToArray();
    }

    private const byte Mine = LandChannel.LAND_TYPE_OWNED_BY_REQUESTER;
    private const byte Theirs = LandChannel.LAND_TYPE_OWNED_BY_OTHER;
    private const byte West = LandChannel.LAND_FLAG_PROPERTY_BORDER_WEST;
    private const byte South = LandChannel.LAND_FLAG_PROPERTY_BORDER_SOUTH;

    [Fact]
    public void LegacyViewerGetsFourPacketsOfAOneKilometreRegion()
    {
        TestHelpers.InMethod();
        Fixture f = new(1024, only256m: true);

        var packets = f.Overlay();

        Assert.Equal(4, packets.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, packets.Select(p => p.Sequence).ToArray());
        Assert.All(packets, p => Assert.Equal(CellsPerPacket, p.Data.Length));
    }

    [Fact]
    public void LegacyViewerOverlayIsTheSouthWestCornerWithThe256mLayout()
    {
        TestHelpers.InMethod();
        Fixture f = new(1024, only256m: true);

        byte[] cells = Flatten(f.Overlay());

        // Row 0 is the south edge: every cell has the south border. The first cell of each parcel's run has the west
        // border. The west parcel is 128 m, 32 cells, wide.
        Assert.Equal(Mine | South | West, cells[0]);
        Assert.Equal(Mine | South, cells[1]);
        Assert.Equal(Mine | South, cells[31]);
        Assert.Equal(Theirs | South | West, cells[32]);
        Assert.Equal(Theirs | South, cells[63]);
        // Row 1 starts at cell 64 of the 64-wide layout, not at cell 256 of the region's own row.
        Assert.Equal(Mine | West, cells[64]);
        Assert.Equal(Mine, cells[64 + 31]);
        Assert.Equal(Theirs | West, cells[64 + 32]);
        Assert.Equal(Theirs, cells[64 + 63]);
        // The last cell is the north-east corner of the 256 m square, not of the region.
        Assert.Equal(Theirs, cells[4095]);
    }

    [Fact]
    public void LegacyViewerOverlayOfALargeRegionEqualsTheOverlayOfA256mRegionWithTheSameLand()
    {
        TestHelpers.InMethod();
        byte[] large = Flatten(new Fixture(1024, only256m: true).Overlay());
        byte[] small = Flatten(new Fixture(256, only256m: false).Overlay());

        Assert.Equal(small.Length, large.Length);
        Assert.Equal(small, large);
    }

    [Fact]
    public void LegacyViewerOverlayOfA256mRegionIsUnchanged()
    {
        TestHelpers.InMethod();
        var plain = new Fixture(256, only256m: false).Overlay();
        var legacy = new Fixture(256, only256m: true).Overlay();

        Assert.Equal(4, plain.Count);
        Assert.Equal(Flatten(plain), Flatten(legacy));
    }

    [Fact]
    public void OtherViewersStillGetEveryPacketOfAOneKilometreRegion()
    {
        TestHelpers.InMethod();
        Fixture f = new(1024, only256m: false);

        var packets = f.Overlay();

        // (1024 / 4)^2 cells = 65536, in packets of 1024
        Assert.Equal(64, packets.Count);
        Assert.Equal(Enumerable.Range(0, 64).ToArray(), packets.Select(p => p.Sequence).ToArray());
        Assert.All(packets, p => Assert.Equal(CellsPerPacket, p.Data.Length));
    }

    // The bytes sent to a viewer that is not on the list are the bytes this code sent before the legacy-viewer
    // handling existed. The hash was taken from that code with this fixture.
    [Fact]
    public void OtherViewersOverlayOfAOneKilometreRegionIsByteForByteWhatItWas()
    {
        TestHelpers.InMethod();
        Fixture f = new(1024, only256m: false);

        byte[] bytes = Flatten(f.Overlay());

        Assert.Equal(65536, bytes.Length);
        Assert.Equal("1B8333B62E8F32EDD563FBAA15A9D3A0C960EF06AA7CA81A27E92E29C3E2CDF2", Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void OtherViewersOverlayOfA256mRegionIsByteForByteWhatItWas()
    {
        TestHelpers.InMethod();
        Fixture f = new(256, only256m: false);

        byte[] bytes = Flatten(f.Overlay());

        Assert.Equal(4096, bytes.Length);
        Assert.Equal("56C4F6613202911CB9ED83860A3428AEB012F5CE15BFB4E536BFB583E82AC79C", Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
