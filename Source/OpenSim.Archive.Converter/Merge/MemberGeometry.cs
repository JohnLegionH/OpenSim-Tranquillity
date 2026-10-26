/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>The faces and the collision triangles of one part, as the converter builds them for a single part, in the unit box.</summary>
public sealed class MemberShape
{
    public List<EncoderFace> Faces;
    public PhysicsGeometry Physics;
}

/// <summary>Builds the geometry of a prim or sculpt part for the merge, the same way the converter builds it for a part on its own.</summary>
internal static class MemberGeometry
{
    /// <summary>The part's geometry, or null with <paramref name="problem"/> set. <paramref name="maps"/> supplies the maps of sculpts.</summary>
    public static MemberShape Build(string kind, ParsedPart pp, ISculptMapSource maps, bool clean, out string problem)
    {
        problem = null;
        if (kind != "prim")
        {
            problem = "only prims are built";
            return null;
        }
        List<EncoderFace> faces = FaceBuilder.FromPrim(pp.Shape, out _, clean);
        PhysicsGeometry physics = PrimPhysics.Build(pp.Shape, null, null, pp.Scale, out problem);
        return physics == null ? null : new MemberShape { Faces = faces, Physics = physics };
    }
}
