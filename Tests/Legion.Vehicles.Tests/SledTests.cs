/*
 * Legion Grid - xunit coverage for the extracted Halcyon vehicle controller, SLED slice (M8).
 *
 * The sled is a ground vehicle that shares the car's rig but is its opposite in feel: it has NO engine and
 * NO steering (motor timescales 1000 = inert), NO banking (efficiency 0), and glides on very low forward
 * friction while gripping sideways. It slides downhill under gravity (SimulateSledMovement - proven in
 * SledMovementTests). These lock in the sled's DEFINING behaviours: the exact Halcyon preset, and - the
 * signature contrast with the car - that a sled does NOT steer.
 *
 * Uses the shared ground-clamp rig (SledScenario). The determinism note in BoatMotorTests applies; the Do*
 * switches are process-wide statics (assembly disables test parallelization in LinearDeflectionTests.cs),
 * saved + restored per test.
 */

using System;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class SledTests
    {
        private readonly ITestOutputHelper _out;
        public SledTests(ITestOutputHelper output) => _out = output;

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

        private static float HeadingDeg(Quaternion o) { Vector3 n = Vector3.UnitX * o; return (float)(Math.Atan2(n.Y, n.X) * 180.0 / Math.PI); }

        // (a) Golden preset: ProcessTypeChange(SLED) sets exactly the Halcyon SLED defaults. Guards drift -
        // and documents the sled: no engine (motor TS 1000), no banking (0), glide/grip friction (1000/1).
        [Fact]
        public void Sled_Preset_MatchesHalcyonDefaults()
        {
            FakeVehicleBody body = SledScenario.NewGroundedSled();
            LegionVehicleController v = SledScenario.NewController(body);

            Assert.Equal(Vehicle.TYPE_SLED, v.Type);

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
            EqF(0.3f, VehFloatParam.LinearDeflectionTimescale);
            EqF(1f, VehFloatParam.AngularDeflectionEfficiency);
            EqF(1f, VehFloatParam.AngularDeflectionTimescale);
            EqF(0.1f, VehFloatParam.VerticalAttractionEfficiency);
            EqF(10f, VehFloatParam.VerticalAttractionTimescale);
            EqF(0f, VehFloatParam.BankingEfficiency);           // no banking
            EqF(1f, VehFloatParam.BankingMix);
            EqF(10f, VehFloatParam.BankingTimescale);
            EqF(0f, VehFloatParam.DisableMotorsAbove);
            EqF(0f, VehFloatParam.DisableMotorsAfter);
            // vector params: no engine (motor TS 1000), glide/grip friction (X=1000 low, Y=1), low CoG offset
            EqV(new Vector3(1000f, 1f, 1000f), VehVectorParam.LinearFrictionTimescale);
            EqV(new Vector3(1000f, 1000f, 1000f), VehVectorParam.AngularFrictionTimescale);
            EqV(new Vector3(1000f, 1000f, 1000f), VehVectorParam.LinearMotorTimescale);
            EqV(new Vector3(1000f, 1000f, 1000f), VehVectorParam.AngularMotorTimescale);
            EqV(new Vector3(120f, 120f, 120f), VehVectorParam.LinearMotorDecayTimescale);
            EqV(new Vector3(120f, 120f, 120f), VehVectorParam.AngularMotorDecayTimescale);
            EqV(new Vector3(0f, 0f, -0.1f), VehVectorParam.LinearMotorOffset);
        }

        // (b) ★ No-steer - the defining contrast with the car. Runs the SAME steer input (forward velocity +
        // held yaw command) on a CAR and a SLED and compares: the car turns hard, the sled barely turns
        // (angular motor timescale 1000 = inert, and angular deflection weathervanes the nose back to the
        // velocity). A sled slides; it does not steer. Comparative so it can't rot to a magic threshold.
        [Fact]
        public void Sled_DoesNotSteer_UnlikeCar()
        {
            float SteerYaw(Vehicle type)
            {
                FakeVehicleBody body = SledScenario.NewGroundedSled(groundCollision: true);
                body.LinearVelocity = new Vector3(5f, 0f, 0f);     // rolling forward
                LegionVehicleLimits.DoSpikeDetection = false;
                var v = new LegionVehicleController(body);
                v.ProcessTypeChange(type);                         // same body, controller type differs
                for (int i = 0; i < 20; i++)
                {
                    v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 0.5f));
                    v.Step(SledScenario.Dt);
                    body.Integrate(SledScenario.Dt);
                }
                return Math.Abs(HeadingDeg(body.Orientation));
            }

            float carYaw = SteerYaw(Vehicle.TYPE_CAR);
            float sledYaw = SteerYaw(Vehicle.TYPE_SLED);
            _out.WriteLine($"identical yaw command: car turned {carYaw:0.00} deg, sled turned {sledYaw:0.00} deg");

            Assert.True(carYaw > 15f, $"sanity: a car should steer hard under this command, car={carYaw:0.00}");
            Assert.True(sledYaw < carYaw * 0.5f, $"a sled must steer far less than a car (motor TS 1000 inert): sled={sledYaw:0.00} vs car={carYaw:0.00}");
        }

        // (c) No banking: a rolled, moving sled produces ~no yaw (BankingEfficiency 0), vs car -0.2 / boat
        // +1.0. Isolate to the banking path (attractor on so it can run; friction/deflection off).
        [Fact]
        public void Sled_NoBanking_RolledSledDoesNotYaw()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoVerticalAttractor = true; LegionVehicleLimits.DoMotors = true;
                LegionVehicleLimits.DoLinearFriction = false; LegionVehicleLimits.DoAngularFriction = false;
                LegionVehicleLimits.DoLinearDeflection = false; LegionVehicleLimits.DoAngularDeflection = false;
                LegionVehicleLimits.DoBanking = true;

                Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20f * Utils.DEG_TO_RAD);
                FakeVehicleBody body = SledScenario.NewGroundedSled(orientation: roll, groundCollision: false);
                body.Gravity = Vector3.Zero;
                body.LinearVelocity = new Vector3(8f, 0f, 0f);     // forward speed would feed banking IF eff != 0
                LegionVehicleController v = SledScenario.NewController(body);

                for (int i = 0; i < 60; i++) { v.Step(SledScenario.Dt); body.Integrate(SledScenario.Dt); }
                float yaw = Math.Abs(HeadingDeg(body.Orientation));
                _out.WriteLine($"rolled sled: |yaw|={yaw:0.000} deg (banking eff 0 -> expect ~0)");

                Assert.True(yaw < 2f, $"a sled has no banking (eff 0), a rolled sled must not yaw, |yaw|={yaw:0.000}");
            }
            finally { Restore(prev); }
        }

        // (d) Glide / grip: the sled's signature friction profile - forward friction TS X=1000 (glides, speed
        // persists) vs sideways Y=1 (grips, lateral slip decays). Isolate friction (no motor to brake it).
        [Fact]
        public void Sled_GlidesForward_GripsSideways()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = true;
                LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoVerticalAttractor = false;
                LegionVehicleLimits.DoLinearDeflection = false; LegionVehicleLimits.DoAngularDeflection = false;
                LegionVehicleLimits.DoBanking = false;

                FakeVehicleBody body = SledScenario.NewGroundedSled(groundCollision: true);
                body.LinearVelocity = new Vector3(4f, 3f, 0f);     // forward 4 + sideways 3 (body frame, level)
                LegionVehicleController v = SledScenario.NewController(body);

                const int frames = 22;
                for (int i = 0; i < frames; i++) { v.Step(SledScenario.Dt); body.Integrate(SledScenario.Dt); }
                _out.WriteLine($"sled glide/grip: vX={body.LinearVelocity.X:0.000} vY={body.LinearVelocity.Y:0.000}");

                Assert.True(body.LinearVelocity.X > 3.5f, $"forward glide should persist (X-timescale 1000), vX={body.LinearVelocity.X:0.000}");
                Assert.True(Math.Abs(body.LinearVelocity.Y) < Math.Abs(body.LinearVelocity.X) - 1.5f,
                    $"sideways slip should be gripped down below the forward glide (Y-timescale 1), vY={body.LinearVelocity.Y:0.000} vX={body.LinearVelocity.X:0.000}");
            }
            finally { Restore(prev); }
        }

        // (e) Weathervane: a moving sled's nose swings toward its velocity direction (AngularDeflection eff 1,
        // TS 1 - stronger/faster than the boat's 0.5/5). Speed-gated like the boat, so drive it fast.
        [Fact]
        public void Sled_Weathervanes_NoseTowardVelocity()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = false;
                LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoVerticalAttractor = false;
                LegionVehicleLimits.DoLinearDeflection = false;    // hold velocity fixed; only the nose moves
                LegionVehicleLimits.DoBanking = false;
                LegionVehicleLimits.DoAngularDeflection = true;    // the term under test

                FakeVehicleBody body = SledScenario.NewGroundedSled(groundCollision: false);   // nose +X (heading 0)
                body.Gravity = Vector3.Zero;
                body.LinearVelocity = new Vector3(15f, 15f, 0f);   // ~21 m/s, 45 deg off the nose (fires the speed gate)
                LegionVehicleController v = SledScenario.NewController(body);

                float maxHeading = 0f;
                const int frames = 120;
                for (int i = 0; i < frames; i++)
                {
                    v.Step(SledScenario.Dt);
                    body.Integrate(SledScenario.Dt);
                    float h = HeadingDeg(body.Orientation);
                    if (h > maxHeading) maxHeading = h;
                }
                _out.WriteLine($"sled weathervane: max heading={maxHeading:0.00} deg (toward the +45 velocity)");

                Assert.True(maxHeading > 10f, $"the sled nose should weathervane toward the velocity (+Y side), max heading={maxHeading:0.00}");
            }
            finally { Restore(prev); }
        }
    }
}
