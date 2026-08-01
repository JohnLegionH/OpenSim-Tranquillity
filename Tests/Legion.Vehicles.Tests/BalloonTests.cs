/*
 * Legion Grid - xunit coverage for the extracted Halcyon vehicle controller, BALLOON slice (M8, final type).
 *
 * The balloon needed NO new controller code: it reuses the boat's buoyancy (1.0, gravity cancelled) and the
 * existing hover, just in air instead of on water. Its signature is the opposite of every other type - it
 * HANGS in mid-air on buoyancy alone, where the car falls, the plane sinks without airspeed, and the sled
 * sits on the ground. Everything else is floaty/damped: a live vertical motor (no LimitMotorUp), gentle yaw,
 * zero deflection (no weathervane), banking 0.05.
 *
 * Airborne rig: BalloonScenario stages a free-body balloon at hover equilibrium. The Do* switches are
 * process-wide statics (assembly disables test parallelization in LinearDeflectionTests.cs); saved/restored.
 */

using System;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class BalloonTests
    {
        private readonly ITestOutputHelper _out;
        public BalloonTests(ITestOutputHelper output) => _out = output;

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

        // (a) Golden preset: ProcessTypeChange(BALLOON) sets exactly the Halcyon BALLOON defaults - above all
        // buoyancy 1.0 (floats), zero deflection, gentle banking, long motor timescales.
        [Fact]
        public void Balloon_Preset_MatchesHalcyonDefaults()
        {
            FakeVehicleBody body = BalloonScenario.NewFloatingBalloon();
            LegionVehicleController v = BalloonScenario.NewController(body);

            Assert.Equal(Vehicle.TYPE_BALLOON, v.Type);

            void EqF(float exp, VehFloatParam k) => Assert.Equal(exp, v.GetFloatParam(k), 3);
            void EqV(Vector3 exp, VehVectorParam k)
            {
                Vector3 a = v.GetVecParam(k);
                Assert.Equal(exp.X, a.X, 3); Assert.Equal(exp.Y, a.Y, 3); Assert.Equal(exp.Z, a.Z, 3);
            }

            EqF(1f, VehFloatParam.Buoyancy);              // floats (gravity cancelled)
            EqF(5f, VehFloatParam.HoverHeight);
            EqF(0.8f, VehFloatParam.HoverEfficiency);
            EqF(10f, VehFloatParam.HoverTimescale);
            EqF(0f, VehFloatParam.LinearDeflectionEfficiency);
            EqF(5f, VehFloatParam.LinearDeflectionTimescale);
            EqF(0f, VehFloatParam.AngularDeflectionEfficiency);   // no weathervane
            EqF(5f, VehFloatParam.AngularDeflectionTimescale);
            EqF(0.5f, VehFloatParam.VerticalAttractionEfficiency);
            EqF(4f, VehFloatParam.VerticalAttractionTimescale);
            EqF(0.05f, VehFloatParam.BankingEfficiency);          // negligible banking
            EqF(0.5f, VehFloatParam.BankingMix);
            EqF(5f, VehFloatParam.BankingTimescale);
            EqF(0f, VehFloatParam.DisableMotorsAbove);
            EqF(0f, VehFloatParam.DisableMotorsAfter);
            EqV(new Vector3(1f, 1f, 5f), VehVectorParam.LinearFrictionTimescale);
            EqV(new Vector3(2f, 0.5f, 1f), VehVectorParam.AngularFrictionTimescale);
            EqV(new Vector3(1f, 5f, 5f), VehVectorParam.LinearMotorTimescale);
            EqV(new Vector3(2f, 2f, 0.3f), VehVectorParam.AngularMotorTimescale);
            EqV(new Vector3(60f, 60f, 60f), VehVectorParam.LinearMotorDecayTimescale);
            EqV(new Vector3(0.3f, 0.3f, 1f), VehVectorParam.AngularMotorDecayTimescale);
            EqV(new Vector3(0.1f, 0.1f, 0.1f), VehVectorParam.LinearWindEfficiency);
            EqV(new Vector3(0.01f, 0.01f, 0f), VehVectorParam.AngularWindEfficiency);
        }

        // (b) ★ Neutral hover - the SIGNATURE. With NO input, a balloon HANGS at altitude (buoyancy 1.0
        // cancels gravity and hover is neutral at its equilibrium), where a CAR at the same start altitude
        // free-falls. "A balloon floats in mid-air on its own." The balloon's terrain sits 5 m below it so
        // hover is neutral; the car's terrain is far below so it free-falls cleanly (no ground-penetration
        // fix) - both start at the same Z, only the type differs.
        [Fact]
        public void Balloon_HangsInMidAir_WhereCarFalls()
        {
            float FinalZ(Vehicle type, float terrain)
            {
                FakeVehicleBody body = BalloonScenario.NewFloatingBalloon();   // Z = hover equilibrium
                body.TerrainHeight = terrain;
                var v = new LegionVehicleController(body);
                v.ProcessTypeChange(type);
                for (int i = 0; i < 40; i++) { v.Step(BalloonScenario.Dt); body.Integrate(BalloonScenario.Dt); }
                return body.Position.Z;
            }

            float start = BalloonScenario.HoverEquilibriumZ;
            float balloonZ = FinalZ(Vehicle.TYPE_BALLOON, BalloonScenario.GroundLevel); // terrain 5 m below -> hover neutral
            float carZ = FinalZ(Vehicle.TYPE_CAR, 0f);                                  // terrain far below -> clean free-fall
            _out.WriteLine($"no input, start z={start:0.0}: balloon z={balloonZ:0.00} (hangs)  car z={carZ:0.00} (falls)");

            Assert.True(Math.Abs(balloonZ - start) < 1f, $"a balloon should HANG on buoyancy (hold altitude), drift={balloonZ - start:0.00}");
            Assert.True(carZ < start - 20f, $"the car should free-fall (buoyancy 0), carZ={carZ:0.00}");
            Assert.True(balloonZ > carZ + 20f, $"the balloon must float where the car falls: balloon={balloonZ:0.00} car={carZ:0.00}");
        }

        // (c) Vertical motor lift: the balloon is the only type with a LIVE vertical motor (no LimitMotorUp
        // flag), so a Z linear-motor command makes it CLIMB - no airspeed needed (unlike the plane). Its stiff
        // hover (eff 0.8) clamps altitude hard, so the net climb is modest (a balloon really climbs by raising
        // its hover height, not by fighting hover with the Z motor) - but the lift is real, which proves the
        // up-motor is NOT clamped to zero the way car/plane's LimitMotorUp clamps theirs.
        [Fact]
        public void Balloon_VerticalMotor_Lifts()
        {
            FakeVehicleBody body = BalloonScenario.NewFloatingBalloon();
            LegionVehicleController v = BalloonScenario.NewController(body);

            float start = body.Position.Z;
            var up = new Vector3(0f, 0f, 15f);   // Z linear motor - straight up, no airspeed
            const int frames = 60;
            for (int i = 0; i < frames; i++)
            {
                v.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, up);
                v.Step(BalloonScenario.Dt);
                body.Integrate(BalloonScenario.Dt);
            }
            _out.WriteLine($"Z-motor lift: start z={start:0.0} -> end z={body.Position.Z:0.00} (gain {body.Position.Z - start:0.00})");

            Assert.True(body.Position.Z > start + 1f, $"a Z-motor command should LIFT the balloon above its hover hold (live vertical motor, no airspeed), gain={body.Position.Z - start:0.00}");
        }

        // (d) Gentle yaw: the balloon steers, but heavily damped (AngularMotorTimescale.Z 0.3, friction 1,
        // decay 1) - far softer than the car's sharp 0.05. Same yaw command, compared: the balloon turns, but
        // much less than a car.
        [Fact]
        public void Balloon_GentleYaw_SofterThanCar()
        {
            float Yaw(Vehicle type)
            {
                FakeVehicleBody body = BalloonScenario.NewFloatingBalloon();
                body.LinearVelocity = new Vector3(5f, 0f, 0f);
                var v = new LegionVehicleController(body);
                v.ProcessTypeChange(type);
                for (int i = 0; i < 10; i++)   // short window: the ramp difference (car TS 0.05 vs balloon 0.3) shows
                {
                    v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 0.5f));
                    v.Step(BalloonScenario.Dt);
                    body.Integrate(BalloonScenario.Dt);
                }
                return Math.Abs(HeadingDeg(body.Orientation));
            }

            float balloonYaw = Yaw(Vehicle.TYPE_BALLOON);
            float carYaw = Yaw(Vehicle.TYPE_CAR);
            _out.WriteLine($"same yaw command: balloon turned {balloonYaw:0.00} deg (gentle), car turned {carYaw:0.00} deg (sharp)");

            Assert.True(balloonYaw > 2f, $"a balloon should still yaw (gently), balloon={balloonYaw:0.00}");
            Assert.True(balloonYaw < carYaw * 0.7f, $"a balloon must steer far softer than a car: balloon={balloonYaw:0.00} car={carYaw:0.00}");
        }

        // (e) No weathervane: the balloon's AngularDeflectionEfficiency is 0, so - unlike the boat/plane/sled -
        // its nose does NOT swing toward the velocity. Same off-axis, fast-slide setup as the weathervane
        // tests, but the heading stays put.
        [Fact]
        public void Balloon_DoesNotWeathervane()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = false;
                LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoVerticalAttractor = false;
                LegionVehicleLimits.DoLinearDeflection = false; LegionVehicleLimits.DoBanking = false;
                LegionVehicleLimits.DoAngularDeflection = true;    // the term that WOULD weathervane - but eff is 0

                FakeVehicleBody body = BalloonScenario.NewFloatingBalloon();   // nose +X
                body.Gravity = Vector3.Zero;
                body.LinearVelocity = new Vector3(15f, 15f, 0f);   // ~21 m/s, 45 deg off the nose
                LegionVehicleController v = BalloonScenario.NewController(body);

                float maxHeading = 0f;
                for (int i = 0; i < 120; i++)
                {
                    v.Step(BalloonScenario.Dt);
                    body.Integrate(BalloonScenario.Dt);
                    maxHeading = Math.Max(maxHeading, Math.Abs(HeadingDeg(body.Orientation)));
                }
                _out.WriteLine($"balloon weathervane check: max heading swing={maxHeading:0.00} deg (expect ~0, deflection eff 0)");

                Assert.True(maxHeading < 2f, $"a balloon must NOT weathervane (AngularDeflectionEfficiency 0), swing={maxHeading:0.00}");
            }
            finally { Restore(prev); }
        }
    }
}
