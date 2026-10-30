/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// The UDP replies that carry inventory: the reply to FetchInventory (item details, at most 10 items per packet) and
/// the reply to FetchInventoryDescendents (folders and items of one folder, split over several packets). Whatever the
/// number of items or folders, every packet that goes out must be completely filled, the packets together must carry
/// every entry once and in order, and sending must not throw.
/// </summary>
public class InventoryReplyPacketTests : OpenSimTestCase, IClassFixture<InventoryReplyPacketTests.ClientFixture>
{
    /// <summary>A UDP server that copies what it is asked to send, because a sender may reuse one packet object.</summary>
    public sealed class SnapshotUdpServer : TestLLUDPServer
    {
        public readonly List<FetchInventoryReplyPacket.InventoryDataBlock?[]> ItemDetailReplies = new();
        public readonly List<InventoryDescendentsPacket.FolderDataBlock?[]> DescendentFolders = new();
        public readonly List<InventoryDescendentsPacket.ItemDataBlock?[]> DescendentItems = new();

        public SnapshotUdpServer(AgentCircuitManager circuitManager)
            : base(IPAddress.Any, 0, 0, new IniConfigSource(), circuitManager)
        {
        }

        public void Forget()
        {
            ItemDetailReplies.Clear();
            DescendentFolders.Clear();
            DescendentItems.Clear();
        }

        public override void SendPacket(
            LLUDPClient udpClient, Packet packet, ThrottleOutPacketType category, bool allowSplitting, UnackedPacketMethod method)
        {
            if (packet is FetchInventoryReplyPacket reply)
                ItemDetailReplies.Add((FetchInventoryReplyPacket.InventoryDataBlock?[])reply.InventoryData.Clone());
            else if (packet is InventoryDescendentsPacket descendents)
            {
                DescendentFolders.Add((InventoryDescendentsPacket.FolderDataBlock?[])descendents.FolderData.Clone());
                DescendentItems.Add((InventoryDescendentsPacket.ItemDataBlock?[])descendents.ItemData.Clone());
            }

            base.SendPacket(udpClient, packet, category, allowSplitting, method);
        }
    }

    /// <summary>One scene and one viewer circuit for the whole class; each test clears what the server recorded.</summary>
    public sealed class ClientFixture : IDisposable
    {
        public readonly LLClientView Client;
        public readonly SnapshotUdpServer Server;

        public ClientFixture()
        {
            // everything on this thread: the circuit is made when the packet is handled
            Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
            Scene scene = new SceneHelpers().SetupScene();
            Server = new SnapshotUdpServer(scene.AuthenticateHandler);
            Server.AddScene(scene);
            ScenePresence sp = ClientStackHelpers.AddChildClient(
                scene, Server, TestHelpers.ParseTail(0x1), TestHelpers.ParseTail(0x2), 123456);
            Client = (LLClientView)sp.ControllingClient;
            Client.IsActive = true;
        }

        public void Dispose() => Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
    }

    private readonly ClientFixture m_fixture;

    public InventoryReplyPacketTests(ClientFixture fixture)
    {
        m_fixture = fixture;
        m_fixture.Server.Forget();
    }

    private (LLClientView Client, SnapshotUdpServer Server) AddClient() => (m_fixture.Client, m_fixture.Server);

    private static InventoryItemBase[] MakeItems(int count)
    {
        InventoryItemBase[] items = new InventoryItemBase[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new InventoryItemBase(TestHelpers.ParseTail(0x1000 + i), TestHelpers.ParseTail(0x1))
            {
                Name = "item " + i,
                Description = "description " + i,
                AssetID = TestHelpers.ParseTail(0x2000 + i),
                Folder = TestHelpers.ParseTail(0x3000),
                CreatorId = TestHelpers.ParseTail(0x1).ToString()
            };
        }
        return items;
    }

    private static List<InventoryFolderBase> MakeFolders(int count)
    {
        List<InventoryFolderBase> folders = new();
        for (int i = 0; i < count; i++)
            folders.Add(new InventoryFolderBase(TestHelpers.ParseTail(0x4000 + i), TestHelpers.ParseTail(0x1))
            {
                Name = "folder " + i,
                ParentID = TestHelpers.ParseTail(0x3000)
            });
        return folders;
    }

    public static IEnumerable<object[]> ItemCounts()
    {
        // 1 to 12 as ordered, plus the counts at and past the packet size (10) and two packets (20)
        foreach (int count in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 19, 20, 21, 25, 30 })
            yield return new object[] { count };
    }

    [Theory]
    [MemberData(nameof(ItemCounts))]
    public void A_fetch_inventory_reply_carries_every_item_once_in_order_in_packets_of_at_most_ten_with_no_empty_slot(int count)
    {
        TestHelpers.InMethod();
        (LLClientView client, SnapshotUdpServer server) = AddClient();
        InventoryItemBase[] items = MakeItems(count);

        Exception? thrown = Record.Exception(() => client.SendInventoryItemDetails(items));

        Assert.Null(thrown);
        Assert.NotEmpty(server.ItemDetailReplies);
        List<UUID> sent = new();
        foreach (FetchInventoryReplyPacket.InventoryDataBlock?[] blocks in server.ItemDetailReplies)
        {
            Assert.InRange(blocks.Length, 1, 10);
            Assert.DoesNotContain(null, blocks);
            sent.AddRange(blocks.Select(b => b!.ItemID));
        }
        Assert.Equal(items.Select(i => i.ID).ToList(), sent);
    }

    [Fact]
    public void A_fetch_inventory_reply_fills_every_packet_but_the_last_with_ten_items()
    {
        TestHelpers.InMethod();
        (LLClientView client, SnapshotUdpServer server) = AddClient();

        Exception? thrown = Record.Exception(() => client.SendInventoryItemDetails(MakeItems(23)));

        Assert.Null(thrown);
        Assert.Equal(new[] { 10, 10, 3 }, server.ItemDetailReplies.Select(r => r.Length).ToArray());
    }

    public static IEnumerable<object[]> FolderAndItemCounts()
    {
        foreach (int items in new[] { 0, 1, 4, 5, 6, 9, 10, 11, 13 })
            foreach (int folders in new[] { 0, 1, 6, 7, 13 })
                yield return new object[] { folders, items };
    }

    [Theory]
    [MemberData(nameof(FolderAndItemCounts))]
    public void A_folder_contents_reply_carries_every_folder_and_item_once_in_order_with_no_empty_slot(int folderCount, int itemCount)
    {
        TestHelpers.InMethod();
        (LLClientView client, SnapshotUdpServer server) = AddClient();
        List<InventoryItemBase> items = MakeItems(itemCount).ToList();
        List<InventoryFolderBase> folders = MakeFolders(folderCount);

        Exception? thrown = Record.Exception(() => client.SendInventoryFolderDetails(
            TestHelpers.ParseTail(0x1), TestHelpers.ParseTail(0x3000), items, folders, 1, itemCount + folderCount, true, true));

        Assert.Null(thrown);
        Assert.NotEmpty(server.DescendentFolders);
        List<UUID> sentFolders = new();
        List<UUID> sentItems = new();
        for (int p = 0; p < server.DescendentFolders.Count; p++)
        {
            Assert.DoesNotContain(null, server.DescendentFolders[p]);
            Assert.DoesNotContain(null, server.DescendentItems[p]);
            // the zero id entry is the placeholder the protocol wants in a section that has nothing to carry
            sentFolders.AddRange(server.DescendentFolders[p].Select(b => b!.FolderID).Where(id => id != UUID.Zero));
            sentItems.AddRange(server.DescendentItems[p].Select(b => b!.ItemID).Where(id => id != UUID.Zero));
            Assert.True(server.DescendentFolders[p].Length <= 6, "more than 6 folders in one packet");
            Assert.True(server.DescendentItems[p].Length <= 5, "more than 5 items in one packet");
        }
        Assert.Equal(folders.Select(f => f.ID).ToList(), sentFolders);
        Assert.Equal(items.Select(i => i.ID).ToList(), sentItems);
    }
}
