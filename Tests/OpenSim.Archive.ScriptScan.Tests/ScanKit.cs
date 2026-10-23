/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.ScriptScan.Tests;

internal sealed class KitScript
{
    public string Name;
    public Guid Asset = Guid.NewGuid();
    /// <summary>Null leaves the asset out of the archive.</summary>
    public string Source;
    public int AssetType = 10;
}

internal sealed class KitPart
{
    public Guid Id = Guid.NewGuid();
    public string Name;
    public int LinkNum;
    public List<KitScript> Scripts = new List<KitScript>();
}

/// <summary>Builds small archives in the layout the region server's archiver writes.</summary>
internal static class ScanKit
{
    private const string Owner = "a11ce000-0000-4000-8000-000000000001";

    /// <summary>A build of one root and the given children (link numbers 2, 3, ...).</summary>
    public static List<KitPart> Build(string name, int children)
    {
        List<KitPart> parts = new List<KitPart> { new KitPart { Name = name, LinkNum = 1 } };
        for (int i = 0; i < children; i++)
            parts.Add(new KitPart { Name = name + "-" + (i + 2), LinkNum = i + 2 });
        return parts;
    }

    public static KitScript Script(this KitPart part, string source, string name = "script")
    {
        KitScript s = new KitScript { Name = name, Source = source };
        part.Scripts.Add(s);
        return s;
    }

    private static string PartXml(KitPart p)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<SceneObjectPart xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.Append("<AllowedDrop>false</AllowedDrop><CreatorID><UUID>").Append(Owner).Append("</UUID></CreatorID>");
        sb.Append("<FolderID><UUID>").Append(Guid.Empty).Append("</UUID></FolderID><InventorySerial>1</InventorySerial><TaskInventory>");
        foreach (KitScript s in p.Scripts)
        {
            sb.Append("<TaskInventoryItem><AssetID><UUID>").Append(s.Asset).Append("</UUID></AssetID>");
            sb.Append("<BasePermissions>2147483647</BasePermissions><CreationDate>1700000000</CreationDate>");
            sb.Append("<CreatorID><UUID>").Append(Owner).Append("</UUID></CreatorID><Description>d</Description>");
            sb.Append("<EveryonePermissions>0</EveryonePermissions><Flags>0</Flags><GroupID><UUID>").Append(Guid.Empty).Append("</UUID></GroupID>");
            sb.Append("<GroupPermissions>0</GroupPermissions><InvType>10</InvType><ItemID><UUID>").Append(Guid.NewGuid()).Append("</UUID></ItemID>");
            sb.Append("<OldItemID><UUID>").Append(Guid.Empty).Append("</UUID></OldItemID><LastOwnerID><UUID>").Append(Owner).Append("</UUID></LastOwnerID>");
            sb.Append("<Name>").Append(s.Name).Append("</Name><NextPermissions>581632</NextPermissions><OwnerID><UUID>").Append(Owner).Append("</UUID></OwnerID>");
            sb.Append("<CurrentPermissions>2147483647</CurrentPermissions><ParentID><UUID>").Append(p.Id).Append("</UUID></ParentID>");
            sb.Append("<ParentPartID><UUID>").Append(p.Id).Append("</UUID></ParentPartID><PermsGranter><UUID>").Append(Guid.Empty).Append("</UUID></PermsGranter>");
            sb.Append("<PermsMask>0</PermsMask><Type>").Append(s.AssetType).Append("</Type><OwnerChanged>false</OwnerChanged></TaskInventoryItem>");
        }
        sb.Append("</TaskInventory><UUID><UUID>").Append(p.Id).Append("</UUID></UUID><LocalId>").Append(100 + p.LinkNum).Append("</LocalId>");
        sb.Append("<Name>").Append(p.Name).Append("</Name>");
        sb.Append("<GroupPosition><X>1</X><Y>1</Y><Z>1</Z></GroupPosition><OffsetPosition><X>0</X><Y>0</Y><Z>0</Z></OffsetPosition>");
        sb.Append("<RotationOffset><X>0</X><Y>0</Y><Z>0</Z><W>1</W></RotationOffset><Description></Description><LinkNum>").Append(p.LinkNum).Append("</LinkNum>");
        sb.Append("<Shape><ProfileCurve>1</ProfileCurve><TextureEntry>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</TextureEntry>");
        sb.Append("<PathBegin>0</PathBegin><PathCurve>16</PathCurve><PathEnd>0</PathEnd><PathRadiusOffset>0</PathRadiusOffset><PathRevolutions>0</PathRevolutions>");
        sb.Append("<PathScaleX>100</PathScaleX><PathScaleY>100</PathScaleY><PathShearX>0</PathShearX><PathShearY>0</PathShearY><PathSkew>0</PathSkew>");
        sb.Append("<PathTaperX>0</PathTaperX><PathTaperY>0</PathTaperY><PathTwist>0</PathTwist><PathTwistBegin>0</PathTwistBegin><PCode>9</PCode>");
        sb.Append("<ProfileBegin>0</ProfileBegin><ProfileEnd>0</ProfileEnd><ProfileHollow>0</ProfileHollow><State>0</State>");
        sb.Append("<SculptTexture><UUID>").Append(Guid.Empty).Append("</UUID></SculptTexture><SculptType>0</SculptType><SculptEntry>false</SculptEntry></Shape>");
        sb.Append("<Scale><X>0.5</X><Y>0.5</Y><Z>0.5</Z></Scale><OwnerID><UUID>").Append(Owner).Append("</UUID></OwnerID><GroupID><UUID>").Append(Guid.Empty).Append("</UUID></GroupID>");
        sb.Append("<BaseMask>2147483647</BaseMask><OwnerMask>2147483647</OwnerMask><GroupMask>0</GroupMask><EveryoneMask>0</EveryoneMask><NextOwnerMask>581632</NextOwnerMask>");
        sb.Append("</SceneObjectPart>");
        return sb.ToString();
    }

    private static string BuildXml(List<KitPart> parts)
    {
        StringBuilder sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SceneObjectGroup>\n<RootPart>");
        sb.Append(PartXml(parts[0])).Append("</RootPart><OtherParts>");
        foreach (KitPart p in parts.Skip(1))
            sb.Append(PartXml(p));
        sb.Append("</OtherParts></SceneObjectGroup>");
        return sb.ToString();
    }

    public static string WriteOar(params List<KitPart>[] builds)
    {
        string path = TempFile(".oar");
        using FileStream fs = new FileStream(path, FileMode.CreateNew);
        using (GZipStream gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (TarWriter tar = new TarWriter(gz, leaveOpen: true))
        {
            tar.WriteFile("archive.xml", Encoding.UTF8.GetBytes("<archive major_version=\"0\" minor_version=\"8\"><creation_info><datetime>1700000000</datetime><id>00000000-0000-0000-0000-000000000000</id></creation_info></archive>"));
            tar.WriteDirectory("objects/");
            foreach (List<KitPart> b in builds)
                tar.WriteFile("objects/" + b[0].Name + "_" + b[0].Id + ".xml", Encoding.UTF8.GetBytes(BuildXml(b)));
            tar.WriteDirectory("assets/");
            foreach (List<KitPart> b in builds)
                foreach (KitPart p in b)
                    foreach (KitScript s in p.Scripts.Where(x => x.Source != null))
                        tar.WriteFile("assets/" + s.Asset + (s.AssetType == 10 ? "_script.lsl" : "_bytecode.lso"), Encoding.UTF8.GetBytes(s.Source));
            tar.WriteFile("terrains/terrain.r32", new byte[16]);
        }
        return path;
    }

    public static string TempFile(string suffix)
    {
        string dir = Path.Combine(Path.GetTempPath(), "archive-scriptscan-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + suffix);
    }

    public static PartRow Row(ScanResult r, KitPart p) => r.Parts.Single(x => x.PartId == p.Id);
}
