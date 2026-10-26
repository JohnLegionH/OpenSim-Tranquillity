/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using MeshOptimizer;

namespace OpenSim.MeshAsset;

/// <summary>
/// Simplifies one indexed triangle list with meshoptimizer while keeping chosen vertices exactly where they are (and so with the texture
/// coordinates they have). Only vertices of the input are ever used by the output, so no corner gets a position or a coordinate the input
/// did not have. meshoptimizer (MIT, Copyright (c) Arseny Kapoulkine) is called through Alimer.Bindings.MeshOptimizer (MIT, Copyright (c)
/// Amer Koleci and Contributors); see ThirdPartyLicenses.md.
/// </summary>
public static class SeamSimplifier
{
    /// <summary>
    /// The indices of a simplified triangle list, at most about <paramref name="targetIndices"/> of them (fewer collapse when locked vertices
    /// stand in the way). <paramref name="positions"/> holds x, y, z and <paramref name="uvs"/> u, v for each vertex; a coordinate difference
    /// counts as much as <paramref name="uvWeight"/> times the same difference in position when the simplifier picks what to collapse.
    /// Returns null when nothing usable came out; the caller keeps the input then.
    /// </summary>
    public static unsafe uint[]? Simplify(float[] positions, float[] uvs, uint[] indices, bool[] locked, int targetIndices, float uvWeight)
    {
        int vertexCount = positions.Length / 3;
        if (indices.Length < 3 || vertexCount < 3)
            return null;
        byte[] lockBytes = new byte[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            lockBytes[i] = (byte)(locked[i] ? 1 : 0);
        float[] weights = { uvWeight, uvWeight };
        uint[] output = new uint[indices.Length];
        float error = 0;
        int written;
        fixed (uint* dst = output) fixed (uint* idx = indices) fixed (float* p = positions) fixed (float* a = uvs) fixed (float* w = weights) fixed (byte* l = lockBytes)
        {
            written = (int)Meshopt.SimplifyWithAttributes(dst, idx, (UIntPtr)indices.Length, p, (UIntPtr)vertexCount, (UIntPtr)12, a, (UIntPtr)8, w, (UIntPtr)2, l,
                (UIntPtr)Math.Clamp(targetIndices, 3, indices.Length), 1f, SimplificationOptions.None, &error);
        }
        if (written < 3)
            return null;
        return output.AsSpan(0, written).ToArray();
    }
}
