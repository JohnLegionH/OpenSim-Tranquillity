// Legion Grid - an avatar as a Jolt CharacterVirtual (M6.5).
//
// This is the PhysicsActor OpenSim hands back from AddAvatar. It is backed by a Jolt CharacterVirtual,
// NOT a solver rigid body: the movement layer keeps control, so stair-stepping / slope handling /
// moving-platform support come from the controller (M3), it collides against dynamic bodies (M3.5),
// and it carries a kinematic query marker so llCastRay(agent) can find it (M4.5). The backend already
// implements all of that; this class is the OpenSim-facing wiring.
//
// Drive (ScenePresence -> here): TargetVelocity / Velocity (walk/run intent), Flying (gravity on/off),
// AvatarJump (one-shot jump), Position (teleport), Size (appearance). All fold into one push through
// SetCharacterMovement / SetCharacterTransform.
// Drain (here -> ScenePresence): ApplyCharacterState, called once per Step from the scene's character
// drain, writes back Position/Velocity + ground state and fires the terse update so the viewer sees
// smooth movement - the avatar equivalent of JoltPrim.ApplyStepState (the 6.4 body drain).
//
// Types: PhysicsActor speaks OpenMetaverse.Vector3/Quaternion (unqualified here); the backend speaks
// System.Numerics (SVector3/SQuaternion).

using System;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenMetaverse;
using Legion.Physics;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    internal sealed class JoltCharacter : PhysicsActor
    {
        private readonly LegionJoltScene _module;
        private readonly ILegionPhysicsBackend _backend;

        private Vector3 _position;
        private Vector3 _velocity;          // last drained linear velocity (ScenePresence reads this for terse updates)
        private Vector3 _targetVelocity;    // desired velocity from ScenePresence (walk/run intent)
        private Vector3 _size;
        private Quaternion _orientation = Quaternion.Identity;
        private bool _flying;
        private bool _setAlwaysRun;

        // Jump is a LATCH, not a one-shot. The animator calls AvatarJump ONCE, but ScenePresence writes
        // TargetVelocity every movement update (and zeroes its Z - the vertical only ever comes through
        // AvatarJump), so a SetCharacterMovement(jump=false) reliably lands before the physics step
        // consumes the jump. Holding the latch true across those pushes - and clearing it only once the
        // drain shows the avatar has actually left the ground - is what makes the jump survive.
        private bool _jumpLatched;

        private readonly float _feetOffset;   // capsule-centre -> visual feet gap; seats the avatar ON ground

        // Every M3 tuning knob is held here so feel is a knob-turning problem, not an architecture one.
        private float _capsuleHalfHeight;   // EXCLUDING the caps (Jolt convention)
        private float _capsuleRadius;
        private readonly float _mass = CharacterDesc.Default.Mass;

        private int _subscribedMs;

        // Cached ground state from the last drain, surfaced by `jolt avatarstatus`.
        private bool _isSupported;
        private bool _isSliding;
        private Vector3 _groundNormal;
        private BodyId _groundBody = BodyId.Invalid;

        private CharacterId _character = CharacterId.Invalid;

        internal CharacterId CharacterHandle => _character;
        internal bool IsSupported => _isSupported;
        internal bool IsSliding => _isSliding;
        internal Vector3 GroundNormal => _groundNormal;
        internal BodyId GroundBody => _groundBody;
        internal float CapsuleHalfHeight => _capsuleHalfHeight;
        internal float CapsuleRadius => _capsuleRadius;

        // How far the capsule CENTRE sits above the feet: cylinder half-height + one hemispherical cap.
        // Spawn/rest Z = groundZ + StandHalf so the feet touch the surface (not underground).
        internal float StandHalf => _capsuleHalfHeight + _capsuleRadius;
        internal float FeetOffset => _feetOffset;

        internal JoltCharacter(LegionJoltScene module, ILegionPhysicsBackend backend, uint localid, string name,
                               Vector3 position, Vector3 size, float feetOffset, bool isFlying)
        {
            _module = module;
            _backend = backend;
            LocalID = localid;
            Name = name;
            _size = size;
            _position = position;
            _feetOffset = feetOffset;
            _flying = isFlying;

            CapsuleFromSize(size, out _capsuleHalfHeight, out _capsuleRadius);
            CreateCharacterInternal();
        }

        // SL avatar box (X=width, Y=depth, Z=height) -> a standing capsule. Radius from the footprint;
        // the cylinder half-height is what remains after the two hemispherical caps. Clamped so a thin or
        // short appearance still yields a valid capsule.
        private static void CapsuleFromSize(Vector3 size, out float halfHeight, out float radius)
        {
            float r = MathF.Max(0.2f, MathF.Min(size.X, size.Y) * 0.5f);
            float totalHalf = MathF.Max(r + 0.1f, size.Z * 0.5f);   // half-height must exceed a single cap
            radius = r;
            halfHeight = MathF.Max(0.05f, totalHalf - r);
        }

        // The stand-half for a given appearance size, WITHOUT constructing a character - the scene needs
        // it to compute the spawn Z (groundZ + StandHalf) before the capsule exists.
        internal static float StandHalfFor(Vector3 size)
        {
            CapsuleFromSize(size, out float hh, out float r);
            return hh + r;
        }

        private void CreateCharacterInternal()
        {
            CharacterDesc desc = CharacterDesc.Default;
            desc.Position = ToS(_position);
            desc.Orientation = ToS(_orientation);
            desc.CapsuleHalfHeight = _capsuleHalfHeight;
            desc.CapsuleRadius = _capsuleRadius;
            desc.Mass = _mass;
            desc.WantsContactEvents = _subscribedMs > 0;
            desc.UserData = LocalID;   // echoed in every query hit / drain - the M4.5 query-marker identity

            _character = _backend.CreateCharacter(desc);

            // No off-thread activation dance is needed here (unlike the physical prim of 6.4): CreateCharacter
            // adds the controller to _characterList UNDER _characterGate, and Step iterates that same list
            // under the same lock, so the avatar is stepped from the very next frame. The physical BODY needed
            // an explicit ActivateBody only because the step-thread-owned active-set is fed through a queue.
            // Push the initial intent so frame 1 already has flying/desired set and reports a valid state.
            PushMovement();
        }

        // The scene's per-step character drain hands back the CharacterVirtual's post-ExtendedUpdate state.
        // Write the cached values ScenePresence reads (Position/Velocity) + the ground state, then fire the
        // terse update so the viewer sees smooth movement. Ground truth flows one way here: the drain writes
        // _position directly, so it never trips the Position setter (which would force a transform).
        internal void ApplyCharacterState(in CharacterState s)
        {
            _position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            _velocity = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            _isSupported = s.IsSupported;
            _isSliding = s.IsSliding;
            _groundNormal = new Vector3(s.GroundNormal.X, s.GroundNormal.Y, s.GroundNormal.Z);
            _groundBody = s.GroundBody;

            IsColliding = s.IsSupported;
            CollidingGround = s.IsSupported && !s.GroundBody.IsValid;   // supported with no body => on terrain
            CollidingObj = s.IsSupported && s.GroundBody.IsValid;       // supported by a body => standing on a prim

            // Release the jump latch once the avatar has actually left the ground (rising, or no longer
            // supported). Until then the latch keeps re-asserting jump=true so a TargetVelocity push can't
            // cancel it before the step consumes it; after takeoff we stop, so there is no bunny-hop.
            if (_jumpLatched && (_velocity.Z > 0.5f || !s.IsSupported))
            {
                _jumpLatched = false;
                if (LegionJoltScene.CharJumpTrace)
                    LegionJoltScene.m_log.Debug($"{LegionJoltScene.LogHeader} [charjump] id={LocalID} TAKEOFF vZ={_velocity.Z:0.000} supported={s.IsSupported} -> latch cleared");
            }

            RequestPhysicsterseUpdate();
        }

        // One channel for all movement intent: desired velocity + a one-shot jump + the flying flag. The
        // backend integrates gravity, ground velocity and the jump in StepCharacter; jump is consumed there
        // exactly once, so we clear the local pending flag after pushing it.
        private void PushMovement()
        {
            if (!_character.IsValid)
                return;
            // Send the current latch state. StepCharacter jumps from solid ground when it sees jump=true and
            // clears its own request every step; the drain releases our latch on takeoff.
            _backend.SetCharacterMovement(_character, ToS(_targetVelocity), _jumpLatched, _flying);
            if (_jumpLatched && LegionJoltScene.CharJumpTrace)
                LegionJoltScene.m_log.Debug($"{LegionJoltScene.LogHeader} [charjump] id={LocalID} sent jump=true to backend (target={_targetVelocity} flying={_flying})");
        }

        internal void Destroy()
        {
            if (_character.IsValid)
                _backend.RemoveCharacter(_character);
            _character = CharacterId.Invalid;
        }

        private static SVector3 ToS(Vector3 v) => new SVector3(v.X, v.Y, v.Z);
        private static SQuaternion ToS(Quaternion q) => new SQuaternion(q.X, q.Y, q.Z, q.W);

        // ---------------------------------------------------------------------
        // PhysicsActor contract. Live state: Position / Velocity / TargetVelocity / Flying / Size.
        // ---------------------------------------------------------------------

        public override Vector3 Position
        {
            get => _position;
            set
            {
                // Only ScenePresence writes this (teleport / direct set); the drain writes _position
                // directly, so this force-transform never fights passive physics motion.
                _position = value;
                if (_character.IsValid)
                    _backend.SetCharacterTransform(_character, ToS(value), ToS(_orientation));
            }
        }

        // Un-bury: snap the capsule to `pos` (region coords, capsule centre) and clear velocity, so a live
        // terrain raise that left this avatar below the new surface lifts it back onto the ground without
        // carrying the accumulated fall speed into the next step. Routed through the gated backend
        // (ReGroundCharacter) so it can't race the per-step CharacterVirtual update. Called only from
        // LegionJoltScene's post-SetTerrain re-ground pass. The next drain reads the new position back.
        internal void ReGround(Vector3 pos)
        {
            _position = pos;
            _velocity = Vector3.Zero;
            _targetVelocity = Vector3.Zero;
            if (_character.IsValid)
                _backend.ReGroundCharacter(_character, ToS(pos));
        }

        // Facing is radially symmetric for a vertical capsule, so orientation does not change collision.
        // Cache it (the viewer's display rotation comes from the client's body-rot, not physics) and avoid
        // pushing a transform on every turn, which would re-seat position and fight the drain.
        public override Quaternion Orientation
        {
            get => _orientation;
            set => _orientation = value;
        }

        // ScenePresence's Velocity setter routes here as well as TargetVelocity; both are walk/run intent.
        public override Vector3 Velocity
        {
            get => _velocity;
            set { _targetVelocity = value; PushMovement(); }
        }

        // The PRIMARY movement command ScenePresence writes each frame.
        public override Vector3 TargetVelocity
        {
            get => _targetVelocity;
            set { _targetVelocity = value; PushMovement(); }
        }

        public override bool Flying
        {
            get => _flying;
            set { if (_flying == value) return; _flying = value; PushMovement(); }
        }

        public override void AvatarJump(float forceZ)
        {
            _jumpLatched = true;   // held until takeoff (see ApplyCharacterState); jump height is the JumpSpeed knob
            if (LegionJoltScene.CharJumpTrace)
                LegionJoltScene.m_log.Debug($"{LegionJoltScene.LogHeader} [charjump] id={LocalID} AvatarJump(forceZ={forceZ:0.00}) fired -> latch set (supported={_isSupported} flying={_flying})");
            PushMovement();
        }

        public override void SetMomentum(Vector3 momentum)
        {
            _velocity = momentum;
            _targetVelocity = momentum;
            PushMovement();
        }

        public override Vector3 Size
        {
            get => _size;
            set
            {
                if (_size == value) return;
                _size = value;
                CapsuleFromSize(value, out _capsuleHalfHeight, out _capsuleRadius);
                if (_character.IsValid)
                    _backend.SetCharacterShape(_character, _capsuleHalfHeight, _capsuleRadius);
            }
        }

        public override int PhysicsActorType { get => (int)ActorTypes.Agent; set { } }
        public override bool IsPhysical { get => true; set { } }
        public override float Mass => _mass;
        public override bool Stopped => _velocity == Vector3.Zero;

        public override Vector3 GeometricCenter => _position;
        public override Vector3 CenterOfMass => _position;

        public override bool SetAlwaysRun { get => _setAlwaysRun; set => _setAlwaysRun = value; }

        // Collision-event subscription: gates Persist forwarding (M6.6); the window is stored now.
        public override void SubscribeEvents(int ms) { _subscribedMs = ms; }
        public override void UnSubscribeEvents() { _subscribedMs = 0; }
        public override bool SubscribedEvents() => _subscribedMs > 0;

        // ---- inert this slice (the controller owns velocity; forces/vehicles/PID are M6.6+) ----
        public override Vector3 RotationalVelocity { get => Vector3.Zero; set { } }
        public override Vector3 Torque { get => Vector3.Zero; set { } }
        public override Vector3 Force { get => Vector3.Zero; set { } }
        public override Vector3 Acceleration { get => Vector3.Zero; set { } }
        public override float CollisionScore { get; set; }
        public override bool Kinematic { get => false; set { } }
        public override float Buoyancy { get => 0f; set { } }   // gravity is governed by Flying, not buoyancy
        public override bool ThrottleUpdates { get => false; set { } }
        public override bool IsColliding { get; set; }
        public override bool CollidingGround { get; set; }
        public override bool CollidingObj { get; set; }
        public override bool Grabbed { set { } }
        public override bool Selected { set { } }
        public override PrimitiveBaseShape Shape { set { } }

        public override void CrossingFailure() { }
        public override void link(PhysicsActor obj) { }
        public override void delink() { }
        public override void LockAngularMotion(byte axislocks) { }

        public override void AddForce(Vector3 force, bool pushforce) { }
        public override void AddAngularForce(Vector3 force, bool pushforce) { }
        public override void SetVolumeDetect(int param) { }

        public override int VehicleType { get => 0; set { } }
        public override void VehicleFloatParam(int param, float value) { }
        public override void VehicleVectorParam(int param, Vector3 value) { }
        public override void VehicleRotationParam(int param, Quaternion rotation) { }
        public override void VehicleFlags(int param, bool remove) { }

        public override Vector3 PIDTarget { set { } }
        public override bool PIDActive { get => false; set { } }
        public override float PIDTau { set { } }
        public override bool PIDHoverActive { get => false; set { } }
        public override float PIDHoverHeight { set { } }
        public override PIDHoverType PIDHoverType { set { } }
        public override float PIDHoverTau { set { } }
        public override Quaternion APIDTarget { set { } }
        public override bool APIDActive { set { } }
        public override float APIDStrength { set { } }
        public override float APIDDamping { set { } }
    }
}
