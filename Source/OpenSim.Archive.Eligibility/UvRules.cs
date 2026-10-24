/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Eligibility;

/// <summary>
/// The shapes whose texture coordinates, as the mesher builds them, are not the ones the viewer gives the prim. The viewer's
/// rule is in indra/llmath/llvolume.cpp: a side face has u = the profile point's texture position and v = the path point's
/// parameter t (LLVolumeFace::createSide 6795-6890, LLPath::genNGon 1217-1375), and a cap has the profile point itself
/// (LLVolumeFace::createCap 6165-6250). The tests in ViewerUvRuleTests compare the mesher with that rule for every shape in the
/// probe matrix; the cases listed here are the ones where they differ. A part with such a shape stays a prim (UV_UNVERIFIED)
/// until the mesher is fixed.
/// </summary>
public static class UvRules
{
    /// <summary>
    /// Why this shape's texture coordinates are not the viewer's, or null when they are. A circular path with a y shear: the mesher
    /// turns the path's angle by the shear (PrimMesher.cs, in the circular path builder), where the viewer shears the path point and leaves the
    /// angle alone, so v is offset by the shear.
    /// </summary>
    public static string Unverified(ShapeData shape)
    {
        PrimParams p = PrimParams.FromShape(shape);
        bool circular = p.PathCurve != 16 && p.PathCurve != 128;
        if (circular && p.PathShearY != 0)
            return "the mesher turns a circular path's angle by its y shear where the viewer shears the path point, so the texture coordinates along the path are offset";
        return null;
    }
}
