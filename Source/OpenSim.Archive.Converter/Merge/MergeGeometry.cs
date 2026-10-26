/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>One part of a merged part with its geometry as the converter builds it for a single part: faces in the unit box, in the viewer's texture convention.</summary>
public sealed class MergeMember
{
    public Guid PartId;
    public bool IsRoot;
    /// <summary>The part's size, and its place and turn in the root part's frame (the root itself: zero and identity).</summary>
    public Vector3 Scale = Vector3.One;
    public Vector3 Offset;
    public Quaternion Rotation = Quaternion.Identity;
    /// <summary>One entry for each viewer face of the part, with what the planner read about that face (same order and count).</summary>
    public List<EncoderFace> Faces = new List<EncoderFace>();
    public List<MergeFace> FaceInfo = new List<MergeFace>();
    /// <summary>The part's collision triangles in the unit box.</summary>
    public PhysicsGeometry Physics;
}

/// <summary>Where the triangles of one member face went: a run of indices of one merged face.</summary>
public readonly record struct FaceRun(int Member, int Face, int Slot, int FirstIndex, int IndexCount, int SourceFirstIndex);

public sealed class MergedGeometry
{
    /// <summary>The size of the merged part, and its place in the root part's frame; the merged part is not turned in that frame.</summary>
    public Vector3 Scale;
    public Vector3 Position;
    /// <summary>One face for each slot of the plan, in the plan's order.</summary>
    public List<EncoderFace> Faces = new List<EncoderFace>();
    /// <summary>The collision triangles of all members in the merged part's unit box.</summary>
    public PhysicsGeometry Physics = new PhysicsGeometry();
    /// <summary>For each slot, the member face whose texture entry the slot is drawn with (member number, face number).</summary>
    public List<(int Member, int Face)> Template = new List<(int, int)>();
    /// <summary>Where every triangle of every member face went, in order, so the result can be checked against the originals.</summary>
    public List<FaceRun> Runs = new List<FaceRun>();
}

/// <summary>Builds the mesh of a merged part from the geometry of its members.</summary>
public static class MergeGeometry
{
    /// <param name="carrierIsRoot">True when the merged part replaces the linkset's root part: its place is then the root's, so the other parts do not move.</param>
    public static MergedGeometry Build(MergedPartPlan plan, IReadOnlyList<MergeMember> members, bool carrierIsRoot, MergeOptions options = null) => throw new NotImplementedException();

    /// <summary>A point of the member's unit box in the root part's frame.</summary>
    public static Vector3 ToRoot(MergeMember m, Vector3 unit) => m.Offset + Vector3.Transform(unit * m.Scale, m.Rotation);
}
