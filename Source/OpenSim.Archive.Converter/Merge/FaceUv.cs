/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>
/// The texture coordinates a viewer draws a face with, worked out once so they can be written into a merged mesh. The two steps
/// are the viewer's own (Firestorm-lineage indra/newview/llface.cpp): planar projection (planarProjection) for a face whose texture
/// mapping is planar, and the face's texture transform (xform): a turn about the middle of the texture, then the repeats, then the offset.
/// </summary>
public static class FaceUv
{
    /// <summary>The viewer's xform: rotate about (0.5, 0.5), scale by the repeats, add the offset.</summary>
    public static void Transform(ref float s, ref float t, FaceTransform x) => throw new NotImplementedException();

    /// <summary>The viewer's planarProjection for one vertex: the unit-box normal and position, and the part's size.</summary>
    public static void Planar(Vector3 normal, Vector3 position, Vector3 scale, out float u, out float v) => throw new NotImplementedException();

    /// <summary>
    /// The baked texture coordinates of a face: for a planar face the projection of each vertex, then in both cases the face's transform.
    /// <paramref name="uvs"/> are the face's coordinates in the viewer's convention (v = 0 at the bottom); a planar face ignores them.
    /// </summary>
    public static float[] Bake(float[] positions, float[] normals, float[] uvs, FaceTransform transform, bool planar, Vector3 scale) => throw new NotImplementedException();
}
