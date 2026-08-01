/*
 * Legion Grid - shared balloon-scenario setup for the vehicle-controller tests (M8 BALLOON).
 *
 * The balloon is the 5th and final SL type: it FLOATS in air on buoyancy 1.0 (gravity fully cancelled, like
 * the boat - but in air, not water). It is airborne, so - like the plane - it uses a free-body FakeVehicleBody
 * (GroundCollision OFF); unlike the plane it does not fall (buoyancy holds it) and needs no airspeed. Its hover
 * (HoverHeight 5, no hover-target flag) trims it to ~5 m above terrain; staging AT that equilibrium makes hover
 * neutral so buoyancy alone holds it - the "hangs in mid-air" signature. Everything else is floaty/damped
 * (long motor timescales, zero deflection, banking 0.05).
 */

using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;

namespace Legion.Vehicles.Tests
{
    internal static class BalloonScenario
    {
        public const float Dt = 1f / 11f;
        public const float GroundLevel = 200f;                 // a high plateau - the balloon hangs well up
        public const float HoverHeight = 5f;                   // the balloon preset's hover height
        public const float HoverEquilibriumZ = GroundLevel + HoverHeight;   // hover neutral here -> buoyancy alone holds it

        /// <summary>
        /// An AIRBORNE, neutrally-buoyant balloon at its hover equilibrium (hover ~ 0, so the TYPE_BALLOON
        /// preset's buoyancy 1.0 alone holds it). Free-body (no ground clamp - a balloon floats, never rests).
        /// Tests may override z / terrain / orientation.
        /// </summary>
        public static FakeVehicleBody NewFloatingBalloon(float? z = null, Quaternion? orientation = null)
        {
            return new FakeVehicleBody
            {
                Mass = 500f,
                InertiaDiagonal = new Vector3(1000f, 1000f, 1000f),
                Gravity = new Vector3(0f, 0f, -9.80665f),
                WaterLevel = 0f,
                TerrainHeight = GroundLevel,
                GroundCollision = false,                       // airborne - a balloon floats, it does not rest
                Position = new Vector3(128f, 128f, z ?? HoverEquilibriumZ),
                Orientation = orientation ?? Quaternion.Identity,
            };
        }

        /// <summary>Balloon-preset controller driving <paramref name="body"/>.</summary>
        public static LegionVehicleController NewController(FakeVehicleBody body)
        {
            LegionVehicleLimits.DoSpikeDetection = false;
            var v = new LegionVehicleController(body);
            v.ProcessTypeChange(Vehicle.TYPE_BALLOON);
            return v;
        }
    }
}
