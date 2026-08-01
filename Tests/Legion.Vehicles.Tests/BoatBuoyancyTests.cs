/*
 * Legion Grid - boat buoyancy safety (Cause-B of the reload data-loss fix).
 *
 * The production bug: on a region reload a boat could sink through the (non-colliding) water to the
 * seabed and vanish, because the old boat preset used buoyancy=0 and relied ENTIRELY on the hover
 * controller running every frame to hold it up against full gravity - any frame hover did not run
 * (e.g. right after reload, before the controller re-activates) let gravity win.
 *
 * The fix: boat preset buoyancy=1.0 (matching BulletSim's TYPE_BOAT), which cancels gravity outright
 * (ApplyGravity = gravity*(1-buoyancy) = 0). These tests lock that in:
 *   1. the preset value is 1.0;
 *   2. with hover DISABLED, a boat still does not sink - buoyancy alone holds it (under buoyancy=0 it
 *      would fall metres under full gravity).
 */

using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;
using Xunit;

namespace Legion.Vehicles.Tests
{
    public sealed class BoatBuoyancyTests
    {
        [Fact]
        public void BoatPreset_Buoyancy_IsOne()
        {
            FakeVehicleBody body = BoatScenario.NewFloatingBoat();
            LegionVehicleController v = BoatScenario.NewController(body);   // ProcessTypeChange(TYPE_BOAT)

            Assert.Equal(1.0f, v.GetFloatParam(VehFloatParam.Buoyancy), 3);
        }

        [Fact]
        public void BoatWithoutHover_DoesNotSink()
        {
            // Start exactly at the water plane, then DISABLE hover (timescale >= MaxHoverTimescale is the
            // controller's "off" sentinel). The only vertical influence left is gravity - which buoyancy=1.0
            // fully cancels. So the boat must hold its height, not sink.
            FakeVehicleBody body = BoatScenario.NewFloatingBoat(z: BoatScenario.WaterLevel);
            LegionVehicleController v = BoatScenario.NewController(body);
            v.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, LegionVehicleLimits.MaxHoverTimescale);

            float startZ = body.Position.Z;
            for (int i = 0; i < 120; i++)   // ~11 s
            {
                v.Step(BoatScenario.Dt);
                body.Integrate(BoatScenario.Dt);
            }

            // Under the old buoyancy=0 preset this would have fallen many metres. buoyancy=1.0 holds it:
            // no gravity, no hover, no motor -> it stays put (allow a small tolerance for solver noise).
            Assert.True(body.Position.Z > startZ - 0.1f,
                $"boat sank without hover: start {startZ:0.000} -> final {body.Position.Z:0.000} (buoyancy did not hold it)");
        }
    }
}
