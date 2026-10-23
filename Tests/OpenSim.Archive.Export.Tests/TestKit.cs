/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework.Serialization;

namespace OpenSim.Archive.Export.Tests;

/// <summary>Object XML, archives and glTF reading for the exporter tests.</summary>
internal static class TestKit
{
    public static readonly UUID Alice = UUID.Parse("a11ce000-0000-4000-8000-000000000001");
    public static readonly UUID Bob = UUID.Parse("b0b00000-0000-4000-8000-000000000002");

    public sealed class PartSpec
    {
        public string Name = "Part";
        public string Description = "";
        public UUID Id = UUID.Random();
        public int Link = 0;
        public UUID Creator = Alice;
        public UUID Owner = Alice;
        public Vector3 Offset = Vector3.Zero;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = new Vector3(1, 1, 1);
        public string Shape = Box();
        public string TextureEntryBase64 = "";
        public string Inventory = "";
        public uint BaseMask = 0x7fffffff, OwnerMask = 0x7fffffff, GroupMask = 0, EveryoneMask = 0, NextOwnerMask = 0x82000;
    }

    private static string Vec(string element, Vector3 v) =>
        "<" + element + "><X>" + F(v.X) + "</X><Y>" + F(v.Y) + "</Y><Z>" + F(v.Z) + "</Z></" + element + ">";

    private static string F(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The stored shape fields of a default box.</summary>
    public static string Box(int profileCurve = 1, int pathCurve = 16, int profileBegin = 0, int profileEnd = 0, int hollow = 0, int pathBegin = 0, int pathEnd = 0,
        int scaleX = 100, int scaleY = 100, int twist = 0, int twistBegin = 0, int taperX = 0, int taperY = 0, int shearX = 0, int shearY = 0, int radius = 0, int revolutions = 0, int skew = 0,
        int pcode = 9, int sculptType = 0, string sculptTexture = null, bool sculptEntry = false, int state = 0)
    {
        return "<Shape><ProfileCurve>" + profileCurve + "</ProfileCurve><PathBegin>" + pathBegin + "</PathBegin><PathCurve>" + pathCurve + "</PathCurve><PathEnd>" + pathEnd +
               "</PathEnd><PathRadiusOffset>" + radius + "</PathRadiusOffset><PathRevolutions>" + revolutions + "</PathRevolutions><PathScaleX>" + scaleX + "</PathScaleX><PathScaleY>" + scaleY +
               "</PathScaleY><PathShearX>" + shearX + "</PathShearX><PathShearY>" + shearY + "</PathShearY><PathSkew>" + skew + "</PathSkew><PathTaperX>" + taperX + "</PathTaperX><PathTaperY>" + taperY +
               "</PathTaperY><PathTwist>" + twist + "</PathTwist><PathTwistBegin>" + twistBegin + "</PathTwistBegin><PCode>" + pcode + "</PCode><ProfileBegin>" + profileBegin +
               "</ProfileBegin><ProfileEnd>" + profileEnd + "</ProfileEnd><ProfileHollow>" + hollow + "</ProfileHollow><State>" + state + "</State>" +
               "<SculptTexture><UUID>" + (sculptTexture ?? UUID.Zero.ToString()) + "</UUID></SculptTexture><SculptType>" + sculptType + "</SculptType><SculptEntry>" + (sculptEntry ? "true" : "false") + "</SculptEntry></Shape>";
    }

    public static string PartXml(PartSpec p, bool root)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<SceneObjectPart xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.Append("<CreatorID><UUID>").Append(p.Creator).Append("</UUID></CreatorID>");
        sb.Append("<UUID><UUID>").Append(p.Id).Append("</UUID></UUID>");
        sb.Append("<Name>").Append(System.Security.SecurityElement.Escape(p.Name)).Append("</Name>");
        sb.Append(Vec("GroupPosition", new Vector3(10, 20, 30)));
        sb.Append(Vec("OffsetPosition", p.Offset));
        sb.Append("<RotationOffset><X>").Append(F(p.Rotation.X)).Append("</X><Y>").Append(F(p.Rotation.Y)).Append("</Y><Z>").Append(F(p.Rotation.Z)).Append("</Z><W>").Append(F(p.Rotation.W)).Append("</W></RotationOffset>");
        sb.Append("<Description>").Append(System.Security.SecurityElement.Escape(p.Description)).Append("</Description>");
        sb.Append("<LinkNum>").Append(p.Link).Append("</LinkNum>");
        sb.Append(Vec("Scale", p.Scale));
        sb.Append("<OwnerID><UUID>").Append(p.Owner).Append("</UUID></OwnerID>");
        sb.Append("<BaseMask>").Append(p.BaseMask).Append("</BaseMask><OwnerMask>").Append(p.OwnerMask).Append("</OwnerMask><GroupMask>").Append(p.GroupMask)
          .Append("</GroupMask><EveryoneMask>").Append(p.EveryoneMask).Append("</EveryoneMask><NextOwnerMask>").Append(p.NextOwnerMask).Append("</NextOwnerMask>");
        sb.Append(p.Inventory.Length > 0 ? p.Inventory : "<TaskInventory />");
        string shape = p.Shape;
        if (p.TextureEntryBase64.Length > 0)
            shape = shape.Replace("<Shape>", "<Shape><TextureEntry>" + p.TextureEntryBase64 + "</TextureEntry>");
        sb.Append(shape);
        sb.Append("</SceneObjectPart>");
        _ = root;
        return sb.ToString();
    }

    /// <summary>An object in the xml2 form an OAR holds: the root part first, then the others in the order given.</summary>
    public static string ObjectXml(PartSpec root, params PartSpec[] others)
    {
        StringBuilder sb = new StringBuilder("<SceneObjectGroup>");
        sb.Append(PartXml(root, true));
        sb.Append("<OtherParts>");
        foreach (PartSpec o in others)
            sb.Append(PartXml(o, false));
        sb.Append("</OtherParts></SceneObjectGroup>");
        return sb.ToString();
    }

    public static ParsedGroup Group(PartSpec root, params PartSpec[] others)
    {
        ParsedObject obj = ObjectXmlParser.Parse(Utf8(ObjectXml(root, others)));
        return obj.Groups.Single();
    }

    public static string TaskItem(string name, int assetType, int invType) =>
        "<TaskInventoryItem><AssetID><UUID>" + UUID.Random() + "</UUID></AssetID><Name>" + System.Security.SecurityElement.Escape(name) + "</Name><Type>" + assetType + "</Type><InvType>" + invType +
        "</InvType><ItemID><UUID>" + UUID.Random() + "</UUID></ItemID></TaskInventoryItem>";

    public static string TextureEntryBase64(UUID defaultTexture, params (int face, UUID texture, Color4 color)[] faces)
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(defaultTexture);
        foreach ((int face, UUID texture, Color4 color) f in faces)
        {
            Primitive.TextureEntryFace tef = te.CreateFace((uint)f.face);
            tef.TextureID = f.texture;
            tef.RGBA = f.color;
        }
        return Convert.ToBase64String(te.GetBytes());
    }

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

    public static byte[] Oar(params (string objectName, string xml)[] objects)
    {
        List<(string, byte[])> entries = new List<(string, byte[])> { ("archive.xml", OarControl()) };
        foreach ((string objectName, string xml) in objects)
            entries.Add(("objects/" + objectName + ".xml", Utf8(xml)));
        return Tar(entries);
    }

    public static string NewFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oar-export-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------------------------------------------------------- reading a glb back

    public sealed class Glb
    {
        public JsonDocument Json;
        public byte[] Bin;

        public JsonElement Root => Json.RootElement;
        public JsonElement Node(int i) => Root.GetProperty("nodes")[i];
        public JsonElement ObjectNode => Node(0);
        public IEnumerable<JsonElement> PartNodes => ObjectNode.GetProperty("children").EnumerateArray().Select(c => Node(c.GetInt32()));

        public float[] Floats(int accessor)
        {
            JsonElement a = Root.GetProperty("accessors")[accessor];
            JsonElement v = Root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
            int offset = v.GetProperty("byteOffset").GetInt32();
            int comps = a.GetProperty("type").GetString() == "VEC3" ? 3 : 2;
            float[] result = new float[a.GetProperty("count").GetInt32() * comps];
            Buffer.BlockCopy(Bin, offset, result, 0, result.Length * 4);
            return result;
        }
    }

    public static Glb ReadGlb(byte[] glb)
    {
        using MemoryStream ms = new MemoryStream(glb);
        using BinaryReader r = new BinaryReader(ms);
        if (r.ReadUInt32() != 0x46546C67u) throw new InvalidDataException("not a glb");
        if (r.ReadUInt32() != 2u) throw new InvalidDataException("not glTF 2");
        uint total = r.ReadUInt32();
        if (total != glb.Length) throw new InvalidDataException("length field " + total + " differs from " + glb.Length);
        uint jsonLen = r.ReadUInt32();
        if (r.ReadUInt32() != 0x4E4F534Au) throw new InvalidDataException("first chunk is not JSON");
        if (jsonLen % 4 != 0) throw new InvalidDataException("JSON chunk not aligned");
        JsonDocument doc = JsonDocument.Parse(r.ReadBytes((int)jsonLen));
        byte[] bin = Array.Empty<byte>();
        if (ms.Position < glb.Length)
        {
            uint binLen = r.ReadUInt32();
            if (r.ReadUInt32() != 0x004E4942u) throw new InvalidDataException("second chunk is not BIN");
            if (binLen % 4 != 0) throw new InvalidDataException("BIN chunk not aligned");
            bin = r.ReadBytes((int)binLen);
        }
        return new Glb { Json = doc, Bin = bin };
    }
}
