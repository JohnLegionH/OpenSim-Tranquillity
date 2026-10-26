/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.MeshAsset;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>
/// The streaming cost the viewer shows for a plain prim. The viewer has no mesh asset to measure, so it estimates the triangles of the prim's four
/// levels of detail from the points of the path and the profile (llvolume.cpp getLoDTriangleCounts 3693-3709, LLProfile::getNumPoints 624-731,
/// LLPath::getNumPoints 1405-1451) at detail 1, 1.5, 2.5 and 4, takes ten bytes a triangle (llvovolume.cpp getCostData 4393-4416) and prices those
/// sizes as it prices a mesh. The point counting follows the viewer's single-precision arithmetic, because its loops compare running sums.
/// </summary>
public static class PrimStreaming
{
    private static readonly float[] Detail = { 1f, 1.5f, 2.5f, 4f };
    private const int MinDetailFaces = 6;

    /// <summary>Triangles the viewer estimates for the prim at its four levels of detail, lowest first.</summary>
    public static int[] TriangleCounts(ShapeData shape)
    {
        PrimParams p = PrimParams.FromShape(shape);
        int[] counts = new int[4];
        for (int i = 0; i < 4; i++)
        {
            int pathPoints = PathPoints(p, Detail[i]);
            int profilePoints = ProfilePoints(p, Detail[i]);
            counts[i] = (profilePoints - 1) * 2 * (pathPoints - 1) + profilePoints * 2;
        }
        return counts;
    }

    /// <summary>The viewer's streaming cost for the prim at the given size (radius half the length of the scale vector).</summary>
    public static double Cost(ShapeData shape, System.Numerics.Vector3 scale)
    {
        int[] c = TriangleCounts(shape);
        return new StreamingCost(c[0] * 10, c[1] * 10, c[2] * 10, c[3] * 10).StaticCost(0.5 * scale.Length());
    }

    private static int Floor(float x) => (int)MathF.Floor(x);

    // LLPath::getNumPoints
    private static int PathPoints(PrimParams p, float detail)
    {
        switch (p.PathCurve & 0xf0)
        {
            case 0x20:
            {
                float twist = MathF.Abs(p.PathTwistBegin - p.PathTwist);
                int sides = Floor(Floor(MinDetailFaces * detail + twist * 3.5f * (detail - 0.5f)) * p.PathRevolutions);
                return sides;
            }
            case 0x30:
                return PathNGonPoints(p, Floor(MinDetailFaces * detail));
            case 0x40:
                return 5;
            default:
                return Floor(MathF.Abs(p.PathTwistBegin - p.PathTwist) * 3.5f * (detail - 0.5f)) + 2;
        }
    }

    // LLPath::getNumNGonPoints
    private static int PathNGonPoints(PrimParams p, int sides)
    {
        float step = 1.0f / sides;
        float t = p.PathBegin;
        int ret = 1;
        t += step;
        t = (int)(t * sides) / (float)sides;
        while (t < p.PathEnd)
        {
            ret++;
            t += step;
        }
        return ret + 1;
    }

    // LLProfile::getNumPoints
    private static int ProfilePoints(PrimParams p, float detail)
    {
        float hollow = p.ProfileHollow;
        int np;
        switch (p.ProfileCurve & 0x0f)
        {
            case 1:
                np = ProfileNGonPoints(p, 4, 1f);
                return hollow != 0 ? np * 2 : np;
            case 2:
            case 3:
            case 4:
                np = ProfileNGonPoints(p, 3, 1f);
                return hollow != 0 ? np * 2 : np;
            case 0:
            {
                float circle = MinDetailFaces * detail;
                if (hollow != 0 && p.HoleType == 0x20)
                    circle = MathF.Ceiling(circle / 4.0f) * 4.0f;
                np = ProfileNGonPoints(p, (int)circle, 1f);
                return hollow != 0 ? np * 2 : np;
            }
            case 5:
            {
                float circle = MinDetailFaces * detail * 0.5f;
                if (hollow != 0 && p.HoleType == 0x20)
                    circle = MathF.Ceiling(circle / 2.0f) * 2.0f;
                np = ProfileNGonPoints(p, Floor(circle), 0.5f);
                if (hollow != 0)
                    np *= 2;
                // a whole half circle (a sphere's profile) gets one more point; a cut one does not
                if (!(p.ProfileEnd - p.ProfileBegin < 1.0f) && hollow == 0)
                    np++;
                return np;
            }
            default:
                return 0;
        }
    }

    // LLProfile::getNumNGonPoints
    private static int ProfileNGonPoints(PrimParams p, int sides, float angScale)
    {
        int np = 0;
        float begin = p.ProfileBegin, end = p.ProfileEnd;
        float step = 1.0f / sides;
        float first = MathF.Floor(begin * sides) / sides;
        float t = first;
        t += step;
        float fraction = (begin - first) * sides;
        if (fraction < 0.9999f)
            np++;
        while (t < end)
        {
            np++;
            t += step;
        }
        fraction = (end - (t - step)) * sides;
        if (fraction > 0.0001f)
            np++;
        // a cut profile is open; without a hollow it gets a centre point
        if ((end - begin) * angScale < 0.99f && p.ProfileHollow <= 0)
            np++;
        return np;
    }
}
