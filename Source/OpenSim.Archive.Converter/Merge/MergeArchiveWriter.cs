/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>What was built for one merged part, and how far its mesh is from the parts it replaces.</summary>
public sealed class MergedPartReport
{
    public Guid Id { get; set; }
    public Guid MeshId { get; set; }
    public Guid ManifestAssetId { get; set; }
    public string Name { get; set; }
    public string Linkset { get; set; }
    public int Members { get; set; }
    public int Faces { get; set; }
    public int Vertices { get; set; }
    public int Triangles { get; set; }
    public int PlanarFaces { get; set; }
    public int BakedFaces { get; set; }
    public int UnbakedFaces { get; set; }
    public bool TookTheRoot { get; set; }
    public long MeshBytes { get; set; }
    /// <summary>Corners compared against the original parts, and the largest difference found, in metres, in texture units and in normal components.</summary>
    public int Corners { get; set; }
    public double WorstPositionMeters { get; set; }
    public double WorstUv { get; set; }
    public double WorstNormal { get; set; }
    /// <summary>The same, measured on the mesh as written and read back (the asset stores positions and coordinates in 16 bits over the mesh's range).</summary>
    public double WorstPositionMetersDecoded { get; set; }
    public double WorstUvDecoded { get; set; }
    public double WorstNormalDecoded { get; set; }
}

public sealed class MergeWriteResult
{
    public MergePlan Plan { get; set; }
    public string Output { get; set; }
    public RewriteSummary Rewrite { get; set; }
    public List<MergedPartReport> Parts { get; } = new List<MergedPartReport>();
    public int MeshesAdded => Parts.Count;
    public long SourceBytes { get; set; }
    public long OutputBytes { get; set; }
    public double Seconds { get; set; }
}

/// <summary>Writes the merge: the plan's merged parts as mesh parts in a new archive, the other parts as they were.</summary>
public static class MergeArchiveWriter
{
    public static MergeWriteResult Write(string sourcePath, string outputPath, MergeOptions options = null, ConvertOptions convert = null) => throw new NotImplementedException();
}
