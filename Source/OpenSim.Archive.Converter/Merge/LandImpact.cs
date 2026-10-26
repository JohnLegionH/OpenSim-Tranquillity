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
    /// <summary>Triangles the mesh parts' levels hold, high to lowest, and the bytes of their mesh assets.</summary>
    public long HighTriangles, MediumTriangles, LowTriangles, LowestTriangles, MeshBytes;
}

public static class LandImpact
{
    public static LandImpactReport Measure(string archivePath) => throw new NotImplementedException();
}
