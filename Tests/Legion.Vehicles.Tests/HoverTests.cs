/*
 * Legion Grid - boat hover slice (M8 slice b). Locks in the in-world proof: the boat settles to
 * water + ~0.449 m from both above and below, and holds there at rest. HoverWaterOnly + the
 * gravity/hover balance is the whole mechanism; no motor, no engine.
 */

using System.Collections.Generic;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Legion.Vehicles.Tests
{
    public sealed class HoverTests
    {
        private readonly ITestOutputHelper _out;

        public HoverTests(ITestOutputHelper output) => _out = output;

        private float SettleZ(float startZ, string label)
        {
            FakeVehicleBody body = BoatScenario.NewFloatingBoat(z: startZ);
            LegionVehicleController v = BoatScenario.NewController(body);

            const int frames = 120;                 // ~11 s
            var z = new List<float>(frames);
            for (int i = 0; i < frames; i++)
            {
                v.Step(BoatScenario.Dt);
                body.Integrate(BoatScenario.Dt);
                z.Add(body.Position.Z);
            }

            for (int i = 0; i < frames; i += 5)
                _out.WriteLine($"{label}  frame {i,3}  z={z[i]:0.000}  vZ={body.LinearVelocity.Z:0.000}");
            _out.WriteLine($"{label}  final z={z[frames - 1]:0.000}  vZ={body.LinearVelocity.Z:0.0000}");

            // Settled: last second essentially flat.
            float lastSec = 0f;
            int n = (int)(1f / BoatScenario.Dt);
            for (int i = frames - n; i < frames; i++) lastSec += z[i];
            lastSec /= n;
            Assert.True(System.Math.Abs(z[frames - 1] - lastSec) < 0.05f,
                $"{label}: not settled - final {z[frames - 1]:0.000} vs last-sec mean {lastSec:0.000}");

            return z[frames - 1];
        }

        [Fact]
        public void BoatHover_SettlesToWaterPlane_FromAbove()
        {
            float z = SettleZ(BoatScenario.WaterLevel + 3f, "above");
            Assert.InRange(z, BoatScenario.HoverEquilibriumZ - 0.15f, BoatScenario.HoverEquilibriumZ + 0.15f);
        }

        [Fact]
        public void BoatHover_SettlesToWaterPlane_FromBelow()
        {
            float z = SettleZ(BoatScenario.WaterLevel - 3f, "below");
            Assert.InRange(z, BoatScenario.HoverEquilibriumZ - 0.15f, BoatScenario.HoverEquilibriumZ + 0.15f);
        }
    }
}
