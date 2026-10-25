/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using System.Xml.Linq;

namespace OpenSim.MeshAsset.Tests;

/// <summary>Shape rules, the skeleton at a shape, and the fit, on a two-bone skeleton and a four-parameter definition made up here.</summary>
public class ShapeRulesTests
{
    // mA at height 1 with mB 0.5 above it; "Len" stretches mA (so mB moves up the stretch); "Driver" drives "Out", which moves mB forward.
    const string SkeletonXml = """
        <linden_skeleton><bone name="mA" pos="0 0 1"><bone name="mB" pos="0 0 0.5"/></bone></linden_skeleton>
        """;
    const string DefinitionXml = """
        <linden_avatar>
          <param id="1" name="Len" group="0" wearable="shape" value_min="-1" value_max="1" value_default="0.2">
            <param_skeleton><bone name="mA" scale="0 0 1"/></param_skeleton>
          </param>
          <param id="2" name="Driver" group="0" wearable="shape" value_min="0" value_max="1" value_default=".5">
            <param_driver><driven id="3"/></param_driver>
          </param>
          <param id="3" name="Out" group="1" wearable="shape" value_min="-1" value_max="1">
            <param_skeleton><bone name="mB" offset="0.1 0 0"/></param_skeleton>
          </param>
          <param id="4" name="Up" group="0" wearable="shape" value_min="-1" value_max="1">
            <param_skeleton><bone name="mB" offset="0 0 0.05"/></param_skeleton>
          </param>
        </linden_avatar>
        """;

    static (RestSkeleton Bare, ShapeRules Rules) Make() =>
        (RestSkeleton.Parse(XDocument.Parse(SkeletonXml)), ShapeRules.Parse(XDocument.Parse(DefinitionXml)));

    [Fact]
    public void ADrivenParameterTakesItsWeightFromItsDriver()
    {
        var (_, rules) = Make();
        Assert.Equal(0f, rules.Resolve(null)[3], 4);                    // driver at its default, the middle of its range
        Assert.Equal(1f, rules.Resolve(new Dictionary<int, float> { [2] = 1f })[3], 4);
        Assert.Equal(-1f, rules.Resolve(new Dictionary<int, float> { [2] = 0f })[3], 4);
        // a value outside the range is clamped, and a driven value that is given is replaced by the driver's
        Assert.Equal(1f, rules.Resolve(new Dictionary<int, float> { [1] = 5f })[1], 4);
        Assert.Equal(0f, rules.Resolve(new Dictionary<int, float> { [3] = 0.9f })[3], 4);
    }

    [Fact]
    public void TheSkeletonAtAShapeFollowsTheResolvedWeights()
    {
        var (bare, rules) = Make();
        // default: Len 0.2 stretches mA by 1.2, so mB is at 1 + 0.5 * 1.2; the driven offset is 0
        var at = bare.WithSliders(rules, null).World("mB").Translation;
        Assert.Equal(1.6f, at.Z, 4);
        Assert.Equal(0f, at.X, 4);
        // Driver 1 moves mB forward by 0.1, Up 1 raises it by 0.05
        var moved = bare.WithSliders(rules, new Dictionary<int, float> { [1] = 0f, [2] = 1f, [4] = 1f }).World("mB").Translation;
        Assert.Equal(0.1f, moved.X, 4);
        Assert.Equal(1.55f, moved.Z, 4);
    }

    [Fact]
    public void TheFitFindsTheSlidersThatPlaceTheJoints()
    {
        var (bare, rules) = Make();
        var truth = new Dictionary<int, float> { [1] = -0.4f, [2] = 0.8f, [4] = 0.3f };
        var target = bare.WithSliders(rules, truth).World("mB").Translation;
        // three unknowns, three coordinates of one joint
        var fit = ShapeFit.Fit(bare, rules, new[] { new ShapeFit.Target("mB", target, 1) }, new[] { 1, 2, 4 });
        var placed = bare.WithSliders(rules, fit.Values).World("mB").Translation;
        // two of the three unknowns move the joint the same way (stretch and lift), and one step of the stretch is 5 mm: the two-digit rounding is all that is left
        Assert.True((placed - target).Length() < 0.003f, $"placed {placed}, wanted {target}");
        Assert.All(fit.Values.Values, v => Assert.Equal(Math.Round(v, 2), v, 6));                // stored with two digits
        // a joint that cannot be reached is as near as the range allows
        var far = new Vector3(0, 0, 5);
        var nearest = ShapeFit.Fit(bare, rules, new[] { new ShapeFit.Target("mB", far, 1) }, new[] { 1, 2, 4 });
        Assert.Equal(1f, nearest.Values[1]);                                                     // the stretch is at its limit
    }

    [Fact]
    public void AValueCarriedOnTheWireIsOneByteAcrossTheRange()
    {
        var (_, rules) = Make();
        var s = rules.Params[1];
        Assert.Equal(-1f, ShapeRules.WireQuantised(s, -1f));
        Assert.Equal(1f, ShapeRules.WireQuantised(s, 1f), 5);
        Assert.True(Math.Abs(ShapeRules.WireQuantised(s, 0.3f) - 0.3f) <= (s.Max - s.Min) / 255f / 2 + 1e-6f);
    }

    [Fact]
    public void AWearableHoldsEachDriversDrivenWeightBesideIt()
    {
        var (_, rules) = Make();
        var values = ShapeFit.WearableValues(rules, new Dictionary<int, float> { [2] = 0.8f, [1] = 0f });
        Assert.Equal(0.6f, values[3], 4);       // -1 + 0.8 * 2
        Assert.Equal(0.8f, values[2], 4);
    }
}
