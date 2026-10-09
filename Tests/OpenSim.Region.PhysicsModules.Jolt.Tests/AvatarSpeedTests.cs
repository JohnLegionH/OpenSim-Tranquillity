/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Avatar walk, run and fly speeds on level ground, through the harness: ScenePresence's request (4.096 m/s for a walk
/// or a run, 16.384 m/s flying) times the [Jolt] avatar speed factors, whose defaults give Second Life's documented
/// 3.20, 5.13 and 16.00 m/s (https://wiki.secondlife.com/wiki/Default_Avatar_Movement_Speeds). Each speed is held at
/// [Jolt] PhysicsStepRate 45 and at one physics step per heartbeat, under the 11 Hz heartbeat. Serial with the other
/// native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarSpeedTests
{
    private const float Walk = 3.20f, Run = 5.13f, Fly = 16.00f;
    private const float RequestWalk = 4.096f, RequestFly = 4.096f * 4f;

    private static Summary RunScenario(string scenario, double physicsHz, float speedModifier = 1f, params (string key, string value)[] jolt)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz, AvatarSpeedModifier = speedModifier };
        foreach (var (k, v) in jolt)
            o.Jolt[k] = v;
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o).Summary;
    }

    private static void Within(float expected, float percent, float got)
    {
        float tol = expected * percent / 100f;
        Assert.InRange(got, expected - tol, expected + tol);
    }

    [Theory]
    [InlineData("avatar-walk", 45.0, Walk)]
    [InlineData("avatar-walk", 0.0, Walk)]
    [InlineData("avatar-run", 45.0, Run)]
    [InlineData("avatar-run", 0.0, Run)]
    [InlineData("avatar-fly", 45.0, Fly)]
    [InlineData("avatar-fly", 0.0, Fly)]
    public void Steady_speed_on_level_ground_is_second_lifes(string scenario, double physicsHz, float expected)
    {
        Summary m = RunScenario(scenario, physicsHz);
        Within(expected, 2f, m.SteadyHorizontalSpeed);
        Within(expected, 2f, m.SteadySpeed);
    }

    // Each factor changes its own speed and leaves the other two at their defaults.
    [Theory]
    [InlineData("AvatarWalkSpeedFactor", 45.0)]
    [InlineData("AvatarWalkSpeedFactor", 0.0)]
    [InlineData("AvatarRunSpeedFactor", 45.0)]
    [InlineData("AvatarRunSpeedFactor", 0.0)]
    [InlineData("AvatarFlySpeedFactor", 45.0)]
    [InlineData("AvatarFlySpeedFactor", 0.0)]
    public void Each_setting_changes_its_own_speed(string key, double physicsHz)
    {
        const float factor = 0.5f;
        (string key, string value) setting = (key, "0.5");
        float walk = RunScenario("avatar-walk", physicsHz, 1f, setting).SteadyHorizontalSpeed;
        float run = RunScenario("avatar-run", physicsHz, 1f, setting).SteadyHorizontalSpeed;
        float fly = RunScenario("avatar-fly", physicsHz, 1f, setting).SteadyHorizontalSpeed;
        Within(key == "AvatarWalkSpeedFactor" ? RequestWalk * factor : Walk, 2f, walk);
        Within(key == "AvatarRunSpeedFactor" ? RequestWalk * factor : Run, 2f, run);
        Within(key == "AvatarFlySpeedFactor" ? RequestFly * factor : Fly, 2f, fly);
    }

    // ScenePresence's SpeedModifier (osSetSpeed) scales the request; the factor applies on top of it.
    [Theory]
    [InlineData("avatar-walk", Walk)]
    [InlineData("avatar-run", Run)]
    [InlineData("avatar-fly", Fly)]
    public void Cores_speed_modifier_still_scales_the_speed(string scenario, float expected)
    {
        Within(expected * 2f, 2f, RunScenario(scenario, 45.0, 2f).SteadyHorizontalSpeed);
        Within(expected * 0.5f, 2f, RunScenario(scenario, 45.0, 0.5f).SteadyHorizontalSpeed);
    }

    // An invalid value warns (JoltConfigTests holds the warning) and the region walks, runs and flies at the default.
    [Theory]
    [InlineData("AvatarWalkSpeedFactor", "avatar-walk", Walk)]
    [InlineData("AvatarRunSpeedFactor", "avatar-run", Run)]
    [InlineData("AvatarFlySpeedFactor", "avatar-fly", Fly)]
    public void An_invalid_setting_gives_the_default_speed(string key, string scenario, float expected)
    {
        foreach (string bad in new[] { "fast", "0", "-1", "NaN", "11" })
            Within(expected, 2f, RunScenario(scenario, 45.0, 1f, (key, bad)).SteadyHorizontalSpeed);
    }

    // Always run switched on mid-walk, with no new request: the avatar speeds up to a run from that heartbeat.
    [Fact]
    public void Switching_always_run_on_while_walking_speeds_the_avatar_up()
    {
        var sc = new Scenario
        {
            Name = "walk-then-run",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                r.AddAvatar(150f, 60f);
                r.Actor.TargetVelocity = new Vector3(RequestWalk, 0f, 0f);
            },
            Input = r => { if (r.Now >= 3.0) r.Actor.SetAlwaysRun = true; },
        };
        RunResult res = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 });
        Within(Walk, 2f, MeanSpeedX(res.Samples, 1.0, 2.9));
        Within(Run, 2f, MeanSpeedX(res.Samples, 4.0, 6.0));
    }

    // A momentum handed over at a crossing or teleport (SetMomentum) is a velocity the avatar already had: it is kept
    // as it is, not scaled again as a walk request would be.
    [Fact]
    public void A_handed_over_momentum_is_not_scaled()
    {
        var sc = new Scenario
        {
            Name = "momentum",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                r.AddAvatar(150f, 60f);
                r.Actor.SetMomentum(new Vector3(Walk, 0f, 0f));
            },
        };
        RunResult res = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 });
        Within(Walk, 2f, MeanSpeedX(res.Samples, 1.0, 3.0));
    }

    private static float MeanSpeedX(List<Sample> samples, double from, double to)
    {
        Sample a = samples.First(s => s.T >= from - 1e-9);
        Sample b = samples.Last(s => s.T <= to + 1e-9);
        return (float)((b.Position.X - a.Position.X) / (b.T - a.T));
    }
}
