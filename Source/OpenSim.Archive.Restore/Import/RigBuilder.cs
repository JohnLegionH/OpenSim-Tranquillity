/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Numerics;
using OpenSim.MeshAsset;

namespace OpenSim.Archive.Restore.Import;

/// <summary>
/// The skin of a rigged part, worked out from the skins of its four .glb files: the joints (by the viewer's names), their inverse bind matrices and,
/// when asked for, their positions. <see cref="RigBuilder.Apply"/> then rewrites each file's weights to the numbers of this joint list.
/// </summary>
internal sealed class RigPlan
{
    /// <summary>The skin block, with the bind shape matrix still the identity; <see cref="RigBuilder.SetBindShape"/> fills it once the part's box is known.</summary>
    public MeshSkin Skin = new MeshSkin();
    /// <summary>For each level of detail, the final joint number of each joint of that level's skin, or -1 for a joint no vertex uses.</summary>
    public int[][] JointMap = new int[4][];
    /// <summary>For each level of detail, the viewer's name of each joint of that level's skin.</summary>
    public string[][] Canonical = new string[4][];
    public readonly List<string> Notes = new List<string>();
    public int TrimmedVertices;
    public float LargestDropped;
}

/// <summary>
/// Reads the glTF skins of a part's levels of detail into one mesh skin. The rules follow the viewer's own model loader (which reads the same data from a
/// glTF file): joints named as the viewer names them, at most 110 joints, at most four influences a vertex, and every vertex weighted.
/// </summary>
internal static class RigBuilder
{
    /// <summary>The most joints one mesh may weight (the viewer's limit for one rigged mesh).</summary>
    public const int MaxJoints = 110;
    public const float MinimumWeight = 0.001f;

    // glTF (x, y, z) to the viewer's (x, -z, y), as a matrix for row vectors
    private static readonly Matrix4x4 s_toViewer = new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);

    private static Matrix4x4 FromGltf(float[] m) => new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    private static double[] ToAsset(Matrix4x4 m) => new double[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    private static Vector3 ToViewerAxes(Vector3 v) => new Vector3(v.X, -v.Z, v.Y);

    /// <summary>
    /// The nodes that make up skeletons: every joint of a skin, the nodes above them, and what hangs below those, except the nodes that carry a mesh.
    /// A joint node has a position of its own, so the check that refuses moved nodes leaves these alone.
    /// </summary>
    public static HashSet<int> SkeletonNodes(GlbFile file)
    {
        int[] parent = Parents(file);
        HashSet<int> roots = new HashSet<int>();
        foreach (GlbSkin skin in file.Skins)
            foreach (int joint in skin.Joints)
            {
                int n = joint;
                while (n >= 0 && n < parent.Length && parent[n] >= 0)
                    n = parent[n];
                roots.Add(n);
            }
        HashSet<int> result = new HashSet<int>();
        Stack<int> todo = new Stack<int>(roots);
        while (todo.Count > 0)
        {
            int n = todo.Pop();
            if (n < 0 || n >= file.Nodes.Count || !result.Add(n))
                continue;
            foreach (int c in file.Nodes[n].Children)
                todo.Push(c);
        }
        result.RemoveWhere(n => file.Nodes[n].Mesh >= 0);
        return result;
    }

    private static int[] Parents(GlbFile file)
    {
        int[] parent = Enumerable.Repeat(-1, file.Nodes.Count).ToArray();
        for (int n = 0; n < file.Nodes.Count; n++)
            foreach (int c in file.Nodes[n].Children)
                if (c >= 0 && c < parent.Length)
                    parent[c] = n;
        return parent;
    }

    private static Matrix4x4 Local(GlbNode n)
    {
        Matrix4x4 m = Matrix4x4.Identity;
        if (n.Scale != null)
            m *= Matrix4x4.CreateScale(n.Scale[0], n.Scale[1], n.Scale[2]);
        if (n.Rotation != null && n.Rotation.Length >= 4)
            m *= Matrix4x4.CreateFromQuaternion(new Quaternion(n.Rotation[0], n.Rotation[1], n.Rotation[2], n.Rotation[3]));
        if (n.Translation != null)
            m *= Matrix4x4.CreateTranslation(n.Translation[0], n.Translation[1], n.Translation[2]);
        return m;
    }

    private static Vector3 WorldPosition(GlbFile file, int[] parent, int node)
    {
        Matrix4x4 world = Matrix4x4.Identity;
        for (int n = node, guard = 0; n >= 0 && guard < 10000; n = parent[n], guard++)
            world *= Local(file.Nodes[n]);
        return world.Translation;
    }

    // ------------------------------------------------------------------ which way the model faces

    // One counter-clockwise quarter turn about the avatar's vertical axis, in glTF axes: (x, y, z) to (z, y, -x). The viewer's x, y, z are glTF's x, -z, y, so
    // this is the viewer's (x, y) to (-y, x).
    private static float[] Quarter(float[] v, int times)
    {
        float x = v[0], y = v[1], z = v[2];
        for (int i = 0; i < ((times % 4) + 4) % 4; i++)
            (x, z) = (z, -x);
        return new[] { x, y, z };
    }

    // the row-vector matrix of the inverse of one quarter turn: it takes (x, y, z) to (-z, y, x)
    private static readonly Matrix4x4 s_quarterBack = new Matrix4x4(0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1);

    // an inverse bind matrix after the model is turned: the world is turned first, then the old matrix applies (row vectors)
    private static Matrix4x4 TurnedBack(Matrix4x4 m, int quarterTurns)
    {
        for (int i = 0; i < quarterTurns; i++)
            m = Matrix4x4.Multiply(s_quarterBack, m);
        return m;
    }

    // a rest frame as the file had it before the model was turned (the turned frame is the old one followed by the quarter turn)
    private static Matrix4x4 UndoTurn(Matrix4x4 rest, int quarterTurns)
    {
        for (int i = 0; i < quarterTurns; i++)
            rest = Matrix4x4.Multiply(rest, s_quarterBack);
        return rest;
    }

    /// <summary>
    /// Turns a model that lies a quarter turn (or two or three) off the avatar so that it faces the way the avatar does, and returns the number of counter-clockwise
    /// quarter turns it took. The viewer's own glTF loader does this for a model whose shoulders show it lies a quarter off (checkForXYrotation, coord_system_rotationxy,
    /// in indra/newview/gltf/llgltfloader.cpp); here any pair of left and right joints tells it, and the turn is applied to the vertices, the normals, the inverse bind
    /// matrices and the skeleton's nodes alike, in every level of detail and in the collision shape. The avatar's left is its +y axis. A model with no such pair is
    /// left where it is. Turning a model that is already turned does nothing, so a file shared by several parts is turned once.
    /// </summary>
    public static int Orient(GlbFile[] files, GlbNode[] nodes, GlbFile physicsFile, List<string> notes)
    {
        if (nodes[0].Skin < 0 || nodes[0].Skin >= files[0].Skins.Count)
            return 0;
        GlbSkin skin = files[0].Skins[nodes[0].Skin];
        Dictionary<string, Vector3> left = new Dictionary<string, Vector3>(), right = new Dictionary<string, Vector3>();
        for (int k = 0; k < skin.Joints.Count && k < skin.InverseBind.Count; k++)
        {
            int node = skin.Joints[k];
            string name = ViewerJointNames.Canonical(node >= 0 && node < files[0].Nodes.Count ? files[0].Nodes[node].Name : null);
            if (name == null || !ViewerJointNames.Bones.Contains(name) || !Matrix4x4.Invert(FromGltf(skin.InverseBind[k]), out Matrix4x4 rest))
                continue;
            Vector3 at = ToViewerAxes(rest.Translation);
            if (name.EndsWith("Left", StringComparison.Ordinal))
                left[name.Substring(0, name.Length - 4)] = at;
            else if (name.EndsWith("Right", StringComparison.Ordinal))
                right[name.Substring(0, name.Length - 5)] = at;
        }
        Vector3 across = Vector3.Zero;
        foreach (KeyValuePair<string, Vector3> kv in left)
            if (right.TryGetValue(kv.Key, out Vector3 other))
                across += kv.Value - other;
        if (Math.Sqrt(across.X * across.X + across.Y * across.Y) < 0.01)
            return 0;
        double degrees = Math.Atan2(across.Y, across.X) * 180.0 / Math.PI;      // 90 when the left joints already lie on the avatar's left
        int quarterTurns = (((int)Math.Round((90.0 - degrees) / 90.0) % 4) + 4) % 4;
        if (quarterTurns == 0)
            return 0;

        HashSet<object> done = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (GlbFile file in files.Append(physicsFile).Where(f => f != null).Distinct())
        {
            foreach (GlbMesh mesh in file.Meshes)
                foreach (GlbPrimitive prim in mesh.Primitives)
                {
                    if (!done.Add(prim))
                        continue;
                    TurnVectors(prim.Positions, quarterTurns);
                    TurnVectors(prim.Normals, quarterTurns);
                }
            foreach (GlbSkin s in file.Skins)
            {
                if (!done.Add(s))
                    continue;
                for (int k = 0; k < s.InverseBind.Count; k++)
                    s.InverseBind[k] = ToGltf(TurnedBack(FromGltf(s.InverseBind[k]), quarterTurns));
            }
            HashSet<int> skeleton = SkeletonNodes(file);
            int[] parent = Parents(file);
            foreach (int n in skeleton)
            {
                if (parent[n] >= 0 && skeleton.Contains(parent[n]))
                    continue;   // only the top of each skeleton is turned: the nodes below follow
                GlbNode top = file.Nodes[n];
                if (!done.Add(top))
                    continue;
                top.Translation = Quarter(top.Translation ?? new float[3], quarterTurns);
                double half = quarterTurns * Math.PI / 4.0;       // a rotation about the vertical axis by quarterTurns * 90 degrees
                Quaternion turn = new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
                Quaternion own = top.Rotation != null && top.Rotation.Length >= 4 ? new Quaternion(top.Rotation[0], top.Rotation[1], top.Rotation[2], top.Rotation[3]) : Quaternion.Identity;
                Quaternion both = Quaternion.Concatenate(own, turn);
                top.Rotation = new[] { both.X, both.Y, both.Z, both.W };
            }
        }
        notes.Add("the model was turned " + (quarterTurns * 90).ToString(CultureInfo.InvariantCulture) + " degrees about the vertical axis to face the way the avatar does (its left and right joints lay across the avatar, not along its left)");
        return quarterTurns;
    }

    /// <summary>Turns the vertices and normals of every mesh of a file (a collision shape read on its own, after the model it belongs to was turned).</summary>
    public static void TurnGeometry(GlbFile file, int quarterTurns)
    {
        HashSet<GlbPrimitive> done = new HashSet<GlbPrimitive>();
        foreach (GlbMesh mesh in file.Meshes)
            foreach (GlbPrimitive prim in mesh.Primitives)
                if (done.Add(prim))
                {
                    TurnVectors(prim.Positions, quarterTurns);
                    TurnVectors(prim.Normals, quarterTurns);
                }
    }

    private static void TurnVectors(float[] v, int quarterTurns)
    {
        if (v == null)
            return;
        for (int i = 0; i + 2 < v.Length; i += 3)
        {
            float[] t = Quarter(new[] { v[i], v[i + 1], v[i + 2] }, quarterTurns);
            v[i] = t[0]; v[i + 1] = t[1]; v[i + 2] = t[2];
        }
    }

    private static float[] ToGltf(Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    /// <summary>True when some vertex of the mesh gives the skin's joint number <paramref name="joint"/> a weight the viewer would keep.</summary>
    private static bool BoneCarriesWeight(GlbMesh mesh, int joint)
    {
        foreach (GlbPrimitive prim in mesh.Primitives)
        {
            if (prim.Joints == null || prim.Weights == null)
                continue;
            for (int i = 0; i < prim.Joints.Length && i < prim.Weights.Length; i++)
                if (prim.Joints[i] == joint && prim.Weights[i] >= MinimumWeight)
                    return true;
        }
        return false;
    }

    /// <summary>
    /// The plan for a part whose files carry skins, or null when the full mesh has none. Throws <see cref="ImportException"/> for a skin the viewer could
    /// not use: bones that are no joint of its skeleton, no inverse bind matrices, vertices without weight, a level that weights other joints than the
    /// full mesh, more than 110 joints.
    /// </summary>
    public static RigPlan Plan(GlbFile[] files, GlbNode[] nodes, string[] display, bool jointPositions, int quarterTurns = 0)
    {
        bool[] skinned = nodes.Select(n => n.Skin >= 0).ToArray();
        if (!skinned.Any(s => s))
            return null;
        for (int l = 0; l < 4; l++)
            if (!skinned[l])
                throw new ImportException(display[l] + " has no skin but " + display[Array.IndexOf(skinned, true)] + " has one; every level of detail of a rigged mesh needs the same skin");

        RigPlan plan = new RigPlan();
        GlbSkin[] skins = new GlbSkin[4];
        string[][] canonical = new string[4][];
        List<string> problems = new List<string>();
        for (int l = 0; l < 4; l++)
        {
            if (nodes[l].Skin >= files[l].Skins.Count)
                throw new ImportException(display[l] + " names a skin that it does not have");
            GlbSkin skin = skins[l] = files[l].Skins[nodes[l].Skin];
            if (skin.InverseBind.Count != skin.Joints.Count)
                throw new ImportException(display[l] + ": the skin has " + skin.Joints.Count.ToString(CultureInfo.InvariantCulture) + " joints and " + skin.InverseBind.Count.ToString(CultureInfo.InvariantCulture)
                    + " inverse bind matrices; the mesh cannot be placed on the joints without them");
            canonical[l] = new string[skin.Joints.Count];
            List<string> unmapped = new List<string>();
            for (int k = 0; k < skin.Joints.Count; k++)
            {
                int node = skin.Joints[k];
                string name = node >= 0 && node < files[l].Nodes.Count ? files[l].Nodes[node].Name : null;
                canonical[l][k] = ViewerJointNames.Canonical(name);
                if (canonical[l][k] == null && !unmapped.Contains(name ?? "(unnamed)"))
                    unmapped.Add(name ?? "(unnamed)");
            }
            // a bone that is no joint and carries weight stops the file; one that carries none is left out, with a note
            List<string> weighted = new List<string>(), unweightedBones = new List<string>();
            foreach (string name in unmapped)
            {
                bool carries = false;
                for (int k = 0; k < skin.Joints.Count && !carries; k++)
                {
                    int node = skin.Joints[k];
                    string own = node >= 0 && node < files[l].Nodes.Count ? files[l].Nodes[node].Name : null;
                    if (canonical[l][k] == null && (own ?? "(unnamed)") == name)
                        carries = BoneCarriesWeight(files[l].Meshes[nodes[l].Mesh], k);
                }
                (carries ? weighted : unweightedBones).Add(name);
            }
            if (unweightedBones.Count > 0)
                plan.Notes.Add(display[l] + ": " + string.Join(", ", unweightedBones) + (unweightedBones.Count == 1 ? " is" : " are") + " not a joint of the viewer's skeleton and "
                    + (unweightedBones.Count == 1 ? "carries" : "carry") + " no weight; left out");
            unmapped = weighted;
            if (unmapped.Count > 0)
                problems.Add(display[l] + ": " + unmapped.Count.ToString(CultureInfo.InvariantCulture) + (unmapped.Count == 1 ? " bone is" : " bones are") + " not a joint of the viewer's skeleton ("
                    + string.Join(", ", unmapped) + "); export only the deform bones, named as the viewer names them (mPelvis, mTorso ...)");
        }
        if (problems.Count > 0)
            throw new ImportException(string.Join("; ", problems));

        // which joints keep weight after the four-influence rule, and a check that every vertex has some
        bool[][] used = new bool[4][];
        List<(string Name, float Weight)> kept = new List<(string, float)>();
        for (int l = 0; l < 4; l++)
        {
            used[l] = new bool[skins[l].Joints.Count];
            plan.Canonical[l] = canonical[l];
            int unweighted = 0;
            foreach (GlbPrimitive prim in files[l].Meshes[nodes[l].Mesh].Primitives)
            {
                if (prim.Joints == null)
                    throw new ImportException(display[l] + ": a primitive has no JOINTS_0 and WEIGHTS_0; every primitive of a rigged mesh needs weights");
                int per = prim.InfluencesPerVertex, vertices = prim.Positions.Length / 3;
                for (int v = 0; v < vertices; v++)
                {
                    Heaviest(prim, v, canonical[l], used[l].Length, kept, out _, out _, display[l]);
                    if (kept.Count == 0)
                    {
                        unweighted++;
                        continue;
                    }
                    for (int k = 0; k < per; k++)
                        if (prim.Weights[v * per + k] >= MinimumWeight && kept.Any(x => x.Name == canonical[l][prim.Joints[v * per + k]]))
                            used[l][prim.Joints[v * per + k]] = true;
                }
            }
            if (unweighted > 0)
                throw new ImportException(display[l] + ": " + unweighted.ToString(CultureInfo.InvariantCulture) + " vertices carry no weight; every vertex of a rigged mesh needs weights (a vertex with none would follow the pelvis)");
        }

        // the joint list: the joints the full mesh weights, in the file's order; the other levels may use some of them, not others
        List<string> names = plan.Skin.JointNames;
        List<int> source = new List<int>();
        plan.JointMap[0] = Enumerable.Repeat(-1, skins[0].Joints.Count).ToArray();
        for (int k = 0; k < skins[0].Joints.Count; k++)
        {
            if (!used[0][k])
                continue;
            int at = names.IndexOf(canonical[0][k]);
            if (at < 0)
            {
                at = names.Count;
                names.Add(canonical[0][k]);
                source.Add(k);
            }
            plan.JointMap[0][k] = at;
        }
        for (int l = 1; l < 4; l++)
        {
            plan.JointMap[l] = Enumerable.Repeat(-1, skins[l].Joints.Count).ToArray();
            for (int k = 0; k < skins[l].Joints.Count; k++)
            {
                if (!used[l][k])
                    continue;
                int at = names.IndexOf(canonical[l][k]);
                if (at < 0)
                    throw new ImportException(display[l] + " weights the joint '" + canonical[l][k] + "' that " + display[0] + " does not; every level of detail may use only the joints of the full mesh");
                plan.JointMap[l][k] = at;
            }
        }
        if (names.Count == 0)
            throw new ImportException(display[0] + " has a skin that no vertex uses");
        if (names.Count > MaxJoints)
            throw new ImportException(display[0] + " weights " + names.Count.ToString(CultureInfo.InvariantCulture) + " joints; the viewer allows " + MaxJoints.ToString(CultureInfo.InvariantCulture) + " for one mesh");

        // the matrices: a bone is a point (the viewer's bones are not turned or scaled), so the inverse bind matrix is the move from its rest position; a
        // collision volume or an attachment point keeps the file's matrix, changed to the viewer's axes
        Matrix4x4 fromViewer = Matrix4x4.Transpose(s_toViewer);
        int[] parent = Parents(files[0]);
        List<string> turned = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            int k = source[i];
            Matrix4x4 inverseBind = FromGltf(skins[0].InverseBind[k]);
            if (!Matrix4x4.Invert(inverseBind, out Matrix4x4 rest))
                throw new ImportException(display[0] + ": the inverse bind matrix of '" + names[i] + "' cannot be inverted");
            Matrix4x4 viewerMatrix;
            if (ViewerJointNames.Bones.Contains(names[i]))
            {
                Vector3 position = ToViewerAxes(rest.Translation);
                viewerMatrix = Matrix4x4.CreateTranslation(-position);
                if (IsTurned(UndoTurn(rest, quarterTurns)))
                    turned.Add(names[i]);
            }
            else
                viewerMatrix = fromViewer * inverseBind * s_toViewer;
            plan.Skin.InverseBind.Add(ToAsset(viewerMatrix));

            if (jointPositions)
            {
                int node = skins[0].Joints[k];
                Vector3 own = WorldPosition(files[0], parent, node);
                Vector3 above = parent[node] >= 0 ? WorldPosition(files[0], parent, parent[node]) : Vector3.Zero;
                Vector3 offset = ToViewerAxes(own - above);
                double[] alt = ToAsset(viewerMatrix);
                alt[12] = offset.X; alt[13] = offset.Y; alt[14] = offset.Z;
                (plan.Skin.AltInverseBind ??= new List<double[]>()).Add(alt);
            }
        }
        if (jointPositions)
            plan.Skin.PelvisOffset = 0.0;
        if (turned.Count > 0)
            plan.Notes.Add(turned.Count.ToString(CultureInfo.InvariantCulture) + (turned.Count == 1 ? " bone has" : " bones have") + " a turned rest frame in the file (" + string.Join(", ", turned.Take(6)) + (turned.Count > 6 ? " and " + (turned.Count - 6).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty)
                + "); the viewer's bones are not turned, so only their positions were used");
        return plan;
    }

    // a rest frame is turned when its axes are more than about a degree off the file's
    private static bool IsTurned(Matrix4x4 rest) =>
        Math.Abs(rest.M11 - 1f) > 0.02f || Math.Abs(rest.M22 - 1f) > 0.02f || Math.Abs(rest.M33 - 1f) > 0.02f ||
        Math.Abs(rest.M12) > 0.02f || Math.Abs(rest.M13) > 0.02f || Math.Abs(rest.M21) > 0.02f || Math.Abs(rest.M23) > 0.02f || Math.Abs(rest.M31) > 0.02f || Math.Abs(rest.M32) > 0.02f;

    /// <summary>
    /// Rewrites the weights of one level's primitives to the plan's joint numbers: at most the four heaviest influences, weights under the viewer's
    /// floor dropped, the rest renormalised to add up to one. A primitive then has exactly four entries for each vertex (a weight of 0 is unused).
    /// </summary>
    public static void Apply(RigPlan plan, int level, GlbFile file, GlbNode node)
    {
        string[] canonical = plan.Canonical[level];
        foreach (GlbPrimitive prim in file.Meshes[node.Mesh].Primitives)
        {
            int vertices = prim.Positions.Length / 3;
            int[] joints = new int[vertices * 4];
            float[] weights = new float[vertices * 4];
            List<(string Name, float Weight)> kept = new List<(string, float)>(4);
            for (int v = 0; v < vertices; v++)
            {
                Heaviest(prim, v, canonical, canonical.Length, kept, out bool trimmed, out float dropped, null);
                if (trimmed)
                {
                    plan.TrimmedVertices++;
                    plan.LargestDropped = Math.Max(plan.LargestDropped, dropped);
                }
                for (int k = 0; k < kept.Count; k++)
                {
                    joints[v * 4 + k] = plan.Skin.JointNames.IndexOf(kept[k].Name);
                    weights[v * 4 + k] = kept[k].Weight;
                }
            }
            prim.Joints = joints;
            prim.Weights = weights;
            prim.InfluencesPerVertex = 4;
        }
    }

    /// <summary>
    /// The influences a vertex keeps: those under the viewer's weight floor dropped, joints with the same name added together, the four heaviest kept and
    /// their weights made to add up to one. An empty result is a vertex with no weight.
    /// </summary>
    private static void Heaviest(GlbPrimitive prim, int vertex, string[] canonical, int jointCount, List<(string Name, float Weight)> kept, out bool trimmed, out float dropped, string display)
    {
        int per = prim.InfluencesPerVertex;
        List<(string Name, float Weight)> all = new List<(string, float)>(per);
        for (int k = 0; k < per; k++)
        {
            float w = prim.Weights[vertex * per + k];
            if (!(w >= MinimumWeight))
                continue;
            int j = prim.Joints[vertex * per + k];
            if (j < 0 || j >= jointCount)
                throw new ImportException((display ?? "a .glb file") + ": a vertex names joint " + j.ToString(CultureInfo.InvariantCulture) + " but the skin has " + jointCount.ToString(CultureInfo.InvariantCulture) + " joints");
            int at = all.FindIndex(x => x.Name == canonical[j]);
            if (at >= 0)
                all[at] = (canonical[j], all[at].Weight + w);
            else
                all.Add((canonical[j], w));
        }
        all = all.OrderByDescending(x => x.Weight).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        trimmed = all.Count > 4;
        dropped = trimmed ? all[4].Weight : 0f;
        if (trimmed)
            all.RemoveRange(4, all.Count - 4);
        float total = all.Sum(x => x.Weight);
        kept.Clear();
        foreach ((string name, float weight) in all)
            kept.Add((name, weight / total));
    }

    /// <summary>The note about vertices that had more than four influences, or null when none had; call after every level was applied.</summary>
    public static string TrimNote(RigPlan plan) => plan.TrimmedVertices == 0
        ? null
        : plan.TrimmedVertices.ToString(CultureInfo.InvariantCulture) + " vertices had more than four influences; the lightest were dropped (the heaviest dropped weight was "
          + plan.LargestDropped.ToString("0.####", CultureInfo.InvariantCulture) + ") and the rest renormalised";

    /// <summary>
    /// The bind shape matrix: the unit box the vertices are stored in, scaled up to the part's size and moved to its centre (viewer axes). The viewer
    /// applies it to a stored vertex before the joints do; the part's own scale plays no part in a rigged mesh.
    /// </summary>
    public static void SetBindShape(RigPlan plan, Vector3 size, Vector3 centre) =>
        plan.Skin.BindShape = ToAsset(Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(centre));
}
