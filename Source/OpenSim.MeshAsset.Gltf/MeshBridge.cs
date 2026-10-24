/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using OpenSim.MeshAsset;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace OpenSim.MeshAsset.Gltf;

/// <summary>What the glTF to mesh-asset conversion changed, trimmed or could not carry.</summary>
public sealed class BridgeReport
{
    public List<string> Notes { get; } = new();
    public int FacesInFile { get; set; }
    public int FacesWritten { get; set; }
    public int FacesDropped { get; set; }
    public int PrimitivesSplit { get; set; }
    public int Vertices { get; set; }
    public int VerticesTrimmedToFour { get; set; }
    public double LargestDroppedWeight { get; set; }
    public int VerticesWithoutWeight { get; set; }
    public int JointsInFile { get; set; }
    public int JointsWritten { get; set; }
    public int JointsDropped { get; set; }
    public Vector3 NormalizedSize { get; set; }
    public Vector3 NormalizedCentre { get; set; }
}

/// <summary>
/// Converts a skinned (or plain) glTF model into a mesh asset the way the Second Life viewer's glTF loader does:
/// glTF is Y up and SL is Z up, so (x, y, z) becomes (x, -z, y); V is flipped; a skinned mesh has its node
/// transform baked into the vertices; the inverse bind matrices are rotated into the SL frame; the model is
/// normalised into a unit box and the bind shape matrix undoes that.
/// </summary>
public static class GltfToMesh
{
    public const int MaxFaces = 8;
    public const int MaxFaceVertices = 65525;      // the viewer splits a face here (llgltfloader.cpp VERTEX_LIMIT)
    public const int MaxJoints = 110;
    public const int MaxInfluences = 4;

    /// <summary>glTF (x, y, z) to SL (x, -z, y), in the row-vector layout of System.Numerics.</summary>
    public static readonly Matrix4x4 Rotation = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1);

    public static (MeshAssetModel Model, BridgeReport Report) Load(string path, Func<string, bool>? isKnownJoint = null, string? ignoredJoint = null) =>
        Convert(ModelRoot.Load(path), isKnownJoint, ignoredJoint);

    public static (MeshAssetModel Model, BridgeReport Report) Convert(ModelRoot root, Func<string, bool>? isKnownJoint = null, string? ignoredJoint = null)
    {
        var report = new BridgeReport();
        var meshNodes = root.LogicalNodes.Where(n => n.Mesh != null).ToList();
        if (meshNodes.Count == 0) throw new InvalidDataException("the file has no mesh");
        var skins = meshNodes.Select(n => n.Skin).Distinct().ToList();
        if (skins.Count > 1) throw new InvalidDataException("the mesh nodes use different skins (or some use none); encode them one file each");
        var skin = skins[0];
        bool skinned = skin != null;

        // 1. vertices, in SL axes, split to the per-face vertex limit
        var rawFaces = new List<RawFace>();
        foreach (var node in meshNodes)
        {
            // The viewer bakes the node's world transform into a skinned mesh's vertices; for a plain mesh it
            // keeps it in the object instance, which a bare asset does not have, so it is baked here too.
            var world = node.WorldMatrix * Rotation;
            bool flip = new Matrix4x4(world.M11, world.M12, world.M13, 0, world.M21, world.M22, world.M23, 0, world.M31, world.M32, world.M33, 0, 0, 0, 0, 1).GetDeterminant() < 0;
            Matrix4x4.Invert(world, out var inv);
            var normalMatrix = Matrix4x4.Transpose(inv);
            foreach (var prim in node.Mesh.Primitives)
            {
                report.FacesInFile++;
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                {
                    report.Notes.Add($"primitive {report.FacesInFile - 1} is not a triangle list ({prim.DrawPrimitiveType}); skipped");
                    continue;
                }
                rawFaces.AddRange(ReadPrimitive(prim, world, normalMatrix, flip, skinned, report));
            }
        }

        // 2. the 8-face limit
        if (rawFaces.Count > MaxFaces)
        {
            report.FacesDropped = rawFaces.Count - MaxFaces;
            report.Notes.Add($"{rawFaces.Count} faces after splitting; the asset allows {MaxFaces}. The last {report.FacesDropped} were dropped.");
            rawFaces.RemoveRange(MaxFaces, rawFaces.Count - MaxFaces);
        }
        report.FacesWritten = rawFaces.Count;

        // 3. joints: the ones that carry weight, in the file's order
        var model = new MeshAssetModel();
        List<int>? jointMap = null; // glTF joint index -> asset joint index
        if (skin != null)
        {
            report.JointsInFile = skin.JointsCount;
            var legal = new bool[skin.JointsCount];
            for (int i = 0; i < legal.Length; i++)
            {
                var name = skin.GetJoint(i).Joint.Name ?? "";
                legal[i] = name != ignoredJoint && (isKnownJoint == null || isKnownJoint(name));
            }
            var used = new bool[skin.JointsCount];
            foreach (var rf in rawFaces)
                foreach (var v in rf.Influences)
                    foreach (var (j, w) in v)
                        if (w > 0 && j < legal.Length && legal[j]) used[j] = true;
            jointMap = Enumerable.Repeat(-1, skin.JointsCount).ToList();
            var names = new List<string>();
            var ibms = new List<double[]>();
            for (int i = 0; i < skin.JointsCount; i++)
            {
                if (!used[i]) { report.JointsDropped++; continue; }
                var (joint, ibm) = skin.GetJoint(i);
                jointMap[i] = names.Count;
                names.Add(joint.Name ?? $"node{joint.LogicalIndex}");
                Matrix4x4.Invert(Rotation, out var invRot);
                ibms.Add(ToArray(invRot * ibm));
            }
            report.JointsWritten = names.Count;
            if (names.Count > MaxJoints)
                throw new InvalidDataException($"{names.Count} weighted joints; the viewer allows {MaxJoints}");
            if (report.JointsDropped > 0)
                report.Notes.Add($"{report.JointsDropped} of {report.JointsInFile} joints dropped: not weighted, not in the SL skeleton, or the exporter's placeholder");
            model.Skin = new MeshSkin { JointNames = names, InverseBind = ibms };
        }

        // 4. weights: top four by weight, renormalised
        foreach (var rf in rawFaces)
        {
            if (!skinned) continue;
            rf.Face.Weights = new Influence[rf.Face.VertexCount][];
            for (int v = 0; v < rf.Face.VertexCount; v++)
            {
                var kept = rf.Influences[v]
                    .Where(t => t.Weight > 0 && t.Joint < jointMap!.Count && jointMap[t.Joint] >= 0)
                    .OrderByDescending(t => t.Weight).ToList();
                if (kept.Count > MaxInfluences)
                {
                    report.VerticesTrimmedToFour++;
                    report.LargestDroppedWeight = Math.Max(report.LargestDroppedWeight, kept[MaxInfluences].Weight);
                    kept = kept.Take(MaxInfluences).ToList();
                }
                if (kept.Count == 0) report.VerticesWithoutWeight++;
                float total = kept.Sum(t => t.Weight);
                rf.Face.Weights[v] = kept.Select(t => new Influence((byte)jointMap![t.Joint], total > 0 ? t.Weight / total : t.Weight)).ToArray();
            }
        }
        if (report.VerticesWithoutWeight > 0)
            report.Notes.Add($"{report.VerticesWithoutWeight} vertices carry no usable weight; they are written with an empty influence list, which the viewer reads as full weight on joint 0");
        if (report.VerticesTrimmedToFour > 0)
            report.Notes.Add($"{report.VerticesTrimmedToFour} vertices had more than {MaxInfluences} influences; the lightest were dropped (heaviest dropped weight {report.LargestDroppedWeight:0.####}) and the rest renormalised");

        // 5. normalise into the unit box and, for a skinned mesh, undo it in the bind shape matrix
        var all = rawFaces.SelectMany(r => r.Face.Positions).ToList();
        if (all.Count == 0) throw new InvalidDataException("the file has no triangles");
        var min = all.Aggregate(Vector3.Min);
        var max = all.Aggregate(Vector3.Max);
        var size = max - min;
        if (Math.Abs(size.X) < 1e-5f) size.X = 1;
        if (Math.Abs(size.Y) < 1e-5f) size.Y = 1;
        if (Math.Abs(size.Z) < 1e-5f) size.Z = 1;
        var centre = (min + max) * 0.5f;
        report.NormalizedSize = size;
        report.NormalizedCentre = centre;
        model.High = new MeshLod();
        foreach (var rf in rawFaces)
        {
            var f = rf.Face;
            for (int i = 0; i < f.Positions.Length; i++) f.Positions[i] = (f.Positions[i] - centre) / size;
            if (f.Normals != null)
                for (int i = 0; i < f.Normals.Length; i++)
                {
                    var n = f.Normals[i] * size;
                    f.Normals[i] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : n;
                }
            f.NormalizedScale = size;
            model.High.Faces.Add(f);
            report.Vertices += f.VertexCount;
        }
        if (model.Skin != null)
            model.Skin.BindShape = ToArray(Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(centre));
        else
            report.Notes.Add($"unskinned: vertices are normalised to a unit box; the object needs scale {size.X:0.####}, {size.Y:0.####}, {size.Z:0.####} and centre {centre.X:0.####}, {centre.Y:0.####}, {centre.Z:0.####} when it is rezzed");
        report.Notes.Add("alt_inverse_bind_matrix and pelvis_offset are not written: no joint position override is derived from the file (the viewer derives it from the SL skeleton)");
        report.Notes.Add("the viewer's X/Y axis swap heuristic (checkForXYrotation) is not applied");
        return (model, report);
    }

    sealed class RawFace
    {
        public required MeshFace Face { get; init; }
        /// <summary>Per vertex, every (glTF joint index, weight) from every JOINTS_n/WEIGHTS_n set.</summary>
        public required (int Joint, float Weight)[][] Influences { get; init; }
    }

    static double[] ToArray(Matrix4x4 m) => new double[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    static IEnumerable<RawFace> ReadPrimitive(MeshPrimitive prim, Matrix4x4 world, Matrix4x4 normalMatrix, bool flip, bool skinned, BridgeReport report)
    {
        var posAcc = prim.GetVertexAccessor("POSITION") ?? throw new InvalidDataException("a primitive has no POSITION");
        var pos = posAcc.AsVector3Array();
        int n = pos.Count;
        var nrmAcc = prim.GetVertexAccessor("NORMAL");
        var nrm = nrmAcc?.AsVector3Array();
        var uvAcc = prim.GetVertexAccessor("TEXCOORD_0");
        var uv = uvAcc?.AsVector2Array();

        var infl = new List<(int, float)>[n];
        if (skinned)
        {
            for (int i = 0; i < n; i++) infl[i] = new();
            for (int set = 0; ; set++)
            {
                var ja = prim.GetVertexAccessor($"JOINTS_{set}");
                var wa = prim.GetVertexAccessor($"WEIGHTS_{set}");
                if (ja == null || wa == null) break;
                var js = ja.AsVector4Array();
                var ws = wa.AsVector4Array();
                for (int i = 0; i < n; i++)
                {
                    infl[i].Add(((int)js[i].X, ws[i].X)); infl[i].Add(((int)js[i].Y, ws[i].Y));
                    infl[i].Add(((int)js[i].Z, ws[i].Z)); infl[i].Add(((int)js[i].W, ws[i].W));
                }
            }
        }

        var sl = new Vector3[n];
        Vector3[]? sln = new Vector3[n];
        var slu = uv == null ? null : new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            sl[i] = Vector3.Transform(pos[i], world);
            var nv = nrm == null ? Vector3.UnitZ : nrm[i];
            var tn = Vector3.TransformNormal(nv, normalMatrix);
            sln[i] = tn.LengthSquared() > 0 ? Vector3.Normalize(tn) : tn;
            if (slu != null) slu[i] = new Vector2(uv![i].X, 1f - uv[i].Y);
        }
        if (nrm == null) report.Notes.Add("a primitive has no normals; (0, 0, 1) was used, as the viewer does");
        if (uv == null) report.Notes.Add("a primitive has no TEXCOORD_0; the face is written without UVs");

        var tris = prim.GetTriangleIndices().ToList();
        // Split at the viewer's vertex limit, keeping whole triangles together.
        var remaps = new List<Dictionary<int, int>> { new() };
        var chunkTris = new List<List<int>> { new() };
        foreach (var (a, b, c) in tris)
        {
            if (remaps[^1].Count + 3 > MaxFaceVertices)
            {
                remaps.Add(new());
                chunkTris.Add(new());
            }
            var map = remaps[^1];
            foreach (var v in flip ? new[] { a, c, b } : new[] { a, b, c })
            {
                if (!map.TryGetValue(v, out var local)) map[v] = local = map.Count;
                chunkTris[^1].Add(local);
            }
        }
        if (remaps.Count > 1)
        {
            report.PrimitivesSplit++;
            report.Notes.Add($"a primitive of {n} vertices was split into {remaps.Count} faces at {MaxFaceVertices} vertices");
        }

        for (int k = 0; k < remaps.Count; k++)
        {
            var order = remaps[k].OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();
            yield return new RawFace
            {
                Face = new MeshFace
                {
                    Positions = order.Select(o => sl[o]).ToArray(),
                    Normals = order.Select(o => sln[o]).ToArray(),
                    Uvs = slu == null ? null : order.Select(o => slu[o]).ToArray(),
                    Indices = chunkTris[k].ToArray(),
                },
                Influences = skinned ? order.Select(o => infl[o].ToArray()).ToArray() : Array.Empty<(int, float)[]>(),
            };
        }
    }
}

/// <summary>Writes a decoded mesh asset out as a GLB for looking at it in a glTF viewer.</summary>
public static class MeshToGltf
{
    /// <summary>
    /// SL axes back to glTF axes and the V flip undone. A skinned mesh has its bind shape matrix applied, so it
    /// shows at full size; a plain mesh is scaled by its NormalizedScale. Skin joints are placed at their bind
    /// position (the inverse of the inverse bind matrix) under the scene root, without the SL hierarchy.
    /// </summary>
    public static ModelRoot Build(MeshAssetModel model, int lod = 3)
    {
        var l = model.Lod(lod) ?? model.High ?? throw new ArgumentException("no such level of detail");
        Matrix4x4.Invert(GltfToMesh.Rotation, out var toGltf);
        var shape = model.Skin == null ? Matrix4x4.Identity : ToMatrix(model.Skin.BindShape);
        bool skinned = model.Skin != null && l.Faces.Any(f => f.Weights != null);

        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("mesh");
        int fi = 0;
        foreach (var f in l.Faces)
        {
            var mat = new MaterialBuilder($"face{fi++}");
            if (f.NoGeometry || f.VertexCount < 3) continue;
            var scale = f.NormalizedScale ?? Vector3.One;
            var prim = mesh.UsePrimitive(mat);
            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> V(int i)
            {
                var p = Vector3.Transform(model.Skin != null ? Vector3.Transform(f.Positions[i], shape) : f.Positions[i] * scale, toGltf);
                var n = f.Normals == null ? Vector3.UnitY : Vector3.Normalize(Vector3.TransformNormal(f.Normals[i], toGltf));
                var t = f.Uvs == null ? Vector2.Zero : new Vector2(f.Uvs[i].X, 1f - f.Uvs[i].Y);
                var w = skinned && f.Weights != null && f.Weights[i].Length > 0
                    ? f.Weights[i].Select(x => ((int)x.Joint, x.Weight)).ToArray()
                    : new[] { (0, 1f) };
                return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(
                    new VertexPositionNormal(p, n), new VertexTexture1(t), new VertexJoints4(w));
            }
            for (int i = 0; i + 2 < f.Indices.Length; i += 3)
                prim.AddTriangle(V(f.Indices[i]), V(f.Indices[i + 1]), V(f.Indices[i + 2]));
        }

        var scene = new SceneBuilder();
        if (skinned)
        {
            var armature = new NodeBuilder("armature");
            var joints = model.Skin!.JointNames.Select((name, i) =>
            {
                Matrix4x4.Invert(GltfToMesh.Rotation * ToMatrix(model.Skin.InverseBind[i]), out var world);
                var joint = armature.CreateNode(name);
                joint.LocalMatrix = world;
                return joint;
            }).ToArray();
            scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, joints);
        }
        else
        {
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        }
        return scene.ToGltf2();
    }

    static Matrix4x4 ToMatrix(double[] m) => new(
        (float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7],
        (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
}
