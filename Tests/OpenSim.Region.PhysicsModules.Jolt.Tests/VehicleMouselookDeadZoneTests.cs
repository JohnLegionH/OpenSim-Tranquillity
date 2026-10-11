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
/// The mouselook dead zone, under the InWorldz behaviour only. Halcyon takes the sideways part of the camera's
/// direction in the vehicle's frame as a share of the range its azimuth gives (times pi / 2, over MOUSELOOK_AZIMUTH,
/// pi / 4 by default, held to -1..1) and does not steer or bank while the share is no more than pi / 32
/// (VehicleDynamics.cs:819-836; read, not run): a camera within asin(azimuth / 16) = 2.81 degrees of the heading, with the
/// default azimuth. Measured first: with no dead zone the steering holds steady under a shaking camera and a held one
/// (VehicleMouselookHuntingTests), so the dead zone is a matter of feel and Second Life's behaviour, the default, has none.
/// Outside the zone the turn is the shaped share of the script's motor direction (VehicleInWorldzMouselookRulesTests); the
/// rows here that compare it with a Second Life vehicle compare with one whose motor is set to that share.
/// Pure (a body with no engine), parallel.
/// </summary>
public class VehicleMouselookDeadZoneTests
{
    private const double Deg = Math.PI / 180.0;
    private const int SteerFlag = 0x80, BankFlag = 0x100;
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    private static VehicleSettings Settings(VehicleBehaviour b) => new VehicleSettings { Behaviour = b };

    // A car with only the angular motor acting (timescale 1 s, no decay to speak of), the camera looking `yawDegrees` to the
    // left of its heading and `pitchDegrees` below the horizon; the motor direction is set to <roll, pitch, yaw>.
    private static BehaviourRig Car(VehicleBehaviour behaviour, int flag, double yawDegrees, double pitchDegrees, Vector3 motor)
    {
        var rig = new BehaviourRig(Settings(behaviour), Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVehicleFlags(0x2, remove: true);      // the car's LIMIT_ROLL_ONLY: under InWorldz it keeps the pitch out of the steering
        rig.Car.ProcessVehicleFlags(flag, remove: false);
        double y = yawDegrees * Deg, p = pitchDegrees * Deg;
        var at = new Vector3((float)(Math.Cos(p) * Math.Cos(y)), (float)(Math.Cos(p) * Math.Sin(y)), (float)-Math.Sin(p));
        rig.Car.DriverCamera = () => new DriverView(at, true);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, motor);
        return rig;
    }

    private static readonly Vector3 TurnMotor = new(0f, 0f, 2f);

    // The same to within 0.1%: the two behaviours also differ in how late a motor's decay starts (InWorldz holds it back
    // 0.0156 s), which moves a two second turn by a few parts in a hundred thousand.
    private static void Same(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) <= 1e-3 * Math.Abs(expected), $"{actual:R} against {expected:R}");

    // The share InWorldz turns at for a camera `degrees` off the heading (default range pi / 4, no zone edge handling needed
    // here: the rows are outside it): (e^x - 1) / (e - 1) of x = 2 sin(angle) held to 1.
    private static double Shaped(double degrees)
    {
        double x = Math.Min(Math.Abs(Math.Sin(degrees * Deg)) * 2.0, 1.0);
        return Math.Sign(degrees) * (Math.Exp(x) - 1.0) / (Math.E - 1.0);
    }

    // A Second Life vehicle with no flags whose motor direction is set to `motor`, stepped once.
    private static BehaviourRig Plain(Vector3 motor)
    {
        var rig = new BehaviourRig(Settings(VehicleBehaviour.SecondLife), Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, motor);
        return rig;
    }

    private static double Heading(BehaviourRig r) => Math.Atan2((Vector3.UnitX * r.Body.Orientation).Y, (Vector3.UnitX * r.Body.Orientation).X) / Deg;

    // The presets' azimuth, pi / 4 (a script cannot set it here): the zone is the camera within asin(pi / 64) = 2.8145 degrees
    // of the heading.
    [Theory]
    [InlineData(0.5, false)]
    [InlineData(2.0, false)]
    [InlineData(2.7, false)]
    [InlineData(2.9, true)]
    [InlineData(10.0, true)]
    [InlineData(-2.0, false)]
    [InlineData(-2.9, true)]
    public void Under_InWorldz_a_camera_inside_the_zone_does_not_steer(double yawDegrees, bool turns)
    {
        foreach (double rate in Rates)
        {
            double h = 1.0 / rate;
            var iw = Car(VehicleBehaviour.InWorldz, SteerFlag, yawDegrees, 0, TurnMotor);
            var sl = Car(VehicleBehaviour.SecondLife, SteerFlag, yawDegrees, 0, TurnMotor);
            iw.Run(h, (int)Math.Round(rate * 2));
            sl.Run(h, (int)Math.Round(rate * 2));
            // Second Life's, with no dead zone, turns toward any camera that is off the heading.
            Assert.True(Math.Abs(Heading(sl)) > 0.2 * Math.Abs(yawDegrees), $"{rate} Hz, {yawDegrees} degrees: Second Life's turned {Heading(sl):0.0000}");
            if (turns)
            {
                // Outside the zone: the shaped share of the motor direction (compared over the first step).
                var a = Car(VehicleBehaviour.InWorldz, SteerFlag, yawDegrees, 0, TurnMotor);
                var b = Plain(new Vector3(0f, 0f, (float)(2.0 * Shaped(yawDegrees))));
                a.Step(h);
                b.Step(h);
                Same(b.Body.AngularVelocity.Z, a.Body.AngularVelocity.Z);
            }
            else
                Assert.Equal(0.0, Heading(iw), 9);           // inside it: not at all
        }
    }

    // Banking mode: the camera's yaw becomes a roll, and a camera inside the zone rolls nothing.
    [Theory]
    [InlineData(2.0, false)]
    [InlineData(4.0, true)]
    public void The_bank_flag_has_the_same_zone(double yawDegrees, bool rolls)
    {
        var iw = Car(VehicleBehaviour.InWorldz, BankFlag, yawDegrees, 0, new Vector3(2f, 0f, 0f));
        var sl = Car(VehicleBehaviour.SecondLife, BankFlag, yawDegrees, 0, new Vector3(2f, 0f, 0f));
        iw.Run(1.0 / 45, 90);
        sl.Run(1.0 / 45, 90);
        Assert.NotEqual(0f, sl.Body.AngularVelocity.X);
        if (rolls)
        {
            var a = Car(VehicleBehaviour.InWorldz, BankFlag, yawDegrees, 0, new Vector3(2f, 0f, 0f));
            var b = Plain(new Vector3((float)(-2.0 * Shaped(yawDegrees)), 0f, 0f));
            a.Step(1.0 / 45);
            b.Step(1.0 / 45);
            Same(b.Body.AngularVelocity.X, a.Body.AngularVelocity.X);
        }
        else
            Assert.Equal(0f, iw.Body.AngularVelocity.X);
    }

    // Pitch has the same threshold over its own range, MOUSELOOK_ALTITUDE (pi / 4 by default: 2.81 degrees).
    [Theory]
    [InlineData(2.0, false)]
    [InlineData(4.0, true)]
    public void The_pitch_aim_has_the_zone_over_the_altitude_range(double pitchDegrees, bool pitches)
    {
        var iw = Car(VehicleBehaviour.InWorldz, SteerFlag, 0, pitchDegrees, new Vector3(0f, 2f, 0f));
        var sl = Car(VehicleBehaviour.SecondLife, SteerFlag, 0, pitchDegrees, new Vector3(0f, 2f, 0f));
        iw.Run(1.0 / 45, 90);
        sl.Run(1.0 / 45, 90);
        Assert.NotEqual(0f, sl.Body.AngularVelocity.Y);
        if (pitches)
        {
            var a = Car(VehicleBehaviour.InWorldz, SteerFlag, 0, pitchDegrees, new Vector3(0f, 2f, 0f));
            var b = Plain(new Vector3(0f, (float)(2.0 * Shaped(pitchDegrees)), 0f));
            a.Step(1.0 / 45);
            b.Step(1.0 / 45);
            Same(b.Body.AngularVelocity.Y, a.Body.AngularVelocity.Y);
        }
        else
            Assert.Equal(0f, iw.Body.AngularVelocity.Y);
    }

    // The dead zone is InWorldz's alone: Second Life steers on a camera a tenth of a degree off.
    [Fact]
    public void Second_Life_has_no_dead_zone()
    {
        var sl = Car(VehicleBehaviour.SecondLife, SteerFlag, 0.1, 0, TurnMotor);
        sl.Run(1.0 / 45, 45);
        Assert.True(Heading(sl) > 0.01, $"turned {Heading(sl):0.00000}");
    }

    // The turn is a rate that follows the camera's offset, not an aim at the camera. A bare motor (no angular friction, no
    // decay: nothing takes the speed off it but the shrinking share) overshoots a camera at 20 degrees and comes back, hunting
    // about it within 5 degrees (measured: 15.8 to 24.2 after the first 5 s, a period of about 4.5 s); a car with its own
    // friction settles (VehicleInWorldzMouselookTurnTests).
    [Fact]
    public void A_bare_motor_steered_by_the_shaped_share_hunts_about_the_camera_within_five_degrees()
    {
        var iw = Car(VehicleBehaviour.InWorldz, SteerFlag, 20, 0, TurnMotor);
        iw.Run(1.0 / 45, 45 * 5);
        double worst = 0;
        for (int i = 0; i < 35; i++)
        {
            iw.Run(1.0 / 45, 45);
            worst = Math.Max(worst, Math.Abs(Heading(iw) - 20.0));
        }
        Assert.InRange(worst, 3.0, 5.0);
    }
}
