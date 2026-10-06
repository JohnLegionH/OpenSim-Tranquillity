/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// [Jolt] PhysicsStepRate and PhysicsStepCollisionSteps, and the accumulator that turns heartbeats into physics
/// steps. Pure: no backend, no shared state, so these run in parallel with everything else.
/// </summary>
public class PhysicsStepRateTests
{
    private const float Heartbeat = 0.0909f;   // OpenSimDefaults.ini [Startup] FrameTime

    private static JoltConfig Parse(List<string> warnings, params (string key, string value)[] kv)
    {
        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        foreach (var (k, v) in kv)
            cfg.Set(k, v);
        return JoltConfig.FromConfig(src, warnings);
    }

    [Fact]
    public void Rate_is_off_by_default_and_the_solver_keeps_its_collision_steps()
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings);
        Assert.Empty(warnings);
        Assert.Equal(0f, c.PhysicsStepRate);
        Assert.Equal(2, c.PhysicsStepCollisionSteps);
        Assert.Equal(0f, c.EffectivePhysicsStepRate(Heartbeat, out string w));
        Assert.Null(w);
        Assert.Equal(6, c.ToBackendSettings(256, 256).CollisionSteps);
        Assert.Equal(c.ToBackendSettings(256, 256), c.ToBackendSettings(256, 256, substepping: false));
    }

    [Fact]
    public void Both_keys_parse_and_collision_steps_apply_only_when_substepping()
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings, ("PhysicsStepRate", "45"), ("PhysicsStepCollisionSteps", "3"), ("CollisionSteps", "5"));
        Assert.Empty(warnings);
        Assert.Equal(45f, c.PhysicsStepRate);
        Assert.Equal(45f, c.EffectivePhysicsStepRate(Heartbeat, out string w));
        Assert.Null(w);
        Assert.Equal(3, c.ToBackendSettings(256, 256, substepping: true).CollisionSteps);
        Assert.Equal(5, c.ToBackendSettings(256, 256, substepping: false).CollisionSteps);
    }

    [Theory]
    [InlineData("PhysicsStepRate", "-1")]
    [InlineData("PhysicsStepRate", "1001")]
    [InlineData("PhysicsStepRate", "fast")]
    [InlineData("PhysicsStepRate", "NaN")]
    [InlineData("PhysicsStepCollisionSteps", "0")]
    [InlineData("PhysicsStepCollisionSteps", "65")]
    public void An_invalid_value_warns_and_keeps_the_default(string key, string value)
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings, (key, value));
        Assert.Single(warnings);
        Assert.Contains(key, warnings[0]);
        Assert.Equal(0f, c.PhysicsStepRate);
        Assert.Equal(2, c.PhysicsStepCollisionSteps);
    }

    [Theory]
    [InlineData(5f)]     // below an 11 Hz heartbeat
    [InlineData(10.9f)]
    public void A_rate_below_the_heartbeat_is_refused_with_one_warning(float rate)
    {
        JoltConfig c = Parse(new List<string>(), ("PhysicsStepRate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(0f, c.EffectivePhysicsStepRate(Heartbeat, out string w));
        Assert.NotNull(w);
        Assert.Contains("PhysicsStepRate", w);
        Assert.Contains("FrameTime", w);
    }

    [Theory]
    [InlineData(11f)]    // the heartbeat's own rate (1 / 0.0909 = 11.001) is accepted
    [InlineData(45f)]
    [InlineData(90f)]
    public void A_rate_at_or_above_the_heartbeat_is_used(float rate)
    {
        JoltConfig c = Parse(new List<string>(), ("PhysicsStepRate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(rate, c.EffectivePhysicsStepRate(Heartbeat, out string w));
        Assert.Null(w);
    }

    [Theory]
    [InlineData(0.0909, 45f)]
    [InlineData(1.0 / 11.0, 45f)]
    [InlineData(0.0909, 60f)]
    [InlineData(0.05, 45f)]
    [InlineData(0.0222, 45f)]   // a 45 Hz heartbeat: one step each
    public void Over_a_thousand_heartbeats_the_step_count_is_right_to_within_one(double heartbeat, float rate)
    {
        var acc = new SubstepAccumulator(rate);
        int min = int.MaxValue, max = 0;
        for (int i = 0; i < 1000; i++)
        {
            int n = acc.Advance(heartbeat);
            min = Math.Min(min, n);
            max = Math.Max(max, n);
        }
        double expected = 1000 * heartbeat * rate;
        Assert.InRange(acc.Steps, (long)Math.Floor(expected) - 1, (long)Math.Ceiling(expected) + 1);
        Assert.InRange((double)acc.Steps, expected - 1.0, expected + 1.0);
        Assert.True(max - min <= 1, $"steps per heartbeat ranged {min}..{max}");   // never bunched up
        Assert.InRange(acc.Carry, 0.0, 1.0 / rate);
        Assert.Equal(0, acc.CappedFrames);
    }

    [Fact]
    public void Every_step_is_exactly_one_over_the_rate()
    {
        Assert.Equal(1f / 45f, new SubstepAccumulator(45f).StepSeconds);
        Assert.Equal(1f / 60f, new SubstepAccumulator(60f).StepSeconds);
    }

    [Fact]
    public void A_heartbeat_past_the_cap_runs_the_cap_counts_it_and_drops_the_rest()
    {
        var acc = new SubstepAccumulator(45f);
        Assert.Equal(SubstepAccumulator.MaxStepsPerFrame, acc.Advance(1.0));   // 45 steps' worth
        Assert.Equal(1, acc.CappedFrames);
        Assert.Equal(0.0, acc.Carry);                                           // dropped, not carried
        Assert.Equal(4, acc.Advance(0.0909));                                   // back to normal at once
        Assert.Equal(1, acc.CappedFrames);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_heartbeat_with_no_time_runs_no_step(double frame)
    {
        var acc = new SubstepAccumulator(45f);
        Assert.Equal(0, acc.Advance(frame));
        Assert.Equal(0, acc.Steps);
    }
}
