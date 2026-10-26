/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using OpenSim.Archive.Inspection;
using OpenSim.MeshAsset;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>What an archive costs, counted two ways.</summary>
public sealed class LandImpactReport
{
    public int Linksets, Parts, MeshParts;
    /// <summary>
    /// The land impact this region reports for the archive (SceneObjectGroup.GetResourcesCosts): a linkset with no mesh part and no part without
    /// a physics shape costs 1 for each part; otherwise each part costs the largest of its streaming cost (1, SceneObjectPart.StreamingCost), its
    /// simulation cost (0.5) and its physics cost (0.1 until a physics actor reports one), and the linkset costs their sum.
    /// </summary>
    public double RegionLandImpact;
    /// <summary>The viewer's streaming cost of the mesh parts (llmeshrepository.cpp getRadiusBasedStreamingCost, over the byte sizes of the levels in the asset header, radius half the scale vector).</summary>
    public double ViewerStreamingCost;
    /// <summary>The viewer's streaming cost of the plain prims (see <see cref="PrimStreaming"/>) and of both kinds together.</summary>
    public double ViewerStreamingCostPrims;
    public double ViewerStreamingCostTotal => ViewerStreamingCost + ViewerStreamingCostPrims;
    /// <summary>Triangles the mesh parts' levels hold, high to lowest, and the bytes of their mesh assets.</summary>
    public long HighTriangles, MediumTriangles, LowTriangles, LowestTriangles, MeshBytes;
}

public static class LandImpact
{
    private const double StreamingPart = 1.0, SimulationPart = 0.5, PhysicsPart = 0.1;

    public static LandImpactReport Measure(string archivePath)
    {
        ArchiveMergePlanner.Plan(archivePath, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> parsed);
        HashSet<string> wanted = parsed.Values.Where(IsMesh).Select(p => "assets/" + p.Shape.SculptTexture + "_mesh.llmesh").ToHashSet();
        Dictionary<string, byte[]> meshes = new Dictionary<string, byte[]>();
        using (FileStream fs = File.OpenRead(archivePath))
        {
            TarStreamReader tar = new TarStreamReader(new GZipStream(fs, CompressionMode.Decompress));
            TarEntryHeader h;
            while ((h = tar.ReadNext()) != null)
                if (h.IsFile && wanted.Contains(h.Name))
                    meshes[h.Name] = tar.ReadBody();
        }

        LandImpactReport r = new LandImpactReport();
        foreach (IGrouping<Guid, ParsedPart> linkset in parsed.Values.GroupBy(p => p.Part.LinksetId.Guid))
        {
            r.Linksets++;
            List<ParsedPart> parts = linkset.ToList();
            r.Parts += parts.Count;
            // a part whose physics shape type is none, or a mesh part, makes the linkset cost by part (SceneObjectPart.UsesComplexCost)
            bool complex = parts.Any(p => IsMesh(p) || p.PhysicsShapeType == 1);
            r.RegionLandImpact += complex ? parts.Sum(p => Math.Max(StreamingPart, Math.Max(SimulationPart, p.PhysicsShapeType == 1 ? 0 : PhysicsPart))) : parts.Count;
            // a plain prim is priced by the viewer from its shape alone (pcode 9 is a prim; trees and grass have no volume to price)
            r.ViewerStreamingCostPrims += parts.Where(p => !IsMesh(p) && p.Shape != null && p.Shape.PCode == 9)
                .Sum(p => PrimStreaming.Cost(p.Shape, new System.Numerics.Vector3(p.Scale.X, p.Scale.Y, p.Scale.Z)));
            foreach (ParsedPart p in parts.Where(IsMesh))
            {
                r.MeshParts++;
                if (!meshes.TryGetValue("assets/" + p.Shape.SculptTexture + "_mesh.llmesh", out byte[] bytes))
                    continue;
                MeshAssetModel model = MeshCodec.Decode(bytes);
                r.MeshBytes += bytes.Length;
                int Count(MeshLod lod) => lod == null ? 0 : lod.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
                r.HighTriangles += Count(model.High);
                r.MediumTriangles += Count(model.Medium);
                r.LowTriangles += Count(model.Low);
                r.LowestTriangles += Count(model.Lowest);
                r.ViewerStreamingCost += StreamingCost.FromAsset(bytes).StaticCost(0.5 * new System.Numerics.Vector3(p.Scale.X, p.Scale.Y, p.Scale.Z).Length());
            }
        }
        return r;
    }

    private static bool IsMesh(ParsedPart p) => p.Shape != null && p.Shape.SculptEntry && p.Shape.SculptType == (int)OpenMetaverse.SculptType.Mesh;
}
