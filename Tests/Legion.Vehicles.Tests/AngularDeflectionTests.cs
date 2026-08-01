/*
 * Legion Grid - boat angular-deflection slice (M8, boat-completion term 2). The "weathervane": when the
 * boat's velocity diverges from its heading, angular deflection applies a TORQUE that swings the NOSE
 * toward the velocity direction (the boat noses into where it's actually going). Complementary to linear
 * deflection (term 1), which rotates the VELOCITY toward the nose.
 *
 * Isolation: hold the velocity FIXED and rotate only the nose - so disable linear deflection (which would
 * move the velocity), motors, friction, attractor, banking; zero gravity. Angular deflection is then the
 * only thing changing the boat's heading. The Do* switches are process-wide statics (assembly already
 * disables test parallelization in LinearDeflectionTests.cs); saved + restored per test.
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
    public sealed class AngularDeflectionTests
    {
        private readonly ITestOutputHelper _out;
        public AngularDeflectionTests(ITestOutputHelper output) => _out = output;

        private struct DoFlags { public bool Motors, LinFric, AngFric, VAttract, LinDefl, Banking; }

        private static DoFlags IsolateAngularDeflection()
        {
            var prev = new DoFlags {
                Motors = LegionVehicleLimits.DoMotors, LinFric = LegionVehicleLimits.DoLinearFriction,
                AngFric = LegionVehicleLimits.DoAngularFriction, VAttract = LegionVehicleLimits.DoVerticalAttractor,
                LinDefl = LegionVehicleLimits.DoLinearDeflection, Banking = LegionVehicleLimits.DoBanking,
            };
            LegionVehicleLimits.DoMotors = false;
            LegionVehicleLimits.DoLinearFriction = false;
            LegionVehicleLimits.DoAngularFriction = false;
            LegionVehicleLimits.DoVerticalAttractor = false;
            LegionVehicleLimits.DoLinearDeflection = false;    // hold velocity fixed; nose moves, not velocity
            LegionVehicleLimits.DoBanking = false;
            LegionVehicleLimits.DoAngularDeflection = true;    // the term under test
            return prev;
        }
        private static void Restore(DoFlags p)
        {
            LegionVehicleLimits.DoMotors = p.Motors; LegionVehicleLimits.DoLinearFriction = p.LinFric;
            LegionVehicleLimits.DoAngularFriction = p.AngFric; LegionVehicleLimits.DoVerticalAttractor = p.VAttract;
            LegionVehicleLimits.DoLinearDeflection = p.LinDefl; LegionVehicleLimits.DoBanking = p.Banking;
        }

        // Heading of the nose (+X axis) in the XY plane, degrees (0 = +X, +90 = +Y).
        private static float HeadingDeg(Quaternion o) { Vector3 n = Vector3.UnitX * o; return (float)(Math.Atan2(n.Y, n.X) * 180.0 / Math.PI); }

        [Fact]
        public void BoatAngularDeflection_SwingsNoseTowardVelocity()
        {
            DoFlags prev = IsolateAngularDeflection();
            try
            {
                FakeVehicleBody body = BoatScenario.NewFloatingBoat();   // nose +X (heading 0), on water
                body.Gravity = Vector3.Zero;
                LegionVehicleController v = BoatScenario.NewController(body);   // boat preset: ang-defl eff 0.5, TS 5

                // Velocity 45 deg off the nose (toward +Y), held fixed. Angular deflection is SPEED-GATED
                // (per-frame torque must exceed ThresholdAngularMotorDeltaV = PI/512), so it only engages
                // above ~11 m/s at 45 deg - a HIGH-SPEED weathervane, matching the reference exactly. Use a
                // fast slide so the term fires; below that it correctly stays inert (see the no-op case is
                // low-speed-aligned; the speed gate is a real boat characteristic flagged to John).
                body.LinearVelocity = new Vector3(15f, 15f, 0f);   // speed ~21.2 m/s, 45 deg off the nose
                const float velDir = 45f;

                var heading = new List<float>();
                const int frames = 200;
                for (int i = 0; i < frames; i++)
                {
                    v.Step(BoatScenario.Dt);
                    body.Integrate(BoatScenario.Dt);
                    heading.Add(HeadingDeg(body.Orientation));
                }
                for (int i = 0; i < frames; i += 25)
                    _out.WriteLine($"frame {i,3} heading={heading[i]:0.00} (noseVsVel={velDir - heading[i]:0.00})");
                float maxHeading = 0f, closest = 45f;
                foreach (float h in heading) { if (h > maxHeading) maxHeading = h; if (Math.Abs(velDir - h) < closest) closest = Math.Abs(velDir - h); }
                _out.WriteLine($"start 0 -> end {heading[frames-1]:0.00}; max heading {maxHeading:0.00}; closest-to-velDir gap {closest:0.00} deg");

                // (1) nose swings TOWARD the velocity: heading moves off 0 toward +45 (the velocity side).
                Assert.True(maxHeading > 10f, $"nose should weathervane toward the velocity (+Y side): max heading {maxHeading:0.00}");
                // (2) correct SIGN: it turns toward the velocity (+Y), never significantly the wrong way.
                for (int i = 0; i < frames; i++)
                    Assert.True(heading[i] > -2f, $"nose must not turn AWAY from velocity at frame {i}: {heading[i]:0.00}");
                // (3) it CLOSES the gap toward the velocity direction (nose gets near 45, not stuck at 0).
                Assert.True(closest < 15f, $"nose should get close to the velocity direction: closest gap {closest:0.00} deg");
            }
            finally { Restore(prev); }
        }

        [Fact]
        public void BoatAngularDeflection_NoOpWhenHeadingAlignsWithVelocity()
        {
            DoFlags prev = IsolateAngularDeflection();
            try
            {
                FakeVehicleBody body = BoatScenario.NewFloatingBoat();   // nose +X
                body.Gravity = Vector3.Zero;
                LegionVehicleController v = BoatScenario.NewController(body);

                body.LinearVelocity = new Vector3(4f, 0f, 0f);           // velocity already down the nose (+X)
                for (int i = 0; i < 60; i++) { v.Step(BoatScenario.Dt); body.Integrate(BoatScenario.Dt); }

                float end = HeadingDeg(body.Orientation);
                _out.WriteLine($"aligned end heading={end:0.000}");
                // No divergence -> no weathervane torque -> heading stays put.
                Assert.True(Math.Abs(end) < 1.0f, $"aligned heading must stay ~0, was {end:0.000}");
            }
            finally { Restore(prev); }
        }
    }
}
