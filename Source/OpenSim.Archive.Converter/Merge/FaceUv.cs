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
/// Reference (the code is written from the viewer's behaviour, not copied from it): planarProjection is llface.cpp lines 89-123, xform is
/// lines 734-757, and LLFace::getGeometryVolume (lines 1232 onward) applies them per channel: the diffuse coordinates take the face's
/// own transform (lines 1744-1797 and 1850-1930), the normal and specular coordinates take the legacy material's transform for that map and
/// not the face's (lines 1855-1892).
/// </summary>
public static class FaceUv
{
    /// <summary>The viewer's xform: rotate about (0.5, 0.5), scale by the repeats, add the offset.</summary>
    public static void Transform(ref float s, ref float t, FaceTransform x)
    {
        // llface.cpp xform(): the viewer takes cos and sin of the stored rotation as floats
        float cos = (float)Math.Cos(x.Rotation), sin = (float)Math.Sin(x.Rotation);
        float a = s - 0.5f, b = t - 0.5f;
        float turned = a;
        a = a * cos + b * sin;
        b = -turned * sin + b * cos;
        a *= x.RepeatU;
        b *= x.RepeatV;
        s = a + (x.OffsetU + 0.5f);
        t = b + (x.OffsetV + 0.5f);
    }

    /// <summary>The viewer's planarProjection for one vertex: the unit-box normal and position, and the part's size.</summary>
    public static void Planar(Vector3 normal, Vector3 position, Vector3 scale, out float u, out float v)
    {
        // llface.cpp planarProjection(): the binormal is along y when the normal is mostly along x, else along x
        Vector3 binormal = normal.X >= 0.5f || normal.X <= -0.5f
            ? new Vector3(0, normal.X < 0 ? -1 : 1, 0)
            : new Vector3(normal.Y > 0 ? -1 : 1, 0, 0);
        Vector3 tangent = Vector3.Cross(binormal, normal);
        Vector3 vec = position * scale;
        v = -(Vector3.Dot(tangent, vec) * 2 - 0.5f);
        u = 1.0f + (Vector3.Dot(binormal, vec) * 2 - 0.5f);
    }

    /// <summary>
    /// The baked texture coordinates of a face: for a planar face the projection of each vertex, then in both cases the face's transform.
    /// <paramref name="uvs"/> are the face's coordinates in the viewer's convention (v = 0 at the bottom); a planar face ignores them.
    /// </summary>
    public static float[] Bake(float[] positions, float[] normals, float[] uvs, FaceTransform transform, bool planar, Vector3 scale)
    {
        int n = positions.Length / 3;
        float[] result = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            float s, t;
            if (planar)
                Planar(new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]), new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]), scale, out s, out t);
            else
            {
                s = uvs[i * 2];
                t = uvs[i * 2 + 1];
            }
            Transform(ref s, ref t, transform);
            result[i * 2] = s;
            result[i * 2 + 1] = t;
        }
        return result;
    }
}
