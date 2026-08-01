/*
 * Legion Grid - sled movement slice (M8, boat-completion term 4, PARITY COMPLETENESS). SimulateSledMovement
 * is a nose-declination gravity glide (a sled slides forward downhill). It is GATED Type==Sled in Step, so
 * it NEVER runs for a boat - this term is ported purely so the extracted controller matches the BulletSim
 * reference exactly, not for boat behaviour. These tests prove (a) it's a NO-OP for a boat (the gate) and
 * (b) the ported math actually glides a sled, so the term is real when a sled type is eventually built.
 *
 * Isolation: all other XY-touching terms off (motors, friction, deflection, attractor, banking). Sled has
 * no Do* switch (it's type-gated), so a boat gets nothing regardless. Gravity stays ON - the sled force is
 * -_body.Gravity.Z * 3, so zeroing gravity would zero the term. Global Do* switches saved/restored.
 */

using System;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class SledMovementTests
    {
        private readonly ITestOutputHelper _out;
        public SledMovementTests(ITestOutputHelper output) => _out = output;

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
        private static void IsolateSled()
        {
            LegionVehicleLimits.DoMotors = false; LegionVehicleLimits.DoLinearFriction = false;
            LegionVehicleLimits.DoAngularFriction = false; LegionVehicleLimits.DoVerticalAttractor = false;
            LegionVehicleLimits.DoLinearDeflection = false; LegionVehicleLimits.DoAngularDeflection = false;
            LegionVehicleLimits.DoBanking = false;
            LegionVehicleLimits.DoSpikeDetection = false;
        }

        // World-X velocity after N frames of a vehicle of `type` pitched 30 deg nose-DOWN (from rest).
        // Free-body (groundCollision off) high above terrain: the glide is driven purely by orientation +
        // gravity, independent of staging - so the sled comes from SledScenario, the controller type varies
        // (the boat case proves the Type==Sled gate).
        private static float RunPitchedNoseDown(Vehicle type)
        {
            Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30f * Utils.DEG_TO_RAD);   // nose down
            FakeVehicleBody body = SledScenario.NewGroundedSled(z: SledScenario.GroundLevel + 20f, orientation: pitch, groundCollision: false);
            var v = new LegionVehicleController(body);
            v.ProcessTypeChange(type);
            body.LinearVelocity = Vector3.Zero;
            for (int i = 0; i < 20; i++) { v.Step(SledScenario.Dt); body.Integrate(SledScenario.Dt); }
            return body.LinearVelocity.X;
        }

        [Fact]
        public void SledMovement_GlidesForwardForSled_But_NoOpForBoat()
        {
            DoFlags prev = Save();
            try
            {
                IsolateSled();
                float sledX = RunPitchedNoseDown(Vehicle.TYPE_SLED);
                float boatX = RunPitchedNoseDown(Vehicle.TYPE_BOAT);
                _out.WriteLine($"nose-down 30deg: sled worldX={sledX:0.000}  boat worldX={boatX:0.000}");

                // (a) the ported math works: a SLED glides forward down the slope.
                Assert.True(sledX > 0.5f, $"sled should glide forward on a downslope, worldX={sledX:0.000}");
                // (b) the Type==Sled gate: a BOAT gets NO sled force at all.
                Assert.True(Math.Abs(boatX) < 0.05f, $"boat must NOT get sled glide (gated off), worldX={boatX:0.000}");
            }
            finally { Restore(prev); }
        }

        [Fact]
        public void SledMovement_NoOpForLevelSled()
        {
            DoFlags prev = Save();
            try
            {
                IsolateSled();
                FakeVehicleBody body = SledScenario.NewGroundedSled(z: SledScenario.GroundLevel + 20f, groundCollision: false);   // level (identity) -> probe.Z ~0
                var v = SledScenario.NewController(body);
                body.LinearVelocity = Vector3.Zero;
                for (int i = 0; i < 20; i++) { v.Step(SledScenario.Dt); body.Integrate(SledScenario.Dt); }
                _out.WriteLine($"level sled worldX={body.LinearVelocity.X:0.000}");
                // No declination -> below ThresholdDeflectionAngle -> no glide.
                Assert.True(Math.Abs(body.LinearVelocity.X) < 0.05f, $"a level sled should not glide, worldX={body.LinearVelocity.X:0.000}");
            }
            finally { Restore(prev); }
        }
    }
}
