/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>Archive pieces written by hand, so tests can make shapes the savers do not produce.</summary>
internal static class HandMade
{
    private static string Id(string element, UUID id) => "<" + element + "><UUID>" + id + "</UUID></" + element + ">";

    public static string PartXml(string name, UUID partId, UUID creator, UUID owner, UUID lastOwner, string creatorData = null,
        int date = 1700000000, UUID? group = null, string extra = "", bool wrapRoot = false)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<SceneObjectPart xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.Append(Id("CreatorID", creator));
        if (creatorData != null)
            sb.Append("<CreatorData>").Append(System.Security.SecurityElement.Escape(creatorData)).Append("</CreatorData>");
        sb.Append(Id("UUID", partId));
        sb.Append("<Name>").Append(System.Security.SecurityElement.Escape(name)).Append("</Name>");
        sb.Append("<LinkNum>0</LinkNum><GroupPosition><X>10</X><Y>20</Y><Z>30</Z></GroupPosition>");
        sb.Append("<CreationDate>").Append(date).Append("</CreationDate>");
        sb.Append(Id("GroupID", group ?? UUID.Zero)).Append(Id("OwnerID", owner)).Append(Id("LastOwnerID", lastOwner));
        sb.Append("<BaseMask>2147483647</BaseMask><OwnerMask>2147483647</OwnerMask><GroupMask>0</GroupMask><EveryoneMask>0</EveryoneMask><NextOwnerMask>581632</NextOwnerMask>");
        sb.Append(extra);
        sb.Append("</SceneObjectPart>");
        return sb.ToString();
    }

    /// <summary>One single-part object in the xml2 form an OAR holds.</summary>
    public static string Xml2Object(string partXml) => "<SceneObjectGroup>" + partXml + "<OtherParts /></SceneObjectGroup>";

    /// <summary>One single-part object in the original form an object asset holds.</summary>
    public static string OriginalObject(string partXml) => "<SceneObjectGroup><RootPart>" + partXml + "</RootPart><OtherParts /></SceneObjectGroup>";

    public static string TaskItem(string name, UUID item, UUID asset, int assetType, UUID creator, UUID owner, int date = 1700000000, string creatorData = null) =>
        "<TaskInventoryItem>" + Id("AssetID", asset) + "<BasePermissions>2147483647</BasePermissions><CreationDate>" + date + "</CreationDate>" + Id("CreatorID", creator) +
        (creatorData != null ? "<CreatorData>" + creatorData + "</CreatorData>" : "") + "<Description></Description>" + Id("GroupID", UUID.Zero) +
        "<InvType>" + (assetType == 6 ? 6 : 10) + "</InvType>" + Id("ItemID", item) + Id("LastOwnerID", owner) + "<Name>" + System.Security.SecurityElement.Escape(name) + "</Name>" +
        Id("OwnerID", owner) + "<CurrentPermissions>2147483647</CurrentPermissions><Type>" + assetType + "</Type></TaskInventoryItem>";

    public static string Inventory(params string[] items) => "<TaskInventory>" + string.Concat(items) + "</TaskInventory>";

    /// <summary>A plain tar of the control file followed by the given entries.</summary>
    public static byte[] Oar(IEnumerable<(string name, byte[] data)> entries, bool gzip = true, string version = "0.8")
    {
        List<(string, byte[])> all = new List<(string, byte[])> { ("archive.xml", ArchiveTestKit.OarControl(version)) };
        all.AddRange(entries);
        return ArchiveTestKit.Tar(all, gzip);
    }

    public static (string, byte[]) ObjectFile(string objectName, string xml) => ("objects/" + objectName + ".xml", ArchiveTestKit.Utf8(xml));

    /// <summary>An OAR of single-part objects with the given creator, owner and last owner on each.</summary>
    public static byte[] OarOfParts(int count, Func<int, (UUID creator, UUID owner, UUID lastOwner, string creatorData, int date)> who, bool gzip = true)
    {
        List<(string, byte[])> entries = new List<(string, byte[])>();
        for (int i = 0; i < count; i++)
        {
            var w = who(i);
            string part = PartXml("Part " + i, new UUID("00000000-0000-4000-8000-" + (i + 1).ToString("x12")), w.creator, w.owner, w.lastOwner, w.creatorData, w.date);
            entries.Add(ObjectFile("Obj" + i + "_" + i + "-0-0__" + i, Xml2Object(part)));
        }
        return Oar(entries, gzip);
    }

    /// <summary>An IAR of items; each item is given as (creator id or null, creator uuid text, creator data, owner, date).</summary>
    public static byte[] IarOfItems(int count, Func<int, (UUID creator, UUID owner, string creatorData, string uuidText, string idText, int date)> who, string version = "0.3", bool gzip = true)
    {
        List<(string, byte[])> entries = new List<(string, byte[])> { ("archive.xml", ArchiveTestKit.IarControl(version)) };
        for (int i = 0; i < count; i++)
        {
            var w = who(i);
            UUID id = new UUID("00000000-0000-4000-8000-" + (i + 1).ToString("x12"));
            string xml = ArchiveTestKit.IarItemXml("Item " + i, id, w.creator, w.owner, UUID.Random(), 0, w.date, w.uuidText, w.creatorData, w.idText);
            entries.Add(("inventory/Stuff__00000000-0000-4000-8000-0000000000aa/Item " + i + "__" + id + ".xml", ArchiveTestKit.Utf8(xml)));
        }
        return ArchiveTestKit.Tar(entries, gzip);
    }

    /// <summary>The 512 byte header of a tar entry, with a correct checksum.</summary>
    public static byte[] RawHeader(string name, long size, char type = '0')
    {
        byte[] h = new byte[512];
        byte[] n = Encoding.ASCII.GetBytes(name);
        Array.Copy(n, h, Math.Min(100, n.Length));
        Encoding.ASCII.GetBytes("0000644").CopyTo(h, 100);
        Encoding.ASCII.GetBytes("0000000").CopyTo(h, 108);
        Encoding.ASCII.GetBytes("0000000").CopyTo(h, 116);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0')).CopyTo(h, 124);
        Encoding.ASCII.GetBytes("00000000000").CopyTo(h, 136);
        h[156] = (byte)type;
        for (int i = 148; i < 156; i++) h[i] = (byte)' ';
        long sum = 0;
        foreach (byte b in h) sum += b;
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0')).CopyTo(h, 148);
        h[154] = 0;
        h[155] = (byte)' ';
        return h;
    }

    /// <summary>Plays back fixed segments and then a generated run of zero bytes, without ever holding the run.</summary>
    public sealed class LazyTar : Stream
    {
        private readonly byte[] m_head;
        private readonly long m_zeros;
        private readonly byte[] m_tail;
        private long m_pos;
        public LazyTar(byte[] head, long zeros, byte[] tail) { m_head = head; m_zeros = zeros; m_tail = tail; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int copied = 0;
            while (copied < count)
            {
                int n;
                if (m_pos < m_head.Length)
                {
                    n = (int)Math.Min(count - copied, m_head.Length - m_pos);
                    Array.Copy(m_head, m_pos, buffer, offset + copied, n);
                }
                else if (m_pos < m_head.Length + m_zeros)
                {
                    n = (int)Math.Min(count - copied, m_head.Length + m_zeros - m_pos);
                    Array.Clear(buffer, offset + copied, n);
                }
                else if (m_pos < m_head.Length + m_zeros + m_tail.Length)
                {
                    long at = m_pos - m_head.Length - m_zeros;
                    n = (int)Math.Min(count - copied, m_tail.Length - at);
                    Array.Copy(m_tail, at, buffer, offset + copied, n);
                }
                else
                    break;
                m_pos += n;
                copied += n;
            }
            return copied;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => m_head.Length + m_zeros + m_tail.Length;
        public override long Position { get => m_pos; set => m_pos = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            m_pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? m_pos + offset : Length + offset;
            return m_pos;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
