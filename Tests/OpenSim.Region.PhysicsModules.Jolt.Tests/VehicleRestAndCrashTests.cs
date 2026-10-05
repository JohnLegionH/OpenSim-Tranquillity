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
/// Parked vehicles sleep, and vehicles that crash stay physical: the harness's park-* and crash-* scenarios at 11 and
/// 45 Hz. Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleRestAndCrashTests
{
    private static RunResult Run(string scenario, double rate, string presets = null)
    {
        var o = new HarnessOptions { RateHz = rate };
        if (presets != null)
            o.Jolt["VehiclePresets"] = presets;
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    // A parked vehicle goes to sleep within this many seconds of its last motor input (park-new: of the start) and
    // stays asleep to the end of the run; the engine then has no body awake.
    [Theory]
    [InlineData("park-new", 11.0, 6.0, null)]
    [InlineData("park-new", 45.0, 6.0, null)]
    [InlineData("park-faded", 11.0, 14.0, null)]
    [InlineData("park-faded", 45.0, 14.0, null)]
    [InlineData("park-drive", 11.0, 14.0, null)]
    // At 45 Hz angular deflection pitches the slowing legacy test car 0.2 degrees toward the jitter of its contact
    // velocity, and it drifts at about 0.02 m/s (the ground's push along the tilt against no contact friction) before
    // settling.
    [InlineData("park-drive", 45.0, 20.0, "legacy")]
    // The documented test car: as it slows at 45 Hz it comes to rest rolled 0.3 degrees, which nothing levels (no
    // angular friction, a 10 s attractor), and slides sideways at about 0.035 m/s, held only by its velocity friction,
    // until it settles about 26 s after the key.
    [InlineData("park-drive", 45.0, 30.0, "documented")]
    public void A_parked_vehicle_goes_to_sleep_and_stays_asleep(string scenario, double rate, double within, string presets)
    {
        RunResult r = Run(scenario, rate, presets);
        Summary m = r.Summary;
        Assert.Equal(0, m.NonFinite);
        Assert.False(double.IsNaN(m.SleepAfter), $"{r.Name}: still awake at the end");
        Assert.InRange(m.SleepAfter, 0.0, within);
        Assert.Equal(0, m.ActiveAtEnd);
        // Asleep for at least the last 5 s of the run.
        Assert.All(r.Samples.Where(s => s.T >= r.Samples[^1].T - 5.0), s => Assert.Equal(0, s.Active));
    }

    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void A_sleeping_vehicle_wakes_when_its_motor_is_set(double rate)
    {
        RunResult r = Run("park-wake", rate);
        // Asleep for the two seconds before the key.
        Assert.All(r.Samples.Where(s => s.T >= Harness.Harness.WakeKeyAt - 2.0 && s.T < Harness.Harness.WakeKeyAt), s => Assert.Equal(0, s.Active));
        // The key drives it: the car preset covers about 12 m in the 2 s of the key.
        Assert.InRange(r.Summary.DistanceBeforeRelease, 9f, 15f);
    }

    // A crash: every speed stays finite, nothing passes through what it hit or sinks deep into it, and no object
    // leaves the impact faster than the fastest one arrived, by more than LeavingMargin.
    private const float LeavingMargin = 0.05f;
    private const float MaxPenetration = 0.5f;

    [Theory]
    [InlineData("crash-wall", 11.0)]
    [InlineData("crash-wall", 45.0)]
    [InlineData("crash-box", 11.0)]
    [InlineData("crash-box", 45.0)]
    [InlineData("crash-headon", 11.0)]
    [InlineData("crash-headon", 45.0)]
    [InlineData("crash-drop", 11.0)]
    [InlineData("crash-drop", 45.0)]
    public void A_crash_stays_finite_and_adds_no_speed(string scenario, double rate)
    {
        RunResult r = Run(scenario, rate);
        Summary m = r.Summary;
        Assert.Equal(0, m.NonFinite);
        Assert.False(double.IsNaN(m.ImpactT), $"{r.Name}: no impact");
        Assert.Equal(0, m.Tunneled);
        Assert.InRange(m.Penetration, 0f, MaxPenetration);
        Assert.InRange(m.LeavingSpeed, 0f, m.ArrivalSpeed * (1f + LeavingMargin));
    }

    // The two rates tell the same story: the impact at the same time (within one and a half 11 Hz samples, the
    // impact being seen in the first sample after it), the same speed leaving it (within 2 m/s or a
    // tenth of the arrival), and the objects in the same place 3 s later (within 2 m, or a tenth of the way the
    // car covered when it pushes a box ahead of it).
    [Theory]
    [InlineData("crash-wall")]
    [InlineData("crash-box")]
    [InlineData("crash-headon")]
    [InlineData("crash-drop")]
    public void A_crash_comes_out_alike_at_11_and_45_hz(string scenario)
    {
        RunResult a = Run(scenario, 11.0), b = Run(scenario, 45.0);
        Assert.InRange(Math.Abs(a.Summary.ImpactT - b.Summary.ImpactT), 0.0, 1.5 / 11.0);
        float leaving = Math.Abs(a.Summary.LeavingSpeed - b.Summary.LeavingSpeed);
        Assert.True(leaving <= Math.Max(2f, 0.1f * a.Summary.ArrivalSpeed), $"leaving {a.Summary.LeavingSpeed:0.000} vs {b.Summary.LeavingSpeed:0.000}");
        Sample sa = At(a, a.Summary.ImpactT + 3.0), sb = At(b, b.Summary.ImpactT + 3.0);
        float covered = Math.Abs(sa.Position.X - a.Samples[0].Position.X);
        float apart = (sa.Position - sb.Position).Length();
        Assert.True(apart <= Math.Max(2f, 0.1f * covered), $"3 s after the impact: {sa.Position} vs {sb.Position}");
    }

    private static Sample At(RunResult r, double t) => r.Samples.First(s => s.T >= t - 1e-9);

    // Held against a wall with the key down for ten seconds: the car stays put (its centre within 2 cm along the
    // push), does not shake more as time goes on, and does not sink into the wall (5 cm at most, the engine's
    // contact allowance being 2 cm).
    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void A_car_pushing_on_a_wall_stays_put(double rate)
    {
        RunResult r = Run("crash-wall", rate);
        Summary m = r.Summary;
        Assert.True(r.Samples[^1].T >= m.ImpactT + 10.0 - 1e-6, "the run covers ten seconds of pushing");
        Assert.InRange(m.PushRangeEarly, 0f, 0.02f);
        Assert.InRange(m.PushRangeLate, 0f, m.PushRangeEarly + 0.005f);
        Assert.InRange(m.PushPenetration, -0.05f, 0.05f);
    }
}
