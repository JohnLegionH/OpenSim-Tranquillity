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

/// <summary>Fitting the shape sliders to a model's own rig (see <see cref="ShapeFit"/>).</summary>
public class ShapeFitTests
{
    public static readonly string[] ArmChain =
    {
        "mCollarLeft", "mCollarRight", "mShoulderLeft", "mShoulderRight", "mElbowLeft", "mElbowRight", "mWristLeft", "mWristRight",
    };

    static string Mm(double m) => (m * 1000).ToString("0.00", CultureInfo.InvariantCulture);

    [SkeletonFact]
    public void TheSlidersCanBeFittedToMaxsOwnRig()
    {
        if (!File.Exists(SkinRefs.GltfModel)) return;
        var bare = RestSkeleton.Load(SkinRefs.Skeleton);
        var rules = ShapeRules.Load(SkinRefs.AvatarDefinition);
        var root = ModelRoot.Load(SkinRefs.GltfModel);
        var positions = GltfToMesh.JointPositions(root, bare, n => bare.Find(n) != null, "neutral_bone");
        var targets = ShapeFit.TargetsFor(positions, bare);

        var before = ShapeFit.Distances(bare, rules, targets, new Dictionary<int, float>());
        var fit = ShapeFit.Fit(bare, rules, targets, ShapeFit.BodySliders, keepDefaultBelow: 1e-6);
        var after = ShapeFit.Distances(bare, rules, targets, fit.Values);
        // what other viewers see: the wire carries each weight as one byte across its range
        var wire = fit.Values.ToDictionary(kv => kv.Key, kv => ShapeRules.WireQuantised(rules.Params[kv.Key], kv.Value));
        var afterWire = ShapeFit.Distances(bare, rules, targets, wire);

        var sb = new StringBuilder();
        sb.AppendLine($"targets {targets.Count}; cost unrounded {fit.CostUnrounded * 1e6:0.0000} mm2 rounded {fit.Cost * 1e6:0.0000} mm2 (weighted), iterations {fit.Iterations}");
        sb.AppendLine("== slider\tid\tmin\tmax\tdefault\tfitted\tstoredAsText\twireByte");
        foreach (int id in ShapeFit.BodySliders)
        {
            var s = rules.Params[id];
            sb.AppendLine($"{s.Name}\t{id}\t{s.Min}\t{s.Max}\t{s.Default}\t{fit.Values[id].ToString("0.##", CultureInfo.InvariantCulture)}\t{ShapeRules.WireQuantised(s, fit.Values[id]):0.0000}");
        }
        sb.AppendLine("== joint\tweight\tbefore_mm\tafter_mm\tafter_wire_mm");
        foreach (var t in targets)
            sb.AppendLine($"{t.Joint}\t{t.Weight}\t{Mm(before[t.Joint])}\t{Mm(after[t.Joint])}\t{Mm(afterWire[t.Joint])}");
        var dir = Environment.GetEnvironmentVariable("MESH4_REPORT_DIR") ?? "";
        if (dir.Length > 0) File.WriteAllText(Path.Combine(dir, "shape-fit.txt"), sb.ToString());
        Assert.True(ArmChain.All(j => after[j] < 0.002f), "the arm chain is not within 2 mm\n" + sb);
    }
}
