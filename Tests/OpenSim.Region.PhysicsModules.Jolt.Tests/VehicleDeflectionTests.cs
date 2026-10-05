/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Linear deflection with and without VEHICLE_FLAG_NO_DEFLECTION_UP, on the controller alone: a body that keeps
/// the velocities the controller sets and moves nowhere, so deflection is the only thing changing the linear
/// velocity (friction off, motors released, hover off). Pure, parallel.
/// </summary>
public class VehicleDeflectionTests
{
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    /// <summary>A vehicle body that only holds what the controller sets: no integration, no contacts.</summary>
    private sealed class HeldBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => new(0f, 0f, -9.80665f);
        public bool HasCollision { get; set; }
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    // A car (linear deflection efficiency 1, timescale 2 s), friction off, motors released.
    private static (VehicleController car, HeldBody body, Func<double, DateTime> tick) Car(Vector3 velocity, bool noDeflectionUp)
    {
        var body = new HeldBody { LinearVelocity = velocity };
        var car = new VehicleController(body);
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double now = 0;
        car.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        if (!noDeflectionUp)
            car.ProcessVehicleFlags((int)ExtendedVehicleFlags.NoDeflectionUp, remove: true);
        now = 2.0;   // the first step finds the motors stale and releases them
        return (car, body, dt => { now += dt; return car.Clock(); });
    }

    private static void Run(VehicleController car, Func<double, DateTime> tick, double rate, double seconds, Action afterStep = null)
    {
        int n = (int)Math.Round(seconds * rate);
        for (int i = 0; i < n; i++)
        {
            car.Step((float)(1.0 / rate));
            tick(1.0 / rate);
            afterStep?.Invoke();
        }
    }

    [Fact]
    public void In_the_air_deflection_never_adds_speed_at_any_rate()
    {
        // Falling nose-level, moving partly sideways: deflection turns the horizontal part toward the nose and
        // leaves the fall alone. (It used to drop the upward part of a whole-velocity turn, which kept "more forward".)
        Vector3 start = new(4f, 3f, -5f);
        foreach (double rate in Rates)
        {
            var (car, body, tick) = Car(start, noDeflectionUp: true);
            float startSpeed = start.Length();
            Run(car, tick, rate, 2.0, () =>
            {
                Assert.True(body.LinearVelocity.Length() <= startSpeed + 1e-4f, $"{rate} Hz: speed {body.LinearVelocity.Length()} above {startSpeed}");
                Assert.Equal(start.Z, body.LinearVelocity.Z, 5);
            });
            Vector3 v = body.LinearVelocity;
            Assert.Equal(5f, MathF.Sqrt(v.X * v.X + v.Y * v.Y), 3);   // horizontal speed kept
            Assert.True(v.Y < 2.0f, $"{rate} Hz: still heading sideways ({v})");     // turned toward the nose (+x)
        }
    }

    [Fact]
    public void On_the_ground_with_the_flag_it_turns_the_velocity_toward_the_nose_as_before()
    {
        // Level ground, velocity horizontal: the same turn as the old formula (whole velocity toward the nose,
        // speed kept, upward part of the change dropped, which is none here).
        foreach (double rate in Rates)
        {
            var (car, body, tick) = Car(new Vector3(4f, 3f, 0f), noDeflectionUp: true);
            Vector3 expected = new(4f, 3f, 0f);
            float dt = (float)(1.0 / rate);
            Run(car, tick, rate, 1.0, () =>
            {
                expected = OldDeflection(expected, dt, clipUp: true);
                Assert.True(Vector3.Distance(expected, body.LinearVelocity) < 1e-4f, $"{rate} Hz: {body.LinearVelocity} against {expected}");
            });
            Assert.True(body.LinearVelocity.Y < 3f);
        }
    }

    [Fact]
    public void Without_the_flag_nothing_changes()
    {
        foreach (double rate in Rates)
        {
            var (car, body, tick) = Car(new Vector3(4f, 3f, -5f), noDeflectionUp: false);
            Vector3 expected = new(4f, 3f, -5f);
            float dt = (float)(1.0 / rate);
            Run(car, tick, rate, 1.0, () =>
            {
                expected = OldDeflection(expected, dt, clipUp: false);
                Assert.True(Vector3.Distance(expected, body.LinearVelocity) < 1e-4f, $"{rate} Hz: {body.LinearVelocity} against {expected}");
            });
        }
    }

    // The deflection as it was: the whole velocity turned toward the nose (+x) by min(dt / 2 s, 1), speed kept; with
    // the flag, the upward part of the change dropped.
    private static Vector3 OldDeflection(Vector3 v, float dt, bool clipUp)
    {
        float blend = Math.Min(dt / 2f, 1f);
        Vector3 dir = Vector3.Normalize(Vector3.Normalize(v) * (1f - blend) + Vector3.UnitX * blend);
        Vector3 change = dir * v.Length() - v;
        if (clipUp && change.Z > 0) change.Z = 0;
        return v + change;
    }
}
