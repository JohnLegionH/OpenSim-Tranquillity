/*
 * Legion Grid - boat banking slice (M8, boat-completion term 3). Banking is roll->yaw coupling: when the
 * boat is rolled (banked), SimulateBankingToYaw sets Dynamics.BankingDirection, which the banking-turn
 * block inside SimulateMotors converts into a YAW turn - the boat leans into a turn and the lean tightens
 * the turn.
 *
 * Structural note: banking is CALLED inside the DoVerticalAttractor block (it uses the attractor's tilt
 * angle), so it can't be tested with the attractor off. Instead we measure banking's CONTRIBUTION by
 * contrast: the same rolled boat with DoBanking OFF (attractor just rights the roll, ~no yaw) vs ON (roll
 * is converted to yaw). Motors ON (the banking-turn block lives in SimulateMotors, no motor command so the
 * motors themselves are inert); friction/deflection OFF; gravity 0. Global Do* switches saved/restored.
 */

using System;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class BankingTests
    {
        private readonly ITestOutputHelper _out;
        public BankingTests(ITestOutputHelper output) => _out = output;

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

        // Attractor ON (banking needs it), motors ON (the banking-turn consumer), everything else that
        // touches yaw/velocity OFF so the only yaw source is banking.
        private static void IsolateBanking()
        {
            LegionVehicleLimits.DoVerticalAttractor = true;
            LegionVehicleLimits.DoMotors = true;
            LegionVehicleLimits.DoLinearFriction = false;
            LegionVehicleLimits.DoAngularFriction = false;
            LegionVehicleLimits.DoLinearDeflection = false;
            LegionVehicleLimits.DoAngularDeflection = false;
        }

        private static float HeadingDeg(Quaternion o) { Vector3 n = Vector3.UnitX * o; return (float)(Math.Atan2(n.Y, n.X) * 180.0 / Math.PI); }

        // Run a boat rolled `rollDeg` about its nose, moving forward; return (net yaw, final tilt, max tilt).
        private (float yaw, float finalTilt, float maxTilt) RunRolledBoat(float rollDeg)
        {
            Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollDeg * Utils.DEG_TO_RAD);
            FakeVehicleBody body = BoatScenario.NewFloatingBoat(orientation: roll);
            body.Gravity = Vector3.Zero;
            body.LinearVelocity = new Vector3(5f, 0f, 0f);          // forward speed feeds the dynamic banking half
            LegionVehicleController v = BoatScenario.NewController(body);   // boat preset: Eff=1, Mix=0.5, TS=0.2

            float maxTilt = BoatScenario.TiltDegrees(body.Orientation);
            const int frames = 60;
            for (int i = 0; i < frames; i++)
            {
                v.Step(BoatScenario.Dt);
                body.Integrate(BoatScenario.Dt);
                float t = BoatScenario.TiltDegrees(body.Orientation);
                if (t > maxTilt) maxTilt = t;
            }
            return (HeadingDeg(body.Orientation), BoatScenario.TiltDegrees(body.Orientation), maxTilt);
        }

        [Fact]
        public void BoatBanking_RollIsConvertedToYawTurn()
        {
            DoFlags prev = Save();
            try
            {
                IsolateBanking();

                LegionVehicleLimits.DoBanking = false;
                var off = RunRolledBoat(+20f);                     // attractor rights the roll; ~no yaw
                LegionVehicleLimits.DoBanking = true;
                var onPos = RunRolledBoat(+20f);                   // roll -> yaw
                var onNeg = RunRolledBoat(-20f);                   // opposite roll -> opposite yaw

                _out.WriteLine($"banking OFF (+roll): yaw={off.yaw:0.00} finalTilt={off.finalTilt:0.00}");
                _out.WriteLine($"banking ON  (+roll): yaw={onPos.yaw:0.00} finalTilt={onPos.finalTilt:0.00} maxTilt={onPos.maxTilt:0.00}");
                _out.WriteLine($"banking ON  (-roll): yaw={onNeg.yaw:0.00} finalTilt={onNeg.finalTilt:0.00} maxTilt={onNeg.maxTilt:0.00}");

                // (1) banking converts roll into yaw: ON yaws substantially more than OFF (attractor-only).
                Assert.True(Math.Abs(onPos.yaw) > Math.Abs(off.yaw) + 3f,
                    $"banking should turn a roll into yaw: off {off.yaw:0.00} vs on {onPos.yaw:0.00}");
                // (2) roll -> yaw SIGN coupling: reversing the roll reverses the yaw.
                Assert.True(Math.Sign(onPos.yaw) == -Math.Sign(onNeg.yaw) && Math.Abs(onNeg.yaw) > 3f,
                    $"opposite roll should yaw opposite: +roll {onPos.yaw:0.00}, -roll {onNeg.yaw:0.00}");
                // (3) does NOT capsize and self-rights: banking + attractor compose, tilt bounded + decreasing.
                Assert.True(onPos.maxTilt < 45f, $"boat must not capsize in a banked turn, maxTilt {onPos.maxTilt:0.00}");
                Assert.True(onPos.finalTilt < 20f, $"boat should self-right (attractor wins upright), finalTilt {onPos.finalTilt:0.00}");
            }
            finally { Restore(prev); }
        }

        [Fact]
        public void BoatBanking_NoYawWhenLevel()
        {
            DoFlags prev = Save();
            try
            {
                IsolateBanking();
                LegionVehicleLimits.DoBanking = true;
                var level = RunRolledBoat(0f);                     // no roll -> no banking
                _out.WriteLine($"level: yaw={level.yaw:0.00} finalTilt={level.finalTilt:0.00}");
                Assert.True(Math.Abs(level.yaw) < 2f, $"a level boat should not banking-yaw, yaw {level.yaw:0.00}");
            }
            finally { Restore(prev); }
        }
    }
}
