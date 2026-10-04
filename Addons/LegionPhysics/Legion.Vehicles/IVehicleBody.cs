/*
 * Legion Grid - backend-agnostic vehicle controller (M8).
 *
 * IVehicleBody is the NEUTRAL seam between the extracted Halcyon vehicle math
 * (LegionVehicleController) and whatever physics engine hosts the body. No engine
 * types cross this line in either direction.
 *
 * THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 */

using OpenMetaverse;

namespace Legion.Vehicles
{
    /// <summary>
    /// The minimal physics surface the vehicle controller needs, per frame:
    ///
    ///   read : Position, Orientation, LinearVelocity, AngularVelocity, Mass,
    ///          InertiaDiagonal, Gravity, HasCollision, water height, terrain height
    ///   write: LinearVelocity/AngularVelocity setters (instant velocity change),
    ///          AddForce, AddTorque, KeepAwake
    ///
    /// SEMANTICS (these are what make the extracted math exact - see the seam table in
    /// LegionVehicleController.cs):
    ///  - LinearVelocity/AngularVelocity set = an instant velocity change applied NOW; a
    ///    subsequent get in the same frame MUST read back the just-written value (BulletSim's
    ///    ForceVelocity behaves this way - two velocity changes in one frame compound).
    ///  - AddForce(f)  = continuous central force consumed by the NEXT step
    ///    (deltaV = f / mass * dt). Bullet ApplyCentralForce == Jolt AddForce.
    ///  - AddTorque(t) = continuous torque consumed by the NEXT step
    ///    (deltaW = invInertia * t * dt). Bullet ApplyTorque == Jolt AddTorque.
    ///  - Orientation is the PRIM orientation (SL convention), not any engine-internal
    ///    axis-corrected body orientation.
    /// </summary>
    public interface IVehicleBody
    {
        Vector3 Position { get; }
        Quaternion Orientation { get; }

        /// <summary>World linear velocity. Set = instant velocity change (reads back within the frame).</summary>
        Vector3 LinearVelocity { get; set; }

        /// <summary>World angular velocity. Set = instant velocity change (reads back within the frame).</summary>
        Vector3 AngularVelocity { get; set; }

        /// <summary>Total dynamic mass (a linkset root reports the whole linkset's mass).</summary>
        float Mass { get; }

        /// <summary>Local principal moments of inertia (diagonal). Feeds the vertical attractor's
        /// tensor remap (BulletSim: ControllingPrim.Inertia from CalculateLocalInertia).</summary>
        Vector3 InertiaDiagonal { get; }

        /// <summary>World gravity vector, e.g. (0,0,-9.80665). The controller applies gravity
        /// MANUALLY (the host zeroes engine gravity on an active vehicle body).</summary>
        Vector3 Gravity { get; }

        /// <summary>Body currently has a contact (ground-vehicle gravity fudge; unused by boats).</summary>
        bool HasCollision { get; }

        /// <summary>Continuous world-frame central force, consumed by the next step.</summary>
        void AddForce(Vector3 force);

        /// <summary>Continuous world-frame torque, consumed by the next step.</summary>
        void AddTorque(Vector3 torque);

        /// <summary>Wake the body / prevent it idling out (BulletSim: ActivateIfPhysical).</summary>
        void KeepAwake();

        /// <summary>Terrain height at the given position's XY.</summary>
        float GetTerrainHeight(Vector3 pos);

        /// <summary>Water height at the given position's XY.</summary>
        float GetWaterLevel(Vector3 pos);
    }
}
