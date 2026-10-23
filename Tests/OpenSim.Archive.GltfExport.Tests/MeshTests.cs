/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text.Json;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>Mesh parts: the asset format as the viewer reads it, built in code, and the export of the parts.</summary>
public class MeshTests
{
    // ------------------------------------------------------------ building a mesh asset

    public sealed class SubMesh
    {
        public float[] Positions;          // x, y, z per vertex, in [-0.5, 0.5]
        public float[] Normals;            // null for none
        public float[] Uvs;                // null for none, u v per vertex in [0, 1]
        public int[] Indices;
        public bool NoGeometry;
    }

    private static byte[] U16s(IEnumerable<int> values)
    {
        List<byte> b = new List<byte>();
        foreach (int v in values)
        {
            b.Add((byte)(v & 0xFF));
            b.Add((byte)(v >> 8));
        }
        return b.ToArray();
    }

    private static int Q(float v, float min, float max) => (int)Math.Round((v - min) / (max - min) * 65535f);

    private static OSDArray Level(IEnumerable<SubMesh> subs)
    {
        OSDArray array = new OSDArray();
        foreach (SubMesh s in subs)
        {
            OSDMap m = new OSDMap();
            if (s.NoGeometry)
            {
                m["NoGeometry"] = OSD.FromBoolean(true);
                array.Add(m);
                continue;
            }
            m["Position"] = OSD.FromBinary(U16s(s.Positions.Select(p => Q(p, -0.5f, 0.5f))));
            m["PositionDomain"] = new OSDMap { ["Min"] = new OSDArray { OSD.FromReal(-0.5), OSD.FromReal(-0.5), OSD.FromReal(-0.5) }, ["Max"] = new OSDArray { OSD.FromReal(0.5), OSD.FromReal(0.5), OSD.FromReal(0.5) } };
            if (s.Normals != null)
                m["Normal"] = OSD.FromBinary(U16s(s.Normals.Select(n => Q(n, -1f, 1f))));
            if (s.Uvs != null)
            {
                m["TexCoord0"] = OSD.FromBinary(U16s(s.Uvs.Select(t => Q(t, 0f, 1f))));
                m["TexCoord0Domain"] = new OSDMap { ["Min"] = new OSDArray { OSD.FromReal(0), OSD.FromReal(0) }, ["Max"] = new OSDArray { OSD.FromReal(1), OSD.FromReal(1) } };
            }
            m["TriangleList"] = OSD.FromBinary(U16s(s.Indices));
            array.Add(m);
        }
        return array;
    }

    private static byte[] Zip(OSD value)
    {
        byte[] raw = OSDParser.SerializeLLSDBinary(value, false);
        using MemoryStream output = new MemoryStream();
        using (ZLibStream z = new ZLibStream(output, CompressionLevel.Optimal, true))
            z.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    /// <summary>A mesh asset: a header naming each block by offset and size, then the zlib-compressed blocks.</summary>
    public static byte[] Asset(IEnumerable<SubMesh> high, IEnumerable<SubMesh> low = null, string[] joints = null, bool physics = false, float[] bindShape = null)
    {
        List<(string name, byte[] data)> blocks = new List<(string, byte[])>();
        if (low != null) blocks.Add(("low_lod", Zip(Level(low))));
        blocks.Add(("high_lod", Zip(Level(high))));
        if (joints != null)
        {
            OSDArray names = new OSDArray();
            foreach (string j in joints) names.Add(OSD.FromString(j));
            OSDArray bind = new OSDArray();
            for (int i = 0; i < 16; i++) bind.Add(OSD.FromReal(bindShape != null ? bindShape[i] : i % 5 == 0 ? 1.0 : 0.0));
            OSDMap skin = new OSDMap { ["joint_names"] = names, ["bind_shape_matrix"] = bind, ["inverse_bind_matrix"] = new OSDArray { bind, bind } };
            blocks.Add(("skin", Zip(skin)));
        }
        if (physics) blocks.Add(("physics_convex", Zip(new OSDMap { ["HullList"] = new OSDBinary(new byte[] { 1 }) })));

        // integers are fixed-width in the binary form, so a header with placeholder offsets is as long as the real one
        OSDMap header = new OSDMap();
        foreach ((string name, byte[] data) in blocks)
            header[name] = new OSDMap { ["offset"] = OSD.FromInteger(0), ["size"] = OSD.FromInteger(data.Length) };
        int headerLength = OSDParser.SerializeLLSDBinary(header, true).Length;

        int offset = 0;
        foreach ((string name, byte[] data) in blocks)
        {
            header[name] = new OSDMap { ["offset"] = OSD.FromInteger(offset), ["size"] = OSD.FromInteger(data.Length) };
            offset += data.Length;
        }
        byte[] headerBytes = OSDParser.SerializeLLSDBinary(header, true);
        Assert.Equal(headerLength, headerBytes.Length);
        using MemoryStream ms = new MemoryStream();
        ms.Write(headerBytes, 0, headerBytes.Length);
        foreach ((_, byte[] data) in blocks)
            ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    internal static SubMesh Triangle(float z = 0f, bool normals = true, bool uvs = true) => new SubMesh
    {
        Positions = new[] { -0.5f, -0.5f, z, 0.5f, -0.5f, z, 0f, 0.5f, z },
        Normals = normals ? new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f } : null,
        Uvs = uvs ? new[] { 0f, 0f, 1f, 0f, 0.5f, 1f } : null,
        Indices = new[] { 0, 1, 2 }
    };

    // ------------------------------------------------------------ the decoder

    [Fact]
    public void TheDecoderReadsPositionsNormalsTextureCoordinatesAndIndices()
    {
        Assert.True(MeshAsset.TryDecode(Asset(new[] { Triangle() }), out MeshModel model, out string problem), problem);
        MeshFace f = Assert.Single(model.Faces);
        Assert.Equal(0, f.Number);
        float[] expected = { -0.5f, -0.5f, 0f, 0.5f, -0.5f, 0f, 0f, 0.5f, 0f };
        for (int i = 0; i < 9; i++)
            Assert.Equal(expected[i], f.Positions[i], 4);
        Assert.Equal(1f, f.Normals[2], 4);
        Assert.Equal(new[] { 0f, 0f, 1f, 0f, 0.5f, 1f }, f.Uvs.Select(v => (float)Math.Round(v, 3)).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, f.Indices);
        Assert.True(f.HasNormals && f.HasUvs);
    }

    [Fact]
    public void TheDecoderUsesTheDomainsTheAssetStates()
    {
        // positions between 0 and 4, texture coordinates between -1 and 3
        OSDMap sub = new OSDMap
        {
            ["Position"] = OSD.FromBinary(U16s(new[] { 0, 0, 0, 65535, 0, 0, 0, 65535, 0 })),
            ["PositionDomain"] = new OSDMap { ["Min"] = new OSDArray { OSD.FromReal(0), OSD.FromReal(0), OSD.FromReal(0) }, ["Max"] = new OSDArray { OSD.FromReal(4), OSD.FromReal(4), OSD.FromReal(4) } },
            ["TexCoord0"] = OSD.FromBinary(U16s(new[] { 0, 0, 65535, 0, 0, 65535 })),
            ["TexCoord0Domain"] = new OSDMap { ["Min"] = new OSDArray { OSD.FromReal(-1), OSD.FromReal(-1) }, ["Max"] = new OSDArray { OSD.FromReal(3), OSD.FromReal(3) } },
            ["TriangleList"] = OSD.FromBinary(U16s(new[] { 0, 1, 2 }))
        };
        byte[] block = Zip(new OSDArray { sub });
        OSDMap header = new OSDMap { ["high_lod"] = new OSDMap { ["offset"] = OSD.FromInteger(0), ["size"] = OSD.FromInteger(block.Length) } };
        byte[] head = OSDParser.SerializeLLSDBinary(header, true);
        byte[] asset = head.Concat(block).ToArray();
        Assert.True(MeshAsset.TryDecode(asset, out MeshModel model, out _));
        MeshFace f = model.Faces[0];
        Assert.Equal(new[] { 0f, 0f, 0f, 4f, 0f, 0f, 0f, 4f, 0f }, f.Positions);
        Assert.Equal(new[] { -1f, -1f, 3f, -1f, -1f, 3f }, f.Uvs);
    }

    [Fact]
    public void TheHighestLevelOfDetailIsRead()
    {
        byte[] asset = Asset(new[] { Triangle(), Triangle(0.25f) }, low: new[] { Triangle() });
        Assert.True(MeshAsset.TryDecode(asset, out MeshModel model, out _));
        Assert.Equal(2, model.Faces.Count);
        Assert.Contains("low_lod", model.Blocks);
        Assert.Contains("high_lod", model.Blocks);
    }

    [Fact]
    public void ADamagedAssetIsReportedNotThrown()
    {
        Assert.False(MeshAsset.TryDecode(new byte[] { 1, 2, 3 }, out _, out string p1));
        Assert.False(string.IsNullOrEmpty(p1));
        byte[] good = Asset(new[] { Triangle() });
        byte[] cut = good.Take(good.Length - 10).ToArray();
        Assert.False(MeshAsset.TryDecode(cut, out _, out string p2));
        Assert.False(string.IsNullOrEmpty(p2));
        byte[] noLevel = OSDParser.SerializeLLSDBinary(new OSDMap { ["physics_convex"] = new OSDMap { ["offset"] = OSD.FromInteger(0), ["size"] = OSD.FromInteger(1) } }, true);
        Assert.False(MeshAsset.TryDecode(noLevel, out _, out string p3));
        Assert.Contains("no display", p3);
    }

    // ------------------------------------------------------------ the part in an archive

    private static readonly UUID MeshId = UUID.Parse("a0000000-0000-4000-8000-0000000000d1");

    internal static (ExportSummary summary, Glb glb, JsonElement manifest, string folder) Export(byte[] asset, int sculptType = 5, Vector3? scale = null, Primitive.TextureEntry te = null, bool include = true)
    {
        PartSpec root = new PartSpec
        {
            Name = "Mesh", Scale = scale ?? new Vector3(1, 1, 1),
            Shape = Box(profileCurve: 0, pathCurve: 16, sculptType: sculptType, sculptTexture: MeshId.ToString(), sculptEntry: true),
            TextureEntryBase64 = te == null ? "" : Convert.ToBase64String(te.GetBytes())
        };
        string dir = NewFolder();
        string file = Path.Combine(dir, "mesh.oar");
        File.WriteAllBytes(file, OarWithAssets(new[] { ("mesh", ObjectXml(root)) },
            include ? new[] { (MeshId, (sbyte)AssetType.Mesh, asset) } : Array.Empty<(UUID, sbyte, byte[])>()));
        ExportSummary s = ArchiveExporter.Export(file, new ExportOptions { OutputDirectory = Path.Combine(dir, "out") });
        SummaryBuild b = Assert.Single(s.BuildList);
        string folder = Path.Combine(dir, "out", b.Folder);
        Glb glb = b.File == null ? null : ReadGlb(File.ReadAllBytes(Path.Combine(folder, b.File)));
        return (s, glb, JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))).RootElement, folder);
    }

    private static JsonElement PrimitiveOfFace(Glb glb, int face)
    {
        foreach (JsonElement prim in glb.Root.GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray())
            if (prim.GetProperty("extras").GetProperty("primFace").GetInt32() == face)
                return prim;
        throw new InvalidOperationException("no face " + face);
    }

    private static float[] Attribute(Glb glb, JsonElement prim, string name) => glb.Floats(prim.GetProperty("attributes").GetProperty(name).GetInt32());

    [Fact]
    public void ASingleFaceMeshIsExportedWithItsOriginalAssetCopiedBesideTheFile()
    {
        byte[] asset = Asset(new[] { Triangle() }, low: new[] { Triangle() }, physics: true);
        (ExportSummary s, Glb glb, JsonElement manifest, string folder) = Export(asset, scale: new Vector3(2, 4, 6));
        Assert.Equal((1, 0, 1, 0), (s.Exported, s.Placeheld, s.MeshesExported, s.RiggedMeshes));

        JsonElement prim = PrimitiveOfFace(glb, 0);
        // size scales the stored unit shape, and y and z swap into glTF's axes: (x, y, z) -> (x, z, -y)
        float[] pos = Attribute(glb, prim, "POSITION");
        Assert.Equal(new[] { -1f, 0f, 2f, 1f, 0f, 2f, 0f, 0f, -2f }.Select(v => v == 0 ? 0f : v).ToArray(), pos.Select(v => (float)Math.Round(v, 3)).Select(v => v == 0 ? 0f : v).ToArray());
        // the asset's v is up; the file's is down
        float[] uv = Attribute(glb, prim, "TEXCOORD_0");
        Assert.Equal(new[] { 0f, 1f, 1f, 1f, 0.5f, 0f }, uv.Select(v => (float)Math.Round(v, 3)).ToArray());

        // the asset, byte for byte, in the assets folder; lower levels and physics stay inside it
        Assert.Equal(asset, File.ReadAllBytes(Path.Combine(folder, "assets", MeshId + ".llmesh")));
        Assert.Equal(1, glb.Root.GetProperty("meshes").GetArrayLength());
        Assert.Equal(1, s.AssetsCopied);
        JsonElement part = manifest.GetProperty("parts")[0];
        Assert.Equal("mesh", part.GetProperty("kind").GetString());
        Assert.False(part.GetProperty("rigged").GetBoolean());
        JsonElement row = manifest.GetProperty("textures").EnumerateArray().Single(t => t.GetProperty("role").GetString() == "mesh");
        Assert.Equal("assets/" + MeshId + ".llmesh", row.GetProperty("copiedTo").GetString());
        JsonElement extras = glb.Node(1).GetProperty("extras");
        Assert.False(extras.GetProperty("rigged").GetBoolean());
        Assert.Equal("assets/" + MeshId + ".llmesh", extras.GetProperty("geometryAsset").GetProperty("copiedTo").GetString());
    }

    [Fact]
    public void FacesAreNumberedBySubmeshAndAFaceWithNoGeometryKeepsItsNumber()
    {
        byte[] asset = Asset(new[] { Triangle(0f), new SubMesh { NoGeometry = true }, Triangle(0.25f) });
        Primitive.TextureEntry te = new Primitive.TextureEntry(UUID.Parse("a0000000-0000-4000-8000-0000000000e1"));
        Primitive.TextureEntryFace f2 = te.CreateFace(2);
        f2.TextureID = UUID.Parse("a0000000-0000-4000-8000-0000000000e2");
        (ExportSummary s, Glb glb, _, _) = Export(asset, te: te);
        Assert.Equal(1, s.MeshesExported);
        JsonElement mesh = glb.Root.GetProperty("meshes")[0];
        Assert.Equal(new[] { 0, 2 }, mesh.GetProperty("extras").GetProperty("primFaces").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(3, mesh.GetProperty("extras").GetProperty("primFaceCount").GetInt32());

        // the material of face 2 is the texture entry's face 2, as for a prim
        JsonElement m = glb.Root.GetProperty("materials")[PrimitiveOfFace(glb, 2).GetProperty("material").GetInt32()];
        Assert.Equal("a0000000-0000-4000-8000-0000000000e2", m.GetProperty("extras").GetProperty("textureId").GetString());
        JsonElement m0 = glb.Root.GetProperty("materials")[PrimitiveOfFace(glb, 0).GetProperty("material").GetInt32()];
        Assert.Equal("a0000000-0000-4000-8000-0000000000e1", m0.GetProperty("extras").GetProperty("textureId").GetString());
    }

    [Fact]
    public void OnlyTheFirstEightSubmeshesAreFaces()
    {
        byte[] asset = Asset(Enumerable.Range(0, 9).Select(i => Triangle(i * 0.01f)).ToArray());
        (_, Glb glb, _, _) = Export(asset);
        JsonElement mesh = glb.Root.GetProperty("meshes")[0];
        Assert.Equal(8, mesh.GetProperty("primitives").GetArrayLength());
        Assert.Equal(8, mesh.GetProperty("extras").GetProperty("primFaceCount").GetInt32());
        Assert.Equal(1, glb.Node(1).GetProperty("extras").GetProperty("mesh").GetProperty("submeshesBeyondEightFaces").GetInt32());
    }

    [Fact]
    public void AMeshWithoutNormalsGetsFlatOnesAndOneWithoutTextureCoordinatesIsSaid()
    {
        byte[] asset = Asset(new[] { Triangle(normals: false, uvs: false) });
        (ExportSummary s, Glb glb, _, _) = Export(asset);
        Assert.Equal(1, s.MeshesExported);
        float[] n = Attribute(glb, PrimitiveOfFace(glb, 0), "NORMAL");
        // the triangle faces +z in the asset, which is +y in the file's axes... the normal is a unit vector along that face's normal
        Assert.Equal(1f, MathF.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]), 4);
        Assert.Equal(1f, Math.Abs(n[1]), 4);
        JsonElement info = glb.Node(1).GetProperty("extras").GetProperty("mesh");
        Assert.Equal(1, info.GetProperty("facesWithoutNormals").GetInt32());
        Assert.Equal(1, info.GetProperty("facesWithoutTextureCoordinates").GetInt32());
    }

    [Fact]
    public void ARiggedMeshIsExportedStaticAndSaysSoWithItsJointNames()
    {
        string[] joints = { "mPelvis", "mTorso", "mChest" };
        byte[] asset = Asset(new[] { Triangle() }, joints: joints);
        (ExportSummary s, Glb glb, JsonElement manifest, string folder) = Export(asset);
        Assert.Equal((1, 1), (s.MeshesExported, s.RiggedMeshes));
        JsonElement part = manifest.GetProperty("parts")[0];
        Assert.True(part.GetProperty("rigged").GetBoolean());
        Assert.Equal(joints, part.GetProperty("jointNames").EnumerateArray().Select(e => e.GetString()).ToArray());
        JsonElement extras = glb.Node(1).GetProperty("extras");
        Assert.True(extras.GetProperty("rigged").GetBoolean());
        JsonElement skin = extras.GetProperty("skin");
        Assert.Equal(joints, skin.GetProperty("jointNames").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(16, skin.GetProperty("bindShapeMatrix").GetArrayLength());
        Assert.Equal(2, skin.GetProperty("inverseBindMatrices").GetInt32());
        Assert.False(glb.Root.TryGetProperty("skins", out _));
        Assert.True(File.Exists(Path.Combine(folder, "assets", MeshId + ".llmesh")));
        Assert.Equal(1, glb.Root.GetProperty("meshes")[0].GetProperty("primitives").GetArrayLength());
    }

    [Fact]
    public void MirrorAndInvertReverseTheTrianglesAsTheViewerDoes()
    {
        byte[] asset = Asset(new[] { Triangle() });
        (_, Glb plain, _, _) = Export(asset);
        (_, Glb invert, _, _) = Export(asset, sculptType: 5 | SculptGeometry.FlagInvert);
        (_, Glb mirror, _, _) = Export(asset, sculptType: 5 | SculptGeometry.FlagMirror);
        float[] p = Attribute(plain, PrimitiveOfFace(plain, 0), "POSITION");
        float[] i = Attribute(invert, PrimitiveOfFace(invert, 0), "POSITION");
        float[] m = Attribute(mirror, PrimitiveOfFace(mirror, 0), "POSITION");
        // invert: same points, second and third swapped, normals turned around
        Assert.Equal(p.Take(3), i.Take(3));
        Assert.Equal(p.Skip(3).Take(3), i.Skip(6).Take(3));
        Assert.Equal(p.Skip(6).Take(3), i.Skip(3).Take(3));
        Assert.Equal(-Attribute(plain, PrimitiveOfFace(plain, 0), "NORMAL")[1], Attribute(invert, PrimitiveOfFace(invert, 0), "NORMAL")[1], 4);
        // mirror: every point reflected through the origin, triangles reversed, normals reflected
        Assert.Equal(-p[0], m[0], 4);
        Assert.Equal(-p[3], m[6], 4);
        Assert.Equal(-Attribute(plain, PrimitiveOfFace(plain, 0), "NORMAL")[1], Attribute(mirror, PrimitiveOfFace(mirror, 0), "NORMAL")[1], 4);
    }

    [Fact]
    public void AMissingOrDamagedMeshAssetLeavesThePartPlaceheld()
    {
        (ExportSummary s1, Glb glb1, JsonElement manifest1, _) = Export(null, include: false);
        Assert.Equal((0, 1), (s1.Exported, s1.Placeheld));
        Assert.Contains("not in the archive", manifest1.GetProperty("parts")[0].GetProperty("reason").GetString());
        Assert.Equal("pending", glb1.Node(1).GetProperty("extras").GetProperty("geometry").GetString());

        (ExportSummary s2, _, JsonElement manifest2, string folder2) = Export(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 });
        Assert.Equal((0, 1), (s2.Exported, s2.Placeheld));
        Assert.Equal("undecodable", manifest2.GetProperty("parts")[0].GetProperty("geometryAssetStatus").GetString());
        // a damaged asset is still kept: the original is what a restorer may be able to repair
        Assert.True(File.Exists(Path.Combine(folder2, "assets", MeshId + ".llmesh")));
    }
}
