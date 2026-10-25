/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.GltfExport;

/// <summary>One face after cleaning: an indexed triangle list.</summary>
public sealed class CleanedFace
{
    /// <summary>x, y, z for each vertex.</summary>
    public float[] Positions;
    /// <summary>x, y, z for each vertex.</summary>
    public float[] Normals;
    /// <summary>u, v for each vertex.</summary>
    public float[] Uvs;
    /// <summary>Three vertex numbers for each triangle.</summary>
    public int[] Indices;
    /// <summary>True when every triangle of the face was degenerate: the face is returned as it came in.</summary>
    public bool KeptAsWas;
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>Not cleaning yet: returns the face as an indexed list without changing it.</summary>
public static class GeometryCleaner
{
    public static CleanedFace Clean(float[] positions, float[] normals, float[] uvs, int[] indices = null)
    {
        int n = positions.Length / 3;
        return new CleanedFace { Positions = positions, Normals = normals, Uvs = uvs, Indices = indices ?? Enumerable.Range(0, n - n % 3).ToArray() };
    }
}
