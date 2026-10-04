// Legion Grid - the Jolt implementation of the neutral vehicle seam (M8 Task 2).
//
// JoltVehicleBody adapts one JoltPrim's live backend body to Legion.Vehicles.IVehicleBody so the
// extracted Halcyon controller (LegionVehicleController) can drive it without knowing Jolt exists.
// The BulletSim reference reads Force* properties LIVE from the engine; here BeginFrame() snapshots
// the body state once per frame (nothing moves between controller reads - the controller runs
// BEFORE the step), and every velocity WRITE updates the snapshot AND pushes through, preserving
// BulletSim's read-back-what-you-wrote semantics (two velocity changes in one frame compound).
//
// Orientation handed to the controller is the PRIM orientation (axis-correction removed), matching
// what BulletSim's ForceOrientation reports.

using OpenMetaverse;
using Legion.Physics;
using Legion.Vehicles;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    internal sealed class JoltVehicleBody : IVehicleBody
    {
        private readonly LegionJoltScene _module;
        private readonly ILegionPhysicsBackend _backend;
        private readonly JoltPrim _prim;

        // Per-frame snapshot (BeginFrame); velocity writes keep it current within the frame.
        private Vector3 _position;
        private Quaternion _orientation;
        private Vector3 _linVel;
        private Vector3 _angVel;

        internal JoltVehicleBody(LegionJoltScene module, ILegionPhysicsBackend backend, JoltPrim prim)
        {
            _module = module;
            _backend = backend;
            _prim = prim;
        }

        // Refresh the snapshot from the live body. False = no live body this frame (skip the step).
        internal bool BeginFrame()
        {
            if (!_prim.BodyHandle.IsValid || !_backend.TryGetBodyState(_prim.BodyHandle, out BodyState s))
                return false;
            _position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            _orientation = _prim.PrimOrientationOf(s.Orientation);
            _linVel = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            _angVel = new Vector3(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z);
            return true;
        }

        public Vector3 Position => _position;
        public Quaternion Orientation => _orientation;

        public Vector3 LinearVelocity
        {
            get => _linVel;
            set
            {
                _linVel = value;
                if (_prim.BodyHandle.IsValid)
                    _backend.SetBodyLinearVelocity(_prim.BodyHandle, ToS(value));
            }
        }

        public Vector3 AngularVelocity
        {
            get => _angVel;
            set
            {
                _angVel = value;
                if (_prim.BodyHandle.IsValid)
                    _backend.SetBodyAngularVelocity(_prim.BodyHandle, ToS(value));
            }
        }

        public float Mass => _prim.BodyHandle.IsValid ? _backend.GetBodyMass(_prim.BodyHandle) : 0f;

        public Vector3 InertiaDiagonal
        {
            get
            {
                if (!_prim.BodyHandle.IsValid)
                    return Vector3.Zero;
                SVector3 d = _backend.GetBodyInertiaDiagonal(_prim.BodyHandle);
                return new Vector3(d.X, d.Y, d.Z);
            }
        }

        public Vector3 Gravity
        {
            get
            {
                SVector3 g = _module.DefaultGravity;
                return new Vector3(g.X, g.Y, g.Z);
            }
        }

        public bool HasCollision => _prim.IsColliding;

        public void AddForce(Vector3 force)
        {
            if (_prim.BodyHandle.IsValid)
                _backend.ApplyForce(_prim.BodyHandle, ToS(force));
        }

        public void AddTorque(Vector3 torque)
        {
            if (_prim.BodyHandle.IsValid)
                _backend.ApplyTorque(_prim.BodyHandle, ToS(torque));
        }

        public void KeepAwake()
        {
            if (_prim.BodyHandle.IsValid)
                _backend.ActivateBody(_prim.BodyHandle);
        }

        public float GetTerrainHeight(Vector3 pos) => _module.TerrainHeightAt(pos.X, pos.Y);

        public float GetWaterLevel(Vector3 pos) => _module.WaterLevel;

        private static SVector3 ToS(Vector3 v) => new SVector3(v.X, v.Y, v.Z);
    }
}
