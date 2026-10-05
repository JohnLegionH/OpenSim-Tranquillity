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
/// The physics harness (Tests/JoltPhysicsHarness) as regression tests: its output is repeatable, every scenario
/// runs at every heartbeat rate, and the 11 Hz figures (one Simulate per heartbeat, today's only path) hold.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class HarnessTests
{
    private static RunResult Run(string scenario, double rate = 11.0, float? slope = null, float? duration = null, float? keyRepeat = null)
    {
        var o = new HarnessOptions { RateHz = rate, SlopeDeg = slope, Duration = duration };
        if (keyRepeat.HasValue) o.KeyRepeat = keyRepeat.Value;
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    [Theory]
    [InlineData("testcar", 15f)]
    [InlineData("avatar-walk", 5f)]
    [InlineData("drop", 0f)]
    public void Two_runs_give_identical_output(string scenario, float slope)
    {
        RunResult a = Run(scenario, 45.0, slope);
        RunResult b = Run(scenario, 45.0, slope);
        Assert.Equal(a.ToCsv(), b.ToCsv());
        Assert.Equal(a.SummaryLine(), b.SummaryLine());
        Assert.True(a.Samples.Count > 10);
    }

    public static IEnumerable<object[]> EveryScenarioAndRate()
    {
        foreach (Scenario s in Harness.Harness.Scenarios)
            foreach (double r in Harness.Harness.Rates)
                yield return new object[] { s.Name, r };
    }

    // Each scenario on its first slope, 8 simulated seconds, at every rate the later substep work compares.
    [Theory]
    [MemberData(nameof(EveryScenarioAndRate))]
    public void Every_scenario_runs_at_every_rate(string scenario, double rate)
    {
        RunResult r = Run(scenario, rate, duration: 8f);
        Assert.Equal(0, r.Summary.NonFinite);
        Assert.True(r.Samples.Count >= (int)(rate * 0.9), $"{r.Name}: {r.Samples.Count} samples");
        double dt = 1.0 / rate;
        for (int i = 0; i < r.Samples.Count; i++)
            Assert.Equal((i + 1) * dt, r.Samples[i].T, 9);
    }

    // The tolerances below cover float noise and nothing more: today the output is bit-for-bit repeatable.
    // "Expected to change" marks a value that should move toward the 45 / 90 Hz rows of the baseline once the
    // vehicle controller or the character runs at a physics rate above the heartbeat (substeps).

    [Fact]
    public void Test_car_on_level_ground_at_11_hz()
    {
        Summary m = Run("testcar", slope: 0f).Summary;
        Assert.InRange(m.ReleaseSpeed, 5.084f, 5.184f);           // 5.134; expected to change (45 Hz: 5.949)
        Assert.InRange(m.DistanceBeforeRelease, 5.86f, 5.96f);    // 5.909; expected to change (45 Hz: 7.707)
        Assert.InRange(m.TimeToRest, 3.6, 3.85);                   // 3.727 s after release
        Assert.InRange(m.MaxTilt, 0f, 1f);
    }

    // The region feed on a test course's pad drive (3 s key, the course's test car). Measured in a region before the
    // motor ramp was made independent of the step: 5.59 m / 5.00 m/s under the key at one step per heartbeat and
    // 8.43 m / 6.14 m/s at PhysicsStepRate 45, which this feed then gave to within 1% (5.62 / 5.01 and 8.40 / 6.12;
    // the heartbeat feed gave 5.91 and 7.45 m). The figures below are this code's; a region proof should land on them.
    [Theory]
    [InlineData(0.0, 8.715f, 6.265f)]
    [InlineData(45.0, 9.223f, 6.427f)]
    public void Region_feed_drives_the_pad_as_a_region_does(double physicsRate, float distance, float releaseSpeed)
    {
        var o = new HarnessOptions { RateHz = 11.0, SlopeDeg = 0f, PhysicsRateHz = physicsRate, Feed = InputFeed.Region };
        Summary m = Harness.Harness.Run(Harness.Harness.Find("testcar"), o).Summary;
        Assert.InRange(m.DistanceBeforeRelease, distance * 0.99f, distance * 1.01f);
        Assert.InRange(m.ReleaseSpeed, releaseSpeed * 0.99f, releaseSpeed * 1.01f);
    }

    [Theory]
    [InlineData(5f, 0.904f)]     // in-world 0.90
    [InlineData(15f, 2.852f)]    // in-world 2.85
    [InlineData(33f, 7.842f)]    // in-world 7.94; expected to change a little (45 Hz: 10.19, see the baseline)
    public void Test_car_rolls_down_a_slope_at_a_steady_speed_at_11_hz(float slope, float steady)
    {
        Summary m = Run("testcar", slope: slope).Summary;
        Assert.InRange(m.SteadySpeed, steady * 0.98f, steady * 1.02f);
        Assert.True(m.End.Y < Course.RampBottomY, $"stopped on the ramp at y {m.End.Y}");
        Assert.False(double.IsNaN(m.TimeToRest), "came to rest on the run-out");
    }

    [Fact]
    public void Preset_car_on_level_ground_at_11_hz()
    {
        Summary m = Run("car", slope: 0f).Summary;
        Assert.InRange(m.SteadySpeed, 7.405f, 7.505f);   // 7.455 over the last second of the hold
        Assert.InRange(m.TimeToRest, 6.0, 6.35);           // 6.182
    }

    [Theory]
    [InlineData(0f, 4.096f)]     // in-world 4.1
    [InlineData(5f, 4.079f)]     // in-world 4.10
    [InlineData(15f, 3.949f)]    // in-world 3.95
    [InlineData(33f, 3.431f)]    // in-world 3.45
    public void Avatar_walks_at_11_hz(float slope, float along)
    {
        Summary m = Run("avatar-walk", slope: slope).Summary;
        Assert.InRange(m.SteadySpeed, along - 0.03f, along + 0.03f);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(15f)]
    public void Avatar_stands_still_on_a_slope_at_11_hz(float slope)
    {
        RunResult r = Run("avatar-stand", slope: slope);
        Sample first = r.Samples.First(s => s.T >= 1.0);
        Sample last = r.Samples[^1];
        Assert.InRange((last.Position - first.Position).Length(), 0f, 0.01f);
        Assert.InRange(r.Summary.ZRange, 0f, 0.01f);
    }

    [Fact]
    public void Avatar_jump_at_11_hz()
    {
        Summary m = Run("avatar-jump").Summary;
        Assert.InRange(m.PeakRise, 0.62f, 0.67f);         // 0.644 m; expected to change (90 Hz: 0.794)
        Assert.InRange(m.TimeToRest, 0.7, 0.95);           // back on the ground 0.818 s after the jump
    }

    [Fact]
    public void Dropped_box_settles_at_11_hz()
    {
        Summary m = Run("drop").Summary;
        Assert.InRange(m.TimeToRest, 1.0, 1.2);             // 1.091 s after the release
        Assert.InRange(m.End.Z, 25.45f, 25.52f);            // 25.480; rests 2 cm low at 11 Hz, 25.500 at 22.5 Hz and up
    }

    [Fact]
    public void Boat_balloon_and_airplane_at_11_hz()
    {
        Summary boat = Run("boat").Summary;
        Assert.InRange(boat.SteadySpeed, 4.586f, 4.686f);  // 4.636 under the motor
        Assert.InRange(boat.End.Z, 20.45f, 20.55f);         // hovers 0.5 m above the water

        Summary balloon = Run("balloon").Summary;
        Assert.InRange(balloon.End.Z, 30.24f, 30.44f);      // 30.339: hover 5 m over the ground; 29.999 at 90 Hz

        // The airplane preset has no lift: with the motor held it flies level at 15 m/s and sinks to the ground.
        Summary plane = Run("airplane").Summary;
        Assert.InRange(plane.SteadySpeed, 14.9f, 15.1f);
        Assert.InRange(plane.End.Z, 25f, 26f);
    }

    [Fact]
    public void Sled_runs_at_11_hz()
    {
        // The sled preset has almost no forward friction (timescale 1000): let go on a 15 degree slope it slides
        // down, leaves the ramp at about 20 m/s and runs off the region's south edge, which ends the run.
        RunResult r = Run("sled");
        Assert.Equal(0, r.Summary.NonFinite);
        Assert.InRange(r.Summary.LeftRegionT, 7.0, 7.7);                        // 7.364 s; 4.578 s at 90 Hz
        Sample bottom = r.Samples.First(s => s.Position.Y <= Course.RampBottomY);
        Assert.InRange(bottom.Speed, 19.5f, 20.2f);                             // 19.85 m/s; expected to change (rate-dependent, see the baseline)
    }

    [Fact]
    public void Vehicle_param_overrides_reach_the_controller()
    {
        // The same car with a shorter linear friction timescale stops sooner: an override changes the run.
        var o = new HarnessOptions { RateHz = 11.0, SlopeDeg = 0f };
        Summary plain = Harness.Harness.Run(Harness.Harness.Find("testcar"), o).Summary;
        o.VehicleParams.Add(VehicleParamSetting.Parse("LINEAR_FRICTION_TIMESCALE=0.3,1,1000"));
        Summary damped = Harness.Harness.Run(Harness.Harness.Find("testcar"), o).Summary;
        Assert.True(damped.DistanceAfterRelease < plain.DistanceAfterRelease * 0.6f,
            $"after release: {damped.DistanceAfterRelease} vs {plain.DistanceAfterRelease}");
    }

    [Fact]
    public void Command_line_writes_only_to_its_out_folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jolt-harness-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = new StringWriter();
            int rc = Program.Run(new[] { "--scenario", "drop", "--rate", "11,45", "--out", dir }, output);
            Assert.Equal(0, rc);
            string[] files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "drop-s0-r11.csv", "drop-s0-r45.csv", "summary.csv" }, files);
            Assert.StartsWith(RunResult.SummaryHeader, output.ToString());
            Assert.StartsWith(RunResult.CsvHeader, File.ReadAllText(Path.Combine(dir, "drop-s0-r11.csv")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
