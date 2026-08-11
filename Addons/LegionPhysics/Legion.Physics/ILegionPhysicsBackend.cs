// Legion Grid - Physics backend abstraction
//
// This is the seam between OpenSim's PhysicsScene/PhysicsActor contract and a
// concrete physics engine. Nothing above this interface knows what engine is
// running; nothing below it knows what OpenSim is.
//
// Design rules:
//   1. Handles, not objects. A region can hold 50k+ prims; we do not allocate a
//      managed wrapper per body. Handles are blittable structs that map onto the
//      engine's own id type (Jolt: BodyID).
//   2. Bulk step output into caller-owned buffers. Zero per-frame allocation.
//   3. Shapes have independent lifetime from bodies. Meshes are expensive to
//      cook and are shared across every prim using the same asset.
//   4. No SL semantics in here. The SL vehicle model, llCastRay filtering rules,
//      collision_start/end dispatch, and permission checks all live ABOVE this
//      interface. This layer knows about rigid bodies, not about scripts.

using System;
using System.Numerics;

namespace Legion.Physics
{
    // ---------------------------------------------------------------------
    // Handles
    // ---------------------------------------------------------------------

    /// <summary>Opaque reference to a rigid body owned by the backend.</summary>
    public readonly struct BodyId : IEquatable<BodyId>
    {
        public static readonly BodyId Invalid = default;

        public readonly uint Value;
        public BodyId(uint value) => Value = value;

        public bool IsValid => Value != 0u;
        public bool Equals(BodyId other) => Value == other.Value;
        public override bool Equals(object? o) => o is BodyId b && Equals(b);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"Body({Value})";
    }

    /// <summary>
    /// Opaque reference to a collision shape. Shapes are reference-counted and
    /// shared: one cooked mesh backs every prim that uses that asset.
    /// </summary>
    public readonly struct ShapeId : IEquatable<ShapeId>
    {
        public static readonly ShapeId Invalid = default;

        public readonly uint Value;
        public ShapeId(uint value) => Value = value;

        public bool IsValid => Value != 0u;
        public bool Equals(ShapeId other) => Value == other.Value;
        public override bool Equals(object? o) => o is ShapeId s && Equals(s);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"Shape({Value})";
    }

    /// <summary>Opaque reference to a character controller (avatar or NPC).</summary>
    public readonly struct CharacterId : IEquatable<CharacterId>
    {
        public static readonly CharacterId Invalid = default;

        public readonly uint Value;
        public CharacterId(uint value) => Value = value;

        public bool IsValid => Value != 0u;
        public bool Equals(CharacterId other) => Value == other.Value;
        public override bool Equals(object? o) => o is CharacterId c && Equals(c);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"Character({Value})";
    }

    /// <summary>Opaque reference to a joint/constraint between two bodies.</summary>
    public readonly struct ConstraintId : IEquatable<ConstraintId>
    {
        public static readonly ConstraintId Invalid = default;

        public readonly uint Value;
        public ConstraintId(uint value) => Value = value;

        public bool IsValid => Value != 0u;
        public bool Equals(ConstraintId other) => Value == other.Value;
        public override bool Equals(object? o) => o is ConstraintId c && Equals(c);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"Constraint({Value})";
    }

    // ---------------------------------------------------------------------
    // Enums
    // ---------------------------------------------------------------------

    /// <summary>
    /// Collision categories. Maps to Jolt ObjectLayer, and is coarsened into a
    /// BroadPhaseLayer by the backend. Keep this small - broad phase layers are
    /// a fixed cost and 4-5 is the sweet spot.
    /// </summary>
    public enum PhysicsLayer : byte
    {
        /// <summary>Region heightfield. Never moves.</summary>
        Terrain = 0,

        /// <summary>Non-physical prims. Collide with dynamics and avatars, never move.</summary>
        Static = 1,

        /// <summary>Physical prims. Full dynamics.</summary>
        Dynamic = 2,

        /// <summary>Avatar and NPC character controllers.</summary>
        Avatar = 3,

        /// <summary>VolumeDetect / phantom-with-events. Reports overlap, never resolves.</summary>
        Sensor = 4,

        /// <summary>
        /// Cheap tier: collides with Terrain, Static, Dynamic and Avatar, but NEVER
        /// with other Debris - the Debris-vs-Debris pair is what would be O(n^2).
        /// Useful for bullet-hell / particle-ish content. Not required for first light.
        /// </summary>
        Debris = 5,

        /// <summary>
        /// INTERNAL: the query-visible marker carried by each avatar. Collides with NOTHING in the
        /// simulation (never enters the solve - no push, no contacts), so it is inert; it exists ONLY
        /// so RayCast/Overlap/ShapeCast can find an avatar (whose CharacterVirtual is not a body).
        /// Backend-managed; do not put prims on this layer. Found only by Avatar-filtered queries.
        /// </summary>
        AvatarQuery = 6,
    }

    public enum BodyMotionType : byte
    {
        /// <summary>Immovable. Infinite mass. Non-physical prims and terrain.</summary>
        Static = 0,

        /// <summary>Moved by script/animation, pushes dynamics, ignores forces.</summary>
        Kinematic = 1,

        /// <summary>Full rigid body dynamics.</summary>
        Dynamic = 2,
    }

    [Flags]
    public enum BodyStateFlags : byte
    {
        None = 0,
        /// <summary>Body is awake and simulating.</summary>
        Active = 1 << 0,
        /// <summary>Body went to sleep this step. Emit one final update, then stop.</summary>
        JustDeactivated = 1 << 1,
        /// <summary>Body woke this step.</summary>
        JustActivated = 1 << 2,
    }

    /// <summary>
    /// Contact lifecycle. Maps directly onto LSL collision_start / collision /
    /// collision_end so the dispatch layer above does not have to diff sets.
    /// </summary>
    public enum ContactPhase : byte
    {
        Begin = 0,
        Persist = 1,
        End = 2,
    }

    [Flags]
    public enum QueryFilter : uint
    {
        None = 0,
        Terrain = 1 << 0,
        Static = 1 << 1,
        Dynamic = 1 << 2,
        Avatar = 1 << 3,
        Sensor = 1 << 4,

        /// <summary>Everything a default llCastRay should see.</summary>
        Default = Terrain | Static | Dynamic | Avatar,
        All = Terrain | Static | Dynamic | Avatar | Sensor,
    }

    public enum ConstraintKind : byte
    {
        Fixed = 0,
        Point = 1,
        Distance = 2,   // supports spring parameters
        Hinge = 3,
        Slider = 4,
        Cone = 5,
        SwingTwist = 6,
        SixDof = 7,
        Pulley = 8,
        Gear = 9,
        RackAndPinion = 10,
        Path = 11,      // smooth spline path
    }

    public enum MotorMode : byte
    {
        Off = 0,
        Velocity = 1,
        Position = 2,
    }

    // ---------------------------------------------------------------------
    // Descriptors
    // ---------------------------------------------------------------------

    /// <summary>One child of a compound shape, in parent-local space.</summary>
    public struct CompoundChild
    {
        public ShapeId Shape;
        public Vector3 Position;
        public Quaternion Orientation;
        /// <summary>Copied into contact reports so a linkset hit knows which prim.</summary>
        public uint UserData;
    }

    public struct BodyDesc
    {
        public ShapeId Shape;
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;

        public PhysicsLayer Layer;
        public BodyMotionType MotionType;

        /// <summary>Explicit mass in kg. If &lt;= 0, computed from shape volume and Density.</summary>
        public float Mass;
        public float Density;

        public float Friction;
        public float Restitution;
        public float LinearDamping;
        public float AngularDamping;

        /// <summary>1.0 = normal gravity. 0 = floating. Negative = rises. Drives llSetBuoyancy.</summary>
        public float GravityFactor;

        /// <summary>Reports contacts but never resolves them. VolumeDetect.</summary>
        public bool IsSensor;

        /// <summary>
        /// Whether this body wants per-step contact (Persist) events forwarded. Begin/End edge
        /// events are ALWAYS reported; Persist is gated on this so a resting or standing object
        /// does not emit a contact every step forever. Set it from whether the object has a
        /// <c>collision</c> handler registered. (Jolt also stops firing Persist once a body sleeps,
        /// so this gate only ever suppresses awake-but-touching pairs.)
        /// </summary>
        public bool WantsContactEvents;

        /// <summary>
        /// Whether to wake the body on insertion. Default FALSE and that matters:
        /// region startup inserts tens of thousands of bodies and waking each one
        /// is the single easiest way to make startup pathological.
        /// </summary>
        public bool StartActive;

        /// <summary>Continuous collision detection. Expensive - reserve for fast movers.</summary>
        public bool UseCcd;

        /// <summary>SceneObjectPart.LocalId. Echoed back in every report to avoid a lookup.</summary>
        public uint UserData;

        public static BodyDesc Default => new BodyDesc
        {
            Orientation = Quaternion.Identity,
            Layer = PhysicsLayer.Static,
            MotionType = BodyMotionType.Static,
            Mass = 0f,
            Density = 1000f,       // water; OpenSim's historical prim density
            Friction = 0.6f,
            Restitution = 0.0f,
            LinearDamping = 0.05f,
            AngularDamping = 0.05f,
            GravityFactor = 1.0f,
            StartActive = false,
        };
    }

    public struct CharacterDesc
    {
        public Vector3 Position;
        public Quaternion Orientation;

        /// <summary>Capsule half-height EXCLUDING the caps.</summary>
        public float CapsuleHalfHeight;
        public float CapsuleRadius;

        public float Mass;
        public float Friction;

        /// <summary>Max slope the character can walk up, radians.</summary>
        public float MaxSlopeAngle;

        /// <summary>Max step height auto-climbed without a jump. SL feel lives here.</summary>
        public float StepHeight;

        /// <summary>
        /// Relative push strength against dynamic bodies the character walks into. 1.0 = the backend's
        /// default push force; scales linearly. (The backend maps this onto Jolt's MaxStrength in N.)
        /// </summary>
        public float PushStrength;

        /// <summary>Initial upward speed (m/s) of a jump from solid ground. SL jump feel lives here.</summary>
        public float JumpSpeed;

        /// <summary>
        /// Whether this avatar wants per-step contact (Persist) events forwarded. Same gate as
        /// <see cref="BodyDesc.WantsContactEvents"/>: Begin/End always report; Persist is gated so a
        /// standing avatar (whose controller never sleeps) does not emit a floor contact every step
        /// forever. Set from whether the avatar has a <c>collision</c> handler registered.
        /// </summary>
        public bool WantsContactEvents;

        public uint UserData;

        public static CharacterDesc Default => new CharacterDesc
        {
            Orientation = Quaternion.Identity,
            CapsuleHalfHeight = 0.45f,
            CapsuleRadius = 0.30f,
            Mass = 80f,
            Friction = 0.5f,
            MaxSlopeAngle = 50f * (MathF.PI / 180f),
            StepHeight = 0.45f,
            PushStrength = 1.0f,
            JumpSpeed = 4.0f,
        };
    }

    public struct ConstraintDesc
    {
        public ConstraintKind Kind;
        public BodyId BodyA;
        /// <summary>Invalid = attach to world.</summary>
        public BodyId BodyB;

        /// <summary>Anchor points in each body's local space.</summary>
        public Vector3 AnchorA;
        public Vector3 AnchorB;

        /// <summary>Primary axis in local space. Hinge axis, slider axis, cone axis.</summary>
        public Vector3 AxisA;
        public Vector3 AxisB;

        /// <summary>Kind-dependent limits. Hinge/Slider: [min,max]. Cone: half-angle in X.</summary>
        public Vector2 Limits;
        public bool LimitsEnabled;

        /// <summary>Distance constraint spring. Frequency in Hz, damping 0-1.</summary>
        public float SpringFrequency;
        public float SpringDamping;

        /// <summary>Gear / rack-and-pinion ratio.</summary>
        public float Ratio;

        /// <summary>Break force threshold in newtons. 0 = unbreakable.</summary>
        public float BreakForce;

        public uint UserData;
    }

    // ---------------------------------------------------------------------
    // Step output
    // ---------------------------------------------------------------------

    public struct BodyState
    {
        public BodyId Body;
        public uint UserData;
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public BodyStateFlags Flags;
    }

    public struct CharacterState
    {
        public CharacterId Character;
        public uint UserData;
        public Vector3 Position;
        public Vector3 LinearVelocity;
        public Vector3 GroundNormal;
        public BodyId GroundBody;
        public bool IsSupported;
        /// <summary>Standing on a slope too steep to hold. Drives the SL slide-off behaviour.</summary>
        public bool IsSliding;
    }

    public struct ContactReport
    {
        public BodyId BodyA;
        public BodyId BodyB;
        public uint UserDataA;
        public uint UserDataB;
        /// <summary>The STRUCK part's UserData on each side, resolved from the contact sub-shape: for a
        /// compound (linkset) body this is the specific child prim's id; for a single-shape body it is the
        /// body's own UserData. Drives per-child collision identity (llDetectedLinkNumber).</summary>
        public uint ChildUserDataA;
        public uint ChildUserDataB;
        public Vector3 Point;
        /// <summary>Points from A toward B.</summary>
        public Vector3 Normal;
        /// <summary>Newton-seconds. Feeds collision sound volume and damage models.</summary>
        public float Impulse;
        public ContactPhase Phase;
    }

    /// <summary>
    /// Result of one Step. Counts tell you how much of each caller-owned buffer
    /// was filled; overflow flags tell you whether data was DROPPED, which is
    /// something the layer above must surface rather than silently eat.
    /// </summary>
    public readonly struct StepResult
    {
        public readonly int BodyUpdateCount;
        public readonly int CharacterUpdateCount;
        public readonly int ContactCount;
        public readonly bool BodyBufferOverflowed;
        public readonly bool ContactBufferOverflowed;
        public readonly int ActiveBodyCount;
        public readonly float PhysicsMilliseconds;

        public StepResult(
            int bodyUpdateCount,
            int characterUpdateCount,
            int contactCount,
            bool bodyOverflow,
            bool contactOverflow,
            int activeBodyCount,
            float physicsMs)
        {
            BodyUpdateCount = bodyUpdateCount;
            CharacterUpdateCount = characterUpdateCount;
            ContactCount = contactCount;
            BodyBufferOverflowed = bodyOverflow;
            ContactBufferOverflowed = contactOverflow;
            ActiveBodyCount = activeBodyCount;
            PhysicsMilliseconds = physicsMs;
        }
    }

    public struct RayHit
    {
        public BodyId Body;
        public uint UserData;
        /// <summary>For compound shapes, the UserData of the struck child prim.</summary>
        public uint ChildUserData;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
    }

    // ---------------------------------------------------------------------
    // The interface
    // ---------------------------------------------------------------------

    /// <summary>
    /// A physics engine, as Legion needs one.
    ///
    /// THREADING: implementations must permit Create/Remove/Set* calls from
    /// threads other than the one calling Step, and must permit queries to run
    /// concurrently with Step. This is not a courtesy requirement - it is the
    /// reason we can drop the taint-list pattern that ODE and BulletSim carry.
    /// A backend that cannot honour it must serialise internally rather than
    /// pushing that burden back up.
    /// </summary>
    public interface ILegionPhysicsBackend : IDisposable
    {
        string Name { get; }
        string Version { get; }

        // -- lifecycle ----------------------------------------------------

        void Initialize(in PhysicsBackendSettings settings);

        // -- shapes -------------------------------------------------------
        // Reference counted. CreateXShape returns a shape with refcount 1;
        // attaching to a body adds a reference. Call ReleaseShape when the
        // asset cache evicts it, not when a body dies.

        ShapeId CreateBoxShape(Vector3 halfExtents);
        ShapeId CreateSphereShape(float radius);
        ShapeId CreateCapsuleShape(float halfHeight, float radius);
        ShapeId CreateCylinderShape(float halfHeight, float radius);
        ShapeId CreateConvexHullShape(ReadOnlySpan<Vector3> points);
        ShapeId CreateMeshShape(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices);
        ShapeId CreateCompoundShape(ReadOnlySpan<CompoundChild> children);

        ShapeId CreateHeightFieldShape(
            ReadOnlySpan<float> heights, int sampleCountX, int sampleCountY, Vector3 scale);

        /// <summary>
        /// Wrap an existing shape with a scale. This is how prim resize works
        /// without re-cooking geometry - critical, because cooking a sculpt or
        /// mesh is orders of magnitude more expensive than wrapping it.
        /// </summary>
        ShapeId CreateScaledShape(ShapeId baseShape, Vector3 scale);

        void AddShapeRef(ShapeId shape);
        void ReleaseShape(ShapeId shape);

        // -- bodies -------------------------------------------------------

        BodyId CreateBody(in BodyDesc desc);
        void RemoveBody(BodyId body);
        bool IsBodyValid(BodyId body);

        void SetBodyShape(BodyId body, ShapeId shape, bool recomputeMass);
        void SetBodyMotionType(BodyId body, BodyMotionType motionType, bool activate);
        void SetBodyLayer(BodyId body, PhysicsLayer layer);

        void SetBodyTransform(BodyId body, Vector3 position, Quaternion orientation, bool activate);
        void SetBodyLinearVelocity(BodyId body, Vector3 velocity);
        void SetBodyAngularVelocity(BodyId body, Vector3 velocity);

        void SetBodyMass(BodyId body, float mass);
        /// <summary>Read the body's assigned mass (explicit, or shape Volume x Density). 0 if unknown/static.</summary>
        float GetBodyMass(BodyId body);
        /// <summary>Local principal moments of inertia (diagonal), kg*m^2. Zero for non-dynamic bodies.
        /// The vehicle controller's vertical attractor scales its restoring torque by this (the
        /// BulletSim equivalent is the prim's CalculateLocalInertia result).</summary>
        Vector3 GetBodyInertiaDiagonal(BodyId body);
        /// <summary>Recompute + apply the dynamic mass as (shape geometric Volume x physicalDensity kg/m^3).
        /// Lets the module honour a prim's SceneObjectPart.Density instead of the BodyDesc default.</summary>
        void SetBodyDensity(BodyId body, float physicalDensity);
        void SetBodyFriction(BodyId body, float friction);
        void SetBodyRestitution(BodyId body, float restitution);
        void SetBodyDamping(BodyId body, float linear, float angular);
        void SetBodyGravityFactor(BodyId body, float factor);

        /// <summary>Lock translation/rotation on world axes. Backs ExtendedPhysics axis locks.</summary>
        void SetBodyAxisLocks(BodyId body, Vector3 allowedTranslation, Vector3 allowedRotation);

        void ApplyForce(BodyId body, Vector3 force);
        void ApplyTorque(BodyId body, Vector3 torque);
        void ApplyImpulse(BodyId body, Vector3 impulse);
        void ApplyImpulseAtPoint(BodyId body, Vector3 impulse, Vector3 worldPoint);
        void ApplyAngularImpulse(BodyId body, Vector3 angularImpulse);

        /// <summary>
        /// Archimedes impulse for one step. The water plane and the object's
        /// submerged fraction are computed by the engine from the shape.
        /// This is what boat vehicles should ride on rather than hand-rolled lift.
        /// </summary>
        void ApplyBuoyancy(
            BodyId body, float waterHeight, float buoyancy, float linearDrag, float angularDrag);

        void ActivateBody(BodyId body);
        void DeactivateBody(BodyId body);

        /// <summary>Allow/forbid the engine to sleep this body. Vehicles disable sleeping while
        /// active (Bullet's DISABLE_DEACTIVATION) - their controller must run every frame even when
        /// the body is momentarily at rest. No-op on static bodies.</summary>
        void SetBodyAllowSleeping(BodyId body, bool allow);

        /// <summary>Toggle the Persist (ongoing-contact) gate for a live body - a prim's collision-script
        /// subscription flips this so the script `collision` event streams while touching.</summary>
        void SetBodyWantsContactEvents(BodyId body, bool wants);

        bool TryGetBodyState(BodyId body, out BodyState state);

        // -- characters ---------------------------------------------------

        CharacterId CreateCharacter(in CharacterDesc desc);
        void RemoveCharacter(CharacterId character);

        void SetCharacterTransform(CharacterId character, Vector3 position, Quaternion orientation);
        void SetCharacterShape(CharacterId character, float capsuleHalfHeight, float capsuleRadius);

        /// <summary>
        /// Atomically (under the character gate, so it cannot race the per-step CharacterVirtual update)
        /// move a character to <paramref name="position"/> AND clear its linear velocity. Used to un-bury a
        /// standing/flying avatar after a live terrain raise swaps the heightfield body out from under it:
        /// the snap lifts it onto the new surface; zeroing velocity stops the accumulated fall speed from
        /// lurching it back down / re-penetrating on the next step.
        /// </summary>
        void ReGroundCharacter(CharacterId character, Vector3 position);

        /// <summary>
        /// Desired horizontal velocity plus explicit vertical control. Called once
        /// per step from the movement layer. The backend resolves stepping, slopes
        /// and moving platforms; it does not know about avatar animation state.
        /// </summary>
        void SetCharacterMovement(
            CharacterId character, Vector3 desiredVelocity, bool jump, bool flying);

        bool TryGetCharacterState(CharacterId character, out CharacterState state);

        // -- constraints --------------------------------------------------

        ConstraintId CreateConstraint(in ConstraintDesc desc);
        void RemoveConstraint(ConstraintId constraint);
        void SetConstraintEnabled(ConstraintId constraint, bool enabled);
        void SetConstraintMotor(ConstraintId constraint, MotorMode mode, float target, float maxForce);
        void SetConstraintLimits(ConstraintId constraint, float min, float max);
        bool IsConstraintBroken(ConstraintId constraint);

        // -- world --------------------------------------------------------

        void SetGravity(Vector3 gravity);
        void SetTerrain(ShapeId heightFieldShape, Vector3 position);
        void SetWaterHeight(float height);

        // -- queries ------------------------------------------------------
        // Must be safe to call concurrently with Step.

        bool RayCast(
            Vector3 origin, Vector3 direction, float maxDistance,
            QueryFilter filter, out RayHit hit);

        int RayCastAll(
            Vector3 origin, Vector3 direction, float maxDistance,
            QueryFilter filter, Span<RayHit> hits);

        int OverlapSphere(Vector3 center, float radius, QueryFilter filter, Span<BodyId> results);

        int OverlapBox(
            Vector3 center, Vector3 halfExtents, Quaternion orientation,
            QueryFilter filter, Span<BodyId> results);

        bool ShapeCast(
            ShapeId shape, Vector3 origin, Quaternion orientation, Vector3 direction,
            float maxDistance, QueryFilter filter, out RayHit hit);

        // -- step ---------------------------------------------------------

        /// <summary>
        /// Advance the simulation and drain results into caller-owned buffers.
        /// One call per frame. Buffers should be allocated once at region start
        /// and reused; nothing here allocates.
        /// </summary>
        StepResult Step(
            float deltaTime,
            Span<BodyState> bodyUpdates,
            Span<CharacterState> characterUpdates,
            Span<ContactReport> contacts);
    }

    public struct PhysicsBackendSettings
    {
        public Vector3 Gravity;

        /// <summary>Upper bound on bodies. Jolt preallocates; size for worst case.</summary>
        public int MaxBodies;
        public int MaxBodyPairs;
        public int MaxContactConstraints;

        /// <summary>Worker threads. 0 = auto (Environment.ProcessorCount - 1).</summary>
        public int ThreadCount;

        /// <summary>Solver position iterations. Higher = stiffer joints, slower.</summary>
        public int PositionIterations;
        public int VelocityIterations;

        /// <summary>Physics substeps per Step() call.</summary>
        public int CollisionSteps;

        /// <summary>
        /// Force single-threaded, bit-reproducible stepping. Costs throughput.
        /// Worth having as a config flag for reproducing user-reported bugs and
        /// for content-parity regression runs.
        /// </summary>
        public bool DeterministicMode;

        public static PhysicsBackendSettings Default => new PhysicsBackendSettings
        {
            Gravity = new Vector3(0f, 0f, -9.80665f),
            MaxBodies = 65536,
            MaxBodyPairs = 65536,
            MaxContactConstraints = 16384,
            ThreadCount = 0,
            PositionIterations = 2,
            VelocityIterations = 10,
            CollisionSteps = 1,
            DeterministicMode = false,
        };
    }
}
