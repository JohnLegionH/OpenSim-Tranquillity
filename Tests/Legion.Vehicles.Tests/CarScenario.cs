/*
 * Legion Grid - shared car-scenario setup for the vehicle-controller tests (M8 CAR).
 *
 * The ground-vehicle analog of BoatScenario: stages a car resting on terrain and builds a TYPE_CAR
 * controller on a FakeVehicleBody. The car preset uses buoyancy=0 (gravity NOT cancelled - unlike the
 * boat), so the body would fall forever in the free-body fake; the scenario turns on FakeVehicleBody's
 * opt-in ground clamp (models Jolt's terrain contact) so the car rests deterministically at
 * TerrainHeight + RideHeight. Angle helpers are shared with BoatScenario (pure geometry).
 */

using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;

namespace Legion.Vehicles.Tests
{
    internal static class CarScenario
    {
        // Match the in-world cartest / boattest frame rate (~0.0909 s/frame) so ramp/settle shapes line up.
        public const float Dt = 1f / 11f;

        public const float GroundLevel = 20f;
        public const float RideHeight = 0.5f;                 // body half-height: where a car's origin rests on the ground
        public const float RestingZ = GroundLevel + RideHeight;

        /// <summary>
        /// A car at height <paramref name="z"/> (default = resting on the ground), level unless an
        /// orientation is given. Ground collision is ON by default so a non-buoyant car settles on the
        /// terrain instead of falling through; pass groundCollision:false to prove it is not floaty.
        /// </summary>
        public static FakeVehicleBody NewGroundedCar(float? z = null, Quaternion? orientation = null, bool groundCollision = true)
        {
            return new FakeVehicleBody
            {
                Mass = 500f,
                InertiaDiagonal = new Vector3(1000f, 1000f, 1000f),
                Gravity = new Vector3(0f, 0f, -9.80665f),
                WaterLevel = 0f,                              // no water under a car
                TerrainHeight = GroundLevel,
                RideHeight = RideHeight,
                GroundCollision = groundCollision,
                Position = new Vector3(128f, 128f, z ?? RestingZ),
                Orientation = orientation ?? Quaternion.Identity,
            };
        }

        /// <summary>Car-preset controller driving <paramref name="body"/>, throttle not yet applied.</summary>
        public static LegionVehicleController NewController(FakeVehicleBody body)
        {
            LegionVehicleLimits.DoSpikeDetection = false;
            var v = new LegionVehicleController(body);
            v.ProcessTypeChange(Vehicle.TYPE_CAR);
            return v;
        }
    }
}
