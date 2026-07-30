/*
 * Legion Grid - shared boat-scenario setup + geometry helpers for the vehicle-controller tests.
 *
 * Keeps the four boat-slice test files (motor / hover / attractor / steering) DRY: one place that
 * stages a floating boat, builds a boat-preset controller on a FakeVehicleBody, and extracts the
 * objective angles (tilt from vertical, yaw heading) the in-world proofs were judged on.
 */

using System;
using OpenMetaverse;
using Legion.Vehicles;
using Legion.Vehicles.Tests.Fakes;

namespace Legion.Vehicles.Tests
{
    internal static class BoatScenario
    {
        // In-world boattest stepped at ~0.0909 s/frame; match it so ramp/settle shapes line up
        // with the slice a-d proofs.
        public const float Dt = 1f / 11f;

        // Boat preset hovers to water + HoverHeight(0.5); the gravity/hover balance settles it a
        // hair below the target, which is the in-world "water + 0.449" equilibrium.
        public const float WaterLevel = 20f;
        public const float HoverEquilibriumZ = 20.449f;

        /// <summary>A boat floating at the given height (default = its hover equilibrium), level.</summary>
        public static FakeVehicleBody NewFloatingBoat(float? z = null, Quaternion? orientation = null)
        {
            return new FakeVehicleBody
            {
                Mass = 500f,
                InertiaDiagonal = new Vector3(1000f, 1000f, 1000f),
                Gravity = new Vector3(0f, 0f, -9.80665f),
                WaterLevel = WaterLevel,
                TerrainHeight = 0f,                                   // well below water; ground fix never fires
                Position = new Vector3(128f, 128f, z ?? HoverEquilibriumZ),
                Orientation = orientation ?? Quaternion.Identity,
            };
        }

        /// <summary>
        /// Boat-preset controller driving <paramref name="body"/>, throttle not yet applied.
        /// Disables the wall-clock-coupled spike detector so the run is deterministic (see the
        /// determinism note in BoatMotorTests).
        /// </summary>
        public static LegionVehicleController NewController(FakeVehicleBody body)
        {
            LegionVehicleLimits.DoSpikeDetection = false;
            var v = new LegionVehicleController(body);
            v.ProcessTypeChange(Vehicle.TYPE_BOAT);
            return v;
        }

        /// <summary>Angle (degrees) of the body's local +Z axis away from world +Z = tilt from level.</summary>
        public static float TiltDegrees(Quaternion orientation)
        {
            Vector3 localUp = Vector3.UnitZ * orientation;           // local +Z expressed in world
            float cos = Utils.Clamp(localUp.Z, -1f, 1f);
            return (float)(Math.Acos(cos) * 180.0 / Math.PI);
        }

        /// <summary>Yaw / heading (degrees) about world Z.</summary>
        public static float YawDegrees(Quaternion orientation)
        {
            orientation.GetEulerAngles(out _, out _, out float yaw);
            return (float)(yaw * 180.0 / Math.PI);
        }
    }
}
