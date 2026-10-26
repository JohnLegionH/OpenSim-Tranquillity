/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The mesh asset stores each texture coordinate in 16 bits over the range of its face, so a face whose coordinates run over many
/// repeats is drawn with a visible error. The merge keeps that error under half a texel of the face's real texture. Each test builds its
/// own parts or archive and shares nothing, so the class runs in parallel with others.
/// </summary>
public class MergeUvPrecisionTests
{
    // ---------------------------------------------------------------- the planner on hand-made parts

    private static MergePart Part(int link, float rangeU, int width = 1024, int height = 1024)
    {
        MergePart p = new MergePart
        {
            PartId = Guid.NewGuid(), LinkNum = link, IsRoot = link == 1, Name = "Part " + link, Creator = "c", Masks = "m", PhysicsShapeType = 0, PhysicsVertices = 8,
            BoxMin = new[] { link * 1f, 0f, 0f }, BoxMax = new[] { link * 1f + 1, 1f, 1f }
        };
        p.Faces.Add(new MergeFace { TextureId = "tex", Vertices = 24, TextureWidth = width, TextureHeight = height, Uv = new UvBounds(0, rangeU, 0, 1) });
        return p;
    }

    private static LinksetPlan Plan(params MergePart[] parts)
    {
        MergeLinkset l = new MergeLinkset { Id = Guid.NewGuid(), Name = "Set", Entry = "objects/set.xml" };
        l.Parts.AddRange(parts);
        return MergePlanner.PlanLinkset(l, new MergeOptions());
    }

    [Fact]
    public void TheErrorOfAFaceIsHalfAStepOf65535OverItsRangeInTexelsOfItsTexture()
    {
        // 100 repeats on a 1024 texture: 100 * 1024 / (2 * 65535)
        Assert.Equal(100.0 * 1024 / 131070, UvPrecision.Texels(new UvBounds(0, 100, 0, 1), 1024, 1024), 6);
        // each direction counts in its own texture size, and the larger one is the figure
        Assert.Equal(100.0 * 64 / 131070, UvPrecision.Texels(new UvBounds(0, 100, 0, 1), 64, 1024), 6);
        Assert.Equal(1.0 * 4096 / 131070, UvPrecision.Texels(new UvBounds(0, 1, 0, 1), 4096, 4), 6);
    }

    [Fact]
    public void AFaceWhoseRangeWouldShowMoreThanHalfATexelDoesNotShareAMeshFaceWithNarrowOnes()
    {
        MergePart narrowA = Part(1, 1), wide = Part(2, 100), narrowB = Part(3, 1);
        LinksetPlan plan = Plan(narrowA, wide, narrowB);
        MergedPartPlan merged = Assert.Single(plan.Merged);
        Assert.DoesNotContain(merged.Members, m => m.PartId == wide.PartId);
        Assert.Contains(plan.Kept, k => k.PartId == wide.PartId);
    }

    [Fact]
    public void AModerateRangeMergesOrNotAccordingToTheSizeOfTheTexture()
    {
        // 40 repeats: 0.31 texel at 1024, 1.25 at 4096, 0.02 at 64
        Assert.Single(Plan(Part(1, 1), Part(2, 40), Part(3, 1)).Merged);
        Assert.Empty(Plan(Part(1, 1, 4096, 4096), Part(2, 40, 4096, 4096), Part(3, 1, 4096, 4096)).Merged);
        Assert.Single(Plan(Part(1, 1, 64, 64), Part(2, 40, 64, 64), Part(3, 1, 64, 64)).Merged);
    }

    [Fact]
    public void APlanarFaceTooWideOnItsOwnKeepsItsPartAndSaysWhy()
    {
        MergePart planar = Part(2, 1);
        planar.Faces[0].UvTooWide = true;
        LinksetPlan plan = Plan(Part(1, 1), planar, Part(3, 1), Part(4, 1));
        KeptPartPlan kept = Assert.Single(plan.Kept, k => k.PartId == planar.PartId);
        Assert.Contains(kept.Rules, r => r.Code == "UV_PRECISION");
    }

    // ---------------------------------------------------------------- the archive: real textures and real faces

    private static byte[] Jp2(int width, int height)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bmp.Erase(new SKColor(10, 20, 30, 255));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] j2k));
        return j2k;
    }

    /// <summary>Five boxes in a row showing one texture; the third has the given repeats (and planar mapping when asked).</summary>
    private static string Archive(Guid texture, byte[] jp2, float repeat, bool planar)
    {
        List<KitPart> parts = Enumerable.Range(1, 5).Select(i => new KitPart("Box " + i, i) { Offset = (i * 2) + ",0,0", Scale = "1,1,1" }).ToList();
        foreach (KitPart p in parts)
        {
            Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(texture));
            if (p == parts[2])
            {
                te.DefaultTexture.RepeatU = repeat;
                te.DefaultTexture.RepeatV = repeat;
                if (planar)
                    te.DefaultTexture.TexMapType = MappingType.Planar;
            }
            p.TextureEntry = Convert.ToBase64String(te.GetBytes());
        }
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        if (jp2 != null)
            assets["assets/" + texture + "_texture.jp2"] = jp2;
        return EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, assets);
    }

    private static MergeWriteResult Merge(string archive)
    {
        string output = Path.Combine(Path.GetTempPath(), "merge-uv-tests", Guid.NewGuid().ToString("N"), "out.oar");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        return MergeArchiveWriter.Write(archive, output);
    }

    [Fact]
    public void ADefaultMappedFaceWithHugeRepeatsIsNotBakedAndKeepsItsRepeatsOnItsOwnFace()
    {
        MergeWriteResult r = Merge(Archive(Guid.NewGuid(), null, 300, planar: false));
        MergedPartReport part = Assert.Single(r.Parts);
        Assert.Equal(5, part.Members);
        Assert.Equal(6, part.UnbakedFaces);
        Assert.Equal(24, part.BakedFaces);
        Assert.True(part.WorstUvTexels <= UvPrecision.MaxTexels, part.WorstUvTexels + " texels");
    }

    [Fact]
    public void APlanarFaceWithHugeRepeatsKeepsItsPartOutOfTheMerge()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(Archive(Guid.NewGuid(), null, 300, planar: true), new MergeOptions());
        LinksetPlan l = Assert.Single(plan.Linksets);
        Assert.Contains(l.Kept, k => k.Name == "Box 3" && k.Rules.Any(x => x.Code == "UV_PRECISION"));
        Assert.DoesNotContain(l.Merged.SelectMany(m => m.Members), m => m.Name == "Box 3");
    }

    [Fact]
    public void ThePlanReadsTheRealSizeOfAWideTextureInTheArchive()
    {
        // 40 repeats across 4096 pixels is 1.25 texels; a default of 1024 would have said 0.31 and baked the face
        MergeWriteResult r = Merge(Archive(Guid.NewGuid(), Jp2(4096, 4), 40, planar: false));
        Assert.Equal(6, Assert.Single(r.Parts).UnbakedFaces);
    }

    [Fact]
    public void ThePlanReadsTheRealSizeOfATinyTextureInTheArchive()
    {
        // 5000 repeats across 8 pixels is 0.15 texel; a default of 1024 would have said 39 and left the face unbaked
        MergeWriteResult r = Merge(Archive(Guid.NewGuid(), Jp2(8, 8), 5000, planar: false));
        MergedPartReport part = Assert.Single(r.Parts);
        Assert.Equal(0, part.UnbakedFaces);
        Assert.True(part.WorstUvTexels <= UvPrecision.MaxTexels);
    }

    [Fact]
    public void TheReportGivesTheWorstCaseInTexelsAndWhatWasMeasuredAfterEncoding()
    {
        MergeWriteResult r = Merge(Archive(Guid.NewGuid(), null, 20, planar: false));
        MergedPartReport part = Assert.Single(r.Parts);
        Assert.True(part.WorstUvTexels > 0 && part.WorstUvTexels <= UvPrecision.MaxTexels, part.WorstUvTexels + " texels");
        Assert.True(part.MeasuredUvTexels <= part.WorstUvTexels + 1e-6, part.MeasuredUvTexels + " measured, bound " + part.WorstUvTexels);
        Assert.Contains("texels", r.ToText());
    }
}
