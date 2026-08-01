/*
 * Legion Grid - xunit coverage for the extracted Halcyon vehicle controller, AIRPLANE slice (M8).
 *
 * The airplane is the most complex type and the first fully airborne one, yet it needed NO new controller
 * code: the flight machinery already exists (airplane-aware vertical attractor - fly inverted, free pitch,
 * gentle roll; lift via SimulateLinearDeflection since the plane omits NoDeflectionUp; banking->yaw for
 * bank-to-turn; weathervane). These lock in the DEFINING flight behaviours - above all the signature
 * comparative: a plane makes LIFT where a ground vehicle cannot.
 *
 * Airborne rig: PlaneScenario stages a free-body plane (no ground clamp). The Do* switches are process-wide
 * statics (assembly disables test parallelization in LinearDeflectionTests.cs); saved + restored per test.
 */

using System;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class PlaneTests
    {
        private readonly ITestOutputHelper _out;
        public PlaneTests(ITestOutputHelper output) => _out = output;

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
        private static float PitchDeg(Quaternion o) { Vector3 n = Vector3.UnitX * o; return (float)(Math.Asin(Utils.Clamp(n.Z, -1f, 1f)) * 180.0 / Math.PI); }
        // Nose pitched UP by deg (rotation about world -Y so the nose's +Z component is positive).
        private static Quaternion NoseUp(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -deg * Utils.DEG_TO_RAD);

        // (a) Golden preset: ProcessTypeChange(AIRPLANE) sets exactly the Halcyon AIRPLANE defaults.
        [Fact]
        public void Plane_Preset_MatchesHalcyonDefaults()
        {
            FakeVehicleBody body = PlaneScenario.NewAirbornePlane();
            LegionVehicleController v = PlaneScenario.NewController(body);

            Assert.Equal(Vehicle.TYPE_AIRPLANE, v.Type);

            void EqF(float exp, VehFloatParam k) => Assert.Equal(exp, v.GetFloatParam(k), 3);
            void EqV(Vector3 exp, VehVectorParam k)
            {
                Vector3 a = v.GetVecParam(k);
                Assert.Equal(exp.X, a.X, 3); Assert.Equal(exp.Y, a.Y, 3); Assert.Equal(exp.Z, a.Z, 3);
            }

            EqF(0f, VehFloatParam.Buoyancy);
            EqF(0f, VehFloatParam.HoverHeight);
            EqF(0.5f, VehFloatParam.HoverEfficiency);
            EqF(1000f, VehFloatParam.HoverTimescale);
            EqF(0.5f, VehFloatParam.LinearDeflectionEfficiency);
            EqF(0.5f, VehFloatParam.LinearDeflectionTimescale);
            EqF(1f, VehFloatParam.AngularDeflectionEfficiency);
            EqF(2f, VehFloatParam.AngularDeflectionTimescale);
            EqF(0.9f, VehFloatParam.VerticalAttractionEfficiency);
            EqF(2f, VehFloatParam.VerticalAttractionTimescale);
            EqF(1f, VehFloatParam.BankingEfficiency);        // banks to turn, like the boat
            EqF(0.7f, VehFloatParam.BankingMix);
            EqF(1f, VehFloatParam.BankingTimescale);
            EqF(0f, VehFloatParam.DisableMotorsAbove);
            EqF(0f, VehFloatParam.DisableMotorsAfter);
            EqV(new Vector3(200f, 10f, 5f), VehVectorParam.LinearFrictionTimescale);
            EqV(new Vector3(1f, 0.1f, 0.5f), VehVectorParam.AngularFrictionTimescale);
            EqV(new Vector3(2f, 2f, 2f), VehVectorParam.LinearMotorTimescale);
            EqV(new Vector3(1f, 2f, 1f), VehVectorParam.AngularMotorTimescale);
            EqV(new Vector3(60f, 60f, 60f), VehVectorParam.LinearMotorDecayTimescale);
            EqV(new Vector3(8f, 8f, 8f), VehVectorParam.AngularMotorDecayTimescale);
            EqV(new Vector3(0.1f, 0f, 0f), VehVectorParam.LinearWindEfficiency);
            EqV(new Vector3(0.05f, 0f, 0f), VehVectorParam.AngularWindEfficiency);
        }

        // (b) ★ Lift/climb - the SIGNATURE test, comparative. A nose-up body with horizontal airspeed: the
        // PLANE converts that airspeed to a climb (linear deflection redirects velocity up along the nose,
        // and the plane omits NoDeflectionUp), while a CAR in the identical state cannot lift (NoDeflectionUp
        // zeroes the upward redirect) and falls under gravity. Isolate to linear deflection + gravity so the
        // only variable is the flag: "a plane makes lift; a ground vehicle can't."
        [Fact]
        public void Plane_MakesLift_WhereCarFalls()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = false;
                LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoVerticalAttractor = false;
                LegionVehicleLimits.DoAngularDeflection = false; LegionVehicleLimits.DoBanking = false;
                LegionVehicleLimits.DoLinearDeflection = true;    // the lift mechanism under test

                float FinalZ(Vehicle type)
                {
                    FakeVehicleBody body = PlaneScenario.NewAirbornePlane(airspeed: 50f, orientation: NoseUp(20f));
                    var v = new LegionVehicleController(body);
                    v.ProcessTypeChange(type);                    // same airborne state, controller type differs
                    for (int i = 0; i < 15; i++) { v.Step(PlaneScenario.Dt); body.Integrate(PlaneScenario.Dt); }
                    return body.Position.Z;
                }

                float start = PlaneScenario.Altitude;
                float planeZ = FinalZ(Vehicle.TYPE_AIRPLANE);
                float carZ = FinalZ(Vehicle.TYPE_CAR);
                _out.WriteLine($"nose-up 20deg + 50 m/s airspeed, deflection only: start={start:0.0}  plane endZ={planeZ:0.00}  car endZ={carZ:0.00}");

                Assert.True(planeZ > start, $"the plane should CLIMB (lift from deflection), start={start:0.0} planeZ={planeZ:0.00}");
                Assert.True(carZ < start, $"the car should FALL (NoDeflectionUp blocks lift), start={start:0.0} carZ={carZ:0.00}");
                Assert.True(planeZ > carZ + 5f, $"plane must out-climb the car by a clear margin: plane={planeZ:0.00} car={carZ:0.00}");
            }
            finally { Restore(prev); }
        }

        // (c) Bank-to-turn: a plane turns by ROLLING - a banked, moving plane yaws (banking->yaw, eff 1),
        // and reversing the roll reverses the turn. This is the plane's primary steering (contrast car -0.2,
        // sled 0). Isolate banking the way BankingTests does.
        [Fact]
        public void Plane_BanksToTurn()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoVerticalAttractor = true; LegionVehicleLimits.DoMotors = true;
                LegionVehicleLimits.DoLinearFriction = false; LegionVehicleLimits.DoAngularFriction = false;
                LegionVehicleLimits.DoLinearDeflection = false; LegionVehicleLimits.DoAngularDeflection = false;

                float BankYaw(float rollDeg)
                {
                    Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollDeg * Utils.DEG_TO_RAD);
                    FakeVehicleBody body = PlaneScenario.NewAirbornePlane(airspeed: 15f, orientation: roll);
                    body.Gravity = Vector3.Zero;                  // isolate the turn from the climb/fall
                    var v = PlaneScenario.NewController(body);     // plane preset: banking eff 1, mix 0.7, ts 1
                    LegionVehicleLimits.DoBanking = true;
                    for (int i = 0; i < 60; i++) { v.Step(PlaneScenario.Dt); body.Integrate(PlaneScenario.Dt); }
                    return HeadingDeg(body.Orientation);
                }

                float onPos = BankYaw(+25f);
                float onNeg = BankYaw(-25f);
                _out.WriteLine($"plane bank-to-turn: +roll yaw={onPos:0.00}  -roll yaw={onNeg:0.00}");

                Assert.True(Math.Abs(onPos) > 3f, $"a banked plane should turn (banking->yaw), +roll yaw={onPos:0.00}");
                Assert.True(Math.Sign(onPos) == -Math.Sign(onNeg) && Math.Abs(onNeg) > 3f,
                    $"reversing the bank should reverse the turn: +roll {onPos:0.00}, -roll {onNeg:0.00}");
            }
            finally { Restore(prev); }
        }

        // (d) Free pitch: a plane holds a pilot-set pitch attitude - the airplane vertical attractor does NO
        // pitch restoration (vtwix.Y = 0 for LimitRollOnly + airplane), so a nose-up plane stays nose-up. A
        // car (also LimitRollOnly, but non-airplane) gets a restoring component and levels off more.
        [Fact]
        public void Plane_HoldsPitchAttitude_UnlikeCar()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = false;
                LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoLinearDeflection = false;
                LegionVehicleLimits.DoAngularDeflection = false; LegionVehicleLimits.DoBanking = false;
                LegionVehicleLimits.DoVerticalAttractor = true;   // the attractor under test

                float FinalPitch(Vehicle type)
                {
                    FakeVehicleBody body = PlaneScenario.NewAirbornePlane(airspeed: 0f, orientation: NoseUp(20f));
                    body.Gravity = Vector3.Zero;                  // isolate attitude from translation
                    var v = new LegionVehicleController(body);
                    v.ProcessTypeChange(type);
                    for (int i = 0; i < 150; i++) { v.Step(PlaneScenario.Dt); body.Integrate(PlaneScenario.Dt); }
                    return PitchDeg(body.Orientation);
                }

                float planePitch = FinalPitch(Vehicle.TYPE_AIRPLANE);
                float carPitch = FinalPitch(Vehicle.TYPE_CAR);
                _out.WriteLine($"start pitch 20deg -> plane={planePitch:0.00}  car={carPitch:0.00}");

                Assert.True(planePitch > 15f, $"a plane should HOLD its nose-up attitude (no pitch restoration), pitch={planePitch:0.00}");
                Assert.True(carPitch < planePitch - 2f, $"a car should level off more than the plane: car={carPitch:0.00} plane={planePitch:0.00}");
            }
            finally { Restore(prev); }
        }

        // (e) Weathervane: a fast plane's nose swings toward its velocity direction (angular deflection eff 1,
        // TS 2). Speed-gated like the boat, so fly it fast at an off-nose heading.
        [Fact]
        public void Plane_Weathervanes_NoseTowardVelocity()
        {
            DoFlags prev = Save();
            try
            {
                LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = false;
                LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoVerticalAttractor = false;
                LegionVehicleLimits.DoLinearDeflection = false;   // hold velocity fixed; only the nose moves
                LegionVehicleLimits.DoBanking = false;
                LegionVehicleLimits.DoAngularDeflection = true;   // the term under test

                FakeVehicleBody body = PlaneScenario.NewAirbornePlane();   // nose +X (heading 0)
                body.Gravity = Vector3.Zero;
                body.LinearVelocity = new Vector3(15f, 15f, 0f);   // ~21 m/s, 45 deg off the nose (fires the speed gate)
                LegionVehicleController v = PlaneScenario.NewController(body);

                float maxHeading = 0f;
                const int frames = 120;
                for (int i = 0; i < frames; i++)
                {
                    v.Step(PlaneScenario.Dt);
                    body.Integrate(PlaneScenario.Dt);
                    float h = HeadingDeg(body.Orientation);
                    if (h > maxHeading) maxHeading = h;
                }
                _out.WriteLine($"plane weathervane: max heading={maxHeading:0.00} deg (toward the +45 velocity)");

                Assert.True(maxHeading > 10f, $"the plane nose should weathervane toward the velocity (+Y side), max heading={maxHeading:0.00}");
            }
            finally { Restore(prev); }
        }
    }
}
