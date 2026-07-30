/*
 * Legion Grid - boat vertical-attractor slice (M8 slice c). Locks in the in-world proof: rezzed at
 * 30 deg roll, the boat rights to under 8 deg within ~0.5 s and does not induce a yaw (heading is
 * left to the steering motor). Pure torque math through the IVehicleBody seam.
 */

using System.Collections.Generic;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class AttractorTests
    {
        private readonly ITestOutputHelper _out;

        public AttractorTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void BoatAttractor_RightsFrom30DegRoll_WithoutYaw()
        {
            // Rez rolled 30 deg about the body X axis (a roll, not a yaw).
            Quaternion rolled = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Utils.DEG_TO_RAD * 30f);
            FakeVehicleBody body = BoatScenario.NewFloatingBoat(orientation: rolled);
            LegionVehicleController v = BoatScenario.NewController(body);

            float startTilt = BoatScenario.TiltDegrees(body.Orientation);
            float startYaw = BoatScenario.YawDegrees(body.Orientation);

            const int frames = 12;                  // ~1.1 s; we assert the 0.5 s mark too
            int halfSec = (int)(0.5f / BoatScenario.Dt);
            var tilt = new List<float>(frames);
            var yaw = new List<float>(frames);

            for (int i = 0; i < frames; i++)
            {
                v.Step(BoatScenario.Dt);
                body.Integrate(BoatScenario.Dt);
                tilt.Add(BoatScenario.TiltDegrees(body.Orientation));
                yaw.Add(BoatScenario.YawDegrees(body.Orientation));
            }

            _out.WriteLine($"start tilt={startTilt:0.00} yaw={startYaw:0.00}");
            for (int i = 0; i < frames; i++)
                _out.WriteLine($"frame {i,2}  t={(i + 1) * BoatScenario.Dt:0.00}s  tilt={tilt[i]:0.00}  yaw={yaw[i]:0.00}");

            Assert.InRange(startTilt, 29f, 31f);                            // staged at ~30 deg

            // Rights to < 8 deg within ~0.5 s and stays down.
            Assert.True(tilt[halfSec] < 8f,
                $"tilt at 0.5 s should be < 8 deg, was {tilt[halfSec]:0.00}");
            Assert.True(tilt[frames - 1] < 8f,
                $"tilt should stay righted, final was {tilt[frames - 1]:0.00}");

            // The attractor rights roll without turning the boat: yaw barely moves.
            Assert.True(System.Math.Abs(yaw[frames - 1] - startYaw) < 2f,
                $"attractor should not induce yaw: {startYaw:0.00} -> {yaw[frames - 1]:0.00}");
        }
    }
}
