/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Xml.Linq;

namespace OpenSim.MeshAsset.Tests;

/// <summary>
/// The skeleton at the default shape is the one the viewer poses: a slider that drives other parameters (Head Size,
/// Eye Spacing) sets them from its own default, it does not leave them at their own defaults.
/// </summary>
public class DefaultShapeTests
{
    const string SkeletonXml = """
        <linden_skeleton><bone name="mA" pos="0 0 1"><bone name="mB" pos="0 0 0.5"/></bone></linden_skeleton>
        """;
    // "Driver" defaults to 0.75 of its 0..1 range, so the viewer sets "Out" (range -1..1, no default of its own) to 0.5.
    const string DefinitionXml = """
        <linden_avatar>
          <param id="2" name="Driver" group="0" wearable="shape" value_min="0" value_max="1" value_default=".75">
            <param_driver><driven id="3"/></param_driver>
          </param>
          <param id="3" name="Out" group="1" wearable="shape" value_min="-1" value_max="1">
            <param_skeleton><bone name="mB" offset="0.1 0 0"/></param_skeleton>
          </param>
        </linden_avatar>
        """;

    [Fact]
    public void TheDefaultShapeAppliesADriversDefaultToTheParameterItDrives()
    {
        var file = ShapeRules.Parse(XDocument.Parse(DefinitionXml));
        {
            var bare = RestSkeleton.Parse(XDocument.Parse(SkeletonXml));
            Assert.Equal(0.05f, bare.WithShape(file, null).Find("mB")!.Position.X - bare.Find("mB")!.Position.X, 4);
            // the same through the by-name form with nothing set, and an explicit value for the driver
            Assert.Equal(0.05f, bare.WithShape(file, null).Find("mB")!.Position.X, 4);
            Assert.Equal(0.1f, bare.WithShape(file, new Dictionary<string, float> { ["Driver"] = 1f }).Find("mB")!.Position.X, 4);
            // a driven parameter named directly is set as given, after the drivers (the Male switch is given this way)
            Assert.Equal(0.02f, bare.WithShape(file, new Dictionary<string, float> { ["Out"] = 0.2f }).Find("mB")!.Position.X, 4);
        }
    }

    /// <summary>
    /// At the avatar definition's defaults the head is 0.925 of its size (Head Size .5 of 0..1 drives a -0.25..0.10 scale
    /// parameter to -0.075) and the eye bones sit a third of the eye-spread offset out (Eye Spacing 0 of -2..1 drives a
    /// -1..1 parameter to 1/3 of the way up, offset 0.009 on Y).
    /// </summary>
    [SkeletonFact]
    public void TheDefaultSkeletonsHeadAndEyesAreWhereTheViewerPutsThem()
    {
        var bare = RestSkeleton.Load(SkinRefs.Skeleton);
        var shaped = bare.WithDefaultShape(SkinRefs.AvatarDefinition);
        Assert.Equal(0.925f, shaped.Find("mHead")!.Scale.X, 4);
        Assert.Equal(0.925f, shaped.Find("mSkull")!.Scale.Z, 4);
        float spread = 0.009f * (-1f + 2f * 2f / 3f) ; // driven weight of Eye Spacing 0
        Assert.Equal(bare.Find("mEyeLeft")!.Position.Y + spread, shaped.Find("mEyeLeft")!.Position.Y, 5);
        Assert.Equal(bare.Find("mEyeRight")!.Position.Y - spread, shaped.Find("mEyeRight")!.Position.Y, 5);
    }
}
