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

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// Does a glTF character converted to a mesh asset move like the glTF rig it came from? Both are posed with the same
/// world-space joint motions (each joint's subtree turned about that joint's own world position, parents first) and
/// every vertex is compared. The source rig is posed by plain glTF skinning with its own matrices and its own, untrimmed
/// weights; the asset by the viewer's rules (bind shape, then the weighted sum of inverse bind matrix times the avatar
/// joint's world matrix). At the rest pose both reproduce the model, so only a pose can tell a wrong joint, a wrong
/// weight or a wrong pivot.
/// </summary>
public class MotionEquivalenceTests
{
    // ---- poses -------------------------------------------------------------------------------------------------

    /// <summary>Joint name and the turn in degrees about the world X, then Y, then Z axes, applied about the joint's own world position.</summary>
    public sealed record PoseSpec(string Name, (string Joint, Vector3 Degrees)[] Turns);

    public static readonly PoseSpec Rest = new("rest", Array.Empty<(string, Vector3)>());

    /// <summary>
    /// A standing avatar's arms hang down: the collars drop a little, the shoulders turn the arms about the world X axis
    /// (the arms lie along Y in the rest pose) by 72 degrees and forward 8 degrees, the elbows and wrists bend a little.
    /// These are representative angles of a stand animation, not read from a particular animation file.
    /// </summary>
    public static readonly PoseSpec ArmsDown = new("arms-down", new (string, Vector3)[]
    {
        ("mCollarLeft", new(-8, 0, 0)), ("mCollarRight", new(8, 0, 0)),
        ("mShoulderLeft", new(-72, -8, 0)), ("mShoulderRight", new(72, -8, 0)),
        ("mElbowLeft", new(0, -20, 0)), ("mElbowRight", new(0, -20, 0)),
        ("mWristLeft", new(0, -6, 0)), ("mWristRight", new(0, -6, 0)),
    });

    public static readonly string[] MajorJoints =
    {
        "mCollarLeft", "mCollarRight", "mShoulderLeft", "mShoulderRight", "mElbowLeft", "mElbowRight", "mWristLeft", "mWristRight",
        "mNeck", "mHead", "mChest", "mTorso", "mPelvis", "mHipLeft", "mHipRight", "mKneeLeft", "mKneeRight",
    };

    public static IEnumerable<PoseSpec> SingleJointPoses()
    {
        foreach (var j in MajorJoints)
            foreach (var (axis, label) in new[] { (Vector3.UnitX, "X"), (Vector3.UnitY, "Y"), (Vector3.UnitZ, "Z") })
                foreach (var sign in new[] { 45f, -45f })
                    yield return new PoseSpec($"{j} {label}{(sign > 0 ? "+" : "-")}45", new[] { (j, axis * sign) });
    }

    static Matrix4x4 TurnAbout(Vector3 centre, Vector3 degrees)
    {
        var r = degrees * (MathF.PI / 180f);
        return Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y)
             * Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateTranslation(centre);
    }

    /// <summary>
    /// The motion of every joint, as a matrix taking a rest-pose world position to its posed world position. A joint is
    /// carried by its parent's motion, then turned about its own (carried) pivot.
    /// </summary>
    public static Func<string, Matrix4x4> Motion(RestSkeleton sk, Func<string, Vector3> pivot, PoseSpec pose)
    {
        var turns = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var (joint, deg) in pose.Turns) turns[sk.Find(joint)!.Name] = deg;
        var cache = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        Matrix4x4 M(string name)
        {
            var j = sk.Find(name)!;
            if (cache.TryGetValue(j.Name, out var c)) return c;
            var parent = j.Parent == null ? Matrix4x4.Identity : M(j.Parent);
            var m = parent;
            if (turns.TryGetValue(j.Name, out var deg)) m = parent * TurnAbout(Vector3.Transform(pivot(j.Name), parent), deg);
            return cache[j.Name] = m;
        }
        return M;
    }

    // ---- the two rigs ------------------------------------------------------------------------------------------

    /// <summary>The glTF model as exported: every vertex with all of its influences, and the skin's own matrices.</summary>
    public sealed class SourceRig
    {
        public string[] JointNames = Array.Empty<string>();
        public string?[] Canonical = Array.Empty<string?>();
        /// <summary>C^-1 * inverse bind * joint world * C: the identity when the model was exported in its bind pose.</summary>
        public Matrix4x4[] K = Array.Empty<Matrix4x4>();
        public Vector3[] Pivot = Array.Empty<Vector3>();
        public Vector3[] Rest = Array.Empty<Vector3>();
        public (int Joint, float Weight)[][] Influences = Array.Empty<(int, float)[]>();
        public int[] FaceVertexCounts = Array.Empty<int>();
        public bool MeshNodeIsIdentity;
        public int VerticesOverFour, MaxInfluencesOnAVertex;
        public float MinWeightSum, MaxWeightSum;
    }

    public static SourceRig ReadSource(ModelRoot root, RestSkeleton sk, int quarterTurns)
    {
        var rig = new SourceRig();
        var meshNodes = root.LogicalNodes.Where(n => n.Mesh != null).ToList();
        var skin = meshNodes[0].Skin!;
        var yaw = Matrix4x4.CreateRotationZ(quarterTurns * MathF.PI / 2);
        var c = GltfToMesh.Rotation * yaw;
        Matrix4x4.Invert(c, out var cInv);
        int n = skin.JointsCount;
        rig.JointNames = new string[n]; rig.Canonical = new string?[n]; rig.K = new Matrix4x4[n]; rig.Pivot = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var (joint, ibm) = skin.GetJoint(i);
            rig.JointNames[i] = joint.Name ?? $"node{i}";
            rig.Canonical[i] = sk.Find(rig.JointNames[i])?.Name;
            var w = joint.WorldMatrix;
            rig.K[i] = cInv * ibm * w * c;
            rig.Pivot[i] = Vector3.Transform(w.Translation, c);
        }
        var rest = new List<Vector3>();
        var infl = new List<(int, float)[]>();
        var counts = new List<int>();
        rig.MeshNodeIsIdentity = meshNodes.All(m => m.WorldMatrix.Equals(Matrix4x4.Identity));
        rig.MinWeightSum = float.MaxValue;
        foreach (var node in meshNodes)
        {
            var world = node.WorldMatrix * c;
            bool flip = new Matrix4x4(world.M11, world.M12, world.M13, 0, world.M21, world.M22, world.M23, 0, world.M31, world.M32, world.M33, 0, 0, 0, 0, 1).GetDeterminant() < 0;
            foreach (var prim in node.Mesh.Primitives)
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                var pos = prim.GetVertexAccessor("POSITION").AsVector3Array();
                var sets = new List<(IList<Vector4> J, IList<Vector4> W)>();
                for (int s = 0; ; s++)
                {
                    var ja = prim.GetVertexAccessor($"JOINTS_{s}"); var wa = prim.GetVertexAccessor($"WEIGHTS_{s}");
                    if (ja == null || wa == null) break;
                    sets.Add((ja.AsVector4Array(), wa.AsVector4Array()));
                }
                var map = new Dictionary<int, int>();
                var order = new List<int>();
                foreach (var (a, b, cc) in prim.GetTriangleIndices())
                    foreach (var v in flip ? new[] { a, cc, b } : new[] { a, b, cc })
                        if (map.TryAdd(v, order.Count)) order.Add(v);
                counts.Add(order.Count);
                foreach (var v in order)
                {
                    rest.Add(Vector3.Transform(pos[v], world));
                    var list = new List<(int, float)>();
                    foreach (var (js, ws) in sets)
                    {
                        if (ws[v].X > 0) list.Add(((int)js[v].X, ws[v].X));
                        if (ws[v].Y > 0) list.Add(((int)js[v].Y, ws[v].Y));
                        if (ws[v].Z > 0) list.Add(((int)js[v].Z, ws[v].Z));
                        if (ws[v].W > 0) list.Add(((int)js[v].W, ws[v].W));
                    }
                    infl.Add(list.ToArray());
                    if (list.Count > 4) rig.VerticesOverFour++;
                    rig.MaxInfluencesOnAVertex = Math.Max(rig.MaxInfluencesOnAVertex, list.Count);
                    float sum = list.Sum(t => t.Item2);
                    rig.MinWeightSum = Math.Min(rig.MinWeightSum, sum);
                    rig.MaxWeightSum = Math.Max(rig.MaxWeightSum, sum);
                }
            }
        }
        rig.Rest = rest.ToArray(); rig.Influences = infl.ToArray(); rig.FaceVertexCounts = counts.ToArray();
        return rig;
    }

    /// <summary>The source rig posed by glTF skinning: the weighted sum of the vertex through each joint's own matrices.</summary>
    public static Vector3[] PoseSource(SourceRig rig, RestSkeleton sk, PoseSpec pose)
    {
        var pivots = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        for (int i = 0; i < rig.Pivot.Length; i++) if (rig.Canonical[i] != null) pivots[rig.Canonical[i]!] = rig.Pivot[i];
        Vector3 Pivot(string name) => pivots.TryGetValue(name, out var p) ? p : sk.World(name).Translation;
        var motion = Motion(sk, Pivot, pose);
        var skinM = new Matrix4x4[rig.K.Length];
        for (int i = 0; i < skinM.Length; i++) skinM[i] = rig.Canonical[i] == null ? rig.K[i] : rig.K[i] * motion(rig.Canonical[i]!);
        return Blend(rig.Rest, rig.Influences, skinM, normalise: false);
    }

    public static Vector3[] Blend(Vector3[] rest, (int Joint, float Weight)[][] infl, Matrix4x4[] skin, bool normalise)
    {
        var posed = new Vector3[rest.Length];
        for (int v = 0; v < rest.Length; v++)
        {
            var w = infl[v];
            float total = normalise ? w.Sum(x => x.Weight) : 1f;
            if (w.Length == 0 || total <= 0) { posed[v] = Vector3.Transform(rest[v], skin[0]); continue; }
            var p = Vector3.Zero;
            foreach (var (j, wt) in w) p += Vector3.Transform(rest[v], skin[j]) * (wt / total);
            posed[v] = p;
        }
        return posed;
    }

    /// <summary>The asset posed by the viewer's rules, with the avatar skeleton as the frame the joints move in.</summary>
    public static Vector3[] PoseAsset(MeshAssetModel model, RestSkeleton frame, bool useJointPositions, PoseSpec pose, out (int Joint, float Weight)[][] influences, int lod = 3)
    {
        var skin = model.Skin!;
        var overrides = useJointPositions ? SkinningCheck.JointPositionOverrides(skin, frame) : null;
        var motion = Motion(frame, name => frame.World(name, overrides).Translation, pose);
        int n = skin.JointNames.Count;
        var skinM = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            var j = frame.Find(skin.JointNames[i]);
            skinM[i] = j == null ? Matrix4x4.Identity
                : SkinningCheck.ToMatrix(skin.InverseBind[i]) * frame.World(j.Name, overrides) * motion(j.Name);
        }
        var bind = SkinningCheck.ToMatrix(skin.BindShape);
        var rest = new List<Vector3>();
        var infl = new List<(int, float)[]>();
        foreach (var f in model.Lod(lod)!.Faces)
        {
            if (f.NoGeometry) continue;
            for (int v = 0; v < f.VertexCount; v++)
            {
                rest.Add(Vector3.Transform(f.Positions[v], bind));
                infl.Add(f.Weights?[v].Select(x => ((int)x.Joint, x.Weight)).ToArray() ?? Array.Empty<(int, float)>());
            }
        }
        influences = infl.ToArray();
        return Blend(rest.ToArray(), influences, skinM, normalise: true);
    }

    /// <summary>A test-only copy of the asset whose weights are the source's, untrimmed (up to eight influences a vertex).</summary>
    public static MeshAssetModel WithUntrimmedWeights(MeshAssetModel model, SourceRig rig)
    {
        var copy = new MeshAssetModel { Skin = model.Skin, High = new MeshLod() };
        int vi = 0;
        foreach (var f in model.High!.Faces)
        {
            var g = f.Copy();
            g.Weights = new Influence[f.VertexCount][];
            for (int v = 0; v < f.VertexCount; v++, vi++)
                g.Weights[v] = rig.Influences[vi]
                    .Select(t => (Index: model.Skin!.JointNames.IndexOf(rig.Canonical[t.Joint] ?? "\0"), t.Weight))
                    .Where(t => t.Index >= 0)
                    .Select(t => new Influence((byte)t.Index, t.Weight)).ToArray();
            copy.High.Faces.Add(g);
        }
        return copy;
    }

    // ---- comparison --------------------------------------------------------------------------------------------

    public sealed record Diff(float Max, float Mean, float P99, int WorstVertex, float[] PerVertex);

    public static Diff Compare(Vector3[] a, Vector3[] b)
    {
        var e = new float[a.Length];
        for (int i = 0; i < e.Length; i++) e[i] = (a[i] - b[i]).Length();
        var sorted = (float[])e.Clone(); Array.Sort(sorted);
        int worst = Array.IndexOf(e, sorted[^1]);
        return new Diff(sorted[^1], e.Average(), sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.99))], worst, e);
    }

    /// <summary>The largest relative change of any triangle edge longer than 2 mm at rest: a tear or a crumple at a seam shows here.</summary>
    public static (float Min, float Max) EdgeRatio(Vector3[] rest, Vector3[] posed, int[][] indices, int[] faceStart)
    {
        float lo = float.MaxValue, hi = 0;
        for (int f = 0; f < indices.Length; f++)
        {
            var t = indices[f];
            for (int i = 0; i + 2 < t.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = faceStart[f] + t[i + k], b = faceStart[f] + t[i + (k + 1) % 3];
                    float r = (rest[a] - rest[b]).Length();
                    if (r < 0.002f) continue;
                    float q = (posed[a] - posed[b]).Length() / r;
                    lo = Math.Min(lo, q); hi = Math.Max(hi, q);
                }
        }
        return (lo, hi);
    }

    static string Mm(float m) => (m * 1000).ToString("0.00", CultureInfo.InvariantCulture);

    static string Describe((int Joint, float Weight)[] w, Func<int, string> name) =>
        string.Join(" ", w.OrderByDescending(x => x.Weight).Select(x => $"{name(x.Joint)}:{x.Weight:0.000}"));

    /// <summary>
    /// Where the asset and the source differ across a triangle edge: the posed difference (asset minus source) of one end
    /// against the other's. A smooth difference moves a limb; a jump across an edge opens a seam.
    /// </summary>
    public static void DescribeSeams(StringBuilder sb, string label, Vector3[] posed, Vector3[] src, Vector3[] rest, int[][] indices, int[] faceStart, SourceRig source, int top = 12)
    {
        var jumps = new List<(float Jump, int A, int B)>();
        for (int f = 0; f < indices.Length; f++)
            for (int i = 0; i + 2 < indices[f].Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = faceStart[f] + indices[f][i + k], b = faceStart[f] + indices[f][i + (k + 1) % 3];
                    jumps.Add((((posed[a] - src[a]) - (posed[b] - src[b])).Length(), a, b));
                }
        string G(int v) => source.Influences[v].Length == 0 ? "-" : source.JointNames[source.Influences[v].OrderByDescending(x => x.Weight).First().Joint];
        sb.AppendLine($"== seams, {label}: largest change of the (asset - source) difference across one edge");
        foreach (var (j, a, b) in jumps.OrderByDescending(x => x.Jump).Take(top))
            sb.AppendLine($"   {Mm(j)} mm across edge {a}-{b} at ({rest[a].X:0.000},{rest[a].Y:0.000},{rest[a].Z:0.000}); rest edge {Mm((rest[a] - rest[b]).Length())} mm; {G(a)} / {G(b)}");
    }

    static void DescribePivots(StringBuilder sb, SourceRig source, RestSkeleton frame, MeshAssetModel model)
    {
        var overrides = SkinningCheck.JointPositionOverrides(model.Skin!, frame);
        sb.AppendLine("== pivots: the joint position the source rig turns about, against the avatar's (default shape), in mm");
        foreach (var name in MajorJoints)
        {
            int i = Array.FindIndex(source.Canonical, c => c == name);
            var avatar = frame.World(name).Translation;
            bool inAsset = model.Skin!.JointNames.Contains(name);
            string src = i < 0 ? "not in the source" : $"{Mm((source.Pivot[i] - avatar).Length())} mm apart (source {source.Pivot[i].X:0.000},{source.Pivot[i].Y:0.000},{source.Pivot[i].Z:0.000}; avatar {avatar.X:0.000},{avatar.Y:0.000},{avatar.Z:0.000})";
            sb.AppendLine($"   {name}: weighted in the asset {inAsset}; {src}");
        }
        int moved = 0, unweightedMoved = 0;
        for (int i = 0; i < source.Pivot.Length; i++)
        {
            if (source.Canonical[i] == null) continue;
            if ((source.Pivot[i] - frame.World(source.Canonical[i]!).Translation).Length() > 0.0001f)
            { moved++; if (!model.Skin!.JointNames.Contains(source.Canonical[i]!)) unweightedMoved++; }
        }
        sb.AppendLine($"   {moved} of {source.Pivot.Length} source joints lie more than 0.1 mm from the avatar's; {unweightedMoved} of them have no weight in the asset (the viewer takes no override for a joint the asset does not list)");
    }

    /// <summary>The posed vertices and the triangles as raw little-endian data for the image script: counts, positions, indices; the heaviest source joint of each vertex goes to a text file.</summary>
    static void WritePose(string variant, Vector3[] posed, int[][] indices, int[] faceStart, SourceRig source)
    {
        if (ReportDir.Length == 0) return;
        var tris = new List<int>();
        for (int f = 0; f < indices.Length; f++) tris.AddRange(indices[f].Select(i => i + faceStart[f]));
        using (var w = new BinaryWriter(File.Create(Path.Combine(ReportDir, $"pose-{variant}.bin"))))
        {
            w.Write(posed.Length); w.Write(tris.Count);
            foreach (var p in posed) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            foreach (var t in tris) w.Write(t);
        }
        File.WriteAllLines(Path.Combine(ReportDir, "pose-groups.txt"),
            source.Influences.Select(i => i.Length == 0 ? "-" : source.JointNames[i.OrderByDescending(x => x.Weight).First().Joint]));
    }

    // ---- the check ---------------------------------------------------------------------------------------------

    static string ReportDir => Environment.GetEnvironmentVariable("MESH3_REPORT_DIR") ?? "";

    [SkeletonFact]
    public void TheConvertedBodyMovesLikeTheSourceRig()
    {
        if (!File.Exists(SkinRefs.GltfModel)) return;
        var sk = SkinRefs.LoadSkeleton();
        var male = RestSkeleton.Load(SkinRefs.Skeleton).WithShape(SkinRefs.AvatarDefinition, new Dictionary<string, float> { ["Male_Skeleton"] = 1f });
        var root = ModelRoot.Load(SkinRefs.GltfModel);
        var (model, report) = GltfToMesh.Convert(root, n => sk.Find(n) != null, "neutral_bone", new GltfToMesh.JointBinding(sk, false));
        var (modelJp, _) = GltfToMesh.Convert(root, n => sk.Find(n) != null, "neutral_bone", new GltfToMesh.JointBinding(sk, true));
        var source = ReadSource(root, sk, report.QuarterTurns);

        var decoded = MeshCodec.Decode(MeshCodec.Encode(model));
        var decodedJp = MeshCodec.Decode(MeshCodec.Encode(modelJp));
        var untrimmed = WithUntrimmedWeights(model, source);

        var sb = new StringBuilder();
        var tsv = new StringBuilder("frame\tpose\tvariant\tmax_mm\tmean_mm\tp99_mm\tedge_min\tedge_max\n");

        // alignment: the asset's vertices are the source's, in the same order
        var asset0 = decoded.High!.Faces.Where(f => !f.NoGeometry).ToList();
        Assert.Equal(source.FaceVertexCounts.Length, asset0.Count);
        Assert.Equal(source.FaceVertexCounts.Take(asset0.Count), asset0.Select(f => f.VertexCount));
        var faceStart = new int[asset0.Count];
        for (int i = 1; i < faceStart.Length; i++) faceStart[i] = faceStart[i - 1] + asset0[i - 1].VertexCount;
        var indices = asset0.Select(f => f.Indices).ToArray();

        sb.AppendLine($"source: {source.JointNames.Length} joints, {source.Rest.Length} vertices, mesh node identity {source.MeshNodeIsIdentity}, weight sum {source.MinWeightSum:0.0000}..{source.MaxWeightSum:0.0000}, {source.VerticesOverFour} vertices over 4 influences (max {source.MaxInfluencesOnAVertex}), quarter turns {report.QuarterTurns}");
        float worstK = 0;
        for (int i = 0; i < source.K.Length; i++)
        {
            var d = source.K[i] - Matrix4x4.Identity;
            worstK = Math.Max(worstK, new[] { d.M11, d.M12, d.M13, d.M21, d.M22, d.M23, d.M31, d.M32, d.M33, d.M41, d.M42, d.M43 }.Max(Math.Abs));
        }
        sb.AppendLine($"source skin matrices at rest: largest deviation of inverse bind x joint world from the identity {worstK:0.000000}");

        var variants = new (string Name, MeshAssetModel Model, bool Jp)[]
        {
            ("asset", decoded, false), ("asset+jointpos", decodedJp, true), ("asset-unquantised", model, false), ("asset-untrimmed", untrimmed, false),
        };
        var frames = new (string Name, RestSkeleton Frame)[] { ("default", sk), ("male", male) };

        foreach (var (fname, frame) in frames)
        {
            var poses = new List<PoseSpec> { Rest, ArmsDown };
            if (fname == "default") poses.AddRange(SingleJointPoses());
            foreach (var pose in poses)
            {
                var src = PoseSource(source, sk, pose);   // the source rig does not know the avatar's shape
                var srcRest = PoseSource(source, sk, Rest);
                var edge = EdgeRatio(srcRest, src, indices, faceStart);
                if (fname == "default" && pose == ArmsDown) WritePose("source", src, indices, faceStart, source);
                tsv.AppendLine($"{fname}\t{pose.Name}\tsource\t0\t0\t0\t{edge.Min:0.000}\t{edge.Max:0.000}");
                foreach (var (vname, vmodel, jp) in variants)
                {
                    var posed = PoseAsset(vmodel, frame, jp, pose, out var infl);
                    var d = Compare(posed, src);
                    var e = EdgeRatio(srcRest, posed, indices, faceStart);
                    if (fname == "default" && pose == ArmsDown) WritePose(vname, posed, indices, faceStart, source);
                    tsv.AppendLine($"{fname}\t{pose.Name}\t{vname}\t{Mm(d.Max)}\t{Mm(d.Mean)}\t{Mm(d.P99)}\t{e.Min:0.000}\t{e.Max:0.000}");
                    if (pose == ArmsDown || pose == Rest)
                        DescribePose(sb, fname, pose, vname, d, source, posed, src, infl, vmodel, srcRest);
                    if (pose == ArmsDown && (vname == "asset" || vname == "asset+jointpos"))
                        DescribeSeams(sb, $"{fname} {vname}", posed, src, srcRest, indices, faceStart, source);
                    if (pose == Rest && vname == "asset" && fname == "default") DescribePivots(sb, source, frame, vmodel);
                }
            }
        }
        if (ReportDir.Length > 0)
        {
            File.WriteAllText(Path.Combine(ReportDir, "motion-equivalence.txt"), sb.ToString());
            File.WriteAllText(Path.Combine(ReportDir, "motion-equivalence.tsv"), tsv.ToString());
        }
        // The rest pose must reproduce the model for the source rig and the asset alike.
        var restSrc = PoseSource(source, sk, Rest);
        Assert.True(Compare(restSrc, source.Rest).Max < 0.001f, "the source rig does not reproduce its own model at rest\n" + sb);
        // Max's own rig is the reference: with the file's joint positions the arms-down body moves as Max's rig does
        // (quantisation only); without them the collar and shoulder turn about the avatar's joints and open a seam.
        var armsSource = PoseSource(source, sk, ArmsDown);
        var armsOn = Compare(PoseAsset(decodedJp, sk, true, ArmsDown, out _), armsSource);
        var armsOff = Compare(PoseAsset(decoded, sk, false, ArmsDown, out _), armsSource);
        Assert.True(armsOn.Max < 0.001f, $"joint positions on: {armsOn.Max * 1000:0.00} mm\n" + sb);
        Assert.True(armsOff.Max > 0.01f, $"joint positions off: {armsOff.Max * 1000:0.00} mm");
        var restAsset = PoseAsset(decoded, sk, false, Rest, out _);
        Assert.True(Compare(restAsset, source.Rest).Max < 0.002f, "the asset does not reproduce the model at rest\n" + sb);
    }

    static void DescribePose(StringBuilder sb, string frame, PoseSpec pose, string variant, Diff d, SourceRig source, Vector3[] posed, Vector3[] src,
        (int Joint, float Weight)[][] assetInfl, MeshAssetModel model, Vector3[] rest)
    {
        sb.AppendLine($"== frame {frame}, pose {pose.Name}, {variant}: max {Mm(d.Max)} mm, mean {Mm(d.Mean)} mm, p99 {Mm(d.P99)} mm");
        // per joint group: the source's heaviest joint
        var groups = new Dictionary<string, List<float>>();
        for (int v = 0; v < d.PerVertex.Length; v++)
        {
            var w = source.Influences[v];
            string g = w.Length == 0 ? "(none)" : source.JointNames[w.OrderByDescending(x => x.Weight).First().Joint];
            if (!groups.TryGetValue(g, out var l)) groups[g] = l = new();
            l.Add(d.PerVertex[v]);
        }
        foreach (var (g, l) in groups.OrderByDescending(x => x.Value.Max()).Take(8))
            sb.AppendLine($"   group {g}: {l.Count} vertices, max {Mm(l.Max())} mm, mean {Mm(l.Average())} mm");
        if (pose != ArmsDown || d.Max < 0.0005f) return;
        var worst = Enumerable.Range(0, d.PerVertex.Length).OrderByDescending(i => d.PerVertex[i]).Take(20);
        foreach (var i in worst)
            sb.AppendLine($"   vertex {i} at ({rest[i].X:0.000},{rest[i].Y:0.000},{rest[i].Z:0.000}) diff {Mm(d.PerVertex[i])} mm | source: {Describe(source.Influences[i], j => source.JointNames[j])} | asset: {Describe(assetInfl[i], j => model.Skin!.JointNames[j])}");
    }
}
