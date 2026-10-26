/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Inspection;
using OpenSim.MeshAsset;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>The streaming cost the viewer shows for a plain prim (not yet written).</summary>
public static class PrimStreaming
{
    /// <summary>Triangles the viewer estimates for the prim at its four levels of detail, lowest first.</summary>
    public static int[] TriangleCounts(ShapeData shape) => throw new NotImplementedException();

    /// <summary>The viewer's streaming cost for the prim at the given size.</summary>
    public static double Cost(ShapeData shape, System.Numerics.Vector3 scale) => throw new NotImplementedException();
}
