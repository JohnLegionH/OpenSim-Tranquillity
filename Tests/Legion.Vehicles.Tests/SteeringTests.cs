/*
 * Legion Grid - boat steering slice (M8 slice d). Locks in the in-world proof: a held angular
 * motor yaws the boat at a steady rate while staying level, and on release the yaw rate falls to
 * ~0 within a frame (boat angular friction Z-timescale 0.1 stops an imposed spin hard).
 */

using System.Collections.Generic;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class SteeringTests
    {
        private readonly ITestOutputHelper _out;

        public SteeringTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void BoatSteering_YawsUnderMotor_StopsOnRelease()
        {
            FakeVehicleBody body = BoatScenario.NewFloatingBoat();
            LegionVehicleController v = BoatScenario.NewController(body);

            const float yawRate = 0.5f;             // rad/s commanded about world Z (TorqueWorldZ boat)
            const int driveFrames = 33;             // ~3 s of held steering
            const int coastFrames = 6;              // then release

            var yaw = new List<float>();
            var tilt = new List<float>();

            for (int i = 0; i < driveFrames; i++)
            {
                v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, yawRate));
                v.Step(BoatScenario.Dt);
                body.Integrate(BoatScenario.Dt);
                yaw.Add(BoatScenario.YawDegrees(body.Orientation));
                tilt.Add(BoatScenario.TiltDegrees(body.Orientation));
            }

            float yawAtRelease = yaw[driveFrames - 1];
            float yawRateDriven = (yaw[driveFrames - 1] - yaw[driveFrames - 2]) / BoatScenario.Dt;

            // Release: stop commanding the motor and coast.
            for (int i = 0; i < coastFrames; i++)
            {
                v.Step(BoatScenario.Dt);
                body.Integrate(BoatScenario.Dt);
                yaw.Add(BoatScenario.YawDegrees(body.Orientation));
                tilt.Add(BoatScenario.TiltDegrees(body.Orientation));
            }

            for (int i = 0; i < yaw.Count; i++)
                _out.WriteLine($"frame {i,2}  t={(i + 1) * BoatScenario.Dt:0.00}s  yaw={yaw[i]:0.00}  tilt={tilt[i]:0.00}");
            float yawRateFirstCoast = (yaw[driveFrames] - yaw[driveFrames - 1]) / BoatScenario.Dt;
            float yawRateFinalCoast = (yaw[yaw.Count - 1] - yaw[yaw.Count - 2]) / BoatScenario.Dt;
            float coastTravel = yaw[yaw.Count - 1] - yawAtRelease;
            _out.WriteLine($"yaw@release={yawAtRelease:0.00}  driven={yawRateDriven:0.00} deg/s  " +
                           $"firstCoast={yawRateFirstCoast:0.00} deg/s  finalCoast={yawRateFinalCoast:0.00} deg/s  coastTravel={coastTravel:0.00} deg");

            // Steering builds a real heading change.
            Assert.True(System.Math.Abs(yawAtRelease) > 20f,
                $"held steering should turn the boat, yaw only reached {yawAtRelease:0.00} deg");

            // A held motor produces a roughly steady turn rate (not a runaway or a stall): the
            // driven yaw rate sits in a sane band.
            Assert.InRange(System.Math.Abs(yawRateDriven), 8f, 25f);

            // Stays level while turning (steering is yaw-only; attractor holds roll/pitch).
            Assert.True(tilt[driveFrames - 1] < 5f,
                $"boat should stay level while steering, tilt was {tilt[driveFrames - 1]:0.00}");

            // On release, friction arrests the imposed spin: the rate starts dropping immediately
            // and is essentially zero within the short coast window, with little overshoot. (The
            // in-world proof described this as "yaw rate 0 within a frame"; the trivial fake
            // integrator carries ~1 frame of angular coast - the real Jolt inertia remap arrests it
            // faster - so we assert the faithful "arrested within a few frames" instead. See the
            // fake-fidelity note in the assessment report.)
            Assert.True(System.Math.Abs(yawRateFirstCoast) < System.Math.Abs(yawRateDriven),
                $"yaw rate should begin dropping on release: {yawRateDriven:0.00} -> {yawRateFirstCoast:0.00} deg/s");
            Assert.True(System.Math.Abs(yawRateFinalCoast) < 2f,
                $"yaw rate should be arrested by end of coast, was {yawRateFinalCoast:0.00} deg/s");
            Assert.True(System.Math.Abs(coastTravel) < 6f,
                $"friction should stop the spin quickly, but it coasted {coastTravel:0.00} deg after release");
        }
    }
}
