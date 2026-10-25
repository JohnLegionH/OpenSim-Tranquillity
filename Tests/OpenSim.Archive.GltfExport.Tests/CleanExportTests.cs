/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>The exporter with cleaning on (the default) and off: what the .glb holds, what the stamp says, what stays as it was.</summary>
public class CleanExportTests
{
    private static readonly UUID FixedId = UUID.Parse("c1ea0000-0000-4000-8000-000000000001");

    private static readonly Dictionary<string, string> Shapes = new Dictionary<string, string>
    {
        ["box-hollow-cut"] = Box(hollow: 20000, profileBegin: 6250, profileEnd: 6250),
        ["cylinder-twist-taper"] = Box(profileCurve: 0, twist: 45, twistBegin: -45, taperX: 40, taperY: -30),
        ["prism"] = Box(profileCurve: 3),
        ["sphere"] = Box(profileCurve: 5, pathCurve: 32),
        ["torus-hollow"] = Box(profileCurve: 0, pathCurve: 32, scaleY: 175, hollow: 15000),
        ["tube-cut"] = Box(profileCurve: 1, pathCurve: 32, scaleY: 175, pathBegin: 2000, pathEnd: 2000),
        ["ring"] = Box(profileCurve: 3, pathCurve: 32, scaleY: 175)
    };

    public static IEnumerable<object[]> ShapeNames() => Shapes.Keys.Select(k => new object[] { k });

    private static byte[] BuildGlb(string shape, bool clean)
    {
        PartSpec p = new PartSpec { Name = "Part", Id = FixedId, Scale = new Vector3(2, 3, 4), Shape = Shapes[shape] };
        return LinksetExporter.Build(Group(p), "test.oar", "objects/test.xml", "test.glb", clean: clean).Glb;
    }

    private sealed class Corners
    {
        public float[] P, N, T;
        public int Count => P.Length / 3;
    }

    private static int[] IndicesOf(Glb g, JsonElement prim)
    {
        if (!prim.TryGetProperty("indices", out JsonElement ix))
            return null;
        JsonElement a = g.Root.GetProperty("accessors")[ix.GetInt32()];
        JsonElement v = g.Root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        int offset = v.GetProperty("byteOffset").GetInt32(), count = a.GetProperty("count").GetInt32(), type = a.GetProperty("componentType").GetInt32();
        int[] result = new int[count];
        for (int i = 0; i < count; i++)
            result[i] = type == 5125 ? (int)BitConverter.ToUInt32(g.Bin, offset + i * 4) : type == 5123 ? BitConverter.ToUInt16(g.Bin, offset + i * 2) : g.Bin[offset + i];
        return result;
    }

    /// <summary>The corners of one primitive's triangles in order, whether the primitive is indexed or not.</summary>
    private static Corners CornersOf(Glb g, int meshIndex, int primIndex, out int vertexCount)
    {
        JsonElement prim = g.Root.GetProperty("meshes")[meshIndex].GetProperty("primitives")[primIndex];
        JsonElement at = prim.GetProperty("attributes");
        float[] p = g.Floats(at.GetProperty("POSITION").GetInt32()), n = g.Floats(at.GetProperty("NORMAL").GetInt32()), t = g.Floats(at.GetProperty("TEXCOORD_0").GetInt32());
        vertexCount = p.Length / 3;
        int[] idx = IndicesOf(g, prim) ?? Enumerable.Range(0, vertexCount).ToArray();
        Corners c = new Corners { P = new float[idx.Length * 3], N = new float[idx.Length * 3], T = new float[idx.Length * 2] };
        for (int i = 0; i < idx.Length; i++)
        {
            Array.Copy(p, idx[i] * 3, c.P, i * 3, 3);
            Array.Copy(n, idx[i] * 3, c.N, i * 3, 3);
            Array.Copy(t, idx[i] * 2, c.T, i * 2, 2);
        }
        return c;
    }

    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x + 0f);

    [Theory]
    [InlineData("box-hollow-cut", "f954042312e8a76bd6fc51b5b83b23a6366d861db7b86339cf5521accf6fe002")]
    [InlineData("sphere", "2ef880e0d48724935a41b1578699c77dc641f3196b3c53eb6f5481b43cb69bb6")]
    public void WithCleaningOffTheFileIsByteForByteWhatTheExporterWroteBeforeCleaningExisted(string shape, string expectedSha256)
    {
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(BuildGlb(shape, clean: false))).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void CleaningLeavesTheFacesAndTheirNumbersAsTheyWere(string shape)
    {
        Glb off = ReadGlb(BuildGlb(shape, false)), on = ReadGlb(BuildGlb(shape, true));
        JsonElement a = off.Root.GetProperty("meshes")[0], b = on.Root.GetProperty("meshes")[0];
        Assert.Equal(a.GetProperty("primitives").GetArrayLength(), b.GetProperty("primitives").GetArrayLength());
        Assert.Equal(a.GetProperty("extras").GetRawText(), b.GetProperty("extras").GetRawText());
        for (int i = 0; i < a.GetProperty("primitives").GetArrayLength(); i++)
        {
            Assert.Equal(a.GetProperty("primitives")[i].GetProperty("extras").GetRawText(), b.GetProperty("primitives")[i].GetProperty("extras").GetRawText());
            // the _PRIM_FACE value of every vertex is the face's number
            float face = off.Floats(a.GetProperty("primitives")[i].GetProperty("attributes").GetProperty("_PRIM_FACE").GetInt32())[0];
            Assert.All(on.Floats(b.GetProperty("primitives")[i].GetProperty("attributes").GetProperty("_PRIM_FACE").GetInt32()), f => Assert.Equal(face, f));
        }
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void EverySurvivingTriangleCornerKeepsItsTextureCoordinateBitForBitAndItsPositionAndNormalWithinTheTolerances(string shape)
    {
        Glb off = ReadGlb(BuildGlb(shape, false)), on = ReadGlb(BuildGlb(shape, true));
        int faces = off.Root.GetProperty("meshes")[0].GetProperty("primitives").GetArrayLength();
        int totalBefore = 0, totalAfter = 0;
        for (int f = 0; f < faces; f++)
        {
            Corners before = CornersOf(off, 0, f, out int vb), after = CornersOf(on, 0, f, out int va);
            totalBefore += vb; totalAfter += va;
            int next = 0;
            for (int t = 0; t < after.Count / 3; t++)
            {
                bool found = false;
                while (next < before.Count / 3 && !found)
                {
                    bool all = true;
                    for (int c = 0; c < 3 && all; c++)
                    {
                        int bi = next * 3 + c, ai = t * 3 + c;
                        all = Bits(before.T[bi * 2]) == Bits(after.T[ai * 2]) && Bits(before.T[bi * 2 + 1]) == Bits(after.T[ai * 2 + 1]);
                        for (int k = 0; k < 3 && all; k++)
                            all = Math.Abs(before.P[bi * 3 + k] - after.P[ai * 3 + k]) <= 1e-6f && Math.Abs(before.N[bi * 3 + k] - after.N[ai * 3 + k]) <= 1e-4f;
                    }
                    found = all;
                    next++;
                }
                Assert.True(found, "face " + f + " triangle " + t + " matches no triangle of the uncleaned file");
            }
        }
        Assert.True(totalAfter < totalBefore, "cleaning left " + totalAfter + " vertices of " + totalBefore);
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void ACleanedPrimitiveIsIndexedAndAnUncleanedOneIsNot(string shape)
    {
        Glb off = ReadGlb(BuildGlb(shape, false)), on = ReadGlb(BuildGlb(shape, true));
        foreach (JsonElement prim in off.Root.GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray())
            Assert.False(prim.TryGetProperty("indices", out _));
        foreach (JsonElement prim in on.Root.GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray())
            Assert.True(prim.TryGetProperty("indices", out _));
    }

    [Fact]
    public void TheFileCarriesTheExporterVersionAndWhetherCleaningWasOnAndAnUncleanedFileCarriesNothing()
    {
        JsonElement stamp = ReadGlb(BuildGlb("sphere", true)).Root.GetProperty("asset").GetProperty("extras").GetProperty("oarExport");
        Assert.Equal("0.2", stamp.GetProperty("version").GetString());
        Assert.True(stamp.GetProperty("clean").GetBoolean());
        Assert.False(ReadGlb(BuildGlb("sphere", false)).Root.GetProperty("asset").TryGetProperty("extras", out _));
    }

    [Fact]
    public void TheSameInputGivesTheSameBytesTwice()
    {
        Assert.Equal(BuildGlb("torus-hollow", true), BuildGlb("torus-hollow", true));
    }

    [Fact]
    public void AFaceOfOnlyZeroAreaTrianglesIsKeptAsItWasAndListedInTheManifest()
    {
        // a sculpt map whose points all lie on one line: every triangle has no area
        SculptMap line = SculptTests.Map(16, 16, (c, r) => (c / 15.0 - 0.5, 0.0, 0.0));
        byte[] j2k = SculptTests.J2k(line);
        UUID mapId = UUID.Parse("a0000000-0000-4000-8000-0000000000c1");
        PartSpec root = new PartSpec { Name = "Line", Id = FixedId, Scale = new Vector3(2, 3, 4), Shape = Box(profileCurve: 0, pathCurve: 32, sculptType: SculptGeometry.TypePlane, sculptTexture: mapId.ToString(), sculptEntry: true) };
        string dir = NewFolder();
        string file = Path.Combine(dir, "line.oar");
        File.WriteAllBytes(file, OarWithAssets(new[] { ("line", ObjectXml(root)) }, new[] { (mapId, (sbyte)AssetType.Texture, j2k) }));

        int[] triangles = new int[2];
        JsonElement part = default;
        foreach (bool clean in new[] { false, true })
        {
            ExportSummary s = ArchiveExporter.Export(file, new ExportOptions { SelfContained = true, Clean = clean, OutputDirectory = Path.Combine(dir, clean ? "on" : "off") });
            SummaryBuild b = Assert.Single(s.BuildList);
            string folder = Path.Combine(dir, clean ? "on" : "off", b.Folder);
            Glb glb = ReadGlb(File.ReadAllBytes(Path.Combine(folder, b.File)));
            JsonElement prim = glb.Root.GetProperty("meshes")[0].GetProperty("primitives")[0];
            triangles[clean ? 1 : 0] = CornersOf(glb, 0, 0, out _).Count / 3;
            if (clean)
                part = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))).RootElement.GetProperty("parts")[0].Clone();
        }
        Assert.True(triangles[0] > 0);
        Assert.Equal(triangles[0], triangles[1]);
        Assert.Contains(0, part.GetProperty("uncleanedFaces").EnumerateArray().Select(e => e.GetInt32()));
    }
}
