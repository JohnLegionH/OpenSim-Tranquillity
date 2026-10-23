/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.GltfExport;

/// <summary>What one face of a prim is.</summary>
public enum PrimFaceKind
{
    /// <summary>The end cap at the far end of the path: face 0 of a prim that has end caps.</summary>
    Top,
    /// <summary>A side of the outside of the profile. A square has four, a triangle three, a circle one.</summary>
    Outer,
    /// <summary>The inside wall of a hollow prim.</summary>
    Inner,
    /// <summary>The end cap at the start of the path.</summary>
    Bottom,
    /// <summary>The flat face along the start of a profile cut.</summary>
    ProfileCutBegin,
    /// <summary>The flat face along the end of a profile cut.</summary>
    ProfileCutEnd
}

/// <summary>
/// The list of faces a prim has, in face-number order, by the rule the Second Life viewer follows when it builds a
/// volume: the order the viewer's profile generator adds faces (LLProfile::generate in indra/llmath/llvolume.cpp,
/// public source of the Second Life viewer) and whether the path is open (LLPath::generate and LLPath::genNGon in the
/// same file). A face's number is its position in this list, which is the slot of the face in a texture entry and the
/// face number scripts use. The list can hold a face that has no area (the cut faces of a sphere lie on its axis);
/// it still takes its number.
/// </summary>
public static class ViewerFaceRule
{
    public static List<PrimFaceKind> Faces(PrimParams p)
    {
        bool pathOpen = PathIsOpen(p);
        float begin = p.ProfileBegin, end = p.ProfileEnd;
        bool hollow = p.ProfileHollow > 0f;

        int outer;
        bool profileOpen;
        switch (p.ProfileCurve & 0x0F)
        {
            case 1: // square: one face per side the cut leaves
                outer = (int)MathF.Floor(end * 4f + .999f) - (int)MathF.Floor(begin * 4f);
                profileOpen = (end - begin) < 0.99f;
                break;
            case 2:
            case 3:
            case 4: // triangles
                outer = (int)MathF.Floor(end * 3f + .999f) - (int)MathF.Floor(begin * 3f);
                profileOpen = (end - begin) < 0.99f;
                break;
            case 5: // half circle: open unless it is whole and solid
                outer = 1;
                profileOpen = (end - begin) < 1f || hollow;
                break;
            default: // circle
                outer = 1;
                profileOpen = (end - begin) < 0.99f;
                break;
        }

        List<PrimFaceKind> faces = new List<PrimFaceKind>();
        if (pathOpen)
            faces.Add(PrimFaceKind.Top);
        for (int i = 0; i < Math.Max(outer, 1); i++)
            faces.Add(PrimFaceKind.Outer);
        if (hollow)
            faces.Add(PrimFaceKind.Inner);
        if (pathOpen)
            faces.Add(PrimFaceKind.Bottom);
        if (profileOpen)
        {
            faces.Add(PrimFaceKind.ProfileCutBegin);
            faces.Add(PrimFaceKind.ProfileCutEnd);
        }
        return faces;
    }

    /// <summary>
    /// A straight path is open. A circular path is open when it is cut, tapered, skewed, twisted, or given a radius
    /// offset that moves its ends apart. The radius offset counts only through the hole size, which is why a sphere
    /// (hole size 1) ignores it.
    /// </summary>
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
