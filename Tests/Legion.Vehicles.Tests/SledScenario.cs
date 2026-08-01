/*
 * Legion Grid - shared sled-scenario setup for the vehicle-controller tests (M8 SLED).
 *
 * The sled is a ground vehicle like the car (buoyancy=0, rides the terrain), so it reuses FakeVehicleBody's
 * opt-in ground-clamp rig - identical staging to CarScenario, only the controller type differs (TYPE_SLED).
 * A sled has NO engine and NO steering: its preset motor timescales are 1000 (inert), it slides under
 * gravity via SimulateSledMovement (the term-4 downhill glide), and grips/glides via its friction
 * timescales. Angle helpers are shared with BoatScenario (pure geometry).
 */

using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;

namespace Legion.Vehicles.Tests
{
    internal static class SledScenario
    {
        // Match the in-world sledtest / cartest frame rate (~0.0909 s/frame).
        public const float Dt = 1f / 11f;

        public const float GroundLevel = 20f;
        public const float RideHeight = 0.5f;
        public const float RestingZ = GroundLevel + RideHeight;

        /// <summary>
        /// A sled at height <paramref name="z"/> (default = resting on the ground), level unless an
        /// orientation is given. Ground collision is ON by default so the non-buoyant sled settles on the
        /// terrain; pass groundCollision:false for free-body term tests (e.g. the downhill glide, which is
        /// driven purely by orientation + gravity and is independent of where the body sits).
        /// </summary>
        public static FakeVehicleBody NewGroundedSled(float? z = null, Quaternion? orientation = null, bool groundCollision = true)
        {
            return new FakeVehicleBody
            {
                Mass = 500f,
                InertiaDiagonal = new Vector3(1000f, 1000f, 1000f),
                Gravity = new Vector3(0f, 0f, -9.80665f),
                WaterLevel = 0f,                              // no water under a sled
                TerrainHeight = GroundLevel,
                RideHeight = RideHeight,
                GroundCollision = groundCollision,
                Position = new Vector3(128f, 128f, z ?? RestingZ),
                Orientation = orientation ?? Quaternion.Identity,
            };
        }

        /// <summary>Sled-preset controller driving <paramref name="body"/>.</summary>
        public static LegionVehicleController NewController(FakeVehicleBody body)
        {
            LegionVehicleLimits.DoSpikeDetection = false;
            var v = new LegionVehicleController(body);
            v.ProcessTypeChange(Vehicle.TYPE_SLED);
            return v;
        }
    }
}
