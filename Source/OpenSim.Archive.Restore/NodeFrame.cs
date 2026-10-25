/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;

namespace OpenSim.Archive.Restore;

/// <summary>A node's translation, rotation and scale, in glTF axes (Y up).</summary>
public readonly struct NodePose
{
    public readonly Vector3 Translation;
    public readonly Quaternion Rotation;
    public readonly Vector3 Scale;

    public NodePose(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        Translation = translation;
        Rotation = rotation;
        Scale = scale;
    }

    public static NodePose Identity => new NodePose(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>The matrix that places a point given in this pose's own space (row-vector convention: scale, then rotate, then translate).</summary>
    public Matrix4x4 ToMatrix() => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Rotation)) * Matrix4x4.CreateTranslation(Translation);

    public static NodePose FromMatrix(Matrix4x4 m)
    {
        if (Matrix4x4.Decompose(m, out Vector3 s, out Quaternion r, out Vector3 t))
            return new NodePose(t, r, s);
        // a matrix with shear or a zero scale: keep its translation and its columns' lengths
        return new NodePose(m.Translation, Quaternion.Identity, new Vector3(new Vector3(m.M11, m.M12, m.M13).Length(), new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()));
    }
}

/// <summary>
/// The place of every node of a .glb relative to its object node (the node that carries kind "object"), so a part is judged
/// by where it is in the build and not by where Blender put the build in the scene. The object node's own scale is part of
/// the answer: scaling the whole build scales every part; the object node's translation and rotation are not (they are the
/// build's place in the region, <see cref="ObjectPose"/>).
/// </summary>
public sealed class NodeFrame
{
    private readonly GlbFile m_file;
    private readonly int[] m_parent;
    private readonly Matrix4x4?[] m_world;
    private readonly Matrix4x4 m_objectFrameInverse = Matrix4x4.Identity;

    public int ObjectNode { get; } = -1;
    /// <summary>The object node's translation and rotation in the scene (its scale is left out).</summary>
    public NodePose ObjectPose { get; } = NodePose.Identity;

    public NodeFrame(GlbFile file)
    {
        m_file = file;
        m_parent = Enumerable.Repeat(-1, file.Nodes.Count).ToArray();
        m_world = new Matrix4x4?[file.Nodes.Count];
        foreach (GlbNode n in file.Nodes)
            foreach (int c in n.Children)
                if (c >= 0 && c < m_parent.Length)
                    m_parent[c] = n.Index;
        ObjectNode = file.Nodes.FindIndex(n => GlbFile.Str(n.Extras, "kind") == "object");
        if (ObjectNode >= 0)
        {
            Matrix4x4 world = World(ObjectNode);
            NodePose placed = NodePose.FromMatrix(world);
            ObjectPose = new NodePose(placed.Translation, placed.Rotation, Vector3.One);
            Matrix4x4 reference = ObjectPose.ToMatrix();
            if (!Matrix4x4.Invert(reference, out m_objectFrameInverse))
                m_objectFrameInverse = Matrix4x4.Identity;
        }
    }

    public static NodePose FromNode(GlbNode n)
    {
        Vector3 t = n.Translation == null ? Vector3.Zero : new Vector3(n.Translation[0], n.Translation[1], n.Translation[2]);
        Quaternion r = n.Rotation == null ? Quaternion.Identity : new Quaternion(n.Rotation[0], n.Rotation[1], n.Rotation[2], n.Rotation[3]);
        Vector3 s = n.Scale == null ? Vector3.One : new Vector3(n.Scale[0], n.Scale[1], n.Scale[2]);
        return new NodePose(t, r, s);
    }

    private Matrix4x4 World(int index)
    {
        if (m_world[index].HasValue)
            return m_world[index].Value;
        Matrix4x4 local = FromNode(m_file.Nodes[index]).ToMatrix();
        int guard = 0;
        int parent = m_parent[index];
        Matrix4x4 world = local;
        while (parent >= 0 && guard++ < 1000)
        {
            world *= FromNode(m_file.Nodes[parent]).ToMatrix();
            parent = m_parent[parent];
        }
        m_world[index] = world;
        return world;
    }

    private Matrix4x4 RelativeMatrix(int index) => World(index) * m_objectFrameInverse;

    /// <summary>The node's place relative to the object node (its translation and rotation taken away, its scale kept).</summary>
    public NodePose Relative(int index) => NodePose.FromMatrix(RelativeMatrix(index));

    /// <summary>
    /// The node's place inside the root part's own frame (the root's translation and rotation taken away, its scale left out), which
    /// is what a child part's position and rotation in the archive are. Where the node is in the .glb, wherever its parents are,
    /// decides the answer: a child that moved with its root keeps its answer, and a child left in place while the root moved gets a new one.
    /// </summary>
    public NodePose InRootFrame(int index, int rootIndex)
    {
        if (rootIndex < 0 || rootIndex == index)
            return Relative(index);
        NodePose root = NodePose.FromMatrix(RelativeMatrix(rootIndex));
        Matrix4x4 frame = new NodePose(root.Translation, root.Rotation, Vector3.One).ToMatrix();
        if (!Matrix4x4.Invert(frame, out Matrix4x4 inverse))
            return Relative(index);
        return NodePose.FromMatrix(RelativeMatrix(index) * inverse);
    }
}

/// <summary>What changed between two poses of one node.</summary>
public sealed class PoseChange
{
    public bool Moved, Turned, Scaled;
    /// <summary>How far the node moved, in metres.</summary>
    public float Distance;
    /// <summary>After scale divided by before scale, on each glTF axis.</summary>
    public Vector3 ScaleRatio = Vector3.One;

    public bool Any => Moved || Turned || Scaled;

    public string Describe(bool translation = true, bool rotation = true)
    {
        List<string> what = new List<string>();
        if (Moved && translation)
            what.Add("moved " + Distance.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " m");
        if (Turned && rotation)
            what.Add("turned");
        if (Scaled)
            what.Add("resized by " + ScaleRatio.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " x " + ScaleRatio.Z.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " x " + ScaleRatio.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(", ", what);
    }
}

/// <summary>The axis change between glTF (Y up) and the viewer (Z up), and the arithmetic on poses that the restore needs. Plain float arrays go in and out so callers need not use System.Numerics.</summary>
public static class PoseMath
{
    /// <summary>A scale may differ from 1 by this much (relative) and still be no resize; Blender keeps a scale exact, so this only covers float printing.</summary>
    public const float ScaleTolerance = 1e-4f;
    /// <summary>1 - |dot| of two unit quaternions below which they are the same turn.</summary>
    public const float RotationTolerance = 1e-6f;

    public static PoseChange Compare(NodePose before, NodePose after, float positionTolerance)
    {
        PoseChange c = new PoseChange();
        Vector3 d = after.Translation - before.Translation;
        c.Distance = d.Length();
        c.Moved = Math.Max(Math.Abs(d.X), Math.Max(Math.Abs(d.Y), Math.Abs(d.Z))) > positionTolerance;
        Quaternion a = Quaternion.Normalize(before.Rotation), b = Quaternion.Normalize(after.Rotation);
        c.Turned = 1f - Math.Abs(Quaternion.Dot(a, b)) > RotationTolerance;
        c.ScaleRatio = new Vector3(Ratio(before.Scale.X, after.Scale.X), Ratio(before.Scale.Y, after.Scale.Y), Ratio(before.Scale.Z, after.Scale.Z));
        c.Scaled = Math.Abs(c.ScaleRatio.X - 1f) > ScaleTolerance || Math.Abs(c.ScaleRatio.Y - 1f) > ScaleTolerance || Math.Abs(c.ScaleRatio.Z - 1f) > ScaleTolerance;
        return c;
    }

    private static float Ratio(float before, float after) => before != 0f && float.IsFinite(after / before) ? after / before : 1f;

    public static float[] ViewerVector(Vector3 v) => new[] { v.X, -v.Z, v.Y };

    /// <summary>The glTF-axes vector for viewer axes; the inverse of <see cref="ViewerVector"/>.</summary>
    public static Vector3 GltVector(float[] v) => new Vector3(v[0], v[2], -v[1]);

    /// <summary>The glTF-axes rotation for a viewer-axes one (x, y, z, w); the inverse of <see cref="ViewerQuat"/>.</summary>
    public static Quaternion GltQuat(float[] q) => new Quaternion(q[0], q[2], -q[1], q[3]);

    public static float[] ViewerQuat(Quaternion q)
    {
        Quaternion n = Quaternion.Normalize(q);
        return new[] { n.X, -n.Z, n.Y, n.W };
    }

    /// <summary>The viewer-axes size multipliers for a glTF-axes scale ratio.</summary>
    public static float[] ViewerScale(Vector3 ratio) => new[] { ratio.X, ratio.Z, ratio.Y };

    /// <summary>The place of a root part in the scene: its pose inside the build and the build's place, scales left out.</summary>
    public static NodePose RootPlace(NodePose rootInBuild, NodePose build)
    {
        Matrix4x4 m = new NodePose(rootInBuild.Translation, rootInBuild.Rotation, Vector3.One).ToMatrix() * new NodePose(build.Translation, build.Rotation, Vector3.One).ToMatrix();
        NodePose p = NodePose.FromMatrix(m);
        return new NodePose(p.Translation, p.Rotation, Vector3.One);
    }

    /// <summary>
    /// The linkset's new place in the region. A build exported in world placement carries the region place in its object node, so the
    /// root's new place is the answer; a build exported in root-part placement is drawn in the root's own frame, so the change is
    /// applied to the root's old place.
    /// </summary>
    public static void GroupPlace(bool world, float[] oldPosition, float[] oldRotation, NodePose newPlace, out float[] position, out float[] rotation)
    {
        float[] t = ViewerVector(newPlace.Translation), r = ViewerQuat(newPlace.Rotation);
        if (world)
        {
            position = t;
            rotation = r;
            return;
        }
        Quaternion old = Quaternion.Normalize(new Quaternion(oldRotation[0], oldRotation[1], oldRotation[2], oldRotation[3]));
        Vector3 shift = Vector3.Transform(new Vector3(t[0], t[1], t[2]), old);
        Quaternion turned = Quaternion.Normalize(old * new Quaternion(r[0], r[1], r[2], r[3]));
        position = new[] { oldPosition[0] + shift.X, oldPosition[1] + shift.Y, oldPosition[2] + shift.Z };
        rotation = new[] { turned.X, turned.Y, turned.Z, turned.W };
    }
}
