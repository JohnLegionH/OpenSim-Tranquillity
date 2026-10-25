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
/// A body bound at the shape whose sliders put the avatar's joints where the model's own rig has them, against the same
/// body bound at the default shape, both posed by the viewer's rules and compared with the model's own rig (the
/// reference). With the joints already where the model's rig has them, the limbs turn about the model's pivots without
/// any joint position being written into the asset.
/// </summary>
public class FittedShapeTests
{
    static string Mm(double m) => (m * 1000).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Largest change of (asset - source) across one triangle edge: over all edges, and over the edges between the two arm volumes.</summary>
    static (float All, float Shoulder) SeamJump(Vector3[] posed, Vector3[] src, int[][] indices, int[] faceStart, SourceRig source)
    {
        string G(int v) => source.Influences[v].Length == 0 ? "-" : source.JointNames[source.Influences[v].OrderByDescending(x => x.Weight).First().Joint];
        float all = 0, shoulder = 0;
        for (int f = 0; f < indices.Length; f++)
            for (int i = 0; i + 2 < indices[f].Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = faceStart[f] + indices[f][i + k], b = faceStart[f] + indices[f][i + (k + 1) % 3];
                    float jump = ((posed[a] - src[a]) - (posed[b] - src[b])).Length();
                    all = Math.Max(all, jump);
                    string ga = G(a), gb = G(b);
                    bool clav = ga.EndsWith("CLAVICLE") || gb.EndsWith("CLAVICLE");
                    bool arm = ga.EndsWith("UPPER_ARM") || gb.EndsWith("UPPER_ARM");
                    if (clav && arm) shoulder = Math.Max(shoulder, jump);
                }
        return (all, shoulder);
    }

    public sealed record Row(string Form, string Frame, float RestMax, float ArmsMax, float ArmsMean, float SeamAll, float SeamShoulder);

    public sealed class Setup
    {
        public required RestSkeleton Bare, Default, TrueDefault, Fitted, Male, FittedMale;
        public required ShapeRules Rules;
        public required Dictionary<int, float> Values;
        public required ModelRoot Root;
        public required SourceRig Source;
        public required int[][] Indices;
        public required int[] FaceStart;
        public required (string Name, MeshAssetModel Model, RestSkeleton Own)[] Forms;
    }

    public static Setup Prepare()
    {
        var bare = RestSkeleton.Load(SkinRefs.Skeleton);
        var rules = ShapeRules.Load(SkinRefs.AvatarDefinition);
        var root = ModelRoot.Load(SkinRefs.GltfModel);
        var sk = SkinRefs.LoadSkeleton();
        var positions = GltfToMesh.JointPositions(root, bare, n => bare.Find(n) != null, "neutral_bone");
        var fit = ShapeFit.Fit(bare, rules, ShapeFit.TargetsFor(positions, bare), ShapeFit.BodySliders, keepDefaultBelow: 1e-6);
        var fitted = bare.WithSliders(rules, fit.Values);
        var male = bare.WithShape(SkinRefs.AvatarDefinition, new Dictionary<string, float> { ["Male_Skeleton"] = 1f });
        var fittedMale = bare.WithSliders(rules, new Dictionary<int, float>(fit.Values) { [80] = 1f });

        MeshAssetModel Encode(RestSkeleton at, bool jp)
        {
            var (m, _) = GltfToMesh.Convert(root, n => at.Find(n) != null, "neutral_bone", new GltfToMesh.JointBinding(at, jp));
            return MeshCodec.Decode(MeshCodec.Encode(m));
        }
        var forms = new (string, MeshAssetModel, RestSkeleton)[]
        {
            ("default binding, positions off", Encode(sk, false), sk),
            ("default binding, positions on", Encode(sk, true), sk),
            ("fitted binding, positions off", Encode(fitted, false), fitted),
        };
        var faces = forms[0].Item2.High!.Faces.Where(f => !f.NoGeometry).ToList();
        var faceStart = new int[faces.Count];
        for (int i = 1; i < faceStart.Length; i++) faceStart[i] = faceStart[i - 1] + faces[i - 1].VertexCount;
        var source = ReadSource(root, sk, GltfToMesh.Convert(root, n => sk.Find(n) != null, "neutral_bone", new GltfToMesh.JointBinding(sk, false)).Report.QuarterTurns);
        return new Setup
        {
            Bare = bare, Default = sk, TrueDefault = bare.WithSliders(rules, null), Fitted = fitted, Male = male, FittedMale = fittedMale,
            Rules = rules, Values = fit.Values, Root = root, Source = source, Indices = faces.Select(f => f.Indices).ToArray(), FaceStart = faceStart, Forms = forms,
        };
    }

    public static Row Measure(Setup s, string form, MeshAssetModel model, bool jp, string frameName, RestSkeleton frame)
    {
        var restSrc = PoseSource(s.Source, s.Default, Rest);
        var armsSrc = PoseSource(s.Source, s.Default, ArmsDown);
        var rest = Compare(PoseAsset(model, frame, jp, Rest, out _), restSrc);
        var posed = PoseAsset(model, frame, jp, ArmsDown, out _);
        var arms = Compare(posed, armsSrc);
        var seam = SeamJump(posed, armsSrc, s.Indices, s.FaceStart, s.Source);
        return new Row(form, frameName, rest.Max, arms.Max, arms.Mean, seam.All, seam.Shoulder);
    }

    [SkeletonFact]
    public void TheFittedBindingMovesLikeMaxsRigWithNoJointPositionWritten()
    {
        if (!File.Exists(SkinRefs.GltfModel)) return;
        var s = Prepare();
        var rows = new List<Row>();
        var sb = new StringBuilder();
        foreach (var (name, model, own) in s.Forms)
        {
            bool fitted = ReferenceEquals(own, s.Fitted);
            bool jp = name.Contains("positions on");
            rows.Add(Measure(s, name, model, jp, "own shape", own));
            if (fitted) rows.Add(Measure(s, name, model, jp, "default shape", s.Default));
            rows.Add(Measure(s, name, model, jp, "true default (drivers applied)", s.TrueDefault));
            rows.Add(Measure(s, name, model, jp, fitted ? "Male switch on the fitted shape" : "Male switch on the default shape", fitted ? s.FittedMale : s.Male));
        }
        sb.AppendLine("form\tframe\trest_max_mm\tarmsdown_max_mm\tarmsdown_mean_mm\tseam_all_edges_mm\tseam_clavicle_upperarm_mm");
        foreach (var r in rows)
            sb.AppendLine($"{r.Form}\t{r.Frame}\t{Mm(r.RestMax)}\t{Mm(r.ArmsMax)}\t{Mm(r.ArmsMean)}\t{Mm(r.SeamAll)}\t{Mm(r.SeamShoulder)}");

        // every single-joint pose, each form in its own shape
        sb.AppendLine("== single joints, 45 degrees about X, Y and Z, each form in its own shape: worst pose");
        foreach (var (name, model, own) in s.Forms)
        {
            bool jp = name.Contains("positions on");
            float worst = 0; string worstPose = "";
            var byJoint = new Dictionary<string, float>();
            foreach (var pose in SingleJointPoses())
            {
                var d = Compare(PoseAsset(model, own, jp, pose, out _), PoseSource(s.Source, s.Default, pose));
                if (d.Max > worst) { worst = d.Max; worstPose = pose.Name; }
                string j = pose.Turns[0].Joint;
                byJoint[j] = Math.Max(byJoint.GetValueOrDefault(j), d.Max);
            }
            sb.AppendLine($"{name}: worst {Mm(worst)} mm ({worstPose}); arm chain worst {Mm(ArmChainWorst(byJoint))} mm");
            foreach (var (j, v) in byJoint.OrderByDescending(kv => kv.Value).Take(6)) sb.AppendLine($"   {j}\t{Mm(v)}");
        }
        var dir = Environment.GetEnvironmentVariable("MESH4_REPORT_DIR") ?? "";
        if (dir.Length > 0) File.WriteAllText(Path.Combine(dir, "form-table.txt"), sb.ToString());

        // Max's own rig is the reference. The default binding turns the arm about the avatar's joints, 13 mm from Max's, and
        // opens a seam; the fitted binding turns it about Max's joints.
        var defaultOwn = rows.First(r => r.Form.StartsWith("default binding, positions off") && r.Frame == "own shape");
        var fittedOwn = rows.First(r => r.Form.StartsWith("fitted") && r.Frame == "own shape");
        Assert.True(defaultOwn.SeamShoulder > 0.010f, $"default binding seam {Mm(defaultOwn.SeamShoulder)} mm\n{sb}");
        Assert.True(fittedOwn.SeamShoulder < 0.002f, $"fitted binding seam {Mm(fittedOwn.SeamShoulder)} mm\n{sb}");
        Assert.True(fittedOwn.ArmsMax < 0.002f, $"fitted binding arms-down {Mm(fittedOwn.ArmsMax)} mm\n{sb}");
        Assert.True(fittedOwn.SeamShoulder < defaultOwn.SeamShoulder, sb.ToString());
        Assert.True(fittedOwn.RestMax < 0.002f, $"the fitted body does not reproduce the model at rest: {Mm(fittedOwn.RestMax)} mm");
    }

    static void WritePose(string name, Vector3[] posed, int[][] indices, int[] faceStart)
    {
        var dir = Environment.GetEnvironmentVariable("MESH4_REPORT_DIR") ?? "";
        if (dir.Length == 0) return;
        var tris = new List<int>();
        for (int f = 0; f < indices.Length; f++) tris.AddRange(indices[f].Select(i => i + faceStart[f]));
        using var w = new BinaryWriter(File.Create(Path.Combine(dir, $"pose-{name}.bin")));
        w.Write(posed.Length); w.Write(tris.Count);
        foreach (var p in posed) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        foreach (var t in tris) w.Write(t);
    }

    /// <summary>
    /// The mesh asset that is actually shipped (MAX_ASSET, read from its file) at the shape it is bound for (MAX_SHAPE, the
    /// fitted slider values) moves like Max's own rig. Also writes the arms-down poses of the four forms for the shoulder images.
    /// </summary>
    [SkeletonFact]
    public void TheShippedAssetMovesLikeMaxsRigAtItsShape()
    {
        var shapeFile = Environment.GetEnvironmentVariable("MAX_SHAPE") ?? "";
        if (!File.Exists(SkinRefs.GltfModel) || !File.Exists(SkinRefs.GltfAsset) || !File.Exists(shapeFile)) return;
        var s = Prepare();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(shapeFile));
        var values = doc.RootElement.GetProperty("shape").EnumerateObject().ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetSingle());
        var frame = s.Bare.WithSliders(s.Rules, values);
        var shipped = MeshCodec.Decode(File.ReadAllBytes(SkinRefs.GltfAsset));
        var row = Measure(s, "shipped file", shipped, false, "its shape", frame);
        var report = $"shipped asset at its shape: rest {Mm(row.RestMax)} mm, arms-down max {Mm(row.ArmsMax)} mm mean {Mm(row.ArmsMean)} mm, seam {Mm(row.SeamAll)} mm (clavicle/upper arm {Mm(row.SeamShoulder)} mm)";
        var dir = Environment.GetEnvironmentVariable("MESH4_REPORT_DIR") ?? "";
        if (dir.Length > 0)
        {
            File.WriteAllText(Path.Combine(dir, "shipped-asset-check.txt"), report + Environment.NewLine);
            var armsSrc = PoseSource(s.Source, s.Default, ArmsDown);
            WritePose("source", armsSrc, s.Indices, s.FaceStart);
            WritePose("A-default-off", PoseAsset(s.Forms[0].Model, s.Default, false, ArmsDown, out _), s.Indices, s.FaceStart);
            WritePose("B-default-on", PoseAsset(s.Forms[1].Model, s.Default, true, ArmsDown, out _), s.Indices, s.FaceStart);
            WritePose("C-shipped", PoseAsset(shipped, frame, false, ArmsDown, out _), s.Indices, s.FaceStart);
            WritePose("C-at-default-shape", PoseAsset(shipped, s.Default, false, ArmsDown, out _), s.Indices, s.FaceStart);
            File.WriteAllLines(Path.Combine(dir, "pose-groups.txt"), s.Source.Influences.Select(i => i.Length == 0 ? "-" : s.Source.JointNames[i.OrderByDescending(x => x.Weight).First().Joint]));
            var sh = frame.World("mShoulderRight").Translation; var sl = frame.World("mShoulderLeft").Translation;
            File.WriteAllText(Path.Combine(dir, "shoulder-centres.txt"), $"right {sh.X} {sh.Y} {sh.Z}{Environment.NewLine}left {sl.X} {sl.Y} {sl.Z}{Environment.NewLine}");
        }
        Assert.True(row.RestMax < 0.002f, report);
        Assert.True(row.ArmsMax < 0.002f, report);
        Assert.True(row.SeamShoulder < 0.002f, report);
    }

    static float ArmChainWorst(Dictionary<string, float> byJoint) =>
        new[] { "mCollarLeft", "mCollarRight", "mShoulderLeft", "mShoulderRight", "mElbowLeft", "mElbowRight", "mWristLeft", "mWristRight" }.Max(j => byJoint.GetValueOrDefault(j));
}
