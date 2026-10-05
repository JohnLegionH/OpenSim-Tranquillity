/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The vehicle motor ramp does not depend on the step: the ramp math on its own (pure, parallel), and a car and a
/// boat driven through the harness at the four heartbeat rates against a far finer step.
/// </summary>
public class VehicleMotorRampTests
{
    private static readonly double[] Steps = { 1 / 11.0, 1 / 22.5, 1 / 45.0, 1 / 90.0 };
    private const double FineStep = 1 / 20000.0;
    private const double RunSeconds = 2.0;   // a whole number of steps at every rate above

    private static float Ramp(float v, float target, float timescale, double dt, double seconds)
    {
        int n = (int)Math.Round(seconds / dt);
        for (int i = 0; i < n; i++)
            v = VehicleController.MotorRampStep(v, target, timescale, (float)dt, VehicleController.MotorRampRate(v, target, timescale, (float)dt));
        return v;
    }

    // The ramp as it was stepped before: v + v * ln(target / v) * dt / timescale (GetGrowthRate with timescale / dt).
    private static float OldRamp(float v, float target, float timescale, double dt, double seconds)
    {
        int n = (int)Math.Round(seconds / dt);
        for (int i = 0; i < n; i++)
            v += v * VehicleController.GetGrowthRate(v, target, (float)(timescale / dt));
        return v;
    }

    [Theory]
    [InlineData(0.04f, 8f, 1f)]     // from the stiction seed up to the motor's speed
    [InlineData(0.04f, 8f, 0.5f)]
    [InlineData(6f, 0f, 1f)]        // a motor set to zero brakes
    [InlineData(6f, 0f, 0.5f)]
    [InlineData(5f, -8f, 0.5f)]     // reversed: brakes, flips at the crossover, ramps the other way
    public void The_ramp_reaches_the_same_speed_at_any_step(float start, float target, float timescale)
    {
        float fine = Ramp(start, target, timescale, FineStep, RunSeconds);
        foreach (double dt in Steps)
        {
            float v = Ramp(start, target, timescale, dt, RunSeconds);
            Assert.True(Math.Abs(v - fine) <= 0.01f * Math.Max(Math.Abs(fine), 0.05f), $"dt {dt:0.0000}: {v} against {fine}");
        }
    }

    [Theory]
    [InlineData(0.04f, 8f, 1f)]
    [InlineData(6f, 0f, 1f)]
    [InlineData(5f, -8f, 0.5f)]
    public void As_the_step_goes_to_zero_the_ramp_is_the_old_formula(float start, float target, float timescale)
    {
        float now = Ramp(start, target, timescale, FineStep, RunSeconds);
        float old = OldRamp(start, target, timescale, FineStep, RunSeconds);
        Assert.True(Math.Abs(now - old) <= 0.005f * Math.Max(Math.Abs(old), 0.05f), $"{now} against the old formula's {old}");
    }

    [Fact]
    public void The_ramp_from_the_seed_takes_a_whole_step()
    {
        // The step that starts from the stiction seed ramps from it, as every later step does: two half steps from
        // the seed give what one whole step gives.
        float whole = VehicleController.RampFromSeed(0.04f, 8f, 1f, 0.1f);
        float half = VehicleController.RampFromSeed(0.04f, 8f, 1f, 0.05f);
        float twoHalves = VehicleController.MotorRampStep(half, 8f, 1f, 0.05f, VehicleController.MotorRampRate(half, 8f, 1f, 0.05f));
        Assert.Equal(whole, twoHalves, 4);
        Assert.True(whole > 0.04f);
    }

    [Fact]
    public void The_friction_floor_is_a_rate()
    {
        // MinPhysicsForce per MinPhysicsTimestep: the same least deceleration whatever the step.
        float perSecond11 = VehicleController.FrictionFloor(1 / 11f) * 11f;
        float perSecond90 = VehicleController.FrictionFloor(1 / 90f) * 90f;
        Assert.Equal(perSecond11, perSecond90, 4);
        Assert.Equal(VehicleLimits.MinPhysicsForce, VehicleController.FrictionFloor(VehicleLimits.MinPhysicsTimestep), 6);
    }
}

/// <summary>Driven vehicles through the harness at the four heartbeat rates (serial: real backends on the shared pool).</summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleMotorDriveTests
{
    private const double ReferenceRate = 720.0;

    private static List<Sample> Drive(string scenario, double rate)
    {
        // A 4 s hold lands on a heartbeat at every rate (a whole number of steps at 11, 22.5, 45 and 90 Hz).
        var o = new HarnessOptions { RateHz = rate, SlopeDeg = 0f, Hold = 4f, Duration = 6f };
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o).Samples;
    }

    private static float SpeedAt(List<Sample> samples, double t)
    {
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].T >= t - 1e-9)
            {
                Sample a = samples[i - 1], b = samples[i];
                return (float)(a.Speed + (b.Speed - a.Speed) * (t - a.T) / (b.T - a.T));
            }
        return samples[^1].Speed;
    }

    // The car preset and the boat preset on level ground and water: the motor ramp under the key (1-4 s) and its
    // braking after the key is let go (5 s), each rate within 1% of a 720 Hz heartbeat.
    [Theory]
    [InlineData("car")]
    [InlineData("boat")]
    public void The_same_drive_gives_the_same_speeds_at_every_rate(string scenario)
    {
        List<Sample> reference = Drive(scenario, ReferenceRate);
        foreach (double rate in Harness.Harness.Rates)
        {
            List<Sample> run = Drive(scenario, rate);
            foreach (double t in new[] { 1.0, 2.0, 3.0, 4.0, 5.0 })
            {
                float v = SpeedAt(run, t), vr = SpeedAt(reference, t);
                Assert.True(Math.Abs(v - vr) <= 0.01f * Math.Max(vr, 0.05f), $"{scenario} at {rate} Hz, t = {t}: {v:0.000} against {vr:0.000}");
            }
        }
    }
}
