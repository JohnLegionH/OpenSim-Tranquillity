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
using SharpGLTF.Schema2;
using static OpenSim.MeshAsset.Tests.MotionEquivalenceTests;

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// A female body worn on a male frame (the Male switch moves and scales bones, avatar_lad.xml parameter 32): which vertices
/// move furthest from where the model's own rig puts them, and whether each of the three binding forms shows it.
/// </summary>
public class MaleFrameTests
{
    static string Mm(double m) => (m * 1000).ToString("0.0", CultureInfo.InvariantCulture);
    static string V(Vector3 v) => $"({Mm(v.X)},{Mm(v.Y)},{Mm(v.Z)})";

    public sealed record Region(string Name, Func<int, bool> Has);

    static float HeadSeam(Vector3[] posed, Vector3[] src, BodyShadingTests.Side side, Func<int, bool> head)
    {
        float worst = 0;
        foreach (var t in side.Tris)
            foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                if (head(a) && head(b)) worst = Math.Max(worst, ((posed[a] - src[a]) - (posed[b] - src[b])).Length());
        return worst;
    }

    [SkeletonFact]
    public void TheThreeFormsOnTheMaleFrame()
    {
        var dir = Environment.GetEnvironmentVariable("MESH5_REPORT_DIR") ?? "";
        if (!File.Exists(SkinRefs.GltfModel)) return;
        var s = FittedShapeTests.Prepare();
        var sb = new StringBuilder();
        var forms = new (string Name, MeshAssetModel Model, bool Jp, RestSkeleton Frame, string File)[]
        {
            ("ours: fitted binding, joint positions off, fitted shape + Male", s.Forms[2].Model, false, s.FittedMale, "ours"),
            ("joint-positions form (default binding, positions on), default shape + Male", s.Forms[1].Model, true, s.Male, "jp"),
            ("default binding, joint positions off, default shape + Male", s.Forms[0].Model, false, s.Male, "default"),
        };

        sb.AppendLine("== joints: scale and world position (mm) at the shapes");
        foreach (var j in new[] { "mNeck", "mHead", "mSkull", "mCollarRight", "mShoulderRight", "mElbowRight", "mChest" })
        {
            string Row(string label, RestSkeleton sk) { var jj = sk.Find(j)!; return $"   {label}: scale ({jj.Scale.X:0.000},{jj.Scale.Y:0.000},{jj.Scale.Z:0.000}) world {V(sk.World(j).Translation)}"; }
            sb.AppendLine(j);
            sb.AppendLine(Row("bare file          ", s.Bare)); sb.AppendLine(Row("default shape      ", s.Default));
            sb.AppendLine(Row("default + Male     ", s.Male)); sb.AppendLine(Row("fitted shape      ", s.Fitted)); sb.AppendLine(Row("fitted + Male      ", s.FittedMale));
        }

        var restSrc = PoseSource(s.Source, s.Default, Rest);
        var armsSrc = PoseSource(s.Source, s.Default, ArmsDown);
        string G(int v) => s.Source.Influences[v].Length == 0 ? "-" : s.Source.JointNames[s.Source.Influences[v].OrderByDescending(x => x.Weight).First().Joint];
        bool IsHead(int v) => restSrc[v].Z > 1.55f && Math.Abs(restSrc[v].Y) < 0.15f;
        bool IsShoulder(int v) { var g = G(v); return restSrc[v].Z <= 1.55f && restSrc[v].Z > 1.25f && (g.Contains("CLAVICLE") || g.Contains("UPPER_ARM") || g.Contains("Collar") || g.Contains("Shoulder") || g.Contains("CHEST")); }
        bool IsBackOfHead(int v) => IsHead(v) && restSrc[v].X < -0.06f;
        var regions = new[] { new Region("head (rest z over 1.55 m, within 150 mm of the middle)", IsHead), new Region("back of the head (also x under -60 mm)", IsBackOfHead), new Region("shoulders and upper chest", IsShoulder), new Region("everything", _ => true) };

        var seams = new List<(float Head, float Shoulder)>();
        foreach (var (name, model, jp, frame, file) in forms)
        {
            var (posed, nrm, side) = (default(Vector3[])!, default(Vector3[])!, default(BodyShadingTests.Side)!);
            (posed, nrm) = BodyShadingTests.PoseLit(model, frame, jp, ArmsDown, out side);
            var (restPosed, _) = BodyShadingTests.PoseLit(model, frame, jp, Rest, out _);
            PoseAsset(model, frame, jp, ArmsDown, out var infl);
            sb.AppendLine();
            sb.AppendLine($"== {name}");
            foreach (var r in regions)
            {
                var idx = Enumerable.Range(0, posed.Length).Where(r.Has).ToArray();
                var d = idx.Select(v => (posed[v] - armsSrc[v]).Length()).ToArray();
                sb.AppendLine($"{r.Name}: {idx.Length} vertices, arms-down distance from the model's own rig: largest {Mm(d.Max())} mm, mean {Mm(d.Average())} mm");
            }
            foreach (var r in regions.Take(3))
            {
                sb.AppendLine($"  furthest vertices, {r.Name}:");
                foreach (var v in Enumerable.Range(0, posed.Length).Where(r.Has).OrderByDescending(v => (posed[v] - armsSrc[v]).Length()).Take(5))
                {
                    var w = string.Join(" ", infl[v].OrderByDescending(x => x.Weight).Select(x => $"{model.Skin!.JointNames[x.Joint]} {x.Weight:0.00}"));
                    sb.AppendLine($"    vertex {v} rest {V(restSrc[v])} mm; the model's own rig posed {V(armsSrc[v])}; here {V(posed[v])}; off by {V(posed[v] - armsSrc[v])} = {Mm((posed[v] - armsSrc[v]).Length())} mm; weights {w}");
                }
            }
            var row = FittedShapeTests.Measure(s, name, model, jp, "male", frame);
            sb.AppendLine($"seams (largest change of asset-minus-source across one triangle edge): all edges {Mm(row.SeamAll)} mm, clavicle/upper arm edges {Mm(row.SeamShoulder)} mm; arms-down max {Mm(row.ArmsMax)} mm, mean {Mm(row.ArmsMean)} mm");
            float headSeam = HeadSeam(posed, armsSrc, side, IsHead);
            sb.AppendLine($"head-only edges: {Mm(headSeam)} mm");
            seams.Add((headSeam, row.SeamShoulder));
            if (dir.Length > 0) BodyShadingTests.Write(dir, $"lit-male-{file}", posed, nrm, side);
        }
        if (dir.Length > 0) File.WriteAllText(Path.Combine(dir, "male-frame-report.txt"), sb.ToString());

        // The raised back of the head comes from the model's own weights (back-of-skull vertices weighted to the neck, which the
        // Male switch stretches), so every form shows it alike; our form's shoulder seam is no worse than the joint-positions
        // form's by more than a few millimetres and far better than the default binding's.
        Assert.True(Math.Abs(seams[0].Head - seams[1].Head) < 0.001f, sb.ToString());
        Assert.True(seams[0].Shoulder < seams[1].Shoulder + 0.005f, sb.ToString());
        Assert.True(seams[0].Shoulder < seams[2].Shoulder, sb.ToString());
    }
}
