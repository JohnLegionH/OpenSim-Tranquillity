/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using Xunit;
using Vector3 = System.Numerics.Vector3;
using static OpenSim.Archive.Converter.Tests.ProofKit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Faces that show media (MediaFixture). A part holds one media entry for each face in its Media element, by face number, and marks the
/// faces in its texture entry; a merged part carries the entry of each face to the merged face it becomes. The viewer draws a media face's
/// coordinates as it draws any other face's (llface.cpp getGeometryVolume has no special case for media), so the faces bake as usual.
/// Each test builds its own archive and shares no state, so the class runs in parallel.
/// </summary>
public class MergeMediaFaceTests
{
    private static List<MediaEntry> MediaOf(ParsedPart pp) =>
        pp.MediaXml.Length == 0 ? new List<MediaEntry>() : (PrimitiveBaseShape.MediaList.FromXml(pp.MediaXml) ?? new PrimitiveBaseShape.MediaList());

    // the region reads an empty place of a media list back as a default entry, so a default entry and no entry are the same thing
    private static readonly string DefaultEntry = OSDParser.SerializeJsonString(new MediaEntry().GetOSD());

    private static string Canon(MediaEntry e)
    {
        string text = e == null ? null : OSDParser.SerializeJsonString(e.GetOSD());
        return text == DefaultEntry ? null : text;
    }

    [Fact]
    public void TheFixtureStoresTheMediaEntriesAndFlagsItDescribes()
    {
        ArchiveMergePlanner.Plan(MediaFixture.Write(), new MergeOptions(), null, out Dictionary<Guid, ParsedPart> parsed);
        Assert.Equal(MediaFixture.ObjectCount * MediaFixture.PartsPerObject, parsed.Count);
        foreach (ParsedPart pp in parsed.Values)
        {
            List<MediaEntry> media = MediaOf(pp);
            Assert.Equal(3, media.Count);
            Assert.Equal("https://media.example.org/one", media[0].CurrentURL);
            Assert.Equal("https://media.example.org/one", media[1].CurrentURL);
            Assert.Equal("https://media.example.org/two", media[2].CurrentURL);
            Primitive.TextureEntry te = ArchiveMergePlanner.ReadTextureEntry(pp);
            Assert.True(FaceOf(te, 0).MediaFlags && FaceOf(te, 1).MediaFlags && FaceOf(te, 2).MediaFlags);
            Assert.False(FaceOf(te, 3).MediaFlags);
        }
    }

    [Fact]
    public void TheFixtureCanBeWrittenToAFileForALoadIntoARegion()
    {
        string outPath = Environment.GetEnvironmentVariable("OARX_MEDIA_OUT");
        if (string.IsNullOrEmpty(outPath))
            return;
        File.Copy(MediaFixture.Write(), outPath, false);
    }

    [Fact]
    public void MediaFacesMergeAndEqualEntriesShareAMergedFace()
    {
        MergePlan plan = ArchiveMergePlanner.Plan(MediaFixture.Write(), new MergeOptions());
        Assert.True(plan.KeptParts == 0, "parts kept: " + string.Join("; ", plan.Linksets.SelectMany(l => l.Kept).Select(k => k.Name + " " + string.Join(",", k.Rules.Select(r => r.Code)))));
        Assert.Equal(MediaFixture.ObjectCount, plan.MergedParts);
        foreach (LinksetPlan l in plan.Linksets)
        {
            MergedPartPlan merged = Assert.Single(l.Merged);
            Assert.Equal(MediaFixture.PartsPerObject, merged.Members.Count);
            // the first entry (faces 0 and 1), the second entry (face 2), and the plain faces
            Assert.Equal(3, merged.Slots.Count);
            Assert.All(merged.Slots, s => Assert.True(s.Baked));
        }
    }

    [Fact]
    public void TheMergedPartCarriesEachFacesMediaEntryToTheFaceItBecomes()
    {
        Written w = WriteAndRead(MediaFixture.Write());
        int mediaSlots = 0;
        for (int li = 0; li < w.Plan.Linksets.Count; li++)
            foreach (MergedPartPlan mp in w.Plan.Linksets[li].Merged)
            {
                List<MergeMember> members = Members(w, li, mp);
                MergedGeometry g = MergeGeometry.Build(mp, members, members[0].IsRoot);
                List<MediaEntry> outMedia = MediaOf(w.OutParsed[mp.Id]);
                Primitive.TextureEntry outTe = ArchiveMergePlanner.ReadTextureEntry(w.OutParsed[mp.Id]);
                for (int slot = 0; slot < g.Faces.Count; slot++)
                {
                    (int mi, int fi) = g.Template[slot];
                    ParsedPart src = w.Parsed[members[mi].PartId];
                    List<MediaEntry> srcMedia = MediaOf(src);
                    string want = fi < srcMedia.Count ? Canon(srcMedia[fi]) : null;
                    string got = slot < outMedia.Count ? Canon(outMedia[slot]) : null;
                    Assert.Equal(want, got);
                    Assert.Equal(FaceOf(ArchiveMergePlanner.ReadTextureEntry(src), fi).MediaFlags, FaceOf(outTe, slot).MediaFlags);
                    if (want != null)
                        mediaSlots++;
                }
            }
        Assert.Equal(2 * MediaFixture.ObjectCount, mediaSlots);
    }

    [Fact]
    public void EveryCornerOfAMediaFaceIsWhereTheViewerDrawsIt()
    {
        Written w = WriteAndRead(MediaFixture.Write());
        double worst = 0;
        long corners = 0, mediaCorners = 0;
        for (int li = 0; li < w.Plan.Linksets.Count; li++)
            foreach (MergedPartPlan mp in w.Plan.Linksets[li].Merged)
            {
                Primitive.TextureEntry outTe = ArchiveMergePlanner.ReadTextureEntry(w.OutParsed[mp.Id]);
                List<MergeMember> members = Members(w, li, mp);
                MergedGeometry g = MergeGeometry.Build(mp, members, members[0].IsRoot);
                foreach (FaceRun run in g.Runs)
                {
                    MergeMember m = members[run.Member];
                    Primitive.TextureEntry te = new Primitive.TextureEntry(w.Parsed[m.PartId].TextureEntry, 0, w.Parsed[m.PartId].TextureEntry.Length);
                    Primitive.TextureEntryFace src = FaceOf(te, run.Face), drawn = FaceOf(outTe, run.Slot);
                    Assert.Equal(MappingType.Default, drawn.TexMapType);
                    EncoderFace sf = m.Faces[run.Face], mf = g.Faces[run.Slot];
                    for (int k = 0; k < run.IndexCount; k++)
                    {
                        int sv = (int)sf.Indices[run.SourceFirstIndex + k], mv = (int)mf.Indices[run.FirstIndex + k];
                        Vector3 n = new Vector3(sf.Normals[sv * 3], sf.Normals[sv * 3 + 1], sf.Normals[sv * 3 + 2]);
                        Vector3 p = new Vector3(sf.Positions[sv * 3], sf.Positions[sv * 3 + 1], sf.Positions[sv * 3 + 2]);
                        Viewer(n, p, m.Scale, Of(src), src.TexMapType == MappingType.Planar, sf.Uvs[sv * 2], sf.Uvs[sv * 2 + 1], out float ws, out float wt);
                        Viewer(Vector3.UnitZ, Vector3.Zero, Vector3.One, Of(drawn), false, mf.Uvs[mv * 2], mf.Uvs[mv * 2 + 1], out float gs, out float gt);
                        worst = Math.Max(worst, Math.Max(Math.Abs(ws - gs), Math.Abs(wt - gt)));
                        corners++;
                        if (src.MediaFlags) mediaCorners++;
                    }
                }
            }
        string summary = "corners " + corners + " (media faces " + mediaCorners + "), worst difference " + worst.ToString("0.0e+0", CultureInfo.InvariantCulture) + " (limit 1e-5)";
        string report = Environment.GetEnvironmentVariable("OARX_MEDIA_REPORT");
        if (!string.IsNullOrEmpty(report))
            File.WriteAllText(report, summary + Environment.NewLine);
        Assert.True(mediaCorners > 0, summary);
        Assert.True(worst < 1e-5, summary);
    }

    // ---------------------------------------------------------------- what cannot be carried

    private static MergePlan PlanWith(string badMedia)
    {
        string good = MediaFixture.ShapeXml(MediaFixture.MediaXml(MediaFixture.Entry("https://media.example.org/one", 512), null, null));
        string te = MediaFixture.TextureEntry(0);
        List<KitPart> parts = new List<KitPart>
        {
            MediaFixture.Part("bad", 1, "0,0,0", "1,1,1", "0,0,0,1", te, badMedia),
            MediaFixture.Part("good 1", 2, "1.2,0,0", "1,1,1", "0,0,0,1", te, good),
            MediaFixture.Part("good 2", 3, "2.4,0,0", "1,1,1", "0,0,0,1", te, good)
        };
        return ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }, MediaFixture.Assets()), new MergeOptions());
    }

    [Fact]
    public void MediaDataThatCannotBeReadKeepsThePartAsItIs()
    {
        MergePlan plan = PlanWith("<Media>this is not a media list</Media>");
        Assert.Contains(plan.Linksets[0].Kept, k => k.Name == "bad" && k.Rules.Any(r => r.Code == "MEDIA_DATA"));
        Assert.Equal(2, Assert.Single(plan.Linksets[0].Merged).Members.Count);
    }

    [Fact]
    public void AMediaEntryForAFaceThePartDoesNotHaveKeepsThePart()
    {
        // a box has six faces; the list has nine places
        MediaEntry[] nine = new MediaEntry[9];
        nine[0] = MediaFixture.Entry("https://media.example.org/one", 512);
        nine[8] = MediaFixture.Entry("https://media.example.org/nine", 512);
        MergePlan plan = PlanWith(MediaFixture.ShapeXml(MediaFixture.MediaXml(nine)));
        Assert.Contains(plan.Linksets[0].Kept, k => k.Name == "bad" && k.Rules.Any(r => r.Code == "MEDIA_DATA"));
    }

    [Fact]
    public void AFaceMarkedAsMediaWithNoEntryIsCarriedAsItIs()
    {
        // the flag alone is carried: the merged face is marked and has no entry
        MergePlan plan = PlanWith(MediaFixture.ShapeXml(MediaFixture.MediaXml(new MediaEntry[0])));
        Assert.DoesNotContain(plan.Linksets[0].Kept, k => k.Name == "bad");
    }
}
