/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>
/// Decides whether a part's collision triangles are exactly a convex solid, and gives its hull. A merged part carries a hull for each member
/// (a hull list in its physics_convex block) only when every member is exactly convex: a convex hull of a hollow, cut or concave member would
/// fill what the member leaves empty.
/// </summary>
public static class ConvexPhysics
{
    /// <summary>The most vertices one hull of a hull list may have (a hull list stores each hull's count in a byte, 0 standing for 256).</summary>
    public const int MaxHullVertices = 256;

    /// <summary>
    /// True when the triangles are the closed surface of a convex solid: every edge is shared by exactly two triangles, and the plane of every
    /// triangle has the whole shape on one side of it (within a millionth of the shape's size).
    /// </summary>
    public static bool IsConvex(PhysicsGeometry g) => throw new NotImplementedException();

    /// <summary>The vertices of the convex hull of the shape (at most <see cref="MaxHullVertices"/>), or null when it has none.</summary>
    public static List<Vector3> Hull(PhysicsGeometry g) => throw new NotImplementedException();
}
