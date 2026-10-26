/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.IO.Compression;
using System.Text;
using OpenSim.Archive.Inspection;
using OpenSim.MeshAsset;
using OpenSim.Archive.Converter.Merge;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Measuring aid, not a check: merges an archive at several pixel limits and writes what the viewer would be charged and how far each level is from
/// the full mesh at its switch distance. It runs only when OARX_SWEEP_SRC (the archive), OARX_SWEEP_OUT (the table file) and OARX_SWEEP_DIR (where the
/// merged archives go) are set. Shares no process state.
/// </summary>
public class MergeLodSweepTests
{
    [Fact]
    public void TheArchiveNamedInTheEnvironmentIsMergedAtEachPixelLimitAndTheCostsAreWritten()
    {
        string src = Environment.GetEnvironmentVariable("OARX_SWEEP_SRC"), outPath = Environment.GetEnvironmentVariable("OARX_SWEEP_OUT"), dir = Environment.GetEnvironmentVariable("OARX_SWEEP_DIR");
        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(outPath) || string.IsNullOrEmpty(dir))
            return;
        string limits = Environment.GetEnvironmentVariable("OARX_SWEEP_LIMITS") ?? "1,2,4,8";
        CultureInfo inv = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder("label\tparts\tmesh parts\tregion land impact\tviewer cost meshes\tviewer cost prims\tviewer cost total\thigh tris\tmedium tris\tlow tris\tlowest tris\tmesh bytes\n");
        StringBuilder lv = new StringBuilder("pixel limit\tlevel\tparts\tswitch m min\tswitch m median\tswitch m max\tworst error px\tworst error m\ttriangles\tkept whole\tmembers left out\n");
        LandImpactReport before = LandImpact.Measure(src);
        sb.AppendLine(string.Join("\t", "unmerged", before.Parts, before.MeshParts, before.RegionLandImpact.ToString("0.0", inv), before.ViewerStreamingCost.ToString("0.00", inv), before.ViewerStreamingCostPrims.ToString("0.00", inv), before.ViewerStreamingCostTotal.ToString("0.00", inv), before.HighTriangles, before.MediumTriangles, before.LowTriangles, before.LowestTriangles, before.MeshBytes));
        Directory.CreateDirectory(dir);
        ArchiveMergePlanner.Plan(src, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> sourceParts);
        StringBuilder pp = new StringBuilder("pixel limit	merged part	members	member prim cost	merged mesh cost	mesh bytes	high tris	medium tris	low tris	lowest tris	radius m\n");
        foreach (string item in limits.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            double limit = double.Parse(item, inv);
            MergeOptions options = new MergeOptions();
            options.Lod.PixelLimit = limit;
            string merged = Path.Combine(dir, "merged-" + item + "px.oar");
            MergeWriteResult w = MergeArchiveWriter.Write(src, merged, options);
            LandImpactReport r = LandImpact.Measure(merged);
            ArchiveMergePlanner.Plan(merged, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> mergedParts);
            Dictionary<string, byte[]> meshes = ReadMeshes(merged);
            foreach (MergedPartPlan mp in w.Plan.Linksets.SelectMany(l => l.Merged))
            {
                double memberCost = mp.Members.Sum(m => sourceParts.TryGetValue(m.PartId, out ParsedPart sp) && sp.Shape != null ? PrimStreaming.Cost(sp.Shape, new System.Numerics.Vector3(sp.Scale.X, sp.Scale.Y, sp.Scale.Z)) : 0);
                if (!mergedParts.TryGetValue(mp.Id, out ParsedPart m2) || !meshes.TryGetValue("assets/" + m2.Shape.SculptTexture + "_mesh.llmesh", out byte[] bytes))
                {
                    pp.AppendLine(item + "	" + mp.Name + "	merged part or mesh not found");
                    continue;
                }
                double radius = 0.5 * new System.Numerics.Vector3(m2.Scale.X, m2.Scale.Y, m2.Scale.Z).Length();
                MeshAssetModel model = MeshCodec.Decode(bytes);
                int Count(MeshLod lod) => lod == null ? 0 : lod.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
                pp.AppendLine(string.Join("	", item, mp.Name, mp.Members.Count, memberCost.ToString("0.000", inv), StreamingCost.FromAsset(bytes).StaticCost(radius).ToString("0.000", inv), bytes.Length,
                    Count(model.High), Count(model.Medium), Count(model.Low), Count(model.Lowest), radius.ToString("0.00", inv)));
            }
            sb.AppendLine(string.Join("\t", item + " px", r.Parts, r.MeshParts, r.RegionLandImpact.ToString("0.0", inv), r.ViewerStreamingCost.ToString("0.00", inv), r.ViewerStreamingCostPrims.ToString("0.00", inv), r.ViewerStreamingCostTotal.ToString("0.00", inv), r.HighTriangles, r.MediumTriangles, r.LowTriangles, r.LowestTriangles, r.MeshBytes));
            foreach (int level in new[] { 2, 1, 0 })
            {
                List<LodLevel> all = w.Parts.SelectMany(p => p.Levels.Where(l => l.Level == level)).ToList();
                if (all.Count == 0)
                    continue;
                List<double> d = all.Select(l => l.SwitchDistance).OrderBy(x => x).ToList();
                LodLevel worst = all.OrderByDescending(l => l.ErrorPixels).First();
                lv.AppendLine(string.Join("\t", item, level == 2 ? "medium" : level == 1 ? "low" : "lowest", all.Count, d[0].ToString("0.0", inv), d[d.Count / 2].ToString("0.0", inv), d[^1].ToString("0.0", inv),
                    worst.ErrorPixels.ToString("0.000", inv), Math.Max(worst.PositionErrorMeters, worst.UvErrorMeters).ToString("0.0000", inv), all.Sum(l => l.Triangles), all.Count(l => l.Method.StartsWith("kept whole")), all.Sum(l => l.DroppedMembers)));
            }
        }
        File.WriteAllText(outPath, sb + "\n" + lv);
        File.WriteAllText(Path.ChangeExtension(outPath, ".parts.tsv"), pp.ToString());
    }

    private static Dictionary<string, byte[]> ReadMeshes(string archive)
    {
        Dictionary<string, byte[]> meshes = new Dictionary<string, byte[]>();
        using FileStream fs = File.OpenRead(archive);
        TarStreamReader tar = new TarStreamReader(new GZipStream(fs, CompressionMode.Decompress));
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
            if (h.IsFile && h.Name.EndsWith("_mesh.llmesh"))
                meshes[h.Name] = tar.ReadBody();
        return meshes;
    }
}
