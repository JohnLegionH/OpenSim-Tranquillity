/*
 * Legion Grid - deterministic test double for the vehicle-controller physics seam (M8).
 *
 * FakeVehicleBody implements Legion.Vehicles.IVehicleBody with a trivial, fully deterministic
 * integrator so LegionVehicleController can be driven in xunit with NO physics engine. It mirrors
 * the contract JoltVehicleBody honors in-world:
 *
 *   - velocity SET = an instant velocity change that reads back within the same frame (a plain
 *     field, so two velocity changes in one Step() compound - exactly BulletSim ForceVelocity /
 *     Jolt SetLinearVelocity semantics).
 *   - AddForce(f)  accumulates a central force; Integrate(dt) consumes it as deltaV = f/mass * dt.
 *   - AddTorque(t) accumulates a torque; Integrate(dt) consumes it as deltaW = t/inertia * dt
 *     (per-axis, diagonal inertia).
 *
 * The host loop each frame is:  controller.Step(dt);  body.Integrate(dt);
 * (controller runs BEFORE the engine step, just as the real host steps an active vehicle).
 *
 * THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 */

using OpenMetaverse;

namespace Legion.Vehicles.Tests.Fakes
{
    /// <summary>
    /// Deterministic, engine-free <see cref="IVehicleBody"/> for unit-testing the controller math.
    /// All pose/mass/environment fields are settable so a test can stage any starting condition.
    /// </summary>
    public sealed class FakeVehicleBody : IVehicleBody
    {
        // ---- pose + motion (interface needs get; we expose set for test staging) ----
        public Vector3 Position { get; set; }
        public Quaternion Orientation { get; set; } = Quaternion.Identity;

        /// <summary>World linear velocity. Set = instant change, reads back within the frame.</summary>
        public Vector3 LinearVelocity { get; set; }

        /// <summary>World angular velocity. Set = instant change, reads back within the frame.</summary>
        public Vector3 AngularVelocity { get; set; }

        // ---- body constants ----
        public float Mass { get; set; } = 500f;
        public Vector3 InertiaDiagonal { get; set; } = new Vector3(1000f, 1000f, 1000f);
        public Vector3 Gravity { get; set; } = new Vector3(0f, 0f, -9.80665f);
        public bool HasCollision { get; set; }

        // ---- environment the controller queries ----
        public float TerrainHeight { get; set; } = 0f;
        public float WaterLevel { get; set; } = 20f;

        // ---- opt-in ground collision (models Jolt's terrain contact for non-buoyant vehicles) ----
        // OFF by default so the buoyant/free-body boat tests are unaffected. When ON, Integrate clamps
        // the body to rest at TerrainHeight + RideHeight, kills any downward velocity, and reports
        // HasCollision - the same floor the real physics engine gives a car that isn't held up by buoyancy.
        public bool GroundCollision { get; set; }
        public float RideHeight { get; set; } = 0f;

        // ---- diagnostics a test may assert on ----
        public int KeepAwakeCount { get; private set; }

        // ---- accumulators drained by Integrate(dt) ----
        private Vector3 _forceAccum;
        private Vector3 _torqueAccum;

        public void AddForce(Vector3 force) => _forceAccum += force;
        public void AddTorque(Vector3 torque) => _torqueAccum += torque;
        public void KeepAwake() => KeepAwakeCount++;
        public float GetTerrainHeight(Vector3 pos) => TerrainHeight;
        public float GetWaterLevel(Vector3 pos) => WaterLevel;

        /// <summary>
        /// Advance one frame: consume accumulated force/torque into velocity, then integrate pose
        /// (semi-implicit Euler). Call AFTER controller.Step(dt). Deterministic - no wall clock.
        /// </summary>
        public void Integrate(float dt)
        {
            // Linear: deltaV = f / m * dt
            if (Mass > 0f)
                LinearVelocity += (_forceAccum / Mass) * dt;

            // Angular: deltaW = invInertia (diagonal) * torque * dt
            Vector3 w = AngularVelocity;
            if (InertiaDiagonal.X > 0f) w.X += (_torqueAccum.X / InertiaDiagonal.X) * dt;
            if (InertiaDiagonal.Y > 0f) w.Y += (_torqueAccum.Y / InertiaDiagonal.Y) * dt;
            if (InertiaDiagonal.Z > 0f) w.Z += (_torqueAccum.Z / InertiaDiagonal.Z) * dt;
            AngularVelocity = w;

            // Advance pose
            Position += LinearVelocity * dt;
            if (AngularVelocity != Vector3.Zero)
            {
                // q' = q + 0.5 * (0, w) * q * dt, renormalized
                Quaternion spin = new Quaternion(AngularVelocity.X, AngularVelocity.Y, AngularVelocity.Z, 0f);
                Quaternion dq = spin * Orientation;
                Quaternion q = Orientation;
                q.X += 0.5f * dq.X * dt;
                q.Y += 0.5f * dq.Y * dt;
                q.Z += 0.5f * dq.Z * dt;
                q.W += 0.5f * dq.W * dt;
                q.Normalize();
                Orientation = q;
            }

            // Ground contact: a non-buoyant vehicle falls under gravity until the terrain stops it.
            // Model that floor so the car rests deterministically instead of falling forever.
            if (GroundCollision)
            {
                float floor = TerrainHeight + RideHeight;
                if (Position.Z <= floor)
                {
                    Position = new Vector3(Position.X, Position.Y, floor);
                    if (LinearVelocity.Z < 0f)
                        LinearVelocity = new Vector3(LinearVelocity.X, LinearVelocity.Y, 0f);
                    HasCollision = true;
                }
                else
                {
                    HasCollision = false;
                }
            }

            _forceAccum = Vector3.Zero;
            _torqueAccum = Vector3.Zero;
        }
    }
}
