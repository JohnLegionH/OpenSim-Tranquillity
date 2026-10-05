/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The harness scenarios with [Jolt] PhysicsStepRate = 45 under the default 11 Hz heartbeat, against the same
/// scenarios with one step per heartbeat at a 45 Hz heartbeat (the vehicle controller, the avatar and the solver
/// all stepping at 45 Hz the other way). Serial with the other native tests: every run steps a real backend.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SubstepHarnessTests
{
    private static Summary Run(string scenario, float? slope, double heartbeatHz, double physicsHz, params (string key, string value)[] jolt)
    {
        var o = new HarnessOptions { RateHz = heartbeatHz, SlopeDeg = slope, PhysicsRateHz = physicsHz };
        foreach (var (k, v) in jolt)
            o.Jolt[k] = v;
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o).Summary;
    }

    private static float Figure(Summary m, string figure) => figure switch
    {
        "release" => m.ReleaseSpeed,
        "before" => m.DistanceBeforeRelease,
        "steady" => m.SteadySpeed,
        "top" => m.TopSpeed,
        "rise" => m.PeakRise,
        "peak" => m.PeakHeight,
        "endz" => m.End.Z,
        _ => throw new ArgumentException(figure),
    };

    // Expected: the 45 Hz-heartbeat value (one step per heartbeat). The tolerance covers what differs on purpose:
    // inputs and samples arrive per 11 Hz heartbeat instead of per 45 Hz one, and the solver takes 2 collision steps
    // per physics step instead of 6. Figures this does not hold for are explained beside the module's notes; the
    // ones on the 33 degree crest depend on how the car leaves the crest and are not compared.
    [Theory]
    [InlineData("testcar", 0f, "release", 5.949f, 3f)]
    [InlineData("testcar", 0f, "before", 7.707f, 5f)]
    [InlineData("testcar", 5f, "steady", 0.875f, 2f)]
    [InlineData("testcar", 15f, "steady", 2.756f, 2f)]
    [InlineData("testcar", 15f, "release", 8.465f, 2f)]
    [InlineData("testcar", 33f, "steady", 10.186f, 3f)]
    [InlineData("car", 0f, "release", 7.893f, 2f)]
    [InlineData("car", 15f, "release", 9.316f, 3f)]
    [InlineData("sled", 15f, "steady", 23.670f, 6f)]
    [InlineData("boat", 0f, "steady", 4.718f, 1f)]
    [InlineData("airplane", 0f, "steady", 15.044f, 1f)]
    [InlineData("balloon", 0f, "peak", 6.704f, 1f)]
    [InlineData("avatar-walk", 33f, "steady", 3.436f, 1f)]
    [InlineData("avatar-jump", 0f, "rise", 0.772f, 2f)]
    [InlineData("drop", 0f, "endz", 25.500f, 0.1f)]
    public void Steps_at_45_hz_inside_11_hz_heartbeats_match_a_45_hz_heartbeat(string scenario, float slope, string figure, float expected, float percent)
    {
        float got = Figure(Run(scenario, slope, 11.0, 45.0), figure);
        float tol = Math.Abs(expected) * percent / 100f;
        Assert.InRange(got, expected - tol, expected + tol);
    }

    // With one physics step per heartbeat and the solver's 6 collision steps, the substep path is the single-step
    // path: same steps, same order. Only the vehicle controller's clock differs (physics time, in whole ticks), so
    // a scenario with no vehicle is identical to the last digit and a vehicle within float noise.
    [Theory]
    [InlineData("drop", 0f)]
    [InlineData("avatar-jump", 0f)]
    [InlineData("avatar-walk", 15f)]
    [InlineData("boat", 0f)]
    public void One_step_per_heartbeat_through_the_substep_path_is_the_single_step_path(string scenario, float slope)
    {
        var single = new HarnessOptions { RateHz = 45.0, SlopeDeg = slope, PhysicsRateHz = 0 };
        var stepped = new HarnessOptions { RateHz = 45.0, SlopeDeg = slope, PhysicsRateHz = 45.0 };
        stepped.Jolt["PhysicsStepCollisionSteps"] = "6";
        Scenario sc = Harness.Harness.Find(scenario);
        RunResult a = Harness.Harness.Run(sc, single), b = Harness.Harness.Run(sc, stepped);
        Assert.Equal(a.ToCsv(), b.ToCsv());
    }

    [Fact]
    public void A_rate_below_the_heartbeat_runs_and_reports_as_one_step_per_heartbeat()
    {
        var o = new HarnessOptions { RateHz = 45.0, PhysicsRateHz = 11.0 };
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("drop"), o);
        Assert.Equal(0, r.PhysicsRateHz);
        Assert.Equal("drop", r.Label);
        RunResult single = Harness.Harness.Run(Harness.Harness.Find("drop"), new HarnessOptions { RateHz = 45.0, PhysicsRateHz = 0 });
        Assert.Equal(single.ToCsv(), r.ToCsv());
    }

    [Fact]
    public void The_summary_names_the_physics_rate()
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("drop"), new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 });
        Assert.StartsWith("drop/p45,0,11,", r.SummaryLine());
        Assert.Equal("drop-s0-r11-p45", r.Name);
    }
}
