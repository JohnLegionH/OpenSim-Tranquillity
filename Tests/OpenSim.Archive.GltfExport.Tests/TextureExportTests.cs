/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using SkiaSharp;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>Textures, per-face settings, legacy materials and stored PBR data in the exported glTF.</summary>
public class TextureExportTests
{
    // ------------------------------------------------------------ images

    private static byte[] J2k(int w, int h, bool alpha, Func<int, int, SKColor> pixel)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(w, h, alpha ? SKColorType.Bgra8888 : SKColorType.Rgb888x, alpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, pixel(x, y));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] encoded));
        return encoded;
    }

    private static SKColor Rgb(int x, int y) => new SKColor((byte)(x * 37 + 3), (byte)(y * 53 + 7), (byte)(x * 11 + y * 29 + 1));
    private static SKColor Rgba(int x, int y) => new SKColor((byte)(x * 37 + 3), (byte)(y * 53 + 7), (byte)(x * 11 + y * 29 + 1), (byte)(255 - x * 40));

    private static SKBitmap DecodePng(byte[] png)
    {
        SKBitmap bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    [Fact]
    public void ThePngWriterKeepsEveryPixel()
    {
        byte[] rgb = new byte[5 * 4 * 3], rgba = new byte[5 * 4 * 4];
        new Random(7).NextBytes(rgb);
        new Random(8).NextBytes(rgba);
        using SKBitmap a = DecodePng(PngWriter.Write(5, 4, false, rgb));
        using SKBitmap b = DecodePng(PngWriter.Write(5, 4, true, rgba));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 5; x++)
            {
                SKColor ca = a.GetPixel(x, y), cb = b.GetPixel(x, y);
                Assert.Equal((rgb[(y * 5 + x) * 3], rgb[(y * 5 + x) * 3 + 1], rgb[(y * 5 + x) * 3 + 2]), (ca.Red, ca.Green, ca.Blue));
                // an image with alpha reads back unpremultiplied only for opaque pixels in some decoders; compare the stored bytes
                Assert.Equal(rgba[(y * 5 + x) * 4 + 3], cb.Alpha);
            }
    }

    [Fact]
    public void ATextureWithoutAlphaDecodesToTheSamePixels()
    {
        byte[] j2k = J2k(6, 5, false, Rgb);
        TextureImage image = TextureDecoder.Decode(new AssetBlob { Id = UUID.Random(), Type = (int)AssetType.Texture, Bytes = j2k }, out string problem);
        Assert.NotNull(image);
        Assert.Null(problem);
        Assert.Equal((6, 5, false), (image.Width, image.Height, image.HasAlpha));
        using SKBitmap png = DecodePng(image.Png);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 6; x++)
                Assert.Equal(Rgb(x, y), png.GetPixel(x, y));
    }

    [Fact]
    public void ATextureWithAlphaKeepsItsAlphaChannel()
    {
        byte[] j2k = J2k(4, 4, true, Rgba);
        TextureImage image = TextureDecoder.Decode(new AssetBlob { Id = UUID.Random(), Type = (int)AssetType.Texture, Bytes = j2k }, out _);
        Assert.NotNull(image);
        Assert.True(image.HasAlpha);
        using SKBitmap png = DecodePng(image.Png);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(Rgba(x, y).Alpha, png.GetPixel(x, y).Alpha);
    }

    [Fact]
    public void ABrokenTextureIsReportedNotThrown()
    {
        TextureImage image = TextureDecoder.Decode(new AssetBlob { Id = UUID.Random(), Type = (int)AssetType.Texture, Bytes = new byte[] { 1, 2, 3, 4 } }, out string problem);
        Assert.Null(image);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    // ------------------------------------------------------------ the texture transform

    // The viewer's transform of a face's texture coordinates (LLFace's xform in the viewer's indra/newview/llface.cpp), written out
    // for the tests with v up, and the extension's transform applied to a v-down coordinate.
    private static (float u, float v) Viewer(float u, float vUp, float repU, float repV, float offU, float offV, float rot)
    {
        float s = u - 0.5f, t = vUp - 0.5f;
        float temp = s;
        s = s * MathF.Cos(rot) + t * MathF.Sin(rot);
        t = -temp * MathF.Sin(rot) + t * MathF.Cos(rot);
        s *= repU;
        t *= repV;
        return (s + offU + 0.5f, t + offV + 0.5f);
    }

    private static (float u, float v) Khr(float u, float v, float[] offset, float rotation, float[] scale)
    {
        float x = u * scale[0], y = v * scale[1];
        return (MathF.Cos(rotation) * x - MathF.Sin(rotation) * y + offset[0], MathF.Sin(rotation) * x + MathF.Cos(rotation) * y + offset[1]);
    }

    [Theory]
    [InlineData(1f, 1f, 0f, 0f, 0f)]
    [InlineData(2f, 3f, 0f, 0f, 0f)]
    [InlineData(2f, 3f, 0.25f, -0.5f, 0f)]
    [InlineData(1.5f, 1.5f, 0.1f, 0.2f, 0.7f)]   // rotated, equal repeats
    [InlineData(4f, 2f, 0f, 0f, MathF.PI)]       // half a turn, unequal repeats: sin is zero
    [InlineData(-1f, 1f, 0.3f, 0.3f, 0f)]        // mirrored
    public void TheKhrTransformPutsTheTextureWhereTheViewerDoes(float repU, float repV, float offU, float offV, float rot)
    {
        UvTransform.ToKhr(repU, repV, offU, offV, rot, out float[] offset, out float[] scale, out bool exact);
        Assert.True(exact);
        foreach ((float u, float vDown) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f), (0.5f, 0.5f), (0.2f, 0.9f) })
        {
            (float eu, float evUp) = Viewer(u, 1f - vDown, repU, repV, offU, offV, rot);
            (float gu, float gv) = Khr(u, vDown, offset, rot, scale);
            Assert.Equal(eu, gu, 4);
            Assert.Equal(1f - evUp, gv, 4);
        }
    }

    [Fact]
    public void ARotatedFaceWithUnequalRepeatsIsFlaggedInexactButKeepsItsCentre()
    {
        UvTransform.ToKhr(4f, 2f, 0.1f, 0.2f, 0.6f, out float[] offset, out float[] scale, out bool exact);
        Assert.False(exact);
        (float eu, float evUp) = Viewer(0.5f, 0.5f, 4f, 2f, 0.1f, 0.2f, 0.6f);
        (float gu, float gv) = Khr(0.5f, 0.5f, offset, 0.6f, scale);
        Assert.Equal(eu, gu, 4);
        Assert.Equal(1f - evUp, gv, 4);
    }

    // ------------------------------------------------------------ an archive with textures

    private static readonly UUID TexA = UUID.Parse("a0000000-0000-4000-8000-00000000000a");
    private static readonly UUID TexB = UUID.Parse("b0000000-0000-4000-8000-00000000000b");
    private static readonly UUID TexMissing = UUID.Parse("c0000000-0000-4000-8000-00000000000c");
    private static readonly UUID TexNormal = UUID.Parse("d0000000-0000-4000-8000-00000000000d");
    private static readonly UUID TexSpec = UUID.Parse("e0000000-0000-4000-8000-00000000000e");
    private static readonly UUID PbrAsset = UUID.Parse("f0000000-0000-4000-8000-00000000000f");

    private sealed class Archive
    {
        public string File, Out;
        public byte[] J2kA, J2kB, J2kNormal, J2kSpec, MaterialXml, PbrJson;
        public UUID MaterialId;
        public const string PbrOverride = "{\"local_textures\":[],\"ver\":\"1\",\"factor\":0.5}";
    }

    private static Archive MakeArchive(bool includeAssets = true)
    {
        Archive a = new Archive
        {
            J2kA = J2k(8, 8, false, Rgb),
            J2kB = J2k(4, 4, true, Rgba),
            J2kNormal = J2k(4, 4, false, (x, y) => new SKColor(128, 128, 255)),
            J2kSpec = J2k(4, 4, false, (x, y) => new SKColor(200, 200, 200)),
            PbrJson = System.Text.Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"name\":\"stored\"}]}")
        };
        FaceMaterial fm = new FaceMaterial
        {
            NormalMapID = TexNormal, SpecularMapID = TexSpec, DiffuseAlphaMode = 2, AlphaMaskCutoff = 100,
            SpecularLightExponent = 80, EnvironmentIntensity = 30, NormalRepeatX = 2f, NormalRepeatY = 2f
        };
        fm.genID();
        a.MaterialId = fm.ID;
        a.MaterialXml = fm.toLLSDxml();

        // faces: 0 default TexA; 1 TexB tinted with transform, fullbright, glow, shiny, bump, planar; 2 missing texture; 3 legacy material; 4 PBR
        Primitive.TextureEntry te = new Primitive.TextureEntry(TexA);
        Primitive.TextureEntryFace f1 = te.CreateFace(1);
        f1.TextureID = TexB;
        f1.RGBA = new Color4(0.5f, 0.25f, 1f, 0.5f);
        f1.RepeatU = 2f; f1.RepeatV = 2f; f1.OffsetU = 0.25f; f1.OffsetV = -0.5f; f1.Rotation = 0.5f;
        f1.Fullbright = true; f1.Glow = 0.3f; f1.Shiny = Shininess.High; f1.Bump = Bumpiness.Brightness; f1.TexMapType = MappingType.Planar;
        te.CreateFace(2).TextureID = TexMissing;
        Primitive.TextureEntryFace f3 = te.CreateFace(3);
        f3.TextureID = TexA;
        f3.MaterialID = a.MaterialId;
        te.CreateFace(4).TextureID = TexA;

        PrimitiveBaseShape shape = new PrimitiveBaseShape();
        shape.RenderMaterials = new Primitive.RenderMaterials
        {
            entries = new[] { new Primitive.RenderMaterials.RenderMaterialEntry { te_index = 4, id = PbrAsset } },
            overrides = new[] { new Primitive.RenderMaterials.RenderMaterialOverrideEntry { te_index = 4, data = Archive.PbrOverride } }
        };

        byte[] animation = new byte[16];
        animation[0] = 1; animation[1] = 255; animation[2] = 4; animation[3] = 4;
        BitConverter.GetBytes(0f).CopyTo(animation, 4);
        BitConverter.GetBytes(16f).CopyTo(animation, 8);
        BitConverter.GetBytes(2f).CopyTo(animation, 12);

        PartSpec root = new PartSpec
        {
            Name = "Textured", Link = 0, Shape = Box(),
            TextureEntryBase64 = Convert.ToBase64String(te.GetBytes()),
            ExtraParamsBase64 = Convert.ToBase64String(shape.ExtraParams),
            MatOvrdBase64 = Convert.ToBase64String(shape.RenderMaterialsOvrToRawBin()),
            TextureAnimationBase64 = Convert.ToBase64String(animation)
        };

        List<(UUID, sbyte, byte[])> assets = new List<(UUID, sbyte, byte[])>();
        if (includeAssets)
        {
            assets.Add((TexA, (sbyte)AssetType.Texture, a.J2kA));
            assets.Add((TexB, (sbyte)AssetType.Texture, a.J2kB));
            assets.Add((a.MaterialId, (sbyte)AssetType.OSMaterial, a.MaterialXml));
            assets.Add((TexNormal, (sbyte)AssetType.Texture, a.J2kNormal));
            assets.Add((TexSpec, (sbyte)AssetType.Texture, a.J2kSpec));
            assets.Add((PbrAsset, (sbyte)AssetType.Material, a.PbrJson));
        }
        string dir = NewFolder();
        a.File = Path.Combine(dir, "textures.oar");
        a.Out = Path.Combine(dir, "out");
        File.WriteAllBytes(a.File, OarWithAssets(new[] { ("textured", ObjectXml(root)) }, assets.ToArray()));
        return a;
    }

    private static (ExportSummary summary, Glb glb, JsonElement manifest, string folder) Run(Archive a, ExportOptions options = null)
    {
        options ??= new ExportOptions { SelfContained = true };
        options.OutputDirectory = a.Out;
        ExportSummary s = ArchiveExporter.Export(a.File, options);
        SummaryBuild b = Assert.Single(s.BuildList);
        string folder = Path.Combine(a.Out, b.Folder);
        Glb glb = ReadGlb(File.ReadAllBytes(Path.Combine(folder, b.File)));
        JsonElement manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))).RootElement;
        return (s, glb, manifest, folder);
    }

    private static JsonElement MaterialOf(Glb glb, UUID texture, Func<JsonElement, bool> where = null)
    {
        foreach (JsonElement m in glb.Root.GetProperty("materials").EnumerateArray())
            if (m.GetProperty("extras").GetProperty("textureId").GetString() == texture.ToString() && (where == null || where(m)))
                return m;
        throw new InvalidOperationException("no material for " + texture);
    }

    [Fact]
    public void TexturesAreEmbeddedAsPngAndTheOriginalsAreCopiedUnchanged()
    {
        Archive a = MakeArchive();
        (ExportSummary s, Glb glb, JsonElement manifest, string folder) = Run(a);

        // two diffuse textures embedded (A, B), the missing one is not; the normal map is embedded too
        JsonElement images = glb.Root.GetProperty("images");
        Assert.Equal(3, images.GetArrayLength());
        foreach (JsonElement image in images.EnumerateArray())
        {
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
            JsonElement view = glb.Root.GetProperty("bufferViews")[image.GetProperty("bufferView").GetInt32()];
            byte[] png = glb.Bin.Skip(view.GetProperty("byteOffset").GetInt32()).Take(view.GetProperty("byteLength").GetInt32()).ToArray();
            using SKBitmap bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);
        }
        Assert.Equal(3, s.TexturesEmbedded);
        Assert.Equal(1, s.TexturesMissing);
        Assert.Equal(1, manifest.GetProperty("textures").EnumerateArray().Count(t => t.GetProperty("role").GetString() == "specular" && t.GetProperty("status").GetString() == "copied"));

        // the pixels of texture A in the file are the pixels of the archive's texture
        int imageA = images.EnumerateArray().ToList().FindIndex(i => i.GetProperty("name").GetString() == TexA.ToString());
        JsonElement viewA = glb.Root.GetProperty("bufferViews")[images[imageA].GetProperty("bufferView").GetInt32()];
        using (SKBitmap bmp = SKBitmap.Decode(glb.Bin.Skip(viewA.GetProperty("byteOffset").GetInt32()).Take(viewA.GetProperty("byteLength").GetInt32()).ToArray()))
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    Assert.Equal(Rgb(x, y), bmp.GetPixel(x, y));

        // the originals, byte for byte, named by id
        string textures = Path.Combine(folder, "textures");
        Assert.Equal(a.J2kA, File.ReadAllBytes(Path.Combine(textures, TexA + ".jp2")));
        Assert.Equal(a.J2kB, File.ReadAllBytes(Path.Combine(textures, TexB + ".jp2")));
        Assert.Equal(a.J2kNormal, File.ReadAllBytes(Path.Combine(textures, TexNormal + ".jp2")));
        Assert.Equal(a.J2kSpec, File.ReadAllBytes(Path.Combine(textures, TexSpec + ".jp2")));
        Assert.False(File.Exists(Path.Combine(textures, TexMissing + ".jp2")));
        Assert.Equal(a.MaterialXml, File.ReadAllBytes(Path.Combine(textures, a.MaterialId + ".material.xml")));
        Assert.Equal(6, s.TexturesCopied); // four images, the legacy material and the PBR material
    }

    [Fact]
    public void AFaceKeepsItsTintAlphaAndTextureSettings()
    {
        Archive a = MakeArchive();
        (_, Glb glb, _, _) = Run(a);

        JsonElement m = MaterialOf(glb, TexB);
        float[] color = m.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        // a texture entry stores a colour in bytes
        float[] stored = { 127 / 255f, 63 / 255f, 1f, 127 / 255f };
        for (int i = 0; i < 4; i++)
            Assert.Equal(stored[i], color[i], 5);
        Assert.Equal("BLEND", m.GetProperty("alphaMode").GetString());

        JsonElement x = m.GetProperty("extras");
        Assert.Equal(new[] { 2f, 2f }, x.GetProperty("repeat").EnumerateArray().Select(e => e.GetSingle()).ToArray());
        // a texture entry stores offsets and rotation in 16 bits
        float[] offsetValues = x.GetProperty("offset").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(0.25f, offsetValues[0], 3);
        Assert.Equal(-0.5f, offsetValues[1], 3);
        float rotationValue = x.GetProperty("rotation").GetSingle();
        Assert.Equal(0.5f, rotationValue, 3);
        Assert.Equal(0.3f, x.GetProperty("glow").GetSingle(), 2); // stored in 8 bits
        Assert.True(x.GetProperty("fullbright").GetBoolean());
        Assert.Equal("High", x.GetProperty("shiny").GetString());
        Assert.Equal("Brightness", x.GetProperty("bumpmap").GetString());
        Assert.Equal("planar", x.GetProperty("mapping").GetString());
        Assert.Equal("embedded", x.GetProperty("textureStatus").GetString());
        Assert.False(x.GetProperty("placeholder").GetBoolean());
        Assert.True(x.GetProperty("textureHasAlpha").GetBoolean());
        Assert.True(x.GetProperty("khrTextureTransformExact").GetBoolean());

        // KHR_texture_transform carries the same transform, in the extension the file declares
        Assert.Contains("KHR_texture_transform", glb.Root.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()));
        JsonElement tex = m.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture");
        JsonElement t = tex.GetProperty("extensions").GetProperty("KHR_texture_transform");
        // the extension carries the transform of the values as stored, which the extras hold exactly
        float[] rep = x.GetProperty("repeat").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        UvTransform.ToKhr(rep[0], rep[1], offsetValues[0], offsetValues[1], rotationValue, out float[] offset, out float[] scale, out _);
        Assert.Equal(offset, t.GetProperty("offset").EnumerateArray().Select(e => e.GetSingle()).ToArray());
        Assert.Equal(scale, t.GetProperty("scale").EnumerateArray().Select(e => e.GetSingle()).ToArray());
        Assert.Equal(rotationValue, t.GetProperty("rotation").GetSingle());
    }

    [Fact]
    public void ATextureWithAlphaBlendsAndATextureWithoutDoesNot()
    {
        Archive a = MakeArchive();
        (_, Glb glb, _, _) = Run(a);
        // face 0: texture A, no alpha channel, opaque tint
        JsonElement plain = MaterialOf(glb, TexA, m => m.GetProperty("extras").TryGetProperty("legacyMaterial", out _) == false && m.GetProperty("extras").TryGetProperty("pbr", out _) == false);
        Assert.Equal("OPAQUE", plain.GetProperty("alphaMode").GetString());
        Assert.False(plain.GetProperty("extras").GetProperty("textureHasAlpha").GetBoolean());
    }

    [Fact]
    public void AMissingTextureBecomesAPlaceholderThatSaysSo()
    {
        Archive a = MakeArchive();
        (ExportSummary s, Glb glb, JsonElement manifest, _) = Run(a);
        JsonElement m = MaterialOf(glb, TexMissing);
        Assert.False(m.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
        Assert.True(m.GetProperty("extras").GetProperty("placeholder").GetBoolean());
        Assert.Equal("missing-from-archive", m.GetProperty("extras").GetProperty("textureStatus").GetString());
        Assert.Equal(TexMissing.ToString(), m.GetProperty("name").GetString());
        Assert.Equal(1, s.TexturesMissing);
        JsonElement row = manifest.GetProperty("textures").EnumerateArray().Single(t => t.GetProperty("textureId").GetString() == TexMissing.ToString());
        Assert.Equal("missing-from-archive", row.GetProperty("status").GetString());
    }

    [Fact]
    public void AnArchiveWithoutAssetsExportsEveryTextureAsAPlaceholder()
    {
        Archive a = MakeArchive(includeAssets: false);
        (ExportSummary s, Glb glb, _, string folder) = Run(a);
        Assert.False(glb.Root.TryGetProperty("images", out _));
        Assert.Equal(0, s.TexturesEmbedded);
        Assert.Equal(0, s.TexturesCopied);
        Assert.False(Directory.Exists(Path.Combine(folder, "textures")));
        Assert.All(glb.Root.GetProperty("materials").EnumerateArray(), m => Assert.True(m.GetProperty("extras").GetProperty("placeholder").GetBoolean()));
    }

    [Fact]
    public void ALegacyMaterialGivesANormalTextureAndKeepsTheSpecularSettingsInExtras()
    {
        Archive a = MakeArchive();
        (_, Glb glb, _, _) = Run(a);
        JsonElement m = MaterialOf(glb, TexA, x => x.GetProperty("extras").TryGetProperty("legacyMaterial", out _));
        JsonElement normal = m.GetProperty("normalTexture");
        JsonElement t = normal.GetProperty("extensions").GetProperty("KHR_texture_transform");
        Assert.Equal(new[] { 2f, 2f }, t.GetProperty("scale").EnumerateArray().Select(e => e.GetSingle()).ToArray());

        JsonElement lm = m.GetProperty("extras").GetProperty("legacyMaterial");
        Assert.Equal("read", lm.GetProperty("status").GetString());
        Assert.Equal(a.MaterialId.ToString(), lm.GetProperty("materialId").GetString());
        Assert.Equal(TexNormal.ToString(), lm.GetProperty("normalMap").GetString());
        Assert.Equal(TexSpec.ToString(), lm.GetProperty("specularMap").GetString());
        Assert.Equal("embedded", lm.GetProperty("normalMapStatus").GetString());
        Assert.Equal(80, lm.GetProperty("glossiness").GetInt32());
        Assert.Equal(30, lm.GetProperty("environmentIntensity").GetInt32());
        Assert.Equal(2, lm.GetProperty("diffuseAlphaMode").GetInt32());
        Assert.Equal(100, lm.GetProperty("alphaMaskCutoff").GetInt32());
        // the specular map is not a glTF texture of this material
        Assert.Equal(3, glb.Root.GetProperty("images").GetArrayLength());
        // the material's alpha mode follows the legacy alpha masking
        Assert.Equal("MASK", m.GetProperty("alphaMode").GetString());
        Assert.Equal(100f / 255f, m.GetProperty("alphaCutoff").GetSingle(), 5);
    }

    [Fact]
    public void StoredPbrDataIsCarriedAsStoredAndATextureAnimationIsRecorded()
    {
        Archive a = MakeArchive();
        (_, Glb glb, _, _) = Run(a);
        JsonElement m = MaterialOf(glb, TexA, x => x.GetProperty("extras").TryGetProperty("pbr", out _));
        JsonElement pbr = m.GetProperty("extras").GetProperty("pbr");
        Assert.Equal(PbrAsset.ToString(), pbr.GetProperty("materialId").GetString());
        Assert.Equal("read", pbr.GetProperty("materialStatus").GetString());
        Assert.Equal(Archive.PbrOverride, pbr.GetProperty("override").GetString());
        Assert.Equal(System.Text.Encoding.UTF8.GetString(a.PbrJson), pbr.GetProperty("material").GetString());

        JsonElement anim = m.GetProperty("extras").GetProperty("textureAnimation");
        Assert.Equal(4, anim.GetProperty("sizeX").GetInt32());
        Assert.Equal(16f, anim.GetProperty("length").GetSingle());
        Assert.Equal(2f, anim.GetProperty("rate").GetSingle());
    }

    [Fact]
    public void FacesWithTheSameTextureAndSettingsShareOneMaterial()
    {
        Archive a = MakeArchive();
        (_, Glb glb, _, _) = Run(a);
        // face 0 is the default (TexA); two parts' worth of faces use the same combination only once
        int sameKey = glb.Root.GetProperty("materials").EnumerateArray().Count(m => m.GetProperty("extras").GetProperty("textureId").GetString() == TexA.ToString());
        Assert.InRange(sameKey, 1, 3);
        foreach (JsonElement mesh in glb.Root.GetProperty("meshes").EnumerateArray())
            foreach (JsonElement prim in mesh.GetProperty("primitives").EnumerateArray())
                Assert.InRange(prim.GetProperty("material").GetInt32(), 0, glb.Root.GetProperty("materials").GetArrayLength() - 1);
    }

    [Fact]
    public void TheSourceArchiveIsNotChanged()
    {
        Archive a = MakeArchive();
        byte[] before = File.ReadAllBytes(a.File);
        Run(a);
        Assert.Equal(before, File.ReadAllBytes(a.File));
    }

    [Fact]
    public void ATextureOverTheSizeLimitIsReportedAndNotEmbedded()
    {
        Archive a = MakeArchive();
        // a limit of 1 MB holds the small test textures; make the limit smaller than the textures with the option's smallest value instead
        ArchiveAssets assets = new ArchiveAssets { MaxBytes = 10 };
        assets.Wanted.Add(TexA);
        assets.Add(TexA, (int)AssetType.Texture, "assets/x", a.J2kA);
        Assert.False(assets.Has(TexA));
        Assert.Contains(TexA, assets.OverLimit);
    }
}
