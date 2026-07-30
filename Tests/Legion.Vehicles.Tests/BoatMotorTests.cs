/*
 * Legion Grid - xunit coverage for the extracted Halcyon vehicle controller, boat slice (M8).
 *
 * These lock in the objective, machine-verifiable numbers (motor ramp, hover height, attractor
 * righting, steering rate) that were proven in-world for boat slices a-d, so CI catches a
 * regression in the extracted math without needing a scene. John smoke-tests feel in a viewer;
 * Balpien tunes; these guard the arithmetic.
 *
 * DETERMINISM NOTE: LegionVehicleController.CheckResetMotors reads DateTime.Now, and its return
 * (wall-clock elapsed) is the divisor for MitigateVelocitySpiking. In a tight test loop that
 * elapsed is ~microseconds, which would make spike mitigation fire spuriously and inject wall-clock
 * nondeterminism. BoatScenario.NewController disables the existing LegionVehicleLimits.DoSpikeDetection
 * debug switch so these unit tests stay deterministic. Flagged in the assessment as a candidate for a
 * future injectable-clock refactor of the controller (which would also let us test spike mitigation).
 */

using System.Collections.Generic;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class BoatMotorTests
    {
        private readonly ITestOutputHelper _out;

        public BoatMotorTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void BoatLinearMotor_RampsExponentiallyTowardTarget()
        {
            FakeVehicleBody body = BoatScenario.NewFloatingBoat();
            LegionVehicleController v = BoatScenario.NewController(body);

            const float target = 4f;          // 4 m/s forward (+X) linear motor
            const int frames = 60;
            var fwdSpeed = new List<float>(frames);

            for (int i = 0; i < frames; i++)
            {
                // Held throttle: re-issue the motor command each frame (an LSL script holding a
                // control does exactly this), so the motor sustains rather than decays away.
                v.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(target, 0f, 0f));
                v.Step(BoatScenario.Dt);
                body.Integrate(BoatScenario.Dt);
                fwdSpeed.Add(body.LinearVelocity.X);
            }

            for (int i = 0; i < frames; i++)
                _out.WriteLine($"frame {i,2}  t={(i + 1) * BoatScenario.Dt,5:0.00}s  vX={fwdSpeed[i]:0.0000}");

            // (1) Immediate but small engagement on the first frame - the motor kicks off the
            //     stiction floor, it does NOT jump straight to target. (In-world proof saw ~0.11
            //     early in the ramp; here vX crosses 0.11 around t=0.55 s.)
            Assert.InRange(fwdSpeed[0], 0.02f, 0.30f);

            // (2) Monotonic non-decreasing ramp (tiny float epsilon; a held motor must not stall
            //     or oscillate on the way up).
            for (int i = 1; i < frames; i++)
                Assert.True(fwdSpeed[i] >= fwdSpeed[i - 1] - 1e-4f,
                    $"speed dropped at frame {i}: {fwdSpeed[i - 1]:0.0000} -> {fwdSpeed[i]:0.0000}");

            // (3) Approaches the 4 m/s target from below and does not overshoot it. (In-world proof:
            //     reached ~3.75 m/s; here 4.64 s in.)
            float final = fwdSpeed[frames - 1];
            Assert.InRange(final, 3.5f, target + 0.05f);

            // (4) It is a genuine ramp: still short of target at 1 s, essentially there by the end.
            int oneSecond = (int)(1f / BoatScenario.Dt);
            Assert.True(fwdSpeed[oneSecond] < final,
                $"expected ramp: v@1s ({fwdSpeed[oneSecond]:0.0000}) should be below final ({final:0.0000})");
        }
    }
}
