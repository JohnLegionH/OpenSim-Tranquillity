/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter.Merge;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>The cost of an archive: this region's land impact and the viewer's streaming cost. Each test writes its own archives and shares no state.</summary>
public class LandImpactTests
{
    [Fact]
    public void AnArchiveOfPrimsCostsOneForEachPart()
    {
        LandImpactReport r = LandImpact.Measure(MapsFixture.Write());
        Assert.Equal(MapsFixture.ObjectCount * MapsFixture.PartsPerObject, r.Parts);
        Assert.Equal(MapsFixture.ObjectCount, r.Linksets);
        Assert.Equal(0, r.MeshParts);
        Assert.Equal(r.Parts, r.RegionLandImpact, 6);
        Assert.Equal(0, r.ViewerStreamingCost);
    }

    [Fact]
    public void AMergedArchiveCostsOneForEachMergedPartAndTheViewerChargesTheMeshByItsLevels()
    {
        string output = Path.Combine(Path.GetTempPath(), "land-impact-tests", Guid.NewGuid().ToString("N") + ".oar");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        MergeWriteResult w = MergeArchiveWriter.Write(MapsFixture.Write(), output);
        LandImpactReport r = LandImpact.Measure(output);
        Assert.Equal(w.Plan.PartsAfter, r.Parts);
        Assert.Equal(w.Parts.Count, r.MeshParts);
        Assert.Equal(r.Parts, r.RegionLandImpact, 6);
        Assert.True(r.ViewerStreamingCost > 0);
        Assert.Equal(w.Parts.Sum(p => p.Triangles), r.HighTriangles);
        Assert.Equal(w.Parts.Sum(p => p.Levels[0].Triangles), r.MediumTriangles);
        Assert.Equal(w.Parts.Sum(p => p.Levels[2].Triangles), r.LowestTriangles);
        Assert.True(r.MeshBytes > 0);
    }

    [Fact]
    public void TheArchivesNamedInTheEnvironmentAreMeasuredAndTheTableIsWritten()
    {
        string list = Environment.GetEnvironmentVariable("OARX_COST_LIST"), outPath = Environment.GetEnvironmentVariable("OARX_COST_OUT");
        if (string.IsNullOrEmpty(list) || string.IsNullOrEmpty(outPath))
            return;
        System.Text.StringBuilder sb = new System.Text.StringBuilder("label\tlinksets\tparts\tmesh parts\tregion land impact\tviewer streaming cost\thigh tris\tmedium tris\tlow tris\tlowest tris\tmesh bytes\n");
        foreach (string item in list.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = item.Split('=', 2);
            LandImpactReport r = LandImpact.Measure(kv[1]);
            sb.AppendLine(string.Join("\t", kv[0], r.Linksets, r.Parts, r.MeshParts, r.RegionLandImpact.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), r.ViewerStreamingCost.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), r.HighTriangles, r.MediumTriangles, r.LowTriangles, r.LowestTriangles, r.MeshBytes));
        }
        File.WriteAllText(outPath, sb.ToString());
    }
}
