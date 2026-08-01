/*
 * Legion Grid - xunit coverage for the extracted Halcyon vehicle controller, CAR slice (M8).
 *
 * The ground-vehicle counterpart to the boat proofs. The CAR preset is the biggest contrast to the boat:
 * buoyancy=0 (gravity NOT cancelled - the car rides the ground under gravity, it does not float), no
 * hover, high sideways tire grip, upright vertical attraction, and a subtle opposite-sign banking. These
 * lock in the objective numbers so CI catches a regression in the car math without a scene. John smoke-
 * tests feel in a viewer (jolt cartest); these guard the arithmetic.
 *
 * Ground contact is modelled by FakeVehicleBody's opt-in GroundCollision clamp (see CarScenario) - the
 * same floor Jolt gives an in-world car. Angle helpers are shared with BoatScenario (pure geometry).
 * The determinism note in BoatMotorTests applies (spike detection disabled via the scenario).
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class CarTests
    {
        private readonly ITestOutputHelper _out;
        public CarTests(ITestOutputHelper output) => _out = output;

        // (a1) No buoyancy: with no ground under it, a car FALLS at ~g - gravity is NOT cancelled (unlike
        // the boat's buoyancy=1.0). Isolate gravity from the other actors (ApplyGravity is never gated by
        // a Do* switch), and stage the car high above terrain so neither hover nor the ground-penetration
        // fix engages. A boat under the same isolation would not move; the car must fall.
        [Fact]
        public void Car_NoBuoyancy_FallsWhenUnsupported()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = false;
                LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoVerticalAttractor = false;
                LegionVehicleLimits.DoLinearDeflection = false; LegionVehicleLimits.DoAngularDeflection = false;
                LegionVehicleLimits.DoBanking = false;

                FakeVehicleBody body = CarScenario.NewGroundedCar(z: 50f, groundCollision: false); // 30 m above terrain
                LegionVehicleController v = CarScenario.NewController(body);

                float startZ = body.Position.Z;
                const int frames = 20;
                for (int i = 0; i < frames; i++) { v.Step(CarScenario.Dt); body.Integrate(CarScenario.Dt); }
                float expected = -9.80665f * frames * CarScenario.Dt;
                _out.WriteLine($"unsupported car: startZ={startZ:0.00} endZ={body.Position.Z:0.00} vZ={body.LinearVelocity.Z:0.00} (expect vZ~{expected:0.00})");

                Assert.True(body.LinearVelocity.Z < -10f, $"a non-buoyant car should fall at ~g (gravity not cancelled), vZ={body.LinearVelocity.Z:0.00}");
                Assert.True(body.Position.Z < startZ - 5f, $"a non-buoyant car should drop, startZ={startZ:0.00} endZ={body.Position.Z:0.00}");
            }
            finally { Restore(prev); }
        }

        // (a2) With ground contact, that fall is arrested: the car settles AT the terrain and rests there.
        [Fact]
        public void Car_OnGround_SettlesAndRests()
        {
            FakeVehicleBody body = CarScenario.NewGroundedCar(z: CarScenario.RestingZ + 3f, groundCollision: true);
            LegionVehicleController v = CarScenario.NewController(body);

            const int frames = 60;
            for (int i = 0; i < frames; i++)
            {
                v.Step(CarScenario.Dt);
                body.Integrate(CarScenario.Dt);
            }
            _out.WriteLine($"grounded car: Z={body.Position.Z:0.0000} (target {CarScenario.RestingZ:0.0000}) vZ={body.LinearVelocity.Z:0.0000} collision={body.HasCollision}");

            Assert.True(body.HasCollision, "a car resting on the ground should report collision");
            Assert.InRange(body.Position.Z, CarScenario.RestingZ - 0.01f, CarScenario.RestingZ + 0.01f);
            Assert.True(Math.Abs(body.LinearVelocity.Z) < 0.01f, $"a rested car should have ~zero vertical velocity, vZ={body.LinearVelocity.Z:0.0000}");
        }

        // (b) Vertical attractor rights a rolled car back to level (LimitRollOnly - roll is corrected).
        [Fact]
        public void Car_VerticalAttractor_LevelsARolledCar()
        {
            Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 25f * Utils.DEG_TO_RAD);
            FakeVehicleBody body = CarScenario.NewGroundedCar(orientation: roll, groundCollision: true);
            LegionVehicleController v = CarScenario.NewController(body);

            float startTilt = BoatScenario.TiltDegrees(body.Orientation);
            const int frames = 120;
            for (int i = 0; i < frames; i++)
            {
                v.Step(CarScenario.Dt);
                body.Integrate(CarScenario.Dt);
            }
            float endTilt = BoatScenario.TiltDegrees(body.Orientation);
            _out.WriteLine($"car righting: startTilt={startTilt:0.00} endTilt={endTilt:0.00}");

            Assert.True(startTilt > 20f, $"sanity: should start meaningfully rolled, startTilt={startTilt:0.00}");
            Assert.True(endTilt < 5f, $"vertical attractor should level the car, endTilt={endTilt:0.00}");
        }

        // (c) Linear motor drives the car forward: a held throttle ramps forward speed toward target.
        [Fact]
        public void Car_LinearMotor_DrivesForward()
        {
            FakeVehicleBody body = CarScenario.NewGroundedCar(groundCollision: true);
            LegionVehicleController v = CarScenario.NewController(body);

            const float target = 4f;
            const int frames = 60;
            var fwd = new List<float>(frames);
            for (int i = 0; i < frames; i++)
            {
                v.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(target, 0f, 0f));
                v.Step(CarScenario.Dt);
                body.Integrate(CarScenario.Dt);
                fwd.Add(body.LinearVelocity.X);
            }
            _out.WriteLine($"car drive: v[0]={fwd[0]:0.000} v@1s={fwd[(int)(1f / CarScenario.Dt)]:0.000} final={fwd[frames - 1]:0.000}");

            // small first-frame engagement, monotonic non-decreasing ramp, approaches target, genuine ramp.
            Assert.InRange(fwd[0], 0.005f, 1.5f);
            for (int i = 1; i < frames; i++)
                Assert.True(fwd[i] >= fwd[i - 1] - 1e-4f, $"speed dropped at frame {i}: {fwd[i - 1]:0.0000} -> {fwd[i]:0.0000}");
            float final = fwd[frames - 1];
            Assert.InRange(final, 2.5f, target + 0.1f);
            int early = (int)(0.5f / CarScenario.Dt);
            Assert.True(fwd[early] < final, $"expected a ramp: v@0.5s ({fwd[early]:0.000}) should be below final ({final:0.000})");
        }

        // (d1) Angular motor steers: a held yaw command turns the car's heading in the commanded direction.
        [Fact]
        public void Car_AngularMotor_Steers()
        {
            FakeVehicleBody body = CarScenario.NewGroundedCar(groundCollision: true);
            body.LinearVelocity = new Vector3(5f, 0f, 0f);          // rolling forward while steering
            LegionVehicleController v = CarScenario.NewController(body);

            const float yawRate = 0.5f;                            // rad/s target about body Z
            const int frames = 20;
            for (int i = 0; i < frames; i++)
            {
                v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, yawRate));
                v.Step(CarScenario.Dt);
                body.Integrate(CarScenario.Dt);
            }
            float yaw = BoatScenario.YawDegrees(body.Orientation);
            _out.WriteLine($"car steer: yaw={yaw:0.00} deg after {frames} frames");

            Assert.True(yaw > 15f, $"a positive yaw command should turn the car left (>15 deg), yaw={yaw:0.00}");
        }

        // (d2) Tire grip: high sideways friction (LinearFrictionTimescale Y=0.1) kills lateral slip fast,
        // while a car rolls forward freely (X=100). Isolate friction - with motors on, the linear motor
        // would brake ALL axes toward its zero target (car X-timescale 0.5), masking the friction; with
        // deflection on, it would redirect the velocity. Friction alone shows the directional grip.
        [Fact]
        public void Car_SidewaysFriction_ResistsLateralSlip()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = true;
                LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoVerticalAttractor = false;
                LegionVehicleLimits.DoLinearDeflection = false; LegionVehicleLimits.DoAngularDeflection = false;
                LegionVehicleLimits.DoBanking = false;

                FakeVehicleBody body = CarScenario.NewGroundedCar(groundCollision: true);
                body.LinearVelocity = new Vector3(4f, 3f, 0f);     // forward 4 + sideways 3 (body frame, level car)
                LegionVehicleController v = CarScenario.NewController(body);

                const int frames = 15;
                for (int i = 0; i < frames; i++) { v.Step(CarScenario.Dt); body.Integrate(CarScenario.Dt); }
                _out.WriteLine($"car grip: vX={body.LinearVelocity.X:0.000} vY={body.LinearVelocity.Y:0.000}");

                Assert.True(Math.Abs(body.LinearVelocity.Y) < 0.5f, $"sideways slip should be gripped away (Y-timescale 0.1), vY={body.LinearVelocity.Y:0.000}");
                Assert.True(body.LinearVelocity.X > 3f, $"forward roll should mostly persist (X-timescale 100), vX={body.LinearVelocity.X:0.000}");
            }
            finally { Restore(prev); }
        }

        // ---- (e) Banking: the car reuses the boat's roll->yaw banking term, with its own subtle,
        // opposite-sign efficiency (-0.2). Isolate banking the same way BankingTests does. ----
        private struct DoFlags { public bool Motors, LinFric, AngFric, VAttract, LinDefl, AngDefl, Banking; }
        private static DoFlags Save() => new DoFlags {
            Motors = LegionVehicleLimits.DoMotors, LinFric = LegionVehicleLimits.DoLinearFriction,
            AngFric = LegionVehicleLimits.DoAngularFriction, VAttract = LegionVehicleLimits.DoVerticalAttractor,
            LinDefl = LegionVehicleLimits.DoLinearDeflection, AngDefl = LegionVehicleLimits.DoAngularDeflection,
            Banking = LegionVehicleLimits.DoBanking };
        private static void Restore(DoFlags p) {
            LegionVehicleLimits.DoMotors = p.Motors; LegionVehicleLimits.DoLinearFriction = p.LinFric;
            LegionVehicleLimits.DoAngularFriction = p.AngFric; LegionVehicleLimits.DoVerticalAttractor = p.VAttract;
            LegionVehicleLimits.DoLinearDeflection = p.LinDefl; LegionVehicleLimits.DoAngularDeflection = p.AngDefl;
            LegionVehicleLimits.DoBanking = p.Banking; }
        private static void IsolateBanking() {
            LegionVehicleLimits.DoVerticalAttractor = true; LegionVehicleLimits.DoMotors = true;
            LegionVehicleLimits.DoLinearFriction = false; LegionVehicleLimits.DoAngularFriction = false;
            LegionVehicleLimits.DoLinearDeflection = false; LegionVehicleLimits.DoAngularDeflection = false; }
        private static float HeadingDeg(Quaternion o) { Vector3 n = Vector3.UnitX * o; return (float)(Math.Atan2(n.Y, n.X) * 180.0 / Math.PI); }

        // A car rolled `rollDeg` about its nose, moving forward, gravity off + no floor (isolate rotation).
        private float RunRolledCarYaw(float rollDeg)
        {
            Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollDeg * Utils.DEG_TO_RAD);
            FakeVehicleBody body = CarScenario.NewGroundedCar(orientation: roll, groundCollision: false);
            body.Gravity = Vector3.Zero;
            body.LinearVelocity = new Vector3(8f, 0f, 0f);         // forward speed feeds the dynamic banking half
            LegionVehicleController v = CarScenario.NewController(body);   // car preset: Eff=-0.2, Mix=1, TS=1
            for (int i = 0; i < 60; i++) { v.Step(CarScenario.Dt); body.Integrate(CarScenario.Dt); }
            return HeadingDeg(body.Orientation);
        }

        [Fact]
        public void Car_Banking_RollIsConvertedToYaw()
        {
            DoFlags prev = Save();
            try
            {
                IsolateBanking();

                LegionVehicleLimits.DoBanking = false;
                float offYaw = RunRolledCarYaw(+20f);              // attractor rights the roll; ~no yaw
                LegionVehicleLimits.DoBanking = true;
                float onPos = RunRolledCarYaw(+20f);               // roll -> yaw
                float onNeg = RunRolledCarYaw(-20f);               // opposite roll -> opposite yaw
                _out.WriteLine($"car banking: off={offYaw:0.000} on(+roll)={onPos:0.000} on(-roll)={onNeg:0.000}");

                // (1) banking adds yaw beyond the attractor-only baseline.
                Assert.True(Math.Abs(onPos) > Math.Abs(offYaw) + 0.5f,
                    $"banking should turn a roll into yaw: off {offYaw:0.000} vs on {onPos:0.000}");
                // (2) roll -> yaw sign coupling: reversing the roll reverses the yaw.
                Assert.True(Math.Sign(onPos) == -Math.Sign(onNeg) && Math.Abs(onNeg) > 0.5f,
                    $"opposite roll should yaw opposite: +roll {onPos:0.000}, -roll {onNeg:0.000}");
            }
            finally { Restore(prev); }
        }

        // (f) Golden preset: ProcessTypeChange(CAR) sets exactly the Halcyon CAR defaults. Guards against
        // silent drift (this is what caught the two params we aligned to Halcyon in this slice).
        [Fact]
        public void Car_Preset_MatchesHalcyonDefaults()
        {
            FakeVehicleBody body = CarScenario.NewGroundedCar();
            LegionVehicleController v = CarScenario.NewController(body);

            Assert.Equal(Vehicle.TYPE_CAR, v.Type);

            void EqF(float exp, VehFloatParam k) => Assert.Equal(exp, v.GetFloatParam(k), 3);
            void EqV(Vector3 exp, VehVectorParam k)
            {
                Vector3 a = v.GetVecParam(k);
                Assert.Equal(exp.X, a.X, 3); Assert.Equal(exp.Y, a.Y, 3); Assert.Equal(exp.Z, a.Z, 3);
            }

            // ground vehicle: no buoyancy, no hover
            EqF(0f, VehFloatParam.Buoyancy);
            EqF(0f, VehFloatParam.HoverHeight);
            EqF(0f, VehFloatParam.HoverEfficiency);
            EqF(1000f, VehFloatParam.HoverTimescale);
            // deflection / attraction / banking
            EqF(1f, VehFloatParam.LinearDeflectionEfficiency);
            EqF(2f, VehFloatParam.LinearDeflectionTimescale);
            EqF(0.5f, VehFloatParam.AngularDeflectionEfficiency);
            EqF(2f, VehFloatParam.AngularDeflectionTimescale);
            EqF(0.6f, VehFloatParam.VerticalAttractionEfficiency);
            EqF(2f, VehFloatParam.VerticalAttractionTimescale);
            EqF(-0.2f, VehFloatParam.BankingEfficiency);
            EqF(1f, VehFloatParam.BankingMix);
            EqF(1f, VehFloatParam.BankingTimescale);
            EqF(0.75f, VehFloatParam.DisableMotorsAbove);
            EqF(2.5f, VehFloatParam.DisableMotorsAfter);
            // vector params (incl. the two aligned to Halcyon: AngularFrictionTS.Z=0.3, AngularMotorDecay=(0.3,0.3,0.1))
            EqV(new Vector3(100f, 0.1f, 10f), VehVectorParam.LinearFrictionTimescale);
            EqV(new Vector3(100f, 100f, 0.3f), VehVectorParam.AngularFrictionTimescale);
            EqV(new Vector3(0.5f, 1f, 1f), VehVectorParam.LinearMotorTimescale);
            EqV(new Vector3(0.2f, 0.2f, 0.05f), VehVectorParam.AngularMotorTimescale);
            EqV(new Vector3(10f, 2f, 2f), VehVectorParam.LinearMotorDecayTimescale);
            EqV(new Vector3(0.3f, 0.3f, 0.1f), VehVectorParam.AngularMotorDecayTimescale);
        }
    }
}
