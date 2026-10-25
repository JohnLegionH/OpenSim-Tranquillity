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
/// Where the viewer puts the system eyeballs, and where a model's own eyes and eye sockets are. The viewer attaches the
/// eyeball mesh (avatar_eye.llm: a front dome about 21 mm across, flat side at the joint) to the eye joints mEyeLeft and
/// mEyeRight (LLAvatarAppearance::buildCharacter) and takes the mesh's own position, rotation and scale from its header
/// (LLAvatarJointMesh::setup), so the dome sits on the joint's world matrix.
/// </summary>
public class EyePlacementTests
{
    static string Mm(double m) => (m * 1000).ToString("0.0", CultureInfo.InvariantCulture);

    public sealed record Dome(Vector3[] Vertices, int[] Triangles, Vector3 HeaderPosition, Vector3 HeaderRotationDegrees, Vector3 HeaderScale);

    /// <summary>The eyeball mesh of the viewer's avatar (positions and faces; the file is read as LLPolyMeshSharedData::loadMesh does).</summary>
    public static Dome ReadEyeball(string llmPath)
    {
        using var r = new BinaryReader(File.OpenRead(llmPath));
        r.ReadBytes(24);
        bool weights = r.ReadByte() != 0, detail = r.ReadByte() != 0;
        Vector3 V3() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var pos = V3(); var rot = V3(); r.ReadByte(); var scale = V3();
        int n = r.ReadUInt16();
        var verts = Enumerable.Range(0, n).Select(_ => V3()).ToArray();
        r.BaseStream.Seek(12L * n * 2 + 8L * n + (detail ? 8L * n : 0) + (weights ? 4L * n : 0), SeekOrigin.Current);
        int faces = r.ReadUInt16();
        var tris = new int[faces * 3];
        for (int i = 0; i < tris.Length; i++) tris[i] = r.ReadUInt16();
        return new Dome(verts, tris, pos, rot, scale);
    }

    /// <summary>Every triangle list of a glTF model in the avatar's axes (node transform baked in), as one vertex array and an index array.</summary>
    public static (Vector3[] Vertices, int[] Triangles) ReadSurface(ModelRoot root, int quarterTurns)
    {
        var c = GltfToMesh.Rotation * Matrix4x4.CreateRotationZ(quarterTurns * MathF.PI / 2);
        var verts = new List<Vector3>(); var tris = new List<int>();
        foreach (var node in root.LogicalNodes.Where(n => n.Mesh != null))
        {
            var world = node.WorldMatrix * c;
            bool flip = new Matrix4x4(world.M11, world.M12, world.M13, 0, world.M21, world.M22, world.M23, 0, world.M31, world.M32, world.M33, 0, 0, 0, 0, 1).GetDeterminant() < 0;
            foreach (var prim in node.Mesh.Primitives)
            {
                int start = verts.Count;
                verts.AddRange(prim.GetVertexAccessor("POSITION").AsVector3Array().Select(p => Vector3.Transform(p, world)));
                foreach (var (a, b, cc) in prim.GetTriangleIndices())
                    tris.AddRange(flip ? new[] { start + a, start + cc, start + b } : new[] { start + a, start + b, start + cc });
            }
        }
        return (verts.ToArray(), tris.ToArray());
    }

    /// <summary>Least-squares sphere through the points: centre and radius.</summary>
    public static (Vector3 Centre, float Radius) FitSphere(IReadOnlyList<Vector3> p)
    {
        // |x - c|^2 = r^2  ->  2 c.x + (r^2 - |c|^2) = |x|^2, solved by the normal equations in double precision
        var ata = new double[4, 4]; var atb = new double[4];
        foreach (var v in p)
        {
            var row = new double[] { 2 * v.X, 2 * v.Y, 2 * v.Z, 1 };
            double b = v.X * (double)v.X + v.Y * (double)v.Y + v.Z * (double)v.Z;
            for (int i = 0; i < 4; i++) { for (int j = 0; j < 4; j++) ata[i, j] += row[i] * row[j]; atb[i] += row[i] * b; }
        }
        var x = SolveLinear(ata, atb);
        double r2 = x[3] + x[0] * x[0] + x[1] * x[1] + x[2] * x[2];
        return (new Vector3((float)x[0], (float)x[1], (float)x[2]), (float)Math.Sqrt(Math.Max(0, r2)));
    }

    static double[] SolveLinear(double[,] a, double[] b)
    {
        int n = b.Length; var m = (double[,])a.Clone(); var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            for (int k = 0; k < n; k++) (m[c, k], m[piv, k]) = (m[piv, k], m[c, k]);
            (x[c], x[piv]) = (x[piv], x[c]);
            for (int r = c + 1; r < n; r++) { double f = m[r, c] / m[c, c]; for (int k = c; k < n; k++) m[r, k] -= f * m[c, k]; x[r] -= f * x[c]; }
        }
        for (int r = n - 1; r >= 0; r--) { double s = x[r]; for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k]; x[r] = s / m[r, r]; }
        return x;
    }

    static void WriteMesh(string path, Vector3[] v, int[] t)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(v.Length); w.Write(t.Length);
        foreach (var p in v) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        foreach (var i in t) w.Write(i);
    }

    [SkeletonFact]
    public void WhereTheViewerPutsTheEyeballsAgainstMaxsEyes()
    {
        if (!File.Exists(SkinRefs.GltfModel)) return;
        var dir = Environment.GetEnvironmentVariable("MESH4_REPORT_DIR") ?? "";
        var s = FittedShapeTests.Prepare();
        var folder = Path.GetDirectoryName(SkinRefs.GltfModel)!;
        var eyesFile = Path.Combine(folder, "MaxineV1Draft1_9__MaxEyes_check_positioning.glb");
        var llm = Path.Combine(Path.GetDirectoryName(SkinRefs.Skeleton)!, "avatar_eye.llm");
        if (!File.Exists(eyesFile) || !File.Exists(llm)) return;

        int quarter = GltfToMesh.FacingQuarterTurns(s.Root, s.Bare, n => s.Bare.Find(n) != null);
        var (eyeV, eyeT) = ReadSurface(ModelRoot.Load(eyesFile), quarter);
        var (headV, headT) = ReadSurface(s.Root, quarter);
        var dome = ReadEyeball(llm);
        var sb = new StringBuilder();
        sb.AppendLine($"eyeball mesh header: position {dome.HeaderPosition}, rotation {dome.HeaderRotationDegrees}, scale {dome.HeaderScale}; {dome.Vertices.Length} vertices; x {Mm(dome.Vertices.Min(v => v.X))}..{Mm(dome.Vertices.Max(v => v.X))} mm, y {Mm(dome.Vertices.Min(v => v.Y))}..{Mm(dome.Vertices.Max(v => v.Y))} mm, z {Mm(dome.Vertices.Min(v => v.Z))}..{Mm(dome.Vertices.Max(v => v.Z))} mm (joint frame)");

        var maxEye = new Dictionary<string, (Vector3 Centre, float Radius, Vector3 Pole, int Count)>();
        foreach (var (side, sign) in new[] { ("Left", 1f), ("Right", -1f) })
        {
            var pts = eyeV.Where(v => v.Y * sign > 0).ToList();
            var (c, r) = FitSphere(pts);
            // the pole: the vertex furthest forward within 6 mm of the eye's axis through the centre
            var pole = pts.Where(v => Math.Abs(v.Y - c.Y) < 0.006f && Math.Abs(v.Z - c.Z) < 0.006f).OrderByDescending(v => v.X).First();
            // the model's eye is the same kind of dome as the viewer's: flat side back, so its "joint" is the middle of the flat side
            var baseCentre = new Vector3(pts.Min(v => v.X), (pts.Min(v => v.Y) + pts.Max(v => v.Y)) / 2, (pts.Min(v => v.Z) + pts.Max(v => v.Z)) / 2);
            r = (pts.Max(v => v.Z) - pts.Min(v => v.Z)) / 2;
            c = baseCentre;
            maxEye[side] = (c, r, pole, pts.Count);
            sb.AppendLine($"Max's own eye, {side}: {pts.Count} vertices, flat-side centre ({Mm(c.X)}, {Mm(c.Y)}, {Mm(c.Z)}) mm, half height {Mm(r)} mm, front pole x {Mm(pole.X)} mm, bounding box x {Mm(pts.Min(v => v.X))}..{Mm(pts.Max(v => v.X))} y {Mm(pts.Min(v => v.Y))}..{Mm(pts.Max(v => v.Y))} z {Mm(pts.Min(v => v.Z))}..{Mm(pts.Max(v => v.Z))}");
        }

        // the head surface around each eye: the closest head vertices to Max's eye centre, and who they are weighted to
        foreach (var side in new[] { "Left", "Right" })
        {
            var (c, r, _, _) = maxEye[side];
            var near = Enumerable.Range(0, s.Source.Rest.Length).Select(i => (I: i, D: (s.Source.Rest[i] - c).Length())).OrderBy(x => x.D).Take(400).ToList();
            string G(int i) => s.Source.Influences[i].Length == 0 ? "-" : s.Source.JointNames[s.Source.Influences[i].OrderByDescending(x => x.Weight).First().Joint];
            sb.AppendLine($"head surface around Max's {side} eye: nearest head vertex {Mm(near[0].D)} mm from the eye centre (eye radius {Mm(r)} mm); 400 nearest span {Mm(near[0].D)}..{Mm(near[^1].D)} mm; weighted to: " +
                string.Join(", ", near.GroupBy(x => G(x.I)).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} {g.Count()}")));
        }

        // the sockets: the lid-edge vertices (weighted mostly to a lid joint) bound the opening the eyeball shows through
        foreach (var side in new[] { "Left", "Right" })
        {
            var (c, r, pole, _) = maxEye[side];
            var lid = Enumerable.Range(0, s.Source.Rest.Length).Where(i => s.Source.Influences[i].Any(x => x.Weight > 0.5f && s.Source.JointNames[x.Joint] is var n && (n == "mFaceEyeLidUpper" + side || n == "mFaceEyeLidLower" + side))).Select(i => s.Source.Rest[i]).ToList();
            var near = lid.Where(v => (v - c).Length() < 0.05f).ToList();
            sb.AppendLine($"lid vertices around Max's {side} eye ({near.Count}): opening y {Mm(near.Min(v => v.Y))}..{Mm(near.Max(v => v.Y))} mm, z {Mm(near.Min(v => v.Z))}..{Mm(near.Max(v => v.Z))} mm, x {Mm(near.Min(v => v.X))}..{Mm(near.Max(v => v.X))} mm (mean {Mm(near.Average(v => v.X))}); eye flat side x {Mm(c.X)} mm, pole x {Mm(pole.X)} mm");
            var all = s.Source.Rest.Select(v => (v - c).Length()).ToArray();
            sb.AppendLine($"head vertices by distance from the eye's flat-side centre: <10 mm {all.Count(d => d < 0.010f)}, 10-20 mm {all.Count(d => d >= 0.010f && d < 0.020f)}, 20-30 mm {all.Count(d => d >= 0.020f && d < 0.030f)}");
            var inner = Enumerable.Range(0, all.Length).Where(i => all[i] < 0.020f && s.Source.Rest[i].X < pole.X).Select(i => s.Source.Rest[i]).ToList();
            if (inner.Count > 0) sb.AppendLine($"   those inside 20 mm and behind the pole: {inner.Count}, x {Mm(inner.Min(v => v.X))}..{Mm(inner.Max(v => v.X))} mm, y {Mm(inner.Min(v => v.Y))}..{Mm(inner.Max(v => v.Y))} mm, z {Mm(inner.Min(v => v.Z))}..{Mm(inner.Max(v => v.Z))} mm");
        }

        var frames = new (string Name, RestSkeleton Frame)[] { ("true default shape (the viewer's, drivers applied)", s.TrueDefault), ("fitted shape", s.Fitted) };
        foreach (var (fname, frame) in frames)
        {
            sb.AppendLine($"== system eyeballs at the {fname}");
            foreach (var side in new[] { "Left", "Right" })
            {
                var joint = frame.Find("mEye" + side)!;
                var world = frame.World(joint.Name);
                var centre = world.Translation;
                var domeWorld = dome.Vertices.Select(v => Vector3.Transform(v, world)).ToArray();
                var pole = domeWorld.OrderByDescending(v => v.X).First();
                float rad = domeWorld.Max(v => Math.Abs(v.Y - centre.Y));
                var (mc, mr, mp, _) = maxEye[side];
                float lateral = (side == "Left" ? 1 : -1) * (centre.Y - mc.Y);
                // part of the dome surface that lies inside Max's own eyeball sphere (hidden by it) and part outside it
                int inside = domeWorld.Count(v => (v - mc).Length() < mr), behindPole = domeWorld.Count(v => v.X < mp.X);
                sb.AppendLine($"{side}: joint ({Mm(centre.X)}, {Mm(centre.Y)}, {Mm(centre.Z)}) mm, joint scale {joint.Scale.X:0.000}; dome radius {Mm(rad)} mm, front pole x {Mm(pole.X)} mm");
                sb.AppendLine($"   against Max's eye centre: forward {Mm(centre.X - mc.X)} mm, up {Mm(centre.Z - mc.Z)} mm, outward {Mm(lateral)} mm; dome pole against Max's eye pole: forward {Mm(pole.X - mp.X)} mm; dome radius against Max's eye radius {Mm(rad - mr)} mm; {inside} of {domeWorld.Length} dome vertices inside Max's eyeball sphere");
                if (dir.Length > 0)
                {
                    var tag = fname.StartsWith("fitted") ? "fitted" : "default";
                    WriteMesh(Path.Combine(dir, $"eye-dome-{tag}-{side}.bin"), domeWorld, dome.Triangles);
                }
            }
        }
        if (dir.Length > 0)
        {
            WriteMesh(Path.Combine(dir, "eye-max-eyes.bin"), eyeV, eyeT);
            // the head within 0.16 m of the eyes' midpoint
            var mid = (maxEye["Left"].Centre + maxEye["Right"].Centre) / 2;
            var keep = new List<int>(); var map = new Dictionary<int, int>();
            var hv = new List<Vector3>(); var ht = new List<int>();
            for (int i = 0; i + 2 < headT.Length; i += 3)
            {
                if (!(headT.Skip(i).Take(3).All(k => (headV[k] - mid).Length() < 0.16f))) continue;
                foreach (int k in headT.Skip(i).Take(3))
                {
                    if (!map.TryGetValue(k, out int m)) { map[k] = m = hv.Count; hv.Add(headV[k]); }
                    ht.Add(m);
                }
            }
            WriteMesh(Path.Combine(dir, "eye-head.bin"), hv.ToArray(), ht.ToArray());
            File.WriteAllText(Path.Combine(dir, "eye-centres.txt"), string.Join("\n", maxEye.Select(kv => $"{kv.Key} {kv.Value.Centre.X} {kv.Value.Centre.Y} {kv.Value.Centre.Z} {kv.Value.Radius}")));
            File.WriteAllText(Path.Combine(dir, "eye-placement.txt"), sb.ToString());
        }
        Assert.True(maxEye["Left"].Radius > 0.005f, sb.ToString());
    }
}
