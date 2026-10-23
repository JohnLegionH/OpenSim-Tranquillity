/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>A face whose texture has a rotation and unequal repeats has the viewer's transform baked into its texture coordinates.</summary>
public class BakedUvTests
{
    private static readonly UUID Tex = UUID.Parse("a0000000-0000-4000-8000-0000000000a1");

    private static string Export(float repeatU, float repeatV, float rotation)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgb888x, SKAlphaType.Opaque));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] j2k));

        Primitive.TextureEntry te = new Primitive.TextureEntry(Tex);
        Primitive.TextureEntryFace f = te.CreateFace(1);
        f.TextureID = Tex;
        f.RepeatU = repeatU; f.RepeatV = repeatV; f.OffsetU = 0.1f; f.OffsetV = -0.2f; f.Rotation = rotation;

        PartSpec root = new PartSpec { Name = "Baked", Shape = Box(), TextureEntryBase64 = Convert.ToBase64String(te.GetBytes()) };
        string dir = NewFolder();
        string file = Path.Combine(dir, "baked.oar");
        File.WriteAllBytes(file, OarWithAssets(new[] { ("baked", ObjectXml(root)) }, new[] { (Tex, (sbyte)AssetType.Texture, j2k) }));
        ExportSummary s = ArchiveExporter.Export(file, new ExportOptions { SelfContained = true, OutputDirectory = Path.Combine(dir, "out") });
        SummaryBuild b = Assert.Single(s.BuildList);
        return Path.Combine(dir, "out", b.Folder, b.File);
    }

    private static JsonElement PrimitiveOfFace(Glb glb, int face)
    {
        foreach (JsonElement mesh in glb.Root.GetProperty("meshes").EnumerateArray())
            foreach (JsonElement prim in mesh.GetProperty("primitives").EnumerateArray())
                if (prim.GetProperty("extras").GetProperty("primFace").GetInt32() == face)
                    return prim;
        throw new InvalidOperationException("no face " + face);
    }

    private static float[] UvsOfFace(Glb glb, int face) =>
        glb.Floats(PrimitiveOfFace(glb, face).GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32());

    private static JsonElement MaterialOfFace(Glb glb, int face) =>
        glb.Root.GetProperty("materials")[PrimitiveOfFace(glb, face).GetProperty("material").GetInt32()];

    // The viewer's rule written out (LLFace's xform in indra/newview/llface.cpp), v up.
    private static (float u, float v) Viewer(float u, float vUp, float repU, float repV, float offU, float offV, float rot)
    {
        float s = u - 0.5f, t = vUp - 0.5f;
        float temp = s;
        s = s * MathF.Cos(rot) + t * MathF.Sin(rot);
        t = -temp * MathF.Sin(rot) + t * MathF.Cos(rot);
        return (s * repU + offU + 0.5f, t * repV + offV + 0.5f);
    }

    [Fact]
    public void ARotatedFaceWithUnequalRepeatsHasTheViewersTransformInItsCoordinates()
    {
        // the same shape with no transform gives the raw coordinates
        Glb control = ReadGlb(File.ReadAllBytes(Export(1f, 1f, 0f)));
        Glb baked = ReadGlb(File.ReadAllBytes(Export(4f, 2f, 0.6f)));

        float[] raw = UvsOfFace(control, 1), got = UvsOfFace(baked, 1);
        Assert.Equal(raw.Length, got.Length);
        // the stored offset and rotation are 16-bit steps, so use the values the baked face's extras report
        JsonElement x = MaterialOfFace(baked, 1).GetProperty("extras");
        float[] rep = x.GetProperty("repeat").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        float[] off = x.GetProperty("offset").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        float rot = x.GetProperty("rotation").GetSingle();
        for (int i = 0; i < raw.Length; i += 2)
        {
            (float eu, float ev) = Viewer(raw[i], 1f - raw[i + 1], rep[0], rep[1], off[0], off[1], rot);
            Assert.Equal(eu, got[i], 4);
            Assert.Equal(1f - ev, got[i + 1], 4);
        }
        Assert.NotEqual(raw, got);
    }

    [Fact]
    public void ABakedFaceHasAnIdentityExtensionAndKeepsEverySlValueInExtras()
    {
        Glb glb = ReadGlb(File.ReadAllBytes(Export(4f, 2f, 0.6f)));
        JsonElement m = MaterialOfFace(glb, 1);
        JsonElement t = m.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture").GetProperty("extensions").GetProperty("KHR_texture_transform");
        Assert.Equal(new[] { 0f, 0f }, t.GetProperty("offset").EnumerateArray().Select(e => e.GetSingle()).ToArray());
        Assert.Equal(new[] { 1f, 1f }, t.GetProperty("scale").EnumerateArray().Select(e => e.GetSingle()).ToArray());
        Assert.Equal(0f, t.GetProperty("rotation").GetSingle());

        JsonElement x = m.GetProperty("extras");
        Assert.True(x.GetProperty("uvTransformBaked").GetBoolean());
        Assert.Equal(new[] { 4f, 2f }, x.GetProperty("repeat").EnumerateArray().Select(e => e.GetSingle()).ToArray());
        Assert.Equal(0.6f, x.GetProperty("rotation").GetSingle(), 3);
        Assert.Equal(0.1f, x.GetProperty("offset")[0].GetSingle(), 3);
        Assert.Equal(-0.2f, x.GetProperty("offset")[1].GetSingle(), 3);
    }

    [Fact]
    public void AFaceTheExtensionCanDescribeKeepsUsingTheExtension()
    {
        string equal = Export(2f, 2f, 0.6f);      // rotated, equal repeats
        string unrotated = Export(4f, 2f, 0f);    // unequal repeats, no rotation
        foreach (string path in new[] { equal, unrotated })
        {
            Glb glb = ReadGlb(File.ReadAllBytes(path));
            JsonElement x = MaterialOfFace(glb, 1).GetProperty("extras");
            Assert.False(x.TryGetProperty("uvTransformBaked", out _));
            Assert.True(x.GetProperty("khrTextureTransformExact").GetBoolean());
        }
        Glb scaled = ReadGlb(File.ReadAllBytes(unrotated));
        JsonElement t = MaterialOfFace(scaled, 1).GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture").GetProperty("extensions").GetProperty("KHR_texture_transform");
        Assert.Equal(new[] { 4f, 2f }, t.GetProperty("scale").EnumerateArray().Select(e => e.GetSingle()).ToArray());
    }

    [Fact]
    public void BakeOnItsOwnMatchesTheViewersRule()
    {
        float[] uv = { 0f, 0f, 1f, 0f, 0.25f, 0.75f };
        float[] copy = (float[])uv.Clone();
        UvTransform.Bake(copy, 3f, 0.5f, 0.2f, 0.3f, -1.1f);
        for (int i = 0; i < uv.Length; i += 2)
        {
            (float eu, float ev) = Viewer(uv[i], 1f - uv[i + 1], 3f, 0.5f, 0.2f, 0.3f, -1.1f);
            Assert.Equal(eu, copy[i], 5);
            Assert.Equal(1f - ev, copy[i + 1], 5);
        }
    }
}
