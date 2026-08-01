/*
 * Legion Grid - boat linear-deflection slice (M8, boat-completion term 1). Locks in the "tracking bite":
 * when the boat's velocity diverges from its nose heading, linear deflection rotates the velocity vector
 * back toward the nose (preserving speed) so the hull follows its heading instead of sliding sideways.
 *
 * Isolation: the boat preset's OTHER terms (motors, friction - Y-friction TS 0.5 also fights sideways -,
 * attractor) would confound the measurement, so we turn them OFF via the global LegionVehicleLimits.Do*
 * switches (saved + restored per test) and zero gravity, leaving deflection the only XY-plane force. The
 * Do* switches are process-wide statics, so this assembly disables test parallelization (below).
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

// The controller's enable switches are global statics; serialize tests so isolating deflection in one
// test can't race another test/class reading DoMotors etc.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Legion.Vehicles.Tests
{
    public sealed class LinearDeflectionTests
    {
        private readonly ITestOutputHelper _out;
        public LinearDeflectionTests(ITestOutputHelper output) => _out = output;

        private struct DoFlags { public bool Motors, LinFric, AngFric, VAttract, AngDefl, Banking; }

        // Turn off every term that touches XY velocity except linear deflection; return the prior values.
        private static DoFlags IsolateDeflection()
        {
            var prev = new DoFlags {
                Motors = LegionVehicleLimits.DoMotors, LinFric = LegionVehicleLimits.DoLinearFriction,
                AngFric = LegionVehicleLimits.DoAngularFriction, VAttract = LegionVehicleLimits.DoVerticalAttractor,
                AngDefl = LegionVehicleLimits.DoAngularDeflection, Banking = LegionVehicleLimits.DoBanking,
            };
            LegionVehicleLimits.DoMotors = false;
            LegionVehicleLimits.DoLinearFriction = false;
            LegionVehicleLimits.DoAngularFriction = false;
            LegionVehicleLimits.DoVerticalAttractor = false;
            LegionVehicleLimits.DoAngularDeflection = false;   // not ported yet, explicit
            LegionVehicleLimits.DoBanking = false;
            LegionVehicleLimits.DoLinearDeflection = true;     // the term under test
            return prev;
        }
        private static void Restore(DoFlags p)
        {
            LegionVehicleLimits.DoMotors = p.Motors; LegionVehicleLimits.DoLinearFriction = p.LinFric;
            LegionVehicleLimits.DoAngularFriction = p.AngFric; LegionVehicleLimits.DoVerticalAttractor = p.VAttract;
            LegionVehicleLimits.DoAngularDeflection = p.AngDefl; LegionVehicleLimits.DoBanking = p.Banking;
        }

        // Angle (deg) of the XY velocity away from the nose (+X). 0 = tracking straight; 90 = pure sideways.
        private static float SideAngle(Vector3 v) => (float)(Math.Atan2(Math.Abs(v.Y), v.X) * 180.0 / Math.PI);
        private static float SpeedXY(Vector3 v) => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);

        [Fact]
        public void BoatLinearDeflection_RedirectsSidewaysVelocityTowardNose()
        {
            DoFlags prev = IsolateDeflection();
            try
            {
                FakeVehicleBody body = BoatScenario.NewFloatingBoat();   // faces +X (identity), on water
                body.Gravity = Vector3.Zero;                             // keep it in the XY plane
                LegionVehicleController v = BoatScenario.NewController(body);   // boat preset: defl eff 0.5, TS 3

                // Moving 45 deg off the nose: equal forward (+X) and sideways (+Y) - a hull "sliding".
                body.LinearVelocity = new Vector3(3f, 3f, 0f);
                float startAngle = SideAngle(body.LinearVelocity);       // ~45
                float startSpeed = SpeedXY(body.LinearVelocity);

                var ang = new List<float>();
                const int frames = 60;
                for (int i = 0; i < frames; i++)
                {
                    v.Step(BoatScenario.Dt);
                    body.Integrate(BoatScenario.Dt);
                    ang.Add(SideAngle(body.LinearVelocity));
                }
                for (int i = 0; i < frames; i += 10)
                    _out.WriteLine($"frame {i,2} sideAngle={ang[i]:0.00}");
                _out.WriteLine($"start {startAngle:0.00} deg speed {startSpeed:0.000} -> end {ang[frames-1]:0.00} deg " +
                               $"v=({body.LinearVelocity.X:0.000},{body.LinearVelocity.Y:0.000}) speedXY {SpeedXY(body.LinearVelocity):0.000}");

                // (1) velocity rotates TOWARD the nose (angle from +X shrinks) - the tracking bite.
                Assert.True(ang[frames - 1] < startAngle - 5f,
                    $"deflection should turn velocity toward the nose: {startAngle:0.0} -> {ang[frames-1]:0.0} deg");
                // (2) monotonic: never pushes velocity AWAY from the nose (wrong sign) and no oscillation.
                for (int i = 1; i < frames; i++)
                    Assert.True(ang[i] <= ang[i - 1] + 0.25f,
                        $"side-angle must not grow at frame {i}: {ang[i-1]:0.00} -> {ang[i]:0.00}");
                // (3) sign check on components: sideways Y shrinks, forward X grows.
                Assert.True(body.LinearVelocity.Y < 3f && body.LinearVelocity.X > 3f,
                    $"expected vX up / vY down, got ({body.LinearVelocity.X:0.00},{body.LinearVelocity.Y:0.00})");
                // (4) deflection ROTATES, it doesn't accelerate/brake: XY speed preserved.
                Assert.InRange(SpeedXY(body.LinearVelocity), startSpeed - 0.25f, startSpeed + 0.10f);
            }
            finally { Restore(prev); }
        }

        [Fact]
        public void BoatLinearDeflection_NoOpWhenAlreadyAlignedWithNose()
        {
            DoFlags prev = IsolateDeflection();
            try
            {
                FakeVehicleBody body = BoatScenario.NewFloatingBoat();
                body.Gravity = Vector3.Zero;
                LegionVehicleController v = BoatScenario.NewController(body);

                body.LinearVelocity = new Vector3(4f, 0f, 0f);           // already straight down the nose
                for (int i = 0; i < 30; i++) { v.Step(BoatScenario.Dt); body.Integrate(BoatScenario.Dt); }

                _out.WriteLine($"aligned end v=({body.LinearVelocity.X:0.000},{body.LinearVelocity.Y:0.000})");
                // No sideways velocity gets introduced and forward speed is essentially untouched (deflection ~0).
                Assert.True(Math.Abs(body.LinearVelocity.Y) < 0.05f,
                    $"aligned velocity must stay on-axis, vY={body.LinearVelocity.Y:0.000}");
                Assert.InRange(body.LinearVelocity.X, 3.9f, 4.05f);
            }
            finally { Restore(prev); }
        }
    }
}
