/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The alpha rule read from archives (the viewer's rule is in FaceAlphaTests; more cases are in MergePlanViewerAlphaTests). Each test writes its own archive and shares nothing, so the class runs in parallel with others.
/// </summary>
public class MergePlanAlphaArchiveTests
{
    private static byte[] Jp2(bool translucent)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                bmp.SetPixel(x, y, new SKColor(10, 20, 30, translucent && x == 2 ? (byte)40 : (byte)255));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] j2k));
        return j2k;
    }

    private static string Material(int alphaMode) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><llsd><map><key>DiffuseAlphaMode</key><integer>" + alphaMode + "</integer></map></llsd>";

    private static MergePlan Plan(bool translucent, int? alphaMode)
    {
        Guid texture = Guid.NewGuid(), material = Guid.NewGuid();
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(texture));
        if (alphaMode.HasValue)
            te.DefaultTexture.MaterialID = new UUID(material);
        string teBase64 = Convert.ToBase64String(te.GetBytes());
        List<KitPart> parts = Enumerable.Range(1, 3).Select(i => new KitPart("Box " + i, i) { Offset = (i * 2) + ",0,0", Scale = "1,1,1", TextureEntry = teBase64 }).ToList();
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]> { ["assets/" + texture + "_texture.jp2"] = Jp2(translucent) };
        if (alphaMode.HasValue)
            assets["assets/" + material + "_material.xml"] = Encoding.UTF8.GetBytes(Material(alphaMode.Value));
        return ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, assets), new MergeOptions());
    }

    [Fact]
    public void ATextureWithAnAlphaChannelMakesEveryFaceThatShowsItAlpha()
    {
        MergePlan plan = Plan(translucent: true, alphaMode: null);
        MergedPartPlan m = Assert.Single(plan.Linksets[0].Merged);
        Assert.True(m.Alpha);
        Assert.All(m.Slots, s => Assert.True(s.Alpha));
        Assert.Equal(18, plan.AlphaFaces);
    }

    [Fact]
    public void AnOpaqueTextureMakesNoFaceAlpha()
    {
        MergePlan plan = Plan(translucent: false, alphaMode: null);
        Assert.False(Assert.Single(plan.Linksets[0].Merged).Alpha);
        Assert.Equal(0, plan.AlphaFaces);
    }

    [Fact]
    public void ALegacyMaterialWithAlphaModeNoneOrMaskTurnsTheTextureAlphaOff()
    {
        Assert.Equal(0, Plan(translucent: true, alphaMode: 0).AlphaFaces);
        Assert.Equal(0, Plan(translucent: true, alphaMode: 2).AlphaFaces);
    }

    [Fact]
    public void ALegacyMaterialWithAlphaModeBlendMakesEvenAnOpaqueTextureAlpha()
    {
        Assert.Equal(18, Plan(translucent: false, alphaMode: 1).AlphaFaces);
    }
}
