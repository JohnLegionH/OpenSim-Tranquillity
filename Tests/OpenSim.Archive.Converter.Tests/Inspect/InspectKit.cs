/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework.Serialization;
using Xunit;

namespace OpenSim.Archive.ObjectInspect.Tests;

/// <summary>Archives written by hand with known link numbers, masks and owners, in the forms the region's savers write.</summary>
internal static class InspectKit
{
    public const uint M = 0x4000, C = 0x8000, T = 0x2000;
    public const uint All = 0x7fffffff;

    public static readonly UUID Alice = UUID.Parse("a11ce000-0000-4000-8000-000000000001");
    public static readonly UUID Bob = UUID.Parse("b0b00000-0000-4000-8000-000000000002");
    public static readonly UUID Carol = UUID.Parse("ca701000-0000-4000-8000-000000000003");
    public static readonly UUID Group1 = UUID.Parse("9a009000-0000-4000-8000-000000000005");

    public sealed class KItem
    {
        public string Name = "Item";
        public int AssetType = 10;
        public UUID ItemId = UUID.Random();
        public UUID AssetId = UUID.Random();
        public UUID Creator = Alice, Owner = Alice, LastOwner = Alice, Group = UUID.Zero;
        public string CreatorData;
        public uint BaseMask = All, OwnerMask = All, GroupMask = All, EveryoneMask = All, NextMask = All;
    }

    public sealed class KPart
    {
        public string Name = "Part";
        public int Link;
        public string Desc = "";
        public float X, Y, Z;
        public int? Shape;
        public bool Phantom;
        public UUID Id = UUID.Random();
        public UUID Creator = Alice, Owner = Alice, LastOwner = Alice, Group = UUID.Zero;
        public string CreatorData;
        public uint BaseMask = All, OwnerMask = All, GroupMask = All, EveryoneMask = All, NextMask = All;
        public int ProfileCurve = 1, PathCurve = 16;
        public List<KItem> Items = new List<KItem>();
    }

    /// <summary>The one element of a list, as an assertion: a list that is empty or longer fails the test on its assertion.</summary>
    public static T One<T>(this IEnumerable<T> items) => Assert.Single(items);

    /// <summary>Parses JSON text after asserting there is some, so an empty output fails on an assertion.</summary>
    public static System.Text.Json.JsonDocument Json(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), "the JSON is empty");
        return System.Text.Json.JsonDocument.Parse(text);
    }

    private static string Id(string element, UUID id) => "<" + element + "><UUID>" + id + "</UUID></" + element + ">";
    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Esc(string s) => SecurityElement.Escape(s);

    public static string ItemXml(KItem i)
    {
        StringBuilder sb = new StringBuilder("<TaskInventoryItem>");
        sb.Append(Id("AssetID", i.AssetId));
        sb.Append("<BasePermissions>").Append(i.BaseMask).Append("</BasePermissions><CurrentPermissions>").Append(i.OwnerMask).Append("</CurrentPermissions>");
        sb.Append("<GroupPermissions>").Append(i.GroupMask).Append("</GroupPermissions><EveryonePermissions>").Append(i.EveryoneMask).Append("</EveryonePermissions>");
        sb.Append("<NextPermissions>").Append(i.NextMask).Append("</NextPermissions>");
        sb.Append(Id("CreatorID", i.Creator));
        if (i.CreatorData != null)
            sb.Append("<CreatorData>").Append(Esc(i.CreatorData)).Append("</CreatorData>");
        sb.Append(Id("GroupID", i.Group)).Append("<InvType>").Append(i.AssetType == 10 ? 10 : 7).Append("</InvType>");
        sb.Append(Id("ItemID", i.ItemId)).Append(Id("LastOwnerID", i.LastOwner));
        sb.Append("<Name>").Append(Esc(i.Name)).Append("</Name>").Append(Id("OwnerID", i.Owner));
        sb.Append("<Type>").Append(i.AssetType).Append("</Type></TaskInventoryItem>");
        return sb.ToString();
    }

    public static string PartXml(KPart p)
    {
        StringBuilder sb = new StringBuilder("<SceneObjectPart xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.Append(Id("CreatorID", p.Creator));
        if (p.CreatorData != null)
            sb.Append("<CreatorData>").Append(Esc(p.CreatorData)).Append("</CreatorData>");
        sb.Append(Id("UUID", p.Id));
        sb.Append("<Name>").Append(Esc(p.Name)).Append("</Name>");
        sb.Append("<Description>").Append(Esc(p.Desc)).Append("</Description>");
        sb.Append("<LinkNum>").Append(p.Link).Append("</LinkNum>");
        sb.Append("<GroupPosition><X>10</X><Y>20</Y><Z>30</Z></GroupPosition>");
        sb.Append("<OffsetPosition><X>").Append(F(p.X)).Append("</X><Y>").Append(F(p.Y)).Append("</Y><Z>").Append(F(p.Z)).Append("</Z></OffsetPosition>");
        sb.Append("<CreationDate>1700000000</CreationDate>");
        sb.Append(Id("GroupID", p.Group)).Append(Id("OwnerID", p.Owner)).Append(Id("LastOwnerID", p.LastOwner));
        sb.Append("<BaseMask>").Append(p.BaseMask).Append("</BaseMask><OwnerMask>").Append(p.OwnerMask).Append("</OwnerMask>");
        sb.Append("<GroupMask>").Append(p.GroupMask).Append("</GroupMask><EveryoneMask>").Append(p.EveryoneMask).Append("</EveryoneMask>");
        sb.Append("<NextOwnerMask>").Append(p.NextMask).Append("</NextOwnerMask>");
        sb.Append("<Flags>").Append(p.Phantom ? "Phantom" : "None").Append("</Flags>");
        if (p.Shape.HasValue)
            sb.Append("<PhysicsShapeType>").Append(p.Shape.Value).Append("</PhysicsShapeType>");
        sb.Append("<Shape><PCode>9</PCode><ProfileCurve>").Append(p.ProfileCurve).Append("</ProfileCurve><PathCurve>").Append(p.PathCurve).Append("</PathCurve></Shape>");
        sb.Append("<TaskInventory>").Append(string.Concat(p.Items.Select(ItemXml))).Append("</TaskInventory>");
        sb.Append("</SceneObjectPart>");
        return sb.ToString();
    }

    /// <summary>One linkset in the xml2 form an OAR holds: the root part, then the others.</summary>
    public static string Xml2Group(params KPart[] parts) =>
        "<SceneObjectGroup>" + PartXml(parts[0]) + "<OtherParts>" + string.Concat(parts.Skip(1).Select(PartXml)) + "</OtherParts></SceneObjectGroup>";

    /// <summary>The inner part of the original form an object asset holds: a SceneObjectGroup with RootPart and OtherParts/Part.</summary>
    public static string OriginalGroup(params KPart[] parts) =>
        "<SceneObjectGroup><RootPart>" + PartXml(parts[0]) + "</RootPart><OtherParts>" + string.Concat(parts.Skip(1).Select(p => "<Part>" + PartXml(p) + "</Part>")) + "</OtherParts></SceneObjectGroup>";

    /// <summary>A coalesced object as the region writes it: each linkset in a SceneObjectGroup that carries its offset, around the original form.</summary>
    public static string Coalesced(float sx, float sy, float sz, params (float x, float y, float z, KPart[] parts)[] groups) =>
        "<CoalescedObject x=\"" + F(sx) + "\" y=\"" + F(sy) + "\" z=\"" + F(sz) + "\">" +
        string.Concat(groups.Select(g => "<SceneObjectGroup offsetx=\"" + F(g.x) + "\" offsety=\"" + F(g.y) + "\" offsetz=\"" + F(g.z) + "\">" + OriginalGroup(g.parts) + "</SceneObjectGroup>")) +
        "</CoalescedObject>";

    public static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    public static byte[] Tar(IEnumerable<(string name, byte[] data)> entries, bool gzip = true)
    {
        using MemoryStream raw = new MemoryStream();
        TarArchiveWriter w = new TarArchiveWriter(raw);
        foreach ((string name, byte[] data) in entries)
            w.WriteFile(name, data);
        w.Close();
        byte[] tar = raw.ToArray();
        if (!gzip)
            return tar;
        using MemoryStream gz = new MemoryStream();
        using (GZipStream z = new GZipStream(gz, CompressionLevel.Fastest, true))
            z.Write(tar, 0, tar.Length);
        return gz.ToArray();
    }

    public static byte[] OarControl() =>
        Utf8("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<archive major_version=\"0\" minor_version=\"8\"><creation_info><datetime>1700000000</datetime><id>" + UUID.Random() +
             "</id></creation_info><assets_included>True</assets_included><region_info><size_in_meters>256,256</size_in_meters></region_info></archive>");

    public static byte[] IarControl() =>
        Utf8("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<archive major_version=\"0\" minor_version=\"3\"><assets_included>True</assets_included></archive>");

    /// <summary>An OAR of the given linksets, one object file each, named as the region names them.</summary>
    public static byte[] Oar(params KPart[][] linksets) => Oar(linksets.Select(l => Xml2Group(l)).ToArray(), linksets.Select(l => l[0].Name).ToArray());

    public static byte[] Oar(string[] xmlBodies, string[] names)
    {
        List<(string, byte[])> entries = new List<(string, byte[])> { ("archive.xml", OarControl()) };
        for (int i = 0; i < xmlBodies.Length; i++)
            entries.Add(("objects/" + names[i] + "_" + (10 + i) + "-" + (20 + i) + "-30__" + UUID.Random() + ".xml", Utf8(xmlBodies[i])));
        return Tar(entries);
    }

    public static string WriteFile(string folder, string name, byte[] data)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    public static string NewFolder() => Path.Combine(Path.GetTempPath(), "oar-inspect-" + Guid.NewGuid().ToString("N"));

    public static string IarItemXml(string name, UUID id, UUID asset, int assetType, UUID creator, UUID owner, uint baseMask, uint currentMask, uint groupMask, uint everyoneMask, uint nextMask, string creatorData = null, UUID? group = null)
    {
        StringBuilder sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<InventoryItem>");
        sb.Append("<Name>").Append(Esc(name)).Append("</Name><ID>").Append(id).Append("</ID><InvType>").Append(assetType == 6 ? 6 : 7).Append("</InvType>");
        sb.Append("<CreatorUUID>").Append(creator).Append("</CreatorUUID><CreationDate>1700000000</CreationDate><Owner>").Append(owner).Append("</Owner><Description></Description>");
        sb.Append("<AssetType>").Append(assetType).Append("</AssetType><AssetID>").Append(asset).Append("</AssetID><SaleType>0</SaleType><SalePrice>0</SalePrice>");
        sb.Append("<BasePermissions>").Append(baseMask).Append("</BasePermissions><CurrentPermissions>").Append(currentMask).Append("</CurrentPermissions>");
        sb.Append("<EveryOnePermissions>").Append(everyoneMask).Append("</EveryOnePermissions><NextPermissions>").Append(nextMask).Append("</NextPermissions><Flags>0</Flags>");
        sb.Append("<GroupPermissions>").Append(groupMask).Append("</GroupPermissions>");
        sb.Append("<GroupID>").Append(group ?? UUID.Zero).Append("</GroupID><GroupOwned>False</GroupOwned>");
        if (creatorData != null)
            sb.Append("<CreatorData>").Append(Esc(creatorData)).Append("</CreatorData>");
        sb.Append("</InventoryItem>");
        return sb.ToString();
    }

    public sealed class IarItem
    {
        public string Name = "Item";
        public string Folder = "Objects";
        public UUID Id = UUID.Random();
        public UUID Asset = UUID.Random();
        public int AssetType = 6;
        public UUID Creator = Alice, Owner = Alice;
        public string CreatorData;
        public uint Base = All, Current = All, Group = All, Everyone = All, Next = All;
        /// <summary>The body of the object asset (original or coalesced form); null writes no asset file for the item.</summary>
        public string ObjectXml;
    }

    /// <summary>An IAR of items: each item file under its folder, then the object assets, as the region's saver orders them.</summary>
    public static byte[] Iar(params IarItem[] items)
    {
        List<(string, byte[])> entries = new List<(string, byte[])> { ("archive.xml", IarControl()) };
        foreach (IarItem i in items)
        {
            string folder = "inventory/" + i.Folder + "__" + UUID.Parse("00000000-0000-4000-8000-0000000000aa") + "/";
            entries.Add((folder + i.Name + "__" + i.Id + ".xml", Utf8(IarItemXml(i.Name, i.Id, i.Asset, i.AssetType, i.Creator, i.Owner, i.Base, i.Current, i.Group, i.Everyone, i.Next, i.CreatorData))));
        }
        foreach (IarItem i in items.Where(x => x.ObjectXml != null))
            entries.Add(("assets/" + i.Asset + "_object.xml", Utf8(i.ObjectXml)));
        return Tar(entries);
    }
}
