/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
namespace OpenSim.MeshAsset;

/// <summary>
/// What the Linden Lab viewer charges for streaming a mesh, worked out from the byte size of each level of detail in the
/// asset header. The formulas and constants are the viewer's (documented in FORMAT.md); the triangle numbers are
/// estimates made from compressed byte sizes, not triangle counts.
/// </summary>
public sealed class StreamingCost
{
    const double MetadataDiscount = 384, MinimumByteSize = 16, BytesPerTriangle = 16, TriangleBudget = 250000, CostPerKTri = 1.5;
    const double MaxDistance = 512, MaxArea = 102944, MinArea = 1;

    /// <summary>Byte size per level, index 0 lowest .. 3 high, after a missing level takes the size of the one above.</summary>
    public int[] SizeByLod { get; } = new int[4];
    /// <summary>Estimated triangles per level, same order.</summary>
    public double[] EstTris { get; } = new double[4];

    public StreamingCost(int lowest, int low, int medium, int high)
    {
        int bytesHigh = high, bytesMed = medium == 0 ? bytesHigh : medium, bytesLow = low == 0 ? bytesMed : low, bytesLowest = lowest == 0 ? bytesLow : lowest;
        SizeByLod[0] = bytesLowest; SizeByLod[1] = bytesLow; SizeByLod[2] = bytesMed; SizeByLod[3] = bytesHigh;
        for (int i = 0; i < 4; i++) EstTris[i] = Math.Max(SizeByLod[i] - MetadataDiscount, MinimumByteSize) / BytesPerTriangle;
    }

    /// <summary>Reads the level sizes from an asset's header.</summary>
    public static StreamingCost FromAsset(byte[] asset)
    {
        var header = (IDictionary<string, object?>)LlsdBinary.Read(asset, out _)!;
        int Size(string key) => header.TryGetValue(key, out var e) && e is IDictionary<string, object?> m && m.TryGetValue("size", out var s) && s is int i ? i : 0;
        return new StreamingCost(Size("lowest_lod"), Size("low_lod"), Size("medium_lod"), Size("high_lod"));
    }

    /// <summary>
    /// Triangles the land impact is charged for: the high level's estimate plus, for each lower level, whatever it has
    /// above half the level above it (never fewer than 64 allowed).
    /// </summary>
    public double ChargedTriangles
    {
        get
        {
            double charged = EstTris[3], allowed = EstTris[3];
            for (int i = 2; i >= 0; i--)
            {
                allowed = Math.Clamp(allowed / 2, 64, Math.Max(64, EstTris[i]));
                double excess = EstTris[i] - allowed;
                if (excess > 0) charged += excess;
            }
            return charged;
        }
    }

    /// <summary>Estimated triangles averaged over the area each level covers, for an object of the given radius (metres).</summary>
    public double RadiusWeighted(double radius)
    {
        double dLowest = Math.Min(radius / 0.03, MaxDistance), dLow = Math.Min(radius / 0.06, MaxDistance), dMid = Math.Min(radius / 0.24, MaxDistance);
        double highArea = Math.Min(Math.PI * dMid * dMid, MaxArea), midArea = Math.Min(Math.PI * dLow * dLow, MaxArea), lowArea = Math.Min(Math.PI * dLowest * dLowest, MaxArea), lowestArea = MaxArea;
        lowestArea -= lowArea; lowArea -= midArea; midArea -= highArea;
        highArea = Math.Clamp(highArea, MinArea, MaxArea); midArea = Math.Clamp(midArea, MinArea, MaxArea);
        lowArea = Math.Clamp(lowArea, MinArea, MaxArea); lowestArea = Math.Clamp(lowestArea, MinArea, MaxArea);
        double total = highArea + midArea + lowArea + lowestArea;
        return (EstTris[3] * highArea + EstTris[2] * midArea + EstTris[1] * lowArea + EstTris[0] * lowestArea) / total;
    }

    /// <summary>Streaming cost of a static mesh with the given radius (half the length of its scale vector).</summary>
    public double StaticCost(double radius) => RadiusWeighted(radius) / TriangleBudget * 15000;

    /// <summary>Streaming cost of a rigged mesh worn as an animated object.</summary>
    public double AnimatedObjectCost => CostPerKTri * 0.001 * ChargedTriangles;
}
