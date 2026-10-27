/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A rigged slab as a modelling tool exports one: an armature node with three joints stacked on the y axis (glTF axes), a mesh node bound to a skin
/// of those joints, and four levels of detail (the same grid sizes as ImportKit's slab). The slab spans y 0.95 to 1.35; a vertex's weights depend on its
/// height only, so a test can work out what any vertex must carry from its position. Each call writes its own folder.
/// </summary>
internal static class RigKit
{
    public static readonly string[] DefaultNames = { "mPelvis", "mTorso", "mChest" };

    /// <summary>World height of each joint in the file, glTF axes (the joint's position in the viewer's axes is (0, 0, y)).</summary>
    public static readonly float[] JointY = { 1.0f, 1.1f, 1.25f };

    public const float SlabCentreY = 1.15f, SlabHalfY = 0.2f;

    public sealed class Options
    {
        /// <summary>The name of each of the three joints.</summary>
        public string[] Names = DefaultNames;
        /// <summary>Names of joints added after the three, which no vertex uses.</summary>
        public string[] ExtraJoints = Array.Empty<string>();
        /// <summary>For each level (0 = full mesh, 3 = lowest), the order of the skin's joints as default-joint numbers; null keeps the default order.</summary>
        public int[][] Orders;
        /// <summary>Rotation in degrees about the vertical of each of the three joints' rest frame (the inverse bind matrix then carries the turn).</summary>
        public double[] RotateDegrees = new double[3];
        /// <summary>When set, every vertex carries exactly these weights on the joints 0..n-1 (the extra joints follow the three), instead of the height rule.</summary>
        public float[] Fixed;
        /// <summary>Vertex v carries full weight on joint v modulo the number of joints, instead of the height rule.</summary>
        public bool RoundRobin;
        /// <summary>The first vertex of every level has all weights zero.</summary>
        public bool ZeroWeightVertex;
        /// <summary>The joints of the lowest level are replaced: this joint (a default-joint number) is given a name no other level has.</summary>
        public string LowestLevelOnlyJoint;
        /// <summary>Leave the inverse bind matrices out of the skin.</summary>
        public bool OmitInverseBind;
        /// <summary>Write the joints and weights as unsigned shorts and normalised unsigned shorts instead of bytes and floats.</summary>
        public bool WideAttributes;
    }

    public static string[] FileNames(string name = "rig") => new[] { name + ".glb", name + "_lod2.glb", name + "_lod1.glb", name + "_lod0.glb" };

    /// <summary>The influences a vertex at height y carries under the height rule, as (default joint number, weight).</summary>
    public static (int Joint, float Weight)[] WeightsAt(float y)
    {
        float t = Math.Clamp((y - (SlabCentreY - SlabHalfY)) / (2f * SlabHalfY), 0f, 1f);
        return t < 0.5f
            ? new[] { (0, 1f - 2f * t), (1, 2f * t) }
            : new[] { (1, 2f - 2f * t), (2, 2f * t - 1f) };
    }

    /// <summary>The inverse bind matrix of a joint as the file stores it (column-major, glTF axes): the inverse of a rest frame at height y turned by the given angle.</summary>
    public static float[] InverseBind(float y, double degrees)
    {
        Matrix4x4 world = Matrix4x4.CreateRotationY((float)(degrees * Math.PI / 180.0)) * Matrix4x4.CreateTranslation(0f, y, 0f);
        Matrix4x4.Invert(world, out Matrix4x4 inverse);
        // System.Numerics keeps row vectors with the translation in the last row, which flattens to glTF's column-major layout
        return new[]
        {
            inverse.M11, inverse.M12, inverse.M13, inverse.M14, inverse.M21, inverse.M22, inverse.M23, inverse.M24,
            inverse.M31, inverse.M32, inverse.M33, inverse.M34, inverse.M41, inverse.M42, inverse.M43, inverse.M44,
        };
    }

    public static string Write(Options o = null, string name = "rig", string into = null)
    {
        o ??= new Options();
        string folder = into ?? Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string[] files = FileNames(name);
        for (int level = 0; level < 4; level++)
            File.WriteAllBytes(Path.Combine(folder, files[level]), Glb(o, level));
        return folder;
    }

    public static byte[] Glb(Options o, int level)
    {
        ImportKit.Geo g = new ImportKit.Geo();
        ImportKit.AddBox(g, ImportKit.GridPerLevel[level], _ => true, 0f, SlabCentreY, 0f, 0.2f, SlabHalfY, 0.05f);
        int vertices = g.Pos.Count / 3;
        int jointCount = 3 + o.ExtraJoints.Length;
        int[] order = o.Orders?[level] ?? Enumerable.Range(0, jointCount).ToArray();
        int PositionOf(int defaultJoint) => Array.IndexOf(order, defaultJoint);

        List<byte> bin = new List<byte>();
        JsonArray views = new JsonArray(), accessors = new JsonArray();
        int View(byte[] data)
        {
            while (bin.Count % 4 != 0)
                bin.Add(0);
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Count, ["byteLength"] = data.Length });
            bin.AddRange(data);
            return views.Count - 1;
        }
        int Floats(IList<float> values, int width, string type, bool bounds = false)
        {
            byte[] bytes = new byte[values.Count * 4];
            Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
            JsonObject a = new JsonObject { ["bufferView"] = View(bytes), ["componentType"] = 5126, ["count"] = values.Count / width, ["type"] = type };
            if (bounds)
            {
                JsonArray min = new JsonArray(), max = new JsonArray();
                for (int c = 0; c < width; c++)
                {
                    min.Add(Enumerable.Range(0, values.Count / width).Min(i => values[i * width + c]));
                    max.Add(Enumerable.Range(0, values.Count / width).Max(i => values[i * width + c]));
                }
                a["min"] = min; a["max"] = max;
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }

        // the influences of each vertex: up to four in set 0, the rest in set 1
        float[] j0 = new float[vertices * 4], w0 = new float[vertices * 4], j1 = new float[vertices * 4], w1 = new float[vertices * 4];
        for (int v = 0; v < vertices; v++)
        {
            (int Joint, float Weight)[] list;
            if (o.RoundRobin)
                list = new[] { (v % jointCount, 1f) };
            else if (o.Fixed != null)
                list = o.Fixed.Select((w, i) => (i, w)).ToArray();
            else
                list = WeightsAt(g.Pos[v * 3 + 1]);
            if (o.ZeroWeightVertex && v == 0)
                list = Array.Empty<(int, float)>();
            for (int k = 0; k < list.Length; k++)
            {
                float[] jt = k < 4 ? j0 : j1, wt = k < 4 ? w0 : w1;
                jt[v * 4 + k % 4] = PositionOf(list[k].Joint);
                wt[v * 4 + k % 4] = list[k].Weight;
            }
        }

        JsonObject attributes = new JsonObject
        {
            ["POSITION"] = Floats(g.Pos, 3, "VEC3", true),
            ["NORMAL"] = Floats(g.Nor, 3, "VEC3"),
            ["TEXCOORD_0"] = Floats(g.Uv, 2, "VEC2"),
        };
        void Skinning(string set, float[] joints, float[] weights)
        {
            if (o.WideAttributes)
            {
                byte[] jb = new byte[joints.Length * 2], wb = new byte[weights.Length * 2];
                for (int i = 0; i < joints.Length; i++)
                {
                    BitConverter.TryWriteBytes(jb.AsSpan(i * 2), (ushort)joints[i]);
                    BitConverter.TryWriteBytes(wb.AsSpan(i * 2), (ushort)Math.Round(weights[i] * 65535f));
                }
                accessors.Add(new JsonObject { ["bufferView"] = View(jb), ["componentType"] = 5123, ["count"] = vertices, ["type"] = "VEC4" });
                attributes["JOINTS_" + set] = accessors.Count - 1;
                accessors.Add(new JsonObject { ["bufferView"] = View(wb), ["componentType"] = 5123, ["normalized"] = true, ["count"] = vertices, ["type"] = "VEC4" });
                attributes["WEIGHTS_" + set] = accessors.Count - 1;
            }
            else
            {
                byte[] jb = joints.Select(j => (byte)j).ToArray();
                accessors.Add(new JsonObject { ["bufferView"] = View(jb), ["componentType"] = 5121, ["count"] = vertices, ["type"] = "VEC4" });
                attributes["JOINTS_" + set] = accessors.Count - 1;
                attributes["WEIGHTS_" + set] = Floats(weights, 4, "VEC4");
            }
        }
        Skinning("0", j0, w0);
        if (o.Fixed != null && o.Fixed.Length > 4)
            Skinning("1", j1, w1);

        byte[] idx = new byte[g.Idx.Count * 4];
        Buffer.BlockCopy(g.Idx.ToArray(), 0, idx, 0, idx.Length);
        accessors.Add(new JsonObject { ["bufferView"] = View(idx), ["componentType"] = 5125, ["count"] = g.Idx.Count, ["type"] = "SCALAR" });
        JsonObject prim = new JsonObject { ["attributes"] = attributes, ["indices"] = accessors.Count - 1, ["mode"] = 4 };

        // the joints, as nodes: armature = 0, the default joints 1..3, extra joints 4.., the mesh node last
        string[] jointNames = o.Names.Concat(o.ExtraJoints).ToArray();
        if (level == 3 && o.LowestLevelOnlyJoint != null)
            jointNames[0] = o.LowestLevelOnlyJoint;
        JsonArray nodes = new JsonArray();
        nodes.Add(new JsonObject { ["name"] = "Armature", ["children"] = new JsonArray(1) });
        float[] localY = { JointY[0], JointY[1] - JointY[0], JointY[2] - JointY[1] };
        for (int j = 0; j < jointCount; j++)
        {
            JsonObject n = new JsonObject { ["name"] = jointNames[j], ["translation"] = new JsonArray(0f, j < 3 ? localY[j] : 0.05f, 0f) };
            if (j < 2)
                n["children"] = new JsonArray(j + 2);
            else if (j == 2)
                n["children"] = new JsonArray(Enumerable.Range(4, o.ExtraJoints.Length).Select(i => (JsonNode)i).ToArray());
            nodes.Add(n);
        }
        // an extra joint sits under the chest; the list above gives the chest its children
        int meshNode = nodes.Count;
        nodes.Add(new JsonObject { ["name"] = "slab", ["mesh"] = 0, ["skin"] = 0 });

        float[] ibm = new float[jointCount * 16];
        for (int k = 0; k < jointCount; k++)
        {
            int j = order[k];
            float y = j < 3 ? JointY[j] : JointY[2] + 0.05f;
            double degrees = j < 3 ? o.RotateDegrees[j] : 0.0;
            Array.Copy(InverseBind(y, degrees), 0, ibm, k * 16, 16);
        }
        JsonObject skin = new JsonObject { ["joints"] = new JsonArray(order.Select(j => (JsonNode)(1 + j)).ToArray()), ["skeleton"] = 1 };
        if (!o.OmitInverseBind)
            skin["inverseBindMatrices"] = Floats(ibm, 16, "MAT4");

        JsonObject root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, meshNode) }),
            ["nodes"] = nodes,
            ["meshes"] = new JsonArray(new JsonObject { ["name"] = "slab", ["primitives"] = new JsonArray(prim) }),
            ["skins"] = new JsonArray(skin),
            ["accessors"] = accessors,
            ["bufferViews"] = views,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Count }),
        };
        while (bin.Count % 4 != 0)
            bin.Add(0);
        root["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Count });
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonPadded = (json.Length + 3) & ~3;
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(ms);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonPadded + 8 + bin.Count));
        w.Write((uint)jsonPadded); w.Write(0x4E4F534Au); w.Write(json); for (int i = json.Length; i < jsonPadded; i++) w.Write((byte)0x20);
        w.Write((uint)bin.Count); w.Write(0x004E4942u); w.Write(bin.ToArray());
        return ms.ToArray();
    }
}
