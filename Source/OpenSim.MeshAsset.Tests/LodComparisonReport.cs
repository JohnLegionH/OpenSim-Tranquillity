/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using OpenSim.MeshAsset;

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// For each real asset in the reference corpus: the triangles per level in the asset as uploaded, against what
/// <see cref="LodGenerator"/> makes from that asset's own high level, and the streaming cost of each. The report is written
/// next to the reference data; the only assertions are that the generator ran and kept the face count.
/// </summary>
public class LodComparisonReport
{
    static int Tris(MeshLod? l) => l == null ? 0 : l.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
    static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    [CorpusFact]
    public void WritesTheLodComparisonReport()
    {
        var lines = new List<string>
        {
            "file\trigged\tfaces\thighTris\torigTris(lowest,low,med,high)\tourTris(lowest,low,med)\tratio ours/orig(lowest,low,med)\torigLodsAreCopiesOfHigh\t" +
            "method(lowest,low,med)\torigSizes(lowest,low,med,high)\tourSizes(lowest,low,med,high)\torigCharged\tourCharged\torigCost@r1\tourCost@r1\trepeatCharged\trepeatCost@r1\tmillis",
        };
        foreach (var f in Refs.CorpusFiles())
        {
            var bytes = File.ReadAllBytes(f);
            var orig = MeshCodec.Decode(bytes);
            var ours = MeshCodec.Decode(bytes); // a second copy to put our levels into; the high level is the original's
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var results = LodGenerator.GenerateAll(ours);
            long ms = clock.ElapsedMilliseconds;
            Assert.Equal(orig.High!.Faces.Count, ours.Lowest!.Faces.Count);

            var oursBytes = MeshCodec.Encode(ours);
            var origCost = StreamingCost.FromAsset(bytes);
            var ourCost = StreamingCost.FromAsset(oursBytes);
            var rep = MeshCodec.Decode(bytes); rep.Lowest = rep.Low = rep.Medium = null; rep.RepeatLodAbove();
            var repCost = StreamingCost.FromAsset(MeshCodec.Encode(rep));

            int high = Tris(orig.High);
            var o = new[] { Tris(orig.Lowest), Tris(orig.Low), Tris(orig.Medium), high };
            var u = new[] { Tris(ours.Lowest), Tris(ours.Low), Tris(ours.Medium) };
            bool copies = o[0] == high && o[1] == high && o[2] == high;
            string Ratio(int i) => o[i] == 0 ? "-" : F((double)u[i] / o[i]);
            lines.Add(string.Join('\t', Path.GetFileName(f), orig.Skin != null, orig.High.Faces.Count, high,
                string.Join(',', o), string.Join(',', u), $"{Ratio(0)},{Ratio(1)},{Ratio(2)}", copies,
                string.Join(',', results.OrderBy(r => r.Lod).Select(r => r.Method)),
                string.Join(',', origCost.SizeByLod), string.Join(',', ourCost.SizeByLod),
                F(origCost.ChargedTriangles), F(ourCost.ChargedTriangles), F(origCost.StaticCost(1)), F(ourCost.StaticCost(1)),
                F(repCost.ChargedTriangles), F(repCost.StaticCost(1)), ms));
        }
        File.WriteAllLines(Path.Combine(Refs.Root, "work", "lod-comparison.tsv"), lines);
    }
}
