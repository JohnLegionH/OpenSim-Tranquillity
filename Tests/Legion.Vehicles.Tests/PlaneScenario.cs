/*
 * Legion Grid - shared plane-scenario setup for the vehicle-controller tests (M8 AIRPLANE).
 *
 * The airplane is the first fully AIRBORNE type: it does NOT rest on a surface, so - unlike CarScenario/
 * SledScenario - it does NOT use FakeVehicleBody's ground clamp (GroundCollision stays OFF = free-body).
 * Buoyancy is 0 (full gravity), so a plane with no airspeed/thrust falls; it stays up only via thrust +
 * lift (SimulateLinearDeflection redirects forward speed along a pitched-up nose, and the plane's flags
 * omit NoDeflectionUp so that redirect can go upward). Staged high in the air with optional airspeed.
 */

using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;

namespace Legion.Vehicles.Tests
{
    internal static class PlaneScenario
    {
        public const float Dt = 1f / 11f;
        public const float Altitude = 200f;                  // high in the air - a plane flies, never rests

        /// <summary>
        /// An AIRBORNE plane at altitude, with an optional WORLD-horizontal airspeed (+X) and orientation.
        /// Free-body (no ground clamp): the plane flies. Tests may override LinearVelocity for off-axis /
        /// along-nose cases.
        /// </summary>
        public static FakeVehicleBody NewAirbornePlane(float airspeed = 0f, Quaternion? orientation = null, float? z = null)
        {
            return new FakeVehicleBody
            {
                Mass = 500f,
                InertiaDiagonal = new Vector3(1000f, 1000f, 1000f),
                Gravity = new Vector3(0f, 0f, -9.80665f),
                WaterLevel = 0f,
                TerrainHeight = 0f,
                GroundCollision = false,                     // airborne - the plane does not rest on a surface
                Position = new Vector3(128f, 128f, z ?? Altitude),
                Orientation = orientation ?? Quaternion.Identity,
                LinearVelocity = new Vector3(airspeed, 0f, 0f),   // world-horizontal airspeed
            };
        }

        /// <summary>Airplane-preset controller driving <paramref name="body"/>.</summary>
        public static LegionVehicleController NewController(FakeVehicleBody body)
        {
            LegionVehicleLimits.DoSpikeDetection = false;
            var v = new LegionVehicleController(body);
            v.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);
            return v;
        }
    }
}
