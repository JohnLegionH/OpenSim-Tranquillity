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
/// Angular deflection and banking on the controller alone, at the four rates against their exact solutions: a body
/// that keeps the velocities the controller sets, and turns (or not) by its angular velocity. Pure, parallel.
/// </summary>
public class VehicleBankingTests
{
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    private sealed class Body : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => new(0f, 0f, -9.80665f);
        public void SetGravityFactor(float factor) { }
        public bool HasCollision => false;
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    // A car with everything that would act here off but the behaviour under test; motors released.
    private static (VehicleController car, Body body, Action<double> step) Car(Vector3 velocity, Quaternion orientation, bool turns)
    {
        var body = new Body { LinearVelocity = velocity, Orientation = orientation };
        var car = new VehicleController(body);
        double now = 0, yaw = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        car.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
        car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, 0f);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0f);
        now = 2.0;   // the first step finds the motors stale and releases them
        void Step(double h)
        {
            car.Step((float)h);
            if (turns)
            {
                // The engine's step: the body turns by its angular velocity over the step (a yaw here, kept in double).
                yaw += body.AngularVelocity.Z * h;
                body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)yaw);
            }
            now += h;
        }
        return (car, body, Step);
    }

    // Angular deflection: the nose turns toward a fixed velocity, the angle between them decaying as
    // e^(-eff (speed / 30 m/s) t / T).
    // (The body turns by yaw alone here; the angle is measured in floats, so to 2e-4.)
    [Theory]
    [InlineData(1f, 2f, 5f)]
    [InlineData(0.5f, 1f, 5f)]
    [InlineData(1f, 0.5f, 25f)]
    public void Angular_deflection_turns_the_nose_to_the_velocity_exactly(float efficiency, float timescale, float speed)
    {
        Vector3 velocity = new Vector3(4f, 3f, 0f) * (speed / 5f);
        foreach (double rate in Rates)
        {
            var (car, body, step) = Car(velocity, Quaternion.Identity, turns: true);
            car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
            car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, efficiency);
            car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, timescale);
            double a0 = Math.Atan2(3, 4);
            for (int i = 1; i <= (int)Math.Round(3 * rate); i++)
            {
                step(1.0 / rate);
                Vector3 nose = Vector3.UnitX * body.Orientation;
                double angle = Math.Acos(Math.Clamp(Vector3.Dot(nose, Vector3.Normalize(velocity)), -1f, 1f));
                double expected = a0 * Math.Exp(-efficiency * (speed / 30.0) * (i / rate) / timescale);
                Assert.True(Math.Abs(angle - expected) < 2e-4, $"{rate} Hz at {i / rate:0.000} s (step {i}): {angle} against {expected}");
            }
        }
    }

    // Banking: rolled 0.3 rad and held there, the yaw rate approaches the banking target with the banking timescale:
    // w(t) = target (1 - e^(-t / T)), target = -sin(roll) * eff * pi * ((1 - mix) + mix * speed / 30 m/s).
    [Theory]
    [InlineData(1f, 0f, 0f, 1f)]      // static banking at rest
    [InlineData(-0.2f, 0f, 0f, 1f)]   // the car preset's efficiency: leans out, turns the other way
    [InlineData(1f, 1f, 10f, 0.5f)]   // dynamic banking at 10 m/s
    [InlineData(0.5f, 0.5f, 10f, 2f)] // half and half
    public void Banking_turns_the_yaw_rate_toward_its_target_exactly(float efficiency, float mix, float speed, float timescale)
    {
        const float Roll = 0.3f;
        Quaternion rolled = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Roll);
        double target = -Math.Sin(Roll) * efficiency * Math.PI * ((1 - mix) + mix * speed / 30.0);
        foreach (double rate in Rates)
        {
            var (car, body, step) = Car(new Vector3(speed, 0f, 0f) * rolled, rolled, turns: false);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, efficiency);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, mix);
            car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, timescale);
            // The attractor must be on for banking; with no damping its spring acts about the vehicle's own x axis
            // (level here) only, and leaves the yaw to banking.
            car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0f);
            for (int i = 1; i <= (int)Math.Round(3 * rate); i++)
            {
                step(1.0 / rate);
                double expected = target * (1 - Math.Exp(-(i / rate) / timescale));
                Assert.True(Math.Abs(body.AngularVelocity.Z - expected) < 1e-4, $"{rate} Hz at {i / rate:0.000} s: {body.AngularVelocity.Z} against {expected}");
            }
        }
    }

    [Fact]
    public void Without_the_vertical_attractor_there_is_no_banking()
    {
        Quaternion rolled = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        var (car, body, step) = Car(Vector3.Zero, rolled, turns: false);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 1f);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, 1f);
        car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
        for (int i = 0; i < 45; i++)
            step(1.0 / 45);
        Assert.Equal(0f, body.AngularVelocity.Z);
    }
}
