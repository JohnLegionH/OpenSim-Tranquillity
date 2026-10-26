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
    /// <summary>One face for each slot, in the plan's order of materials; a material past the vertex limit takes more than one.</summary>
    public List<EncoderFace> Faces = new List<EncoderFace>();
    /// <summary>The collision triangles of all members in the merged part's unit box.</summary>
    public PhysicsGeometry Physics = new PhysicsGeometry();
    /// <summary>For each slot, the member face whose texture entry the slot is drawn with (member number, face number).</summary>
    public List<(int Member, int Face)> Template = new List<(int, int)>();
    /// <summary>Where every triangle of every member face went, in order, so the result can be checked against the originals.</summary>
    public List<FaceRun> Runs = new List<FaceRun>();
}

/// <summary>
/// Builds the mesh of a merged part from the geometry of its members. Every vertex goes from its part's unit box into the root part's
/// frame (the part's size, turn and place), and from there into the merged part's unit box. A normal is carried the way the viewer
/// carries it: divided by the size of the part it came from and multiplied by the size of the merged part. Texture coordinates of a
/// bakeable face are baked with the face's mapping and transform (<see cref="FaceUv"/>); an unbakeable default-mapped face keeps its
/// own coordinates and its transform stays on the merged face.
/// </summary>
public static class MergeGeometry
{
    private const float MinSide = 0.001f;

    /// <param name="carrierIsRoot">True when the merged part replaces the linkset's root part: its place is then the root's, so the other parts do not move.</param>
    public static MergedGeometry Build(MergedPartPlan plan, IReadOnlyList<MergeMember> members, bool carrierIsRoot, MergeOptions options = null)
    {
        options ??= new MergeOptions();
        if (members.Count != plan.Members.Count)
            throw new InvalidOperationException("the plan names " + plan.Members.Count + " members and " + members.Count + " were given");

        // 1. every member vertex in the root frame
        List<List<(float[] Pos, float[] Nor)>> rootFrame = new List<List<(float[], float[])>>();
        Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
        void Grow(Vector3 p) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        foreach (MergeMember m in members)
        {
            List<(float[], float[])> faces = new List<(float[], float[])>();
            foreach (EncoderFace f in m.Faces)
            {
                float[] pos = new float[f.Positions.Length], nor = new float[f.Normals.Length];
                for (int i = 0; i < f.VertexCount; i++)
                {
                    Vector3 p = ToRoot(m, new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]));
                    Vector3 n = Vector3.Normalize(Vector3.Transform(new Vector3(f.Normals[i * 3], f.Normals[i * 3 + 1], f.Normals[i * 3 + 2]) / m.Scale, m.Rotation));
                    pos[i * 3] = p.X; pos[i * 3 + 1] = p.Y; pos[i * 3 + 2] = p.Z;
                    nor[i * 3] = n.X; nor[i * 3 + 1] = n.Y; nor[i * 3 + 2] = n.Z;
                    Grow(p);
                }
                faces.Add((pos, nor));
            }
            rootFrame.Add(faces);
            if (m.Physics != null)
                for (int i = 0; i < m.Physics.VertexCount; i++)
                    Grow(ToRoot(m, new Vector3(m.Physics.Positions[i * 3], m.Physics.Positions[i * 3 + 1], m.Physics.Positions[i * 3 + 2])));
        }
        if (lo.X > hi.X)
            throw new InvalidOperationException("the members of the merged part have no geometry");

        MergedGeometry g = new MergedGeometry();
        if (carrierIsRoot)
        {
            // the merged part sits where the root sits, so its box reaches as far as the geometry does on either side of the root's origin
            g.Position = Vector3.Zero;
            g.Scale = Vector3.Max(new Vector3(MinSide), Vector3.Max(Vector3.Abs(lo), Vector3.Abs(hi)) * 2f);
        }
        else
        {
            g.Position = (lo + hi) * 0.5f;
            g.Scale = Vector3.Max(new Vector3(MinSide), hi - lo);
        }

        // 2. the faces, one merged face (or more, past the vertex limit) for each material key of the plan
        // The plan counts a material's faces from its vertex total; packing triangles into faces can need one more when a material is
        // close to the limit, so the faces built are the ones to count (the caller refuses a part that comes to more than eight).
        List<string> keys = new List<string>();
        foreach (SlotPlan s in plan.Slots)
            if (!keys.Contains(s.MaterialKey))
                keys.Add(s.MaterialKey);
        int slotBase = 0;
        foreach (string key in keys)
        {
            List<EncoderFace> parts = BuildKey(key, slotBase, members, rootFrame, g, options.MaxFaceVertices, out (int, int) template);
            foreach (EncoderFace part in parts)
            {
                g.Faces.Add(part);
                g.Template.Add(template);
            }
            slotBase += parts.Count;
        }

        // 3. the collision triangles
        List<float> pp = new List<float>();
        List<int> pi = new List<int>();
        foreach (MergeMember m in members)
        {
            if (m.Physics == null)
                continue;
            int baseVertex = pp.Count / 3;
            for (int i = 0; i < m.Physics.VertexCount; i++)
            {
                Vector3 s = (ToRoot(m, new Vector3(m.Physics.Positions[i * 3], m.Physics.Positions[i * 3 + 1], m.Physics.Positions[i * 3 + 2])) - g.Position) / g.Scale;
                pp.Add(s.X); pp.Add(s.Y); pp.Add(s.Z);
            }
            foreach (int index in m.Physics.Indices)
                pi.Add(baseVertex + index);
        }
        g.Physics = new PhysicsGeometry { Positions = pp.ToArray(), Indices = pi.ToArray() };
        return g;
    }

    /// <summary>A point of the member's unit box in the root part's frame.</summary>
    public static Vector3 ToRoot(MergeMember m, Vector3 unit) => m.Offset + Vector3.Transform(unit * m.Scale, m.Rotation);

    private static List<EncoderFace> BuildKey(string key, int slotBase, IReadOnlyList<MergeMember> members,
        List<List<(float[] Pos, float[] Nor)>> rootFrame, MergedGeometry g, int maxVertices, out (int, int) template)
    {
        template = (-1, -1);
        List<EncoderFace> parts = new List<EncoderFace>();
        Dictionary<(int, int, int, int, int, int, int, int), uint> seen = new Dictionary<(int, int, int, int, int, int, int, int), uint>();
        List<float> pos = new List<float>(), nor = new List<float>(), uv = new List<float>();
        List<uint> index = new List<uint>();

        void Close()
        {
            parts.Add(new EncoderFace { Positions = pos.ToArray(), Normals = nor.ToArray(), Uvs = uv.ToArray(), Indices = index.ToArray() });
            seen.Clear();
            pos.Clear(); nor.Clear(); uv.Clear(); index.Clear();
        }

        for (int mi = 0; mi < members.Count; mi++)
        {
            MergeMember m = members[mi];
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                MergeFace info = m.FaceInfo[fi];
                if (!string.Equals(info.SlotKey, key, StringComparison.Ordinal))
                    continue;
                if (template.Item1 < 0)
                    template = (mi, fi);
                EncoderFace f = m.Faces[fi];
                if (f.IsEmpty)
                    continue;
                float[] uvs = info.Bakeable
                    ? FaceUv.Bake(f.Positions, f.Normals, f.Uvs, info.DrawnTransform, info.TexGen == FaceBaking.PlanarMapping, m.Scale)
                    : f.Uvs;
                (float[] rp, float[] rn) = rootFrame[mi][fi];
                // the face in the merged part's unit box
                float[] sp = new float[rp.Length], sn = new float[rn.Length];
                for (int i = 0; i < f.VertexCount; i++)
                {
                    Vector3 p = (new Vector3(rp[i * 3], rp[i * 3 + 1], rp[i * 3 + 2]) - g.Position) / g.Scale;
                    Vector3 n = Vector3.Normalize(new Vector3(rn[i * 3], rn[i * 3 + 1], rn[i * 3 + 2]) * g.Scale);
                    sp[i * 3] = p.X; sp[i * 3 + 1] = p.Y; sp[i * 3 + 2] = p.Z;
                    sn[i * 3] = n.X; sn[i * 3 + 1] = n.Y; sn[i * 3 + 2] = n.Z;
                }

                int runStart = index.Count, runSource = 0;
                for (int t = 0; t < f.Indices.Length; t += 3)
                {
                    // a triangle that might not fit starts a new face
                    if (pos.Count / 3 + 3 > maxVertices && index.Count > 0)
                    {
                        if (index.Count > runStart)
                            g.Runs.Add(new FaceRun(mi, fi, slotBase + parts.Count, runStart, index.Count - runStart, runSource));
                        Close();
                        runStart = 0;
                        runSource = t;
                    }
                    for (int c = 0; c < 3; c++)
                    {
                        int v = (int)f.Indices[t + c];
                        var k = (Bits(sp[v * 3]), Bits(sp[v * 3 + 1]), Bits(sp[v * 3 + 2]), Bits(sn[v * 3]), Bits(sn[v * 3 + 1]), Bits(sn[v * 3 + 2]), Bits(uvs[v * 2]), Bits(uvs[v * 2 + 1]));
                        if (!seen.TryGetValue(k, out uint at))
                        {
                            at = (uint)(pos.Count / 3);
                            seen[k] = at;
                            pos.Add(sp[v * 3]); pos.Add(sp[v * 3 + 1]); pos.Add(sp[v * 3 + 2]);
                            nor.Add(sn[v * 3]); nor.Add(sn[v * 3 + 1]); nor.Add(sn[v * 3 + 2]);
                            uv.Add(uvs[v * 2]); uv.Add(uvs[v * 2 + 1]);
                        }
                        index.Add(at);
                    }
                }
                if (index.Count > runStart)
                    g.Runs.Add(new FaceRun(mi, fi, slotBase + parts.Count, runStart, index.Count - runStart, runSource));
            }
        }
        if (index.Count > 0 || parts.Count == 0)
            Close();
        return parts;
    }

    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x + 0f);
}
