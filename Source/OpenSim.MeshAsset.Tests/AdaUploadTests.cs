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
/// The three ways a rigged body can be bound, each worn on three skeletons: the shape the body is fitted to, the default
/// shape, and the Male switch on the default shape. "Fitted" binds at a shape found from the model's own rig with joint
/// positions off; "joint positions" is the form the viewer's uploader writes with "Include Joint Positions" checked
/// (the model's joint positions as alternate bind matrices, the skeleton's matrices moved to them, pelvis offset 0, no
/// scale lock); "default" binds at the default shape with joint positions off. A form's shift is how far the worn body's
/// vertices lie from the model as its author drew it, at rest.
/// </summary>
public class AdaUploadTests
{
    static string Mm(double m) => (m * 1000).ToString("0.00", CultureInfo.InvariantCulture);

    public sealed record Shift(string Region, int Count, float Mean, float Max, float Step);

    static Shift Region(string name, Vector3[] posed, Vector3[] src, Func<int, bool> has, IEnumerable<(int A, int B)> edges)
    {
        var idx = Enumerable.Range(0, posed.Length).Where(has).ToArray();
        var d = idx.Select(v => (posed[v] - src[v]).Length()).ToArray();
        float step = 0;
        foreach (var (a, b) in edges)
            if (has(a) && has(b)) step = Math.Max(step, ((posed[a] - src[a]) - (posed[b] - src[b])).Length());
        return new Shift(name, idx.Length, d.Length == 0 ? 0 : d.Average(), d.Length == 0 ? 0 : d.Max(), step);
    }

    /// <summary>The centre of each eyeball (the eye mesh's vertices on each side of the middle), at rest as modelled and as worn.</summary>
    static (Vector3 Left, Vector3 Right) EyeCentres(Vector3[] v)
    {
        var l = v.Where(p => p.Y > 0).ToArray(); var r = v.Where(p => p.Y <= 0).ToArray();
        Vector3 C(Vector3[] a) => a.Length == 0 ? Vector3.Zero : a.Aggregate(Vector3.Zero, (s, p) => s + p) / a.Length;
        return (C(l), C(r));
    }

    [SkeletonFact]
    public void TheThreeFormsWornAtThreeShapes()
    {
        if (!File.Exists(SkinRefs.GltfModel)) return;
        var dir = Environment.GetEnvironmentVariable("MESH8_REPORT_DIR") ?? "";
        var s = FittedShapeTests.Prepare();
        var sb = new StringBuilder();
        var restSrc = PoseSource(s.Source, s.Default, Rest);
        var armsSrc = PoseSource(s.Source, s.Default, ArmsDown);
        bool IsHead(int v) => restSrc[v].Z > 1.55f && Math.Abs(restSrc[v].Y) < 0.15f;
        var edges = new List<(int, int)>();
        for (int f = 0; f < s.Indices.Length; f++)
            for (int t = 0; t + 2 < s.Indices[f].Length; t += 3)
            {
                int a = s.FaceStart[f] + s.Indices[f][t], b = s.FaceStart[f] + s.Indices[f][t + 1], c = s.FaceStart[f] + s.Indices[f][t + 2];
                edges.Add((a, b)); edges.Add((b, c)); edges.Add((c, a));
            }
        var regions = new (string, Func<int, bool>)[]
        {
            ("arm and hand (|y| over 150 mm)", v => Math.Abs(restSrc[v].Y) > 0.15f),
            ("head (z over 1.55 m)", IsHead),
            ("back of the head (head, x under -60 mm)", v => IsHead(v) && restSrc[v].X < -0.06f),
            ("legs (z under 0.9 m)", v => restSrc[v].Z < 0.9f),
        };

        // the forms (index 2 is the fitted binding, 1 the joint positions form, 0 the default binding)
        var forms = new (string Name, MeshAssetModel Model, bool Jp, RestSkeleton Own)[]
        {
            ("#6 fitted binding, positions off", s.Forms[2].Model, false, s.Fitted),
            ("#8 joint positions (as Ada uploads)", s.Forms[1].Model, true, s.Default),
            ("#9 default binding, positions off", s.Forms[0].Model, false, s.Default),
        };
        var frames = new (string Name, RestSkeleton Frame)[]
        {
            ("default female shape", s.Default), ("Male switch on the default shape", s.Male), ("Max's own (fitted) shape", s.Fitted),
        };

        var eyesFile = Path.Combine(Path.GetDirectoryName(SkinRefs.GltfModel)!, "MaxineV1Draft1_9__MaxEyes_check_positioning.glb");
        (string Name, MeshAssetModel Model)[]? eyeForms = null;
        if (File.Exists(eyesFile))
        {
            var eroot = ModelRoot.Load(eyesFile);
            MeshAssetModel EncodeEyes(RestSkeleton at, bool jp)
            {
                var (m, _) = GltfToMesh.Convert(eroot, n => at.Find(n) != null, "neutral_bone", new GltfToMesh.JointBinding(at, jp));
                return MeshCodec.Decode(MeshCodec.Encode(m));
            }
            eyeForms = new[] { ("#6", EncodeEyes(s.Fitted, false)), ("#8", EncodeEyes(s.Default, true)), ("#9", EncodeEyes(s.Default, false)) };
        }

        var table = new Dictionary<string, Shift>();
        var armsDownOnDefault = new float[forms.Length];
        for (int fi = 0; fi < forms.Length; fi++)
        {
            var (name, model, jp, own) = forms[fi];
            sb.AppendLine($"== {name}");
            foreach (var (fname, frame) in frames)
            {
                var rest = PoseAsset(model, frame, jp, Rest, out _);
                var arms = PoseAsset(model, frame, jp, ArmsDown, out _);
                var ad = Compare(arms, armsSrc);
                sb.AppendLine($"  worn on: {fname}");
                foreach (var (rname, has) in regions)
                {
                    var r = Region(rname, rest, restSrc, has, edges);
                    table[$"{fi}|{fname}|{rname}"] = r;
                    sb.AppendLine($"    rest, {rname}: {r.Count} vertices, shift mean {Mm(r.Mean)} mm, max {Mm(r.Max)} mm, largest step across one edge {Mm(r.Step)} mm");
                }
                if (frame == s.Default) armsDownOnDefault[fi] = ad.Max;
                sb.AppendLine($"    arms-down against the model's rig posed the same: mean {Mm(ad.Mean)} mm, max {Mm(ad.Max)} mm");
                if (eyeForms != null)
                {
                    var em = eyeForms[fi].Model;
                    var restEye = PoseAsset(em, own, jp, Rest, out _);
                    var wornEye = PoseAsset(em, frame, jp, Rest, out _);
                    var (l0, r0) = EyeCentres(restEye); var (l1, r1) = EyeCentres(wornEye);
                    sb.AppendLine($"    eyeballs (the model's eye mesh): left centre moves {Mm((l1 - l0).Length())} mm ({Mm(l1.X - l0.X)}, {Mm(l1.Y - l0.Y)}, {Mm(l1.Z - l0.Z)}), right {Mm((r1 - r0).Length())} mm ({Mm(r1.X - r0.X)}, {Mm(r1.Y - r0.Y)}, {Mm(r1.Z - r0.Z)}) against where they sit worn on the shape the form was bound for");
                }
            }
            // single joints, 45 degrees about each axis, on the form's own shape
            float worst = 0; string worstPose = "";
            foreach (var pose in SingleJointPoses())
            {
                var d = Compare(PoseAsset(model, own, jp, pose, out _), PoseSource(s.Source, s.Default, pose)).Max;
                if (d > worst) { worst = d; worstPose = pose.Name; }
            }
            sb.AppendLine($"  single joints (51 x 6 poses) on its own shape: worst {Mm(worst)} mm ({worstPose})");
            var rows = new[] { Rest, ArmsDown }.Select(p => (p.Name, Compare(PoseAsset(model, own, jp, p, out _), PoseSource(s.Source, s.Default, p)).Max));
            foreach (var (pn, mx) in rows) sb.AppendLine($"  {pn} on its own shape: max {Mm(mx)} mm");
        }
        if (dir.Length > 0)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "three-forms-report.txt"), sb.ToString());
            // the posed, lit vertices of each form worn at rest on two skeletons, for the renders
            for (int fi = 0; fi < forms.Length; fi++)
                foreach (var (fname, frame) in new[] { ("default", s.Default), ("male", s.Male) })
                {
                    var (p, n) = BodyShadingTests.PoseLit(forms[fi].Model, frame, forms[fi].Jp, Rest, out var side);
                    BodyShadingTests.Write(dir, $"lit-{fi}-{fname}-body", p, n, side);
                    if (eyeForms != null)
                    {
                        var (ep, en) = BodyShadingTests.PoseLit(eyeForms[fi].Model, frame, forms[fi].Jp, Rest, out var eside);
                        BodyShadingTests.Write(dir, $"lit-{fi}-{fname}-eyes", ep, en, eside);
                    }
                }
        }

        // At the shape a form was bound for nothing worn moves. The joint positions form pins the joints it lists, so it also
        // poses like the model's rig on the default shape (the default binding is 22 mm off in the arms-down pose, the
        // fitted one 67 mm). A pinned joint still follows the scale sliders (the viewer scales a pinned joint unless the
        // scale lock is set), so a form bound with the default shape's bone scales is off on a shape whose scales differ.
        string arm = regions[0].Item1;
        Assert.True(table[$"0|Max's own (fitted) shape|{arm}"].Max < 0.002f, sb.ToString());
        Assert.True(table[$"1|default female shape|{arm}"].Max < 0.002f, sb.ToString());
        Assert.True(table[$"2|default female shape|{arm}"].Max < 0.002f, sb.ToString());
        Assert.True(table[$"0|default female shape|{arm}"].Max > 0.02f, sb.ToString());
        Assert.True(table[$"1|Max's own (fitted) shape|{arm}"].Max > 0.02f, sb.ToString());
        Assert.True(armsDownOnDefault[1] < 0.001f && armsDownOnDefault[2] > armsDownOnDefault[1] && armsDownOnDefault[0] > armsDownOnDefault[2], sb.ToString());
    }
}
