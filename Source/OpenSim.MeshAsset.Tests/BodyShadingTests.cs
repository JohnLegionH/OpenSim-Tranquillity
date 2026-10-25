/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Numerics;
using System.Text;
using OpenSim.MeshAsset.Gltf;
using SharpGLTF.Schema2;
using static OpenSim.MeshAsset.Tests.MotionEquivalenceTests;

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// What shading a rigged body gets from its asset: triangle facing, vertex normals and open edges of the decoded asset
/// against the glTF it was made from, after the turn that makes the model face the way the avatar skeleton does. The
/// viewer lights a rigged mesh with the stored normals taken through the inverse transpose of the bind shape matrix
/// (LLFace::getGeometryVolume), then through the skin matrices, so that is how the asset's normals are read here.
/// </summary>
public class BodyShadingTests
{
    public sealed class Side
    {
        public readonly List<Vector3> Pos = new();
        public readonly List<Vector3> Nrm = new();
        public readonly List<(int Face, int A, int B, int C)> Tris = new();   // global vertex indices
        public readonly List<int> FaceStart = new();
    }

    static string F(double v, string f = "0.00") => v.ToString(f, CultureInfo.InvariantCulture);

    /// <summary>The source's triangles and normals, in the order the asset stores them, in the avatar's frame (after the facing turn).</summary>
    static Side ReadSide(ModelRoot root, int quarterTurns)
    {
        var s = new Side();
        var yaw = Matrix4x4.CreateRotationZ(quarterTurns * MathF.PI / 2);
        int face = 0;
        foreach (var node in root.LogicalNodes.Where(n => n.Mesh != null))
        {
            var world = node.WorldMatrix * GltfToMesh.Rotation * yaw;
            bool flip = new Matrix4x4(world.M11, world.M12, world.M13, 0, world.M21, world.M22, world.M23, 0, world.M31, world.M32, world.M33, 0, 0, 0, 0, 1).GetDeterminant() < 0;
            Matrix4x4.Invert(world, out var inv);
            var nm = Matrix4x4.Transpose(inv);
            foreach (var prim in node.Mesh.Primitives)
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                var pos = prim.GetVertexAccessor("POSITION").AsVector3Array();
                var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var map = new Dictionary<int, int>();
                int baseIndex = s.Pos.Count;
                s.FaceStart.Add(baseIndex);
                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    var t = new int[3];
                    int k = 0;
                    foreach (var v in flip ? new[] { a, c, b } : new[] { a, b, c })
                    {
                        if (!map.TryGetValue(v, out var local))
                        {
                            map[v] = local = s.Pos.Count;
                            s.Pos.Add(Vector3.Transform(pos[v], world));
                            var n = Vector3.TransformNormal(nrm == null ? Vector3.UnitZ : nrm[v], nm);
                            s.Nrm.Add(n.LengthSquared() > 0 ? Vector3.Normalize(n) : n);
                        }
                        t[k++] = local;
                    }
                    s.Tris.Add((face, t[0], t[1], t[2]));
                }
                face++;
            }
        }
        return s;
    }

    /// <summary>The decoded asset's triangles with the bind shape applied to positions and, as the viewer does, its inverse transpose to normals.</summary>
    public static Side ReadAsset(MeshAssetModel model, int lod)
    {
        var s = new Side();
        var bind = SkinningCheck.ToMatrix(model.Skin!.BindShape);
        Matrix4x4.Invert(bind, out var inv);
        var nm = Matrix4x4.Transpose(inv);
        int face = 0;
        foreach (var f in model.Lod(lod)!.Faces)
        {
            if (f.NoGeometry) continue;
            int b0 = s.Pos.Count;
            s.FaceStart.Add(b0);
            for (int v = 0; v < f.VertexCount; v++)
            {
                s.Pos.Add(Vector3.Transform(f.Positions[v], bind));
                var n = Vector3.TransformNormal(f.Normals![v], nm);
                s.Nrm.Add(n.LengthSquared() > 0 ? Vector3.Normalize(n) : n);
            }
            for (int i = 0; i + 2 < f.Indices.Length; i += 3)
                s.Tris.Add((face, b0 + f.Indices[i], b0 + f.Indices[i + 1], b0 + f.Indices[i + 2]));
            face++;
        }
        return s;
    }

    static Vector3 Geo(Side s, (int Face, int A, int B, int C) t) => Vector3.Cross(s.Pos[t.B] - s.Pos[t.A], s.Pos[t.C] - s.Pos[t.A]);

    /// <summary>Triangles whose geometric normal points against the mean of their vertex normals, with the area they cover.</summary>
    static (int Count, double Area) Opposed(Side s)
    {
        int n = 0; double area = 0;
        foreach (var t in s.Tris)
        {
            var g = Geo(s, t);
            if (g.LengthSquared() == 0) continue;
            if (Vector3.Dot(g, s.Nrm[t.A] + s.Nrm[t.B] + s.Nrm[t.C]) < 0) { n++; area += g.Length() / 2; }
        }
        return (n, area);
    }

    /// <summary>Edges used by one triangle only, after joining vertices that sit at the same position. Key: the two welded ids.</summary>
    static (HashSet<(int, int)> Open, int NonManifold, int[] Weld) OpenEdges(Side s, bool weld)
    {
        var ids = new Dictionary<Vector3, int>();
        var wid = new int[s.Pos.Count];
        for (int i = 0; i < wid.Length; i++)
            wid[i] = weld ? (ids.TryGetValue(s.Pos[i], out var id) ? id : ids[s.Pos[i]] = ids.Count) : i;
        var count = new Dictionary<(int, int), int>();
        void Add(int a, int b)
        {
            var k = wid[a] < wid[b] ? (wid[a], wid[b]) : (wid[b], wid[a]);
            count[k] = count.GetValueOrDefault(k) + 1;
        }
        foreach (var t in s.Tris) { Add(t.A, t.B); Add(t.B, t.C); Add(t.C, t.A); }
        return (count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet(), count.Count(kv => kv.Value > 2), wid);
    }

    [SkeletonFact]
    public void TheEncodedBodyShadesAndClosesLikeItsSource()
    {
        var dir = Environment.GetEnvironmentVariable("MESH5_REPORT_DIR") ?? "";
        if (!File.Exists(SkinRefs.GltfModel) || !File.Exists(SkinRefs.GltfAsset)) return;
        var sk = SkinRefs.LoadSkeleton();
        var root = ModelRoot.Load(SkinRefs.GltfModel);
        var quarter = GltfToMesh.Convert(root, n => sk.Find(n) != null, "neutral_bone", new GltfToMesh.JointBinding(sk, false)).Report.QuarterTurns;
        var model = MeshCodec.Decode(File.ReadAllBytes(SkinRefs.GltfAsset));
        var src = ReadSide(root, quarter);
        var ast = ReadAsset(model, 3);
        var sb = new StringBuilder();
        sb.AppendLine($"source: {src.Pos.Count} vertices, {src.Tris.Count} triangles, {src.FaceStart.Count} faces; asset (high): {ast.Pos.Count} vertices, {ast.Tris.Count} triangles, {ast.FaceStart.Count} faces; quarter turns {quarter}");
        Assert.Equal(src.Tris.Count, ast.Tris.Count);

        // 1. winding: the same triangle in both, geometric normal against geometric normal
        var rig = ReadSource(root, sk, quarter);
        string Group(int v) => rig.Influences[v].Length == 0 ? "-" : rig.JointNames[rig.Influences[v].OrderByDescending(x => x.Weight).First().Joint];
        int flipped = 0, degenerate = 0;
        var flippedAreas = new List<float>();
        var flippedBy = new Dictionary<string, int>();
        for (int i = 0; i < src.Tris.Count; i++)
        {
            var a = Geo(src, src.Tris[i]); var b = Geo(ast, ast.Tris[i]);
            if (a.LengthSquared() == 0 || b.LengthSquared() == 0) { degenerate++; continue; }
            if (Vector3.Dot(a, b) < 0) { flipped++; flippedAreas.Add(a.Length() / 2 * 1e6f); var g = Group(src.Tris[i].A); flippedBy[g] = flippedBy.GetValueOrDefault(g) + 1; }
        }
        sb.AppendLine($"1. winding: {flipped} of {src.Tris.Count} triangles face the other way in the asset ({degenerate} degenerate); by region: {string.Join(", ", flippedBy.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value}"))}");
        sb.AppendLine($"   areas of the turned triangles in the source (mm2): {string.Join(", ", flippedAreas.Select(x => F(x, "0.0000")))}; median triangle area {F(src.Tris.Select(t => Geo(src, t).Length() / 2 * 1e6).OrderBy(x => x).ElementAt(src.Tris.Count / 2), "0.00")} mm2");
        var oSrc = Opposed(src); var oAst = Opposed(ast);
        sb.AppendLine($"   geometric normal against the mean vertex normal: source {oSrc.Count} triangles ({F(oSrc.Area * 1e4, "0.0")} cm2), asset {oAst.Count} ({F(oAst.Area * 1e4, "0.0")} cm2)");
        for (int lod = 0; lod < 4; lod++)
        {
            var l = ReadAsset(model, lod);
            var o = Opposed(l);
            sb.AppendLine($"   asset LOD {lod}: {l.Tris.Count} triangles, geometric normal against vertex normals: {o.Count}");
        }

        // 2. vertex normals
        var err = new float[src.Pos.Count];
        for (int i = 0; i < err.Length; i++)
            err[i] = MathF.Acos(Math.Clamp(Vector3.Dot(src.Nrm[i], ast.Nrm[i]), -1f, 1f)) * 180f / MathF.PI;
        var sorted = (float[])err.Clone(); Array.Sort(sorted);
        int worst = Array.IndexOf(err, sorted[^1]);
        sb.AppendLine($"2. normals: angle error largest {F(sorted[^1], "0.000")} deg at vertex {worst} ({Group(worst)}, at {src.Pos[worst].X * 1000:0},{src.Pos[worst].Y * 1000:0},{src.Pos[worst].Z * 1000:0} mm), mean {F(err.Average(), "0.0000")}, p99 {F(sorted[(int)(sorted.Length * 0.99)], "0.000")}, vertices over 1 deg: {err.Count(e => e > 1f)}");

        // 3. open edges
        var (sOpen, sNon, sw) = OpenEdges(src, true);
        var (aOpen, aNon, aw) = OpenEdges(ast, true);
        var (sRawOpen, _, _) = OpenEdges(src, false);
        var (aRawOpen, _, _) = OpenEdges(ast, false);
        // an asset open edge is "added" when the same triangle corner pair is not open in the source (matched through the source's welded ids)
        var cornerSrc = new Dictionary<(int, int), (int, int)>();
        int added = 0;
        var aCount = new Dictionary<(int, int), int>();
        for (int i = 0; i < ast.Tris.Count; i++)
        {
            var ta = ast.Tris[i]; var ts = src.Tris[i];
            foreach (var ((x, y), (sx, sy)) in new[] { ((ta.A, ta.B), (ts.A, ts.B)), ((ta.B, ta.C), (ts.B, ts.C)), ((ta.C, ta.A), (ts.C, ts.A)) })
            {
                var ka = aw[x] < aw[y] ? (aw[x], aw[y]) : (aw[y], aw[x]);
                if (!aOpen.Contains(ka)) continue;
                var ks = sw[sx] < sw[sy] ? (sw[sx], sw[sy]) : (sw[sy], sw[sx]);
                if (!sOpen.Contains(ks)) { added++; cornerSrc[ka] = ks; }
            }
        }
        sb.AppendLine($"3. open boundary edges (vertices joined where they share a position): source {sOpen.Count}, asset {aOpen.Count}; edges the asset opened that the source had closed: {added}; edges shared by more than two triangles: source {sNon}, asset {aNon}; per-face index topology only: source {sRawOpen.Count}, asset {aRawOpen.Count}");

        var openByGroup = sOpen.GroupBy(e => { int v = Array.IndexOf(sw, e.Item1); return Group(v); }).OrderByDescending(g => g.Count()).Take(14);
        sb.AppendLine("   source open edges by the joint that weights their first vertex most: " + string.Join(", ", openByGroup.Select(g => $"{g.Key} {g.Count()}")));
        // 3b. coincident vertices that move differently: a seam that tears when posed
        var gSrc = new Dictionary<Vector3, List<int>>();
        for (int i = 0; i < src.Pos.Count; i++) { if (!gSrc.TryGetValue(src.Pos[i], out var l)) gSrc[src.Pos[i]] = l = new(); l.Add(i); }
        string SrcW(int v) => string.Join(",", rig.Influences[v].Where(t => rig.Canonical[t.Joint] != null).GroupBy(t => rig.Canonical[t.Joint]).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Sum(x => x.Weight):0.000}"));
        var astW = new List<string>();
        foreach (var f in model.Lod(3)!.Faces.Where(f => !f.NoGeometry))
            for (int v = 0; v < f.VertexCount; v++)
                astW.Add(string.Join(",", f.Weights![v].GroupBy(t => model.Skin!.JointNames[t.Joint]).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Sum(x => x.Weight):0.000}")));
        int groups = 0, srcSplit = 0, astSplit = 0, newSplit = 0;
        foreach (var l in gSrc.Values)
        {
            if (l.Count < 2) continue;
            groups++;
            bool s1 = l.Select(SrcW).Distinct().Count() > 1, a1 = l.Select(v => astW[v]).Distinct().Count() > 1;
            if (s1) srcSplit++; if (a1) astSplit++; if (a1 && !s1) newSplit++;
        }
        sb.AppendLine($"3b. positions shared by more than one vertex: {groups}; with different weights in the source {srcSplit}, in the asset {astSplit}, only in the asset {newSplit}");

        if (dir.Length > 0) File.WriteAllText(Path.Combine(dir, "shading-report.txt"), sb.ToString());
        // A triangle can only turn over where the 16-bit position grid decides its facing: slivers a thousand times smaller than a typical one.
        Assert.True(flippedAreas.All(a => a < 0.05f), sb.ToString());
        Assert.True(sorted[^1] < 1.0f, sb.ToString());
        Assert.True(added == 0, sb.ToString());
        Assert.True(newSplit == 0, sb.ToString());
    }

    /// <summary>Writes the arms-down pose of the source rig and of the shipped asset, with posed vertex normals, for lit renders.</summary>
    [SkeletonFact]
    public void WriteLitPoses()
    {
        var dir = Environment.GetEnvironmentVariable("MESH5_REPORT_DIR") ?? "";
        if (dir.Length == 0 || !File.Exists(SkinRefs.GltfModel) || !File.Exists(SkinRefs.GltfAsset)) return;
        var sk = SkinRefs.LoadSkeleton();
        var frame = SkinRefs.LoadAssetFrame();
        var root = ModelRoot.Load(SkinRefs.GltfModel);
        var quarter = GltfToMesh.Convert(root, n => sk.Find(n) != null, "neutral_bone", new GltfToMesh.JointBinding(sk, false)).Report.QuarterTurns;
        var model = MeshCodec.Decode(File.ReadAllBytes(SkinRefs.GltfAsset));
        var src = ReadSide(root, quarter);
        var ast = ReadAsset(model, 3);
        var rig = ReadSource(root, sk, quarter);

        // source: glTF skinning with the same motion the other checks use
        var pivots = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        for (int i = 0; i < rig.Pivot.Length; i++) if (rig.Canonical[i] != null) pivots[rig.Canonical[i]!] = rig.Pivot[i];
        var motion = Motion(sk, n => pivots.TryGetValue(n, out var p) ? p : sk.World(n).Translation, ArmsDown);
        var skinS = new Matrix4x4[rig.K.Length];
        for (int i = 0; i < skinS.Length; i++) skinS[i] = rig.Canonical[i] == null ? rig.K[i] : rig.K[i] * motion(rig.Canonical[i]!);
        var (ps, ns) = Blend(src.Pos.ToArray(), src.Nrm.ToArray(), rig.Influences, skinS, false);
        Write(dir, "lit-source", ps, ns, src);

        // asset: the viewer's rules, in the frame it is bound for
        var skin = model.Skin!;
        var motionA = Motion(frame, n => frame.World(n).Translation, ArmsDown);
        var skinA = new Matrix4x4[skin.JointNames.Count];
        for (int i = 0; i < skinA.Length; i++)
        {
            var j = frame.Find(skin.JointNames[i]);
            skinA[i] = j == null ? Matrix4x4.Identity : SkinningCheck.ToMatrix(skin.InverseBind[i]) * frame.World(j.Name) * motionA(j.Name);
        }
        var inflA = new List<(int, float)[]>();
        foreach (var f in model.Lod(3)!.Faces.Where(f => !f.NoGeometry))
            for (int v = 0; v < f.VertexCount; v++) inflA.Add(f.Weights![v].Select(x => ((int)x.Joint, x.Weight)).ToArray());
        var (pa, na) = Blend(ast.Pos.ToArray(), ast.Nrm.ToArray(), inflA.ToArray(), skinA, true);
        Write(dir, "lit-asset", pa, na, ast);
        File.WriteAllLines(Path.Combine(dir, "pose-groups.txt"), Enumerable.Range(0, src.Pos.Count).Select(v => rig.Influences[v].Length == 0 ? "-" : rig.JointNames[rig.Influences[v].OrderByDescending(x => x.Weight).First().Joint]));
        var sh = frame.World("mShoulderRight").Translation; var sl = frame.World("mShoulderLeft").Translation;
        File.WriteAllText(Path.Combine(dir, "shoulder-centres.txt"), $"right {sh.X} {sh.Y} {sh.Z}{Environment.NewLine}left {sl.X} {sl.Y} {sl.Z}{Environment.NewLine}");
    }

    /// <summary>The asset posed by the viewer's rules in a frame (with its joint positions applied when asked), vertex normals carried through the same matrices.</summary>
    public static (Vector3[] P, Vector3[] N) PoseLit(MeshAssetModel model, RestSkeleton frame, bool useJointPositions, PoseSpec pose, out Side side)
    {
        var skin = model.Skin!;
        var overrides = useJointPositions ? SkinningCheck.JointPositionOverrides(skin, frame) : null;
        var motion = Motion(frame, n => frame.World(n, overrides).Translation, pose);
        var skinM = new Matrix4x4[skin.JointNames.Count];
        for (int i = 0; i < skinM.Length; i++)
        {
            var j = frame.Find(skin.JointNames[i]);
            skinM[i] = j == null ? Matrix4x4.Identity : SkinningCheck.ToMatrix(skin.InverseBind[i]) * frame.World(j.Name, overrides) * motion(j.Name);
        }
        side = ReadAsset(model, 3);
        var infl = new List<(int, float)[]>();
        foreach (var f in model.Lod(3)!.Faces.Where(f => !f.NoGeometry))
            for (int v = 0; v < f.VertexCount; v++) infl.Add(f.Weights![v].Select(x => ((int)x.Joint, x.Weight)).ToArray());
        return Blend(side.Pos.ToArray(), side.Nrm.ToArray(), infl.ToArray(), skinM, true);
    }

    static (Vector3[] P, Vector3[] N) Blend(Vector3[] pos, Vector3[] nrm, (int Joint, float Weight)[][] infl, Matrix4x4[] skin, bool normalise)
    {
        var p = new Vector3[pos.Length]; var n = new Vector3[pos.Length];
        for (int v = 0; v < pos.Length; v++)
        {
            var w = infl[v];
            float total = normalise ? w.Sum(x => x.Weight) : 1f;
            if (w.Length == 0 || total <= 0) { p[v] = Vector3.Transform(pos[v], skin[0]); n[v] = Vector3.Normalize(Vector3.TransformNormal(nrm[v], skin[0])); continue; }
            foreach (var (j, wt) in w) { p[v] += Vector3.Transform(pos[v], skin[j]) * (wt / total); n[v] += Vector3.TransformNormal(nrm[v], skin[j]) * (wt / total); }
            n[v] = n[v].LengthSquared() > 0 ? Vector3.Normalize(n[v]) : n[v];
        }
        return (p, n);
    }

    public static void Write(string dir, string name, Vector3[] p, Vector3[] n, Side s)
    {
        using var w = new BinaryWriter(File.Create(Path.Combine(dir, $"{name}.bin")));
        w.Write(p.Length); w.Write(s.Tris.Count);
        foreach (var v in p) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        foreach (var v in n) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        foreach (var t in s.Tris) { w.Write(t.A); w.Write(t.B); w.Write(t.C); }
    }
}
