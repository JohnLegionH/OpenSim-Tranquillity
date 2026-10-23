/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// The face list of a prim as the Second Life viewer builds it, written from reading the viewer's volume code
/// (LLProfile::generate, LLPath::generate and LLPath::genNGon in indra/llmath/llvolume.cpp, public source of the
/// Second Life viewer). A texture entry's slot for a face is its position in this list. The test project keeps this
/// apart from the exporter so the two can disagree.
/// </summary>
internal static class FaceRuleOracle
{
    public const string Top = "top";
    public const string Outer = "outer";
    public const string Inner = "inner";
    public const string Bottom = "bottom";
    public const string CutBegin = "cutBegin";
    public const string CutEnd = "cutEnd";

    /// <summary>The faces, in face-number order.</summary>
    public static List<string> Faces(PrimParams p)
    {
        bool pathOpen = PathIsOpen(p);
        float begin = p.ProfileBegin, end = p.ProfileEnd;
        bool hollow = p.ProfileHollow > 0f;
        int curve = p.ProfileCurve & 0x0F;

        int outer;
        bool profileOpen;
        switch (curve)
        {
            case 1: // square
                outer = (int)MathF.Floor(end * 4f + .999f) - (int)MathF.Floor(begin * 4f);
                profileOpen = (end - begin) < 0.99f;
                break;
            case 2:
            case 3:
            case 4: // triangles
                outer = (int)MathF.Floor(end * 3f + .999f) - (int)MathF.Floor(begin * 3f);
                profileOpen = (end - begin) < 0.99f;
                break;
            case 5: // half circle: the viewer treats it as open unless it is whole and solid
                outer = 1;
                profileOpen = (end - begin) < 1f || hollow;
                break;
            default: // circle
                outer = 1;
                profileOpen = (end - begin) < 0.99f;
                break;
        }

        List<string> faces = new List<string>();
        if (pathOpen)
            faces.Add(Top);
        for (int i = 0; i < outer; i++)
            faces.Add(Outer);
        if (hollow)
            faces.Add(Inner);
        if (pathOpen)
            faces.Add(Bottom);
        if (profileOpen)
        {
            faces.Add(CutBegin);
            faces.Add(CutEnd);
        }
        return faces;
    }

    /// <summary>A straight path is always open; a circular path is open when cut, tapered, skewed, twisted or given a radius offset.</summary>
    public static bool PathIsOpen(PrimParams p)
    {
        int curve = p.PathCurve & 0xF0;
        if (curve != 0x20 && curve != 0x30)
            return true;
        if (p.PathEnd - p.PathBegin < 1f)
            return true;
        if (MathF.Abs(p.PathSkew) > 0.001f)
            return true;
        if (MathF.Abs(p.PathTaperX) > 0.001f || MathF.Abs(p.PathTaperY) > 0.001f)
            return true;
        if (MathF.Abs(p.PathRadiusOffset) * (1f - p.PathScaleY) * 0.5f > 0.001f)
            return true;
        return p.PathTwist != p.PathTwistBegin;
    }
}
