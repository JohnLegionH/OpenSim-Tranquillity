/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using Nini.Config;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

// [Jolt] FitStepInTempStack: the contact capacity a region's physics system is made with, against joltc's temp stack.
// Pure config arithmetic, no native: runs in parallel.
public class TempStackFitTests
{
    private static JoltConfig Config(params (string Key, string Value)[] keys)
    {
        var source = new IniConfigSource();
        IConfig jolt = source.AddConfig("Jolt");
        foreach ((string key, string value) in keys)
            jolt.Set(key, value);
        var warnings = new List<string>();
        JoltConfig c = JoltConfig.FromConfig(source, warnings);
        Assert.Empty(warnings);
        return c;
    }

    [Fact]
    public void The_default_contact_capacity_does_not_fit_the_temp_stack()
    {
        // 16384 x 868 + 128 KB = 14.4 MB against an 8 MB stack: every step of every region takes its buffer from malloc.
        Assert.False(JoltTempStack.Fits(16384));
        Assert.True(JoltTempStack.StepBytes(16384) > JoltTempStack.StackBytes);
    }

    [Fact]
    public void The_largest_capacity_that_fits_leaves_the_other_scratch_free()
    {
        int fit = JoltTempStack.MaxContactConstraintsThatFit;
        Assert.True(JoltTempStack.Fits(fit));
        Assert.False(JoltTempStack.Fits(fit + 1));
        Assert.True(JoltTempStack.StepBytes(fit) + JoltTempStack.OtherScratchBytes <= JoltTempStack.StackBytes);
    }

    [Fact]
    public void Off_by_default_the_capacity_is_as_configured()
    {
        JoltConfig c = Config();
        Assert.False(c.FitStepInTempStack);
        Assert.Equal(16384, c.ToBackendSettings(256, 256).MaxContactConstraints);
    }

    [Fact]
    public void On_the_capacity_is_capped_to_what_fits()
    {
        JoltConfig c = Config(("FitStepInTempStack", "true"));
        Assert.True(c.FitStepInTempStack);
        Assert.Equal(JoltTempStack.MaxContactConstraintsThatFit, c.ToBackendSettings(256, 256).MaxContactConstraints);
    }

    [Fact]
    public void On_a_capacity_scaled_by_area_is_capped_too()
    {
        // A 1024 x 1024 region scales 16384 by 16; capped, its step still fits.
        JoltConfig c = Config(("FitStepInTempStack", "true"), ("ScaleCapsWithArea", "true"));
        Assert.Equal(JoltTempStack.MaxContactConstraintsThatFit, c.ToBackendSettings(1024, 1024).MaxContactConstraints);
    }

    [Fact]
    public void On_a_capacity_that_already_fits_is_left_alone()
    {
        JoltConfig c = Config(("FitStepInTempStack", "true"), ("MaxContactConstraints", "4000"));
        Assert.Equal(4000, c.ToBackendSettings(256, 256).MaxContactConstraints);
    }
}
