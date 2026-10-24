/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.Eligibility.Tests;

internal sealed class KitItem
{
    public Guid Id = Guid.NewGuid();
    public string Name;
    public Guid Asset;
    public int Type = 10;
}

/// <summary>One part of a fixture archive, with the fields the eligibility rules read.</summary>
internal sealed class KitPart
{
    public Guid Id = Guid.NewGuid();
    public string Name = "Part";
    public int LinkNum;
    public string Scale = "1.5,2.5,3.5";
    public string Creator = "a11ce000-0000-4000-8000-000000000001";
    public string Owner = "a11ce000-0000-4000-8000-000000000001";
    public string Flags = "None";
    public bool Vehicle;
    /// <summary>The PhysicsShapeType element (0 prim, 1 none, 2 convex); -1 leaves the element out.</summary>
    public int PhysicsShapeType = -1;
    /// <summary>The LightEntry element, which the loader reads after ExtraParams and which wins over it.</summary>
    public bool Light;
    public int PCode = 9;
    /// <summary>Shape fields as stored; a plain box by default.</summary>
    public ShapeData Shape = FaceMatrix.Shape("box", "plain");
    public Guid SculptTexture;
    public string ExtraParams = "";
    public uint BaseMask = 2147483647, OwnerMask = 2147483647, EveryoneMask;
    public List<KitItem> Items = new List<KitItem>();

    public KitPart() { }
    public KitPart(string name, int linkNum = 0) { Name = name; LinkNum = linkNum; }
}

internal static class EligibilityKit
{
    public static string Num(Guid g) => g.ToString();

    public static string PartXml(KitPart p)
    {
        ShapeData s = p.Shape;
        StringBuilder sb = new StringBuilder();
        sb.Append("<SceneObjectPart xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.Append("<AllowedDrop>false</AllowedDrop>");
        sb.Append("<CreatorID><UUID>").Append(p.Creator).Append("</UUID></CreatorID>");
        sb.Append("<FolderID><UUID>").Append(Guid.Empty).Append("</UUID></FolderID>");
        sb.Append("<InventorySerial>1</InventorySerial>");
        sb.Append("<TaskInventory>");
        foreach (KitItem it in p.Items)
        {
            sb.Append("<TaskInventoryItem>");
            sb.Append("<AssetID><UUID>").Append(it.Asset).Append("</UUID></AssetID>");
            sb.Append("<BasePermissions>2147483647</BasePermissions><CreationDate>1700000000</CreationDate>");
            sb.Append("<CreatorID><UUID>").Append(p.Creator).Append("</UUID></CreatorID>");
            sb.Append("<Description>d</Description><EveryonePermissions>0</EveryonePermissions><Flags>0</Flags>");
            sb.Append("<GroupID><UUID>").Append(Guid.Empty).Append("</UUID></GroupID>");
            sb.Append("<GroupPermissions>0</GroupPermissions><InvType>").Append(it.Type == 10 ? 10 : 7).Append("</InvType>");
            sb.Append("<ItemID><UUID>").Append(it.Id).Append("</UUID></ItemID>");
            sb.Append("<OldItemID><UUID>").Append(Guid.Empty).Append("</UUID></OldItemID>");
            sb.Append("<LastOwnerID><UUID>").Append(p.Owner).Append("</UUID></LastOwnerID>");
            sb.Append("<Name>").Append(it.Name).Append("</Name><NextPermissions>581632</NextPermissions>");
            sb.Append("<OwnerID><UUID>").Append(p.Owner).Append("</UUID></OwnerID>");
            sb.Append("<CurrentPermissions>2147483647</CurrentPermissions>");
            sb.Append("<ParentID><UUID>").Append(p.Id).Append("</UUID></ParentID><ParentPartID><UUID>").Append(p.Id).Append("</UUID></ParentPartID>");
            sb.Append("<PermsGranter><UUID>").Append(Guid.Empty).Append("</UUID></PermsGranter><PermsMask>0</PermsMask>");
            sb.Append("<Type>").Append(it.Type).Append("</Type><OwnerChanged>false</OwnerChanged>");
            sb.Append("</TaskInventoryItem>");
        }
        sb.Append("</TaskInventory>");
        sb.Append("<UUID><UUID>").Append(p.Id).Append("</UUID></UUID>");
        sb.Append("<LocalId>").Append(100 + p.LinkNum).Append("</LocalId>");
        sb.Append("<Name>").Append(p.Name).Append("</Name>");
        sb.Append("<GroupPosition><X>1</X><Y>1</Y><Z>1</Z></GroupPosition>");
        sb.Append("<OffsetPosition><X>0</X><Y>0</Y><Z>0</Z></OffsetPosition>");
        sb.Append("<RotationOffset><X>0</X><Y>0</Y><Z>0</Z><W>1</W></RotationOffset>");
        sb.Append("<Description></Description><LinkNum>").Append(p.LinkNum).Append("</LinkNum>");
        sb.Append("<Flags>").Append(p.Flags).Append("</Flags>");
        if (p.PhysicsShapeType >= 0)
            sb.Append("<PhysicsShapeType>").Append(p.PhysicsShapeType).Append("</PhysicsShapeType>");
        if (p.Vehicle)
            sb.Append("<Vehicle><TYPE>1</TYPE><FLAGS>0</FLAGS></Vehicle>");
        sb.Append("<Shape>");
        sb.Append("<ProfileCurve>").Append(s.ProfileCurve).Append("</ProfileCurve>");
        sb.Append("<TextureEntry>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</TextureEntry>");
        sb.Append("<ExtraParams>").Append(p.ExtraParams).Append("</ExtraParams>");
        sb.Append("<PathBegin>").Append(s.PathBegin).Append("</PathBegin><PathCurve>").Append(s.PathCurve).Append("</PathCurve><PathEnd>").Append(s.PathEnd).Append("</PathEnd>");
        sb.Append("<PathRadiusOffset>").Append(s.PathRadiusOffset).Append("</PathRadiusOffset><PathRevolutions>").Append(s.PathRevolutions).Append("</PathRevolutions>");
        sb.Append("<PathScaleX>").Append(s.PathScaleX).Append("</PathScaleX><PathScaleY>").Append(s.PathScaleY).Append("</PathScaleY>");
        sb.Append("<PathShearX>").Append(s.PathShearX).Append("</PathShearX><PathShearY>").Append(s.PathShearY).Append("</PathShearY>");
        sb.Append("<PathSkew>").Append(s.PathSkew).Append("</PathSkew><PathTaperX>").Append(s.PathTaperX).Append("</PathTaperX><PathTaperY>").Append(s.PathTaperY).Append("</PathTaperY>");
        sb.Append("<PathTwist>").Append(s.PathTwist).Append("</PathTwist><PathTwistBegin>").Append(s.PathTwistBegin).Append("</PathTwistBegin>");
        sb.Append("<PCode>").Append(p.PCode).Append("</PCode><ProfileBegin>").Append(s.ProfileBegin).Append("</ProfileBegin><ProfileEnd>").Append(s.ProfileEnd)
          .Append("</ProfileEnd><ProfileHollow>").Append(s.ProfileHollow).Append("</ProfileHollow>");
        sb.Append("<State>0</State><LastAttachPoint>0</LastAttachPoint>");
        sb.Append("<ProfileShape>").Append(ProfileName(s.ProfileCurve & 0x0F)).Append("</ProfileShape><HollowShape>").Append(HollowName(s.ProfileCurve & 0xF0)).Append("</HollowShape>");
        sb.Append("<SculptTexture><UUID>").Append(p.SculptTexture).Append("</UUID></SculptTexture><SculptType>").Append(s.SculptType).Append("</SculptType>");
        sb.Append("<SculptEntry>").Append(s.SculptEntry ? "true" : "false").Append("</SculptEntry>");
        sb.Append("<FlexiEntry>false</FlexiEntry><LightEntry>").Append(p.Light ? "true" : "false").Append("</LightEntry>");
        sb.Append("</Shape>");
        string[] sc = p.Scale.Split(',');
        sb.Append("<Scale><X>").Append(sc[0]).Append("</X><Y>").Append(sc[1]).Append("</Y><Z>").Append(sc[2]).Append("</Z></Scale>");
        sb.Append("<OwnerID><UUID>").Append(p.Owner).Append("</UUID></OwnerID><GroupID><UUID>").Append(Guid.Empty).Append("</UUID></GroupID>");
        sb.Append("<BaseMask>").Append(p.BaseMask).Append("</BaseMask><OwnerMask>").Append(p.OwnerMask).Append("</OwnerMask><GroupMask>0</GroupMask><EveryoneMask>")
          .Append(p.EveryoneMask).Append("</EveryoneMask><NextOwnerMask>581632</NextOwnerMask>");
        sb.Append("</SceneObjectPart>");
        return sb.ToString();
    }

    private static string ProfileName(int v) => v switch { 0 => "Circle", 1 => "Square", 2 => "IsometricTriangle", 3 => "EquilateralTriangle", 4 => "RightTriangle", _ => "HalfCircle" };
    private static string HollowName(int v) => v switch { 0 => "Same", 16 => "Circle", 32 => "Square", _ => "Triangle" };

    public static string BuildXml(List<KitPart> parts)
    {
        StringBuilder sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SceneObjectGroup>\n<RootPart>");
        sb.Append(PartXml(parts[0])).Append("</RootPart><OtherParts>");
        foreach (KitPart p in parts.Skip(1))
            sb.Append(PartXml(p));
        sb.Append("</OtherParts></SceneObjectGroup>");
        return sb.ToString();
    }

    /// <summary>Writes the builds and assets as a gzip tar with the control file first, as the archiver does.</summary>
    public static string WriteArchive(List<List<KitPart>> builds, Dictionary<string, byte[]> assets = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "archive-eligibility-tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".oar");
        using FileStream fs = new FileStream(path, FileMode.CreateNew);
        using (GZipStream gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (TarWriter tar = new TarWriter(gz, leaveOpen: true))
        {
            tar.WriteFile("archive.xml", Encoding.UTF8.GetBytes("<archive major_version=\"0\" minor_version=\"8\"><creation_info><datetime>1700000000</datetime><id>00000000-0000-0000-0000-000000000000</id></creation_info></archive>"));
            tar.WriteDirectory("objects/");
            foreach (List<KitPart> b in builds)
                tar.WriteFile("objects/" + b[0].Name + "_" + b[0].Id + ".xml", Encoding.UTF8.GetBytes(BuildXml(b)));
            tar.WriteDirectory("assets/");
            if (assets != null)
                foreach (KeyValuePair<string, byte[]> a in assets)
                    tar.WriteFile(a.Key, a.Value);
            tar.WriteFile("terrains/terrain.r32", new byte[16]);
        }
        return path;
    }

    public static string WriteArchive(params KitPart[] singles) => WriteArchive(singles.Select(p => new List<KitPart> { p }).ToList());

    /// <summary>A script asset and the item that holds it.</summary>
    public static (KitItem item, string entry, byte[] data) Script(string name, string source)
    {
        Guid asset = Guid.NewGuid();
        return (new KitItem { Name = name, Asset = asset, Type = 10 }, "assets/" + asset + "_script.lsl", Encoding.UTF8.GetBytes(source));
    }
}
