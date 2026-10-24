/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.Compare.Tests;

/// <summary>One part of a fixture archive.</summary>
internal sealed class FixturePart
{
    public Guid Id = Guid.NewGuid();
    public string Name;
    public string Description = "";
    public int LinkNum;
    public string Position = "1";
    public string Scale = "0.5";
    public string Creator = "a11ce000-0000-4000-8000-000000000001";
    public string Owner = "a11ce000-0000-4000-8000-000000000001";
    public string Texture = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    /// <summary>Shape fields after the standard ones; a box by default.</summary>
    public string ProfileCurve = "1";
    public string ProfileHollow = "0";
    public bool Mesh;
    /// <summary>The PhysicsShapeType element (0 prim, 1 none, 2 convex); null leaves it out.</summary>
    public string PhysicsShapeType;
    /// <summary>Saved script state of the whole build, written once with the root part.</summary>
    public string ScriptState;
    public List<FixtureItem> Items = new List<FixtureItem>();
}

internal sealed class FixtureItem
{
    public Guid Id = Guid.NewGuid();
    public string Name;
    public Guid Asset;
    public int Type = 7;
    public string Permissions = "2147483647";
}

/// <summary>Builds archives in memory, in the layout the region server's archiver writes.</summary>
internal static class CompareKit
{
    public static readonly Guid ScriptAsset = Guid.Parse("5c21a000-0000-4000-8000-0000000000a1");
    public static readonly Guid NoteAsset = Guid.Parse("5c21a000-0000-4000-8000-0000000000a2");

    /// <summary>A linkset of three parts (one with a script and a notecard in its contents) and a single prim.</summary>
    public static List<List<FixturePart>> SampleBuilds()
    {
        FixturePart root = new FixturePart { Name = "Tower", LinkNum = 0 };
        FixturePart second = new FixturePart { Name = "Tower-2", LinkNum = 1, Description = "second" };
        second.Items.Add(new FixtureItem { Name = "Read me", Asset = NoteAsset, Type = 7 });
        second.Items.Add(new FixtureItem { Name = "Door script", Asset = ScriptAsset, Type = 10 });
        FixturePart third = new FixturePart { Name = "Tower-3", LinkNum = 2, ProfileCurve = "0" };
        FixturePart lamp = new FixturePart { Name = "Lamp", LinkNum = 0, Position = "60" };
        return new List<List<FixturePart>> { new List<FixturePart> { root, second, third }, new List<FixturePart> { lamp } };
    }

    public static Dictionary<string, byte[]> SampleAssets() => new Dictionary<string, byte[]>
    {
        ["assets/" + ScriptAsset + "_script.lsl"] = Encoding.ASCII.GetBytes("default { state_entry() { llSay(0, \"hi\"); } }"),
        ["assets/" + NoteAsset + "_notecard.txt"] = Encoding.ASCII.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 5\nhello}\n")
    };

    private static string Vec(string v, bool three = true) =>
        three ? "<X>" + v + "</X><Y>" + v + "</Y><Z>" + v + "</Z>" : v;

    public static string PartXml(FixturePart p)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<SceneObjectPart xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.Append("<AllowedDrop>false</AllowedDrop>");
        sb.Append("<CreatorID><UUID>").Append(p.Creator).Append("</UUID></CreatorID>");
        sb.Append("<FolderID><UUID>").Append(Guid.Empty).Append("</UUID></FolderID>");
        sb.Append("<InventorySerial>1</InventorySerial>");
        sb.Append("<TaskInventory>");
        foreach (FixtureItem it in p.Items)
        {
            sb.Append("<TaskInventoryItem>");
            sb.Append("<AssetID><UUID>").Append(it.Asset).Append("</UUID></AssetID>");
            sb.Append("<BasePermissions>").Append(it.Permissions).Append("</BasePermissions>");
            sb.Append("<CreationDate>1700000000</CreationDate>");
            sb.Append("<CreatorID><UUID>").Append(p.Creator).Append("</UUID></CreatorID>");
            sb.Append("<Description>d</Description>");
            sb.Append("<EveryonePermissions>0</EveryonePermissions><Flags>0</Flags>");
            sb.Append("<GroupID><UUID>").Append(Guid.Empty).Append("</UUID></GroupID>");
            sb.Append("<GroupPermissions>0</GroupPermissions><InvType>").Append(it.Type == 10 ? 10 : 7).Append("</InvType>");
            sb.Append("<ItemID><UUID>").Append(it.Id).Append("</UUID></ItemID>");
            sb.Append("<OldItemID><UUID>").Append(Guid.Empty).Append("</UUID></OldItemID>");
            sb.Append("<LastOwnerID><UUID>").Append(p.Owner).Append("</UUID></LastOwnerID>");
            sb.Append("<Name>").Append(it.Name).Append("</Name><NextPermissions>581632</NextPermissions>");
            sb.Append("<OwnerID><UUID>").Append(p.Owner).Append("</UUID></OwnerID>");
            sb.Append("<CurrentPermissions>").Append(it.Permissions).Append("</CurrentPermissions>");
            sb.Append("<ParentID><UUID>").Append(p.Id).Append("</UUID></ParentID>");
            sb.Append("<ParentPartID><UUID>").Append(p.Id).Append("</UUID></ParentPartID>");
            sb.Append("<PermsGranter><UUID>").Append(Guid.Empty).Append("</UUID></PermsGranter><PermsMask>0</PermsMask>");
            sb.Append("<Type>").Append(it.Type).Append("</Type><OwnerChanged>false</OwnerChanged>");
            sb.Append("</TaskInventoryItem>");
        }
        sb.Append("</TaskInventory>");
        sb.Append("<UUID><UUID>").Append(p.Id).Append("</UUID></UUID>");
        sb.Append("<LocalId>").Append(100 + p.LinkNum).Append("</LocalId>");
        sb.Append("<Name>").Append(p.Name).Append("</Name>");
        sb.Append("<GroupPosition>").Append(Vec(p.Position)).Append("</GroupPosition>");
        sb.Append("<OffsetPosition>").Append(Vec("0")).Append("</OffsetPosition>");
        sb.Append("<RotationOffset><X>0</X><Y>0</Y><Z>0</Z><W>1</W></RotationOffset>");
        sb.Append("<Description>").Append(p.Description).Append("</Description>");
        sb.Append("<LinkNum>").Append(p.LinkNum).Append("</LinkNum>");
        if (p.PhysicsShapeType != null)
            sb.Append("<PhysicsShapeType>").Append(p.PhysicsShapeType).Append("</PhysicsShapeType>");
        sb.Append("<Shape>");
        sb.Append("<ProfileCurve>").Append(p.Mesh ? "49" : p.ProfileCurve).Append("</ProfileCurve>");
        sb.Append("<TextureEntry>").Append(p.Texture).Append("</TextureEntry>");
        sb.Append("<PathBegin>0</PathBegin><PathCurve>16</PathCurve><PathEnd>0</PathEnd><PathRadiusOffset>0</PathRadiusOffset>");
        sb.Append("<PathRevolutions>0</PathRevolutions><PathScaleX>100</PathScaleX><PathScaleY>100</PathScaleY><PathShearX>0</PathShearX><PathShearY>0</PathShearY>");
        sb.Append("<PathSkew>0</PathSkew><PathTaperX>0</PathTaperX><PathTaperY>0</PathTaperY><PathTwist>0</PathTwist><PathTwistBegin>0</PathTwistBegin>");
        sb.Append("<PCode>9</PCode><ProfileBegin>0</ProfileBegin><ProfileEnd>0</ProfileEnd><ProfileHollow>").Append(p.ProfileHollow).Append("</ProfileHollow>");
        sb.Append("<State>0</State>");
        sb.Append("<SculptTexture><UUID>").Append(Guid.Empty).Append("</UUID></SculptTexture><SculptType>").Append(p.Mesh ? 5 : 0).Append("</SculptType>");
        sb.Append("<SculptEntry>").Append(p.Mesh ? "true" : "false").Append("</SculptEntry>");
        sb.Append("</Shape>");
        sb.Append("<Scale>").Append(Vec(p.Scale)).Append("</Scale>");
        sb.Append("<OwnerID><UUID>").Append(p.Owner).Append("</UUID></OwnerID>");
        sb.Append("<GroupID><UUID>").Append(Guid.Empty).Append("</UUID></GroupID>");
        sb.Append("<BaseMask>2147483647</BaseMask><OwnerMask>2147483647</OwnerMask><GroupMask>0</GroupMask><EveryoneMask>0</EveryoneMask><NextOwnerMask>581632</NextOwnerMask>");
        sb.Append("</SceneObjectPart>");
        return sb.ToString();
    }

    public static string BuildXml(List<FixturePart> parts)
    {
        StringBuilder sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SceneObjectGroup>\n<RootPart>");
        sb.Append(PartXml(parts[0])).Append("</RootPart><OtherParts>");
        foreach (FixturePart p in parts.Skip(1))
            sb.Append(PartXml(p));
        sb.Append("</OtherParts>");
        if (parts[0].ScriptState != null)
            sb.Append("<GroupScriptStates><SavedScriptState>").Append(parts[0].ScriptState).Append("</SavedScriptState></GroupScriptStates>");
        sb.Append("</SceneObjectGroup>");
        return sb.ToString();
    }

    /// <summary>Writes the builds and assets as a gzip tar with the control file first, as the archiver does.</summary>
    public static string WriteArchive(List<List<FixturePart>> builds, Dictionary<string, byte[]> assets)
    {
        string path = TempFile(".oar");
        using FileStream fs = new FileStream(path, FileMode.CreateNew);
        using (GZipStream gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (TarWriter tar = new TarWriter(gz, leaveOpen: true))
        {
            tar.WriteFile("archive.xml", Encoding.UTF8.GetBytes("<archive major_version=\"0\" minor_version=\"8\"><creation_info><datetime>1700000000</datetime><id>00000000-0000-0000-0000-000000000000</id></creation_info></archive>"));
            tar.WriteDirectory("objects/");
            foreach (List<FixturePart> b in builds)
                tar.WriteFile("objects/" + b[0].Name + "_" + b[0].Id + ".xml", Encoding.UTF8.GetBytes(BuildXml(b)));
            tar.WriteDirectory("assets/");
            foreach (KeyValuePair<string, byte[]> a in assets)
                tar.WriteFile(a.Key, a.Value);
            tar.WriteFile("terrains/terrain.r32", new byte[16]);
        }
        return path;
    }

    public static string Sample(Action<List<List<FixturePart>>, Dictionary<string, byte[]>> change = null)
    {
        List<List<FixturePart>> builds = SampleBuilds();
        Dictionary<string, byte[]> assets = SampleAssets();
        change?.Invoke(builds, assets);
        return WriteArchive(builds, assets);
    }

    /// <summary>The same sample, but the original and the changed copy share the part ids.</summary>
    public static (string original, string changed) Pair(Action<List<List<FixturePart>>, Dictionary<string, byte[]>> change)
    {
        List<List<FixturePart>> builds = SampleBuilds();
        Dictionary<string, byte[]> assets = SampleAssets();
        string original = WriteArchive(builds, assets);
        change(builds, assets);
        return (original, WriteArchive(builds, assets));
    }

    public static string TempFile(string suffix)
    {
        string dir = Path.Combine(Path.GetTempPath(), "archive-compare-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + suffix);
    }
}
