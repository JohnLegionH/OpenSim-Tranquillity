// Legion Grid - Jolt implementation of ILegionPhysicsBackend
//
// ============================ READ THIS FIRST ============================
// The ILegionPhysicsBackend interface is the deliverable. THIS is the real
// backend. As of M1 Task 3 only the LIFECYCLE + LAYER/BROAD-PHASE wiring is
// live (Initialize / Dispose / the filter tables); every other member is still
// a NotImplementedException stub, exactly as scoped. The design sketch lives at
// docs/physics/JoltPhysicsBackend.cs and stays there as the mapping reference.
//
// Jolt binding: JoltPhysicsSharp 2.18.6 (newest still shipping lib/net8.0/),
// single precision (Foundation.Init(false) -> joltc.dll). The Jolt calls below
// are the REAL 2.18.6 surface, verified by reflection against the shipped
// assembly - not the sketch's "shapes to verify". See the MILESTONE1 notes for
// the reference-vs-real API deltas.
//
// The parts worth reading carefully are the ones that are easy to get wrong and
// expensive to discover later:
//   - broad phase / object layer filtering  (BroadPhase region + Initialize)
//   - DontActivate on insert                (CreateBody - Task 4+)
//   - ScaledShape for prim resize           (CreateScaledShape - later)
//   - contact ring buffer                   (LegionContactListener - later)
//   - CharacterVirtual stepping order       (Step - Task 4)
// =========================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using JoltPhysicsSharp;

namespace Legion.Physics.Jolt
{
    public sealed class JoltPhysicsBackend : ILegionPhysicsBackend
    {
        public string Name => "Jolt";
        public string Version => "5.x";

        private PhysicsBackendSettings _settings;
        private readonly Stopwatch _stepTimer = new Stopwatch();

        // Handle tables. Jolt hands back its own ids; we keep our own dense
        // tables so a stale Legion handle can never index live Jolt memory.
        private readonly HandleTable<JoltBodyRecord> _bodies = new HandleTable<JoltBodyRecord>();
        private readonly HandleTable<JoltShapeRecord> _shapes = new HandleTable<JoltShapeRecord>();
        private readonly HandleTable<JoltCharacterRecord> _characters = new HandleTable<JoltCharacterRecord>();
        private readonly HandleTable<JoltConstraintRecord> _constraints = new HandleTable<JoltConstraintRecord>();

        private LegionContactListener _contactListener = null!;

        // Native Jolt handles. Nullable + disposed in Dispose() in strict reverse
        // order (delta #6): the PhysicsSystem retains the filter interfaces and the
        // job system for its lifetime, so the system MUST be torn down first.
        private PhysicsSystem? _system;
        private JobSystemThreadPool? _jobSystem;
        private ObjectLayerPairFilterTable? _objectLayerPairFilter;
        private BroadPhaseLayerInterfaceTable? _broadPhaseInterface;
        private ObjectVsBroadPhaseLayerFilterTable? _objectVsBroadPhaseFilter;
        // NOTE (delta #4): 2.18.6 has NO TempAllocator - temp allocation is internal
        // to PhysicsSystem.Update. There is deliberately no _tempAllocator field.

        // Cached LOCKING BodyInterface - safe to call from any thread. This is what lets Legion
        // drop the taint-queue pattern (Create/Remove/Set* run straight from the scene thread).
        // Valid for the PhysicsSystem's lifetime.
        private BodyInterface _bodyInterface;

        // --- Active-body tracking (Task 4; delta #8 mechanism) ---
        // OnBodyActivated/OnBodyDeactivated fire from Jolt WORKER threads during Update(), and
        // activation can also flip from the SCENE thread (SetBodyTransform activate:true - no
        // taint queue). A plain shared HashSet would tear. So the event handlers only ENQUEUE;
        // the HashSet is owned SOLELY by the Step thread. Zero cross-thread set mutation; zero
        // per-frame allocation (the scratch collections are Clear()ed and refilled, not realloc'd;
        // foreach over a concrete HashSet/List uses a struct enumerator).
        private readonly ConcurrentQueue<ActivationDelta> _activationQueue = new ConcurrentQueue<ActivationDelta>();
        private readonly HashSet<uint> _activeBodies = new HashSet<uint>();   // step-thread only
        private readonly HashSet<uint> _justActivated = new HashSet<uint>();  // scratch, per-step
        private readonly List<uint> _justDeactivated = new List<uint>();      // scratch, per-step
        private readonly List<uint> _staleActive = new List<uint>();          // scratch, per-step

        // Reverse map: Jolt BodyID.ID -> our record. Written on Create/Remove (scene thread),
        // read from Step and from the contact/activation callbacks (worker threads).
        private readonly ConcurrentDictionary<uint, JoltBodyRecord> _joltToRecord =
            new ConcurrentDictionary<uint, JoltBodyRecord>();

        // Current terrain body (SetTerrain replaces it). BodyId.Invalid = none.
        private BodyId _terrainBody = BodyId.Invalid;

        // Region water plane height (metres, region-local Z). Stored for buoyancy (M8) and queries;
        // no water collision body in the solve yet - water is a force field, not a surface.
        private float _waterHeight;

        // ObjectLayerFilter objects (native callbacks) keyed by QueryFilter value, built lazily so each
        // distinct filter allocates its callback once. Disposed in Dispose.
        private readonly ConcurrentDictionary<QueryFilter, LayerQueryFilter> _queryFilters =
            new ConcurrentDictionary<QueryFilter, LayerQueryFilter>();

        // Characters (CharacterVirtual) are NOT lock-free like BodyInterface, and they are stepped on
        // the Step thread OUTSIDE _system.Update. So all character create/remove/set/step operations are
        // serialised through this gate and the step-thread-owned list. (Abstraction friction vs the
        // taint-free body path - see the M3 notes.)
        private readonly object _characterGate = new object();
        private readonly List<JoltCharacterRecord> _characterList = new List<JoltCharacterRecord>();

        // Shared avatar-vs-avatar collision. Every character is registered here so their capsules
        // collide (push/block) - Jolt's default matches SL's [BulletSim]AvatarToAvatarCollisionsByDefault
        // = true. Making it a config knob is M6 (see notes). Disposed after the characters.
        private CharacterVsCharacterCollisionSimple? _charVsChar;

        // Jolt's CapsuleShape axis is Y; a Z-up avatar capsule must stand along world Z. Rotate +90 deg
        // about X (Y -> Z), the same Z-up trick the heightfield wrapper uses. Shared, immutable.
        private static readonly Quaternion CapsuleYToZ = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

        // CharacterDesc.PushStrength is a relative scale (1.0 = normal); this is the newton value it
        // scales, chosen to equal Jolt's own MaxStrength default so PushStrength 1.0 = stock behaviour.
        private const float PushStrengthBaseNewtons = 100f;

        // Box convex radius is clamped to min(this, 0.1 * smallest half-extent) so Jolt never
        // asserts "convex radius larger than shape".
        private const float DefaultConvexRadius = 0.05f;

        // Restitution below this closing speed is dropped (Jolt's default 1.0 m/s). Used ONLY to feed
        // the contact-impulse estimator (EstimateCollisionResponse) with the same threshold the solver
        // will use, so the reported impulse matches what actually gets applied.
        private const float MinVelocityForRestitution = 1.0f;

        // RayCastAll coincident-hit collapse: the heightfield's two triangles meeting at the ray XY report
        // two hits at the same point on the same body. Two hits within this radius (1 mm), on the SAME body,
        // collapse to one - matching BulletSim's single terrain hit without touching legitimate multi-hit.
        private const float CoincidentEpsilonSq = 1e-6f;

        // Minimum heightfield sample count per side. joltc 2.18.6 silently mis-cooks n<3 (asserts
        // compiled out); the M6 terrain feed passes region-derived (N+1) odd counts (257, 513, ...),
        // which the M1 "257 result" proved cook faithfully. 4 is a defensive floor above the 3 hard limit.
        private const int MinHeightFieldSampleCount = 4;

        private readonly struct ActivationDelta
        {
            public readonly uint BodyId;
            public readonly bool Activated;
            public ActivationDelta(uint bodyId, bool activated) { BodyId = bodyId; Activated = activated; }
        }

        // Number of ObjectLayers = number of PhysicsLayer members. Derived from the
        // enum so the filter tables never silently drift if a layer is added.
        private static readonly uint ObjectLayerCount = (uint)Enum.GetValues(typeof(PhysicsLayer)).Length;

        // =====================================================================
        // Broad phase / object layers
        //
        // Jolt has two filtering tiers and conflating them is the classic
        // first-integration mistake:
        //
        //   ObjectLayer     - fine grained, per body, decides "can A and B ever
        //                     collide". This is our PhysicsLayer, 1:1.
        //   BroadPhaseLayer - coarse buckets the AABB tree is partitioned by.
        //                     Keep this to 3. More buckets = more tree walks.
        //
        // The win from getting this right: NON_MOVING is a separate tree that is
        // never rebuilt during normal operation. A region with 50k static prims
        // and 200 physical ones only ever re-walks the small tree.
        // =====================================================================
        private static class BroadPhase
        {
            public const byte NonMoving = 0;  // Terrain, Static
            public const byte Moving = 1;     // Dynamic, Avatar, Debris
            public const byte Sensor = 2;     // Sensor
            public const uint Count = 3;
        }

        private static byte ToBroadPhase(PhysicsLayer layer) => layer switch
        {
            PhysicsLayer.Terrain => BroadPhase.NonMoving,
            PhysicsLayer.Static => BroadPhase.NonMoving,
            PhysicsLayer.Dynamic => BroadPhase.Moving,
            PhysicsLayer.Avatar => BroadPhase.Moving,
            PhysicsLayer.Debris => BroadPhase.Moving,
            PhysicsLayer.Sensor => BroadPhase.Sensor,
            PhysicsLayer.AvatarQuery => BroadPhase.Moving, // in the broadphase so queries find it; collides with nothing
            _ => BroadPhase.Moving,
        };

        /// <summary>
        /// The collision matrix. This table IS the behaviour contract - most
        /// "why does my object fall through X" bugs are a wrong cell here.
        /// Terrain/Static never test against each other: that alone removes the
        /// dominant pair count in a built-up region.
        /// </summary>
        private static bool ShouldCollide(PhysicsLayer a, PhysicsLayer b)
        {
            // The avatar query-marker layer NEVER collides in simulation. Keeping it out of every
            // collision pair is exactly what makes the marker inert - no push, no contacts (verified) -
            // so it can be findable by queries without ever entering the solve. (M4.5, resolves #35.)
            if (a == PhysicsLayer.AvatarQuery || b == PhysicsLayer.AvatarQuery)
                return false;

            // Normalise so we only fill the lower triangle.
            if (a > b) (a, b) = (b, a);

            return (a, b) switch
            {
                (PhysicsLayer.Terrain, PhysicsLayer.Terrain) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Static) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Terrain, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Terrain, PhysicsLayer.Sensor) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Static, PhysicsLayer.Static) => false,
                (PhysicsLayer.Static, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Static, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Static, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Static, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Dynamic, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Avatar, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Avatar, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Avatar, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Sensor, PhysicsLayer.Sensor) => false,
                (PhysicsLayer.Sensor, PhysicsLayer.Debris) => false,

                // Debris vs Debris deliberately off - that is the whole point
                // of the tier. Turning it on silently reintroduces the O(n^2).
                (PhysicsLayer.Debris, PhysicsLayer.Debris) => false,

                _ => true,
            };
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Initialize(in PhysicsBackendSettings settings)
        {
            _settings = settings;

            int threads = settings.ThreadCount > 0
                ? settings.ThreadCount
                : Math.Max(1, Environment.ProcessorCount - 1);

            if (settings.DeterministicMode)
                threads = 1;

            // Native boot. false => single precision (joltc.dll), decision #2 closed.
            // Foundation.Init is idempotent-safe to pair with Foundation.Shutdown in Dispose.
            if (!Foundation.Init(false))
                throw new InvalidOperationException("Jolt Foundation.Init(false) failed (native joltc.dll not loaded).");

            // --- Object-layer collision matrix (delta #3) ---
            // ObjectLayerPairFilterTable starts with EVERY pair disabled; we turn on
            // exactly the ShouldCollide cells. Driving the table from ShouldCollide (rather
            // than hand-listing pairs) keeps the matrix the single source of truth AND
            // guarantees every one of the 21 unordered pairs is decided explicitly.
            // EnableCollision is symmetric, so we only walk the lower triangle (a <= b).
            _objectLayerPairFilter = new ObjectLayerPairFilterTable(ObjectLayerCount);
            for (uint a = 0; a < ObjectLayerCount; a++)
            {
                for (uint b = a; b < ObjectLayerCount; b++)
                {
                    if (ShouldCollide((PhysicsLayer)a, (PhysicsLayer)b))
                        _objectLayerPairFilter.EnableCollision(new ObjectLayer(a), new ObjectLayer(b));
                }
            }

            // --- ObjectLayer -> BroadPhaseLayer map (delta #3) ---
            _broadPhaseInterface = new BroadPhaseLayerInterfaceTable(ObjectLayerCount, BroadPhase.Count);
            for (uint a = 0; a < ObjectLayerCount; a++)
            {
                _broadPhaseInterface.MapObjectToBroadPhaseLayer(
                    new ObjectLayer(a),
                    new BroadPhaseLayer(ToBroadPhase((PhysicsLayer)a)));
            }

            // --- Object-vs-broadphase filter (delta #3: the third table the sketch omitted) ---
            // Built FROM the two tables above; it answers "can an object in layer X ever
            // touch broad-phase bucket Y" and is what actually prunes tree walks.
            _objectVsBroadPhaseFilter = new ObjectVsBroadPhaseLayerFilterTable(
                _broadPhaseInterface, BroadPhase.Count, _objectLayerPairFilter, ObjectLayerCount);

            var systemSettings = new PhysicsSystemSettings
            {
                MaxBodies = settings.MaxBodies,
                MaxBodyPairs = settings.MaxBodyPairs,
                MaxContactConstraints = settings.MaxContactConstraints,
                ObjectLayerPairFilter = _objectLayerPairFilter,
                BroadPhaseLayerInterface = _broadPhaseInterface,
                ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhaseFilter,
            };

            _system = new PhysicsSystem(systemSettings);
            _system.Gravity = settings.Gravity;
            _bodyInterface = _system.BodyInterface;

            // Determinism (A/B parity harness, DESIGN.md): single-threaded ALONE is not enough - Jolt
            // also needs its DeterministicSimulation flag on to guarantee bit-identical re-runs. It
            // defaults true in 2.18.6, but we set it EXPLICITLY when asked rather than lean on a default
            // that a future lib bump could flip. (Left untouched otherwise, to keep the fast path fast.)
            if (settings.DeterministicMode)
            {
                PhysicsSettings physicsSettings = _system.Settings;
                physicsSettings.DeterministicSimulation = true;
                _system.Settings = physicsSettings;
            }

            // Contacts + body activation arrive as C# EVENTS in 2.18.6 (delta #7), not a
            // listener object. The handlers ONLY enqueue / push into the ring - they never touch
            // scene state, never allocate, and never mutate the active set (see the field notes).
            _system.OnBodyActivated += HandleBodyActivated;
            _system.OnBodyDeactivated += HandleBodyDeactivated;
            _system.OnContactAdded += HandleContactAdded;
            _system.OnContactPersisted += HandleContactPersisted;
            _system.OnContactRemoved += HandleContactRemoved;

            // Worker pool (delta #4: Update takes this JobSystem; no TempAllocator).
            // Jolt's canonical limits: 2048 jobs, 8 barriers. DeterministicMode / an
            // explicit ThreadCount collapse the pool to a single worker.
            var jobConfig = new JobSystemThreadPoolConfig
            {
                maxJobs = 2048,
                maxBarriers = 8,
                numThreads = threads,
            };
            _jobSystem = new JobSystemThreadPool(jobConfig);

            // Avatar-vs-avatar collision registry (characters add themselves on create).
            _charVsChar = new CharacterVsCharacterCollisionSimple();

            // Contact ring is allocated now; it is engine-agnostic. Wiring it to Jolt is
            // deferred: 2.18.6 exposes contacts as EVENTS on PhysicsSystem
            // (OnContactAdded/Persisted/Removed), NOT a SetContactListener object as the
            // sketch assumed (delta #7). Likewise the Task 4 active-body drain will subscribe
            // OnBodyActivated / OnBodyDeactivated to keep an O(active) set.
            _contactListener = new LegionContactListener(_settings.MaxContactConstraints * 2);
        }

        public void Dispose()
        {
            // Characters own native CharacterVirtual objects + shapes and hold a ref to _system, so
            // dispose them BEFORE the system teardown below.
            lock (_characterGate)
            {
                foreach (JoltCharacterRecord rec in _characterList)
                {
                    rec.Character?.Dispose();
                    rec.Character = null;
                    rec.StandingShape?.Dispose();
                    rec.StandingShape = null;
                    rec.InnerCapsule?.Dispose();
                    rec.InnerCapsule = null;
                }
                _characterList.Clear();
                _charVsChar?.Dispose();
                _charVsChar = null;
            }

            // Query-filter callback objects.
            foreach (LayerQueryFilter f in _queryFilters.Values)
                f.Dispose();
            _queryFilters.Clear();

            // Legion-side handle tables first (pure managed bookkeeping).
            _constraints.Clear();
            _characters.Clear();
            _bodies.Clear();
            _shapes.Clear();
            _joltToRecord.Clear();
            _activeBodies.Clear();
            while (_activationQueue.TryDequeue(out _)) { }

            // Unsubscribe before teardown so no worker-thread callback fires into a half-disposed
            // backend during the final Update-drain window.
            if (_system != null)
            {
                _system.OnBodyActivated -= HandleBodyActivated;
                _system.OnBodyDeactivated -= HandleBodyDeactivated;
                _system.OnContactAdded -= HandleContactAdded;
                _system.OnContactPersisted -= HandleContactPersisted;
                _system.OnContactRemoved -= HandleContactRemoved;
            }

            // Native teardown order (delta #6): system -> jobs -> filters -> Foundation.
            // The PhysicsSystem holds the filter interfaces and steps on the job system,
            // so it must go down first.
            _system?.Dispose();
            _system = null;

            _jobSystem?.Dispose();
            _jobSystem = null;

            _objectVsBroadPhaseFilter?.Dispose();
            _objectVsBroadPhaseFilter = null;
            _broadPhaseInterface?.Dispose();
            _broadPhaseInterface = null;
            _objectLayerPairFilter?.Dispose();
            _objectLayerPairFilter = null;

            Foundation.Shutdown();
        }

        // =====================================================================
        // Jolt event callbacks (delta #7). WORKER-THREAD context: enqueue / push only.
        // No allocation, no scene-state access, no mutation of _activeBodies.
        // =====================================================================

        private void HandleBodyActivated(PhysicsSystem system, in BodyID bodyID, ulong bodyUserData)
            => _activationQueue.Enqueue(new ActivationDelta(bodyID.ID, true));

        private void HandleBodyDeactivated(PhysicsSystem system, in BodyID bodyID, ulong bodyUserData)
            => _activationQueue.Enqueue(new ActivationDelta(bodyID.ID, false));

        private void HandleContactAdded(
            PhysicsSystem system, in Body body1, in Body body2,
            in ContactManifold manifold, ref ContactSettings settings)
        {
            PushContact(in body1, in body2, in manifold, in settings, ContactPhase.Begin);
        }

        private void HandleContactPersisted(
            PhysicsSystem system, in Body body1, in Body body2,
            in ContactManifold manifold, ref ContactSettings settings)
        {
            PushContact(in body1, in body2, in manifold, in settings, ContactPhase.Persist);
        }

        private void HandleContactRemoved(PhysicsSystem system, ref SubShapeIDPair pair)
        {
            // The pair separated (or a body was destroyed): no manifold, so no point/normal/impulse.
            // Still resolve both sides from the reverse map for the collision_end dispatch above. If
            // one body was just removed its record may already be gone -> that side reports Invalid,
            // which is correct (there is nothing left to name).
            _joltToRecord.TryGetValue(pair.Body1ID.ID, out JoltBodyRecord? ra);
            _joltToRecord.TryGetValue(pair.Body2ID.ID, out JoltBodyRecord? rb);
            // End carries the sub-shape pair, so name the struck child on each side (the module ignores the
            // End phase today - OpenSim derives ends from absence - but keep the identity correct for parity).
            _contactListener.Push(BuildContact(ra, rb, default, default, 0f, ContactPhase.End,
                ResolveStruckPart(ra, pair.SubShapeID1), ResolveStruckPart(rb, pair.SubShapeID2)));
        }

        // The STRUCK part's UserData: the compound child hit (ResolveChildUserData), or the body's own
        // UserData for a single-shape body. This is the per-contact link identity (llDetectedLinkNumber).
        private uint ResolveStruckPart(JoltBodyRecord? rec, uint subShapeId)
        {
            uint child = ResolveChildUserData(rec, subShapeId);
            return child != 0 ? child : (rec?.UserData ?? 0u);
        }

        // WORKER-THREAD context. Resolve both sides from the reverse map (never lock a body), apply the
        // Persist gate, estimate the impulse, and push into the ring. No allocation, no scene state.
        private void PushContact(
            in Body body1, in Body body2, in ContactManifold manifold,
            in ContactSettings settings, ContactPhase phase)
        {
            _joltToRecord.TryGetValue(body1.ID.ID, out JoltBodyRecord? ra);
            _joltToRecord.TryGetValue(body2.ID.ID, out JoltBodyRecord? rb);

            // Persist gate (DESIGN.md #4). Persist fires every step for every touching pair; forward it
            // ONLY when a body in the pair wants contact events (has a collision handler). Begin/End are
            // cheap edge events and are never gated. Empirically Jolt STOPS firing Persist once a body
            // sleeps, so this gate only ever suppresses awake-but-touching pairs (e.g. an avatar
            // standing still). Final #4 policy is John's call - this is the mechanism, on by design.
            if (phase == ContactPhase.Persist &&
                !((ra?.WantsContactEvents ?? false) || (rb?.WantsContactEvents ?? false)))
                return;

            // Point on body 1, and the manifold normal. Jolt's WorldSpaceNormal points body1 -> body2,
            // which IS our A->B convention (A = body1) - verified on a box-on-ground drop (normal +Z,
            // ground=A -> box=B). No sign flip.
            Vector3 point = manifold.PointCount > 0 ? manifold.GetWorldSpaceContactPointOn1(0) : default;
            Vector3 normal = manifold.WorldSpaceNormal;

            // Impulse is a POST-solve quantity but Added/Persisted fire PRE-solve, so we use Jolt's own
            // in-callback estimator - the same helper its collision-sound sample uses. It reads only the
            // two bodies Jolt already handed us (NOT a lock we take) plus the manifold, and is
            // allocation-free (measured ~0 bytes/call). Sum the per-point NORMAL impulses -> newton-seconds.
            float impulse = 0f;
            // Fully qualified: our own namespace is Legion.Physics.Jolt, which would otherwise shadow
            // the JoltPhysicsSharp.Jolt static helper class.
            JoltPhysicsSharp.Jolt.EstimateCollisionResponse(
                body1, body2, manifold, out CollisionEstimationResult response,
                settings.CombinedFriction, settings.CombinedRestitution,
                MinVelocityForRestitution, Math.Max(1, _settings.VelocityIterations));
            ReadOnlySpan<CollisionEstimationResult.Impulse> impulses = response.Impulses;
            for (int i = 0; i < impulses.Length; i++)
                impulse += impulses[i].ContactImpulse;

            // Name the struck part on each side from the contact sub-shape (child of a linkset, or the body
            // itself) - the per-child collision identity behind llDetectedLinkNumber.
            uint childA = ResolveStruckPart(ra, manifold.SubShapeID1.Value);
            uint childB = ResolveStruckPart(rb, manifold.SubShapeID2.Value);
            _contactListener.Push(BuildContact(ra, rb, point, normal, MathF.Max(0f, impulse), phase, childA, childB));
        }

        private static ContactReport BuildContact(
            JoltBodyRecord? ra, JoltBodyRecord? rb, Vector3 point, Vector3 normal, float impulse, ContactPhase phase,
            uint childUserDataA, uint childUserDataB)
        {
            return new ContactReport
            {
                BodyA = ra != null ? new BodyId(ra.Handle) : BodyId.Invalid,
                BodyB = rb != null ? new BodyId(rb.Handle) : BodyId.Invalid,
                UserDataA = ra != null ? ra.UserData : 0u,
                UserDataB = rb != null ? rb.UserData : 0u,
                ChildUserDataA = childUserDataA,
                ChildUserDataB = childUserDataB,
                Point = point,
                Normal = normal,
                Impulse = impulse,
                Phase = phase,
            };
        }

        // =====================================================================
        // Shapes
        // =====================================================================

        public ShapeId CreateBoxShape(Vector3 halfExtents)
        {
            float minHalf = MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z));
            float convexRadius = MathF.Max(0f, MathF.Min(DefaultConvexRadius, minHalf * 0.1f));
            var shape = new BoxShape(halfExtents, convexRadius);
            return RegisterShape(shape);
        }

        public ShapeId CreateSphereShape(float radius)
            => RegisterShape(new SphereShape(MathF.Max(0.001f, radius)));

        public ShapeId CreateCapsuleShape(float halfHeight, float radius)
            => RegisterShape(new CapsuleShape(MathF.Max(0.001f, halfHeight), MathF.Max(0.001f, radius)));

        public ShapeId CreateCylinderShape(float halfHeight, float radius)
        {
            float hh = MathF.Max(0.001f, halfHeight);
            float r = MathF.Max(0.001f, radius);
            // Jolt's CylinderShape axis is Y (like the capsule); prim orientation is the layer's job.
            // Convex radius must be <= min(radius, halfHeight) or Jolt asserts - clamp like the box path.
            float cr = MathF.Max(0f, MathF.Min(DefaultConvexRadius, MathF.Min(r, hh) * 0.1f));
            using var settings = new CylinderShapeSettings(hh, r, cr);
            return RegisterShape(settings.Create());
        }

        public ShapeId CreateConvexHullShape(ReadOnlySpan<Vector3> points)
        {
            if (points.Length < 4)
                throw new ArgumentException($"convex hull needs >= 4 points; got {points.Length}.");
            using var settings = new ConvexHullShapeSettings(points, DefaultConvexRadius);
            return RegisterShape(settings.Create());
        }

        public ShapeId CreateMeshShape(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
        {
            // Cook once per ASSET, never per prim. Key the cache on the mesh asset UUID plus LOD, and
            // hand the same ShapeId to every prim that references it. Cooking is the single most
            // expensive operation here and re-cooking per prim is how region startup gets slow.
            // NOTE: a MeshShape reports Volume 0 (Jolt does not integrate triangle-soup volume), so a
            // DYNAMIC body on a mesh gets the clamped fallback mass - meshes are meant to be static.
            if (indices.Length % 3 != 0)
                throw new ArgumentException($"mesh index count {indices.Length} is not a multiple of 3.");
            int triCount = indices.Length / 3;
            var tris = new IndexedTriangle[triCount];
            for (int t = 0; t < triCount; t++)
                tris[t] = new IndexedTriangle(indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2], 0u, 0u);
            var verts = vertices.ToArray();
            using var settings = new MeshShapeSettings(verts.AsSpan(), tris.AsSpan());
            return RegisterShape(settings.Create());
        }

        public ShapeId CreateCompoundShape(ReadOnlySpan<CompoundChild> children)
        {
            // Linksets. StaticCompoundShape (not mutable) builds a small internal tree and is markedly
            // faster to query - the right choice for a rigid linkset. Each child's UserData is stored in
            // order so a raycast/contact hit can name WHICH child prim was struck: Jolt encodes the
            // child index in the LOW SubShapeIDBitsRecursive bits of the hit's SubShapeID (verified),
            // which we decode in ResolveChildUserData.
            // Jolt's StaticCompoundShapeSettings.Create() ACCESS-VIOLATES with fewer than 2 sub-shapes.
            // A single-member set must use that member's shape directly, not a degenerate compound - which
            // is exactly what the linkset path does (a compound is only built for root + >=1 child = >=2
            // sub-shapes; a linkset down to one member reverts to the plain single-prim body). Guard here so
            // a stray 1-child call is a clear exception, never a native crash.
            if (children.Length < 2)
                throw new ArgumentException($"StaticCompoundShape requires >= 2 children (got {children.Length}); use the single member's shape directly.");

            var childUserData = new uint[children.Length];
            using var settings = new StaticCompoundShapeSettings();
            for (int i = 0; i < children.Length; i++)
            {
                CompoundChild c = children[i];
                if (!_shapes.TryGet(c.Shape.Value, out JoltShapeRecord childRec) || childRec.NativeShape == null)
                    throw new ArgumentException($"CreateCompoundShape: child {i} ({c.Shape}) is not a live shape.");
                // Create() AddRefs each child, so the child native survives via the compound even after
                // the caller releases the child's Legion handle.
                settings.AddShape(c.Position, c.Orientation, childRec.NativeShape, c.UserData);
                childUserData[i] = c.UserData;
            }

            // The compound's own index bits: smallest b with (1<<b) >= childCount (0 for a single child).
            int bits = 0;
            while ((1 << bits) < children.Length) bits++;

            var rec = new JoltShapeRecord
            {
                NativeShape = settings.Create(),
                RefCount = 1,
                IsWrapper = true,
                CompoundChildUserData = childUserData,
                CompoundIndexBits = bits,
            };
            return new ShapeId(_shapes.Add(rec));
        }

        public ShapeId CreateHeightFieldShape(
            ReadOnlySpan<float> heights, int sampleCountX, int sampleCountY, Vector3 scale)
        {
            // Jolt HeightFieldShape is SQUARE (one sample count) and Y-UP: a sample at grid
            // (col,row) sits at scale * (col, height, row) - the height axis is Jolt's Y and the
            // grid spans X and Z. Legion's world is Z-up (gravity -Z), so we HIDE the Jolt quirk
            // inside this method (nothing above ILegionPhysicsBackend knows Jolt exists): cook the
            // Y-up field, then wrap it in a RotatedTranslatedShape and return the WRAPPER's handle,
            // which is already Z-up-correct and self-consistent for any caller/query. See below.
            //
            // Sample-count constraint (verified empirically vs joltc 2.18.6 - see the MILESTONE notes):
            // the managed HeightFieldShapeSettings exposes NO block-size / bits-per-sample setter, so the
            // native default block size is used. joltc is a RELEASE build with Jolt's asserts compiled
            // out, so a bad count does NOT throw - it silently mis-cooks (n>=3 incl. odd/non-PoT all
            // return a non-null shape; only n<3 fails). The M1 "257 result" PROVED an odd/prime count
            // reproduces its input faithfully (block divisibility is a NON-constraint), and the varregion
            // decision is CLOSED on one (N+1)-square field per region (257 for a 256 m region, 513 for a
            // 512 m one). So the M6 terrain feed hands ODD (N+1) counts, and this guard now accepts
            // square + >= a sane floor - NOT power-of-two. Non-square is padded to square (edge
            // replication) by the terrain feed above the seam; we still reject it here defensively.
            if (sampleCountX != sampleCountY)
                throw new ArgumentException(
                    $"Jolt HeightFieldShape is square; got {sampleCountX}x{sampleCountY}. " +
                    "Non-square regions must be padded to square (edge replication) before cooking.");
            int n = sampleCountX;
            if (n < MinHeightFieldSampleCount)
                throw new ArgumentException(
                    $"HeightFieldShape sample count must be >= {MinHeightFieldSampleCount}; got {n} " +
                    "(n<3 silently mis-cooks in joltc 2.18.6).");
            if (heights.Length < n * n)
                throw new ArgumentException($"height buffer too small: need {n * n} samples, got {heights.Length}.");

            // The caller's `scale` is in Legion Z-up terms: (X spacing, Y spacing, height scale).
            // Jolt wants (X spacing, HEIGHT scale, Z spacing), so swap Y<->Z going in.
            Vector3 joltScale = new Vector3(scale.X, scale.Z, scale.Y);

            // Convention: heights[y*N + x] is the height at grid (x, y), and must land at world
            // (x, y). The RotatedTranslatedShape wrapper (below) maps Jolt grid-row r to world
            // Y = (N-1-r) - a north-south flip - so we ROW-REVERSE going in (input row y -> Jolt
            // row N-1-y) to cancel it. X is untouched (no X mirror). Verified by the harness's
            // asymmetric per-quadrant check. (settings copies into native storage, so this cook-time
            // temp array is fine - once per terrain asset, not per frame.)
            float[] samples = new float[n * n];
            for (int jy = 0; jy < n; jy++)
                heights.Slice((n - 1 - jy) * n, n).CopyTo(samples.AsSpan(jy * n, n));
            Vector3 offset = Vector3.Zero;
            Shape inner;
            // 2.19.x: HeightFieldShapeSettings takes (float* samples, offset, scale, uint sampleCount)
            // (was float[]/int in 2.18.6). Pin the cook-time temp array; Create() copies into native storage.
            unsafe
            {
                fixed (float* pSamples = samples)
                {
                    var hfSettings = new HeightFieldShapeSettings(pSamples, offset, joltScale, (uint)n);
                    try { inner = hfSettings.Create(); }
                    finally { hfSettings.Dispose(); }
                }
            }

            try
            {
                // R_x(+90) sends Jolt's +Y (height) to world +Z (up). A proper rotation can't also
                // keep the row axis on +Y (that swap is a reflection), so it lands on -Y; the
                // (N-1)*Yspacing translation lifts the field back into the +Y quadrant. Net: the
                // shape, placed at the origin, occupies X in [0,(N-1)*sx], Y in [0,(N-1)*sy], with
                // height along +Z. The -Y row flip this introduces is cancelled by the row-reverse
                // when building `samples` above, so input (x,y) lands at world (x,y) - no mirror.
                Vector3 posW = new Vector3(0f, (n - 1) * scale.Y, 0f);
                Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

                Shape wrapper;
                using (var wrapSettings = new RotatedTranslatedShapeSettings(posW, rot, inner))
                    wrapper = wrapSettings.Create();

                // The wrapper OWNS the inner shape (private, not caller-visible): both are disposed
                // together when this handle's RefCount hits 0.
                var rec = new JoltShapeRecord
                {
                    NativeShape = wrapper,
                    InnerShape = inner,
                    RefCount = 1,
                    IsWrapper = true,
                };
                return new ShapeId(_shapes.Add(rec));
            }
            catch
            {
                inner.Dispose();
                throw;
            }
        }

        public ShapeId CreateScaledShape(ShapeId baseShape, Vector3 scale)
        {
            // The whole reason this is on the interface: prim resize wraps the cooked shape in a
            // ScaledShape - cheap, shares the underlying geometry, no re-cook. Verified per-type in
            // 2.18.6: box/hull/mesh accept ANY scale (incl. mirror/tri-non-uniform); sphere/capsule
            // reject non-uniform (MakeScaleValid uniform-ises to the mean); cylinder allows an axial
            // scale with UNIFORM radial only. We MakeScaleValid so we never cook a distorted/invalid
            // shape; the layer above can pre-check IsValidScale if it wants to degrade differently
            // (e.g. swap a non-uniformly-scaled sphere for an ellipsoid hull) rather than accept the clamp.
            if (!_shapes.TryGet(baseShape.Value, out JoltShapeRecord baseRec) || baseRec.NativeShape == null)
                throw new ArgumentException($"CreateScaledShape: {baseShape} is not a live shape.");

            Vector3 valid = baseRec.NativeShape.MakeScaleValid(scale);
            using var settings = new ScaledShapeSettings(baseRec.NativeShape, valid);
            var rec = new JoltShapeRecord
            {
                NativeShape = settings.Create(),  // AddRefs the base; base survives via its own Legion handle
                RefCount = 1,
                IsWrapper = true,
                BaseShape = baseShape,
            };
            return new ShapeId(_shapes.Add(rec));
        }

        public void AddShapeRef(ShapeId shape)
        {
            if (_shapes.TryGet(shape.Value, out JoltShapeRecord rec))
                Interlocked.Increment(ref rec.RefCount);
        }

        public void ReleaseShape(ShapeId shape)
        {
            if (!_shapes.TryGet(shape.Value, out JoltShapeRecord rec))
                return;
            if (Interlocked.Decrement(ref rec.RefCount) <= 0)
            {
                // Last Legion reference gone. Dispose our managed Shape wrapper (releases one
                // native ref). Any Body still using the shape holds its OWN native ref, so the
                // native RefTarget survives until that body is destroyed - no premature free.
                // A wrapper also owns its private inner shape (heightfield under the Z-up wrapper),
                // so dispose that too.
                rec.NativeShape?.Dispose();
                rec.NativeShape = null;
                rec.InnerShape?.Dispose();
                rec.InnerShape = null;
                _shapes.Remove(shape.Value);
            }
        }

        // Registers a freshly-created Jolt shape, RefCount = 1 (the creator's reference).
        private ShapeId RegisterShape(Shape shape)
        {
            var rec = new JoltShapeRecord { NativeShape = shape, RefCount = 1 };
            return new ShapeId(_shapes.Add(rec));
        }

        // =====================================================================
        // Bodies
        // =====================================================================

        public BodyId CreateBody(in BodyDesc desc)
        {
            if (_system == null)
                throw new InvalidOperationException("CreateBody before Initialize.");
            if (!_shapes.TryGet(desc.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                throw new ArgumentException($"CreateBody: {desc.Shape} is not a live shape handle.");

            MotionType joltMotion = ToJoltMotion(desc.MotionType);
            bool movable = desc.MotionType != BodyMotionType.Static;

            var objectLayer = new ObjectLayer((uint)desc.Layer);
            var bcs = new BodyCreationSettings(
                shapeRec.NativeShape, desc.Position, desc.Orientation, joltMotion, objectLayer);
            float mass = 0f;
            try
            {
                bcs.Friction = desc.Friction;
                bcs.Restitution = desc.Restitution;
                bcs.IsSensor = desc.IsSensor;
                bcs.UserData = desc.UserData;

                if (movable)
                {
                    // Velocities, damping, gravity factor and CCD only mean anything for a body that
                    // actually moves; a Static body has no MotionProperties to hold them.
                    bcs.LinearVelocity = desc.LinearVelocity;
                    bcs.AngularVelocity = desc.AngularVelocity;
                    bcs.LinearDamping = MathF.Max(0f, desc.LinearDamping);
                    bcs.AngularDamping = MathF.Max(0f, desc.AngularDamping);
                    bcs.GravityFactor = desc.GravityFactor;
                    bcs.MotionQuality = desc.UseCcd ? MotionQuality.LinearCast : MotionQuality.Discrete;

                    // Let this body flip Dynamic<->Kinematic<->Static later (SetBodyMotionType). A body
                    // created Static deliberately does NOT get this: allocating MotionProperties for
                    // every one of a region's tens of thousands of non-physical prims is exactly the
                    // memory regression DESIGN.md's DontActivate note guards against. A prim that can
                    // go physical must therefore be CREATED movable, not created static and promoted.
                    bcs.AllowDynamicOrKinematic = true;
                }

                if (desc.MotionType == BodyMotionType.Dynamic)
                {
                    // Mass policy (DESIGN.md / BodyDesc): explicit Mass wins; else shape volume x
                    // Density. We ALWAYS override rather than trust the shape's baked density, because
                    // shapes are shared/refcounted across prims and carry Jolt's default 1000 kg/m^3 -
                    // the per-body Density lives in BodyDesc, not the shape. CalculateInertia keeps the
                    // inertia TENSOR derived from the real geometry, scaled to this mass (verified
                    // exact: asked 42 -> body mass 42.0000).
                    mass = ComputeMass(shapeRec, desc);
                    bcs.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
                    bcs.MassPropertiesOverride = new MassProperties { Mass = mass };
                }

                // The load-bearing line (DESIGN.md): do NOT wake on insert unless asked. A region
                // rezzing tens of thousands of prims with Activate is a pathological startup stall.
                Activation activation = desc.StartActive ? Activation.Activate : Activation.DontActivate;
                BodyID joltId = _bodyInterface.CreateAndAddBody(bcs, activation);

                var rec = new JoltBodyRecord
                {
                    NativeBodyId = joltId.ID,
                    Shape = desc.Shape,
                    Layer = desc.Layer,
                    MotionType = desc.MotionType,
                    UserData = desc.UserData,
                    WantsContactEvents = desc.WantsContactEvents,
                    Mass = mass,
                    AllowMotionChange = movable,
                };
                uint handle = _bodies.Add(rec);
                rec.Handle = handle;
                _joltToRecord[joltId.ID] = rec;
                return new BodyId(handle);
            }
            finally
            {
                // CreateAndAddBody copies the settings; the managed settings object is ours to free.
                bcs.Dispose();
            }
        }

        private static MotionType ToJoltMotion(BodyMotionType t) => t switch
        {
            BodyMotionType.Static => MotionType.Static,
            BodyMotionType.Kinematic => MotionType.Kinematic,
            BodyMotionType.Dynamic => MotionType.Dynamic,
            _ => MotionType.Static,
        };

        // Explicit mass wins; otherwise shape volume x density. Clamped to a small positive so a
        // degenerate (zero-volume) shape can never yield a zero/negative-mass dynamic body, whose
        // inverse mass would be infinite acceleration.
        private static float ComputeMass(JoltShapeRecord shapeRec, in BodyDesc desc)
        {
            if (desc.Mass > 0f)
                return desc.Mass;
            float volume = shapeRec.NativeShape != null ? shapeRec.NativeShape.Volume : 0f;
            float density = desc.Density > 0f ? desc.Density : 1000f;
            return MathF.Max(volume * density, 1e-3f);
        }

        // Body-handle -> live Jolt id. Returns false (idempotent no-op for callers) on a stale/invalid
        // handle, matching RemoveBody's contract.
        private bool TryResolve(BodyId body, out JoltBodyRecord rec, out BodyID jid)
        {
            if (_bodies.TryGet(body.Value, out rec))
            {
                jid = new BodyID(rec.NativeBodyId);
                return true;
            }
            jid = default;
            return false;
        }

        // Force/impulse resolution: only DYNAMIC bodies respond. Static bodies have no MotionProperties
        // (Add* would dereference null natively); kinematic bodies are script/animation-driven and
        // ignore forces. This mirrors SL, where llApplyImpulse et al. only affect physical objects.
        private bool TryResolveDynamic(BodyId body, out BodyID jid)
        {
            if (_bodies.TryGet(body.Value, out JoltBodyRecord rec) && rec.MotionType == BodyMotionType.Dynamic)
            {
                jid = new BodyID(rec.NativeBodyId);
                return true;
            }
            jid = default;
            return false;
        }

        public void RemoveBody(BodyId body)
        {
            if (!_bodies.TryGet(body.Value, out JoltBodyRecord rec))
                return; // stale/invalid handle - idempotent no-op.
            if (rec.IsCharacterMarker)
                return; // an avatar query-marker is owned by its character; RemoveCharacter destroys it.

            var joltId = new BodyID(rec.NativeBodyId);
            _bodyInterface.RemoveAndDestroyBody(joltId);
            _joltToRecord.TryRemove(rec.NativeBodyId, out _);
            _bodies.Remove(body.Value); // bumps the generation so the stale handle fails validation.
            // _activeBodies is step-thread-owned; if this body happened to be active, the stale
            // id is self-healed at the top of Step (it no longer resolves via _joltToRecord).
        }

        public bool IsBodyValid(BodyId body) => _bodies.IsValid(body.Value);

        public void SetBodyShape(BodyId body, ShapeId shape, bool recomputeMass)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            if (!_shapes.TryGet(shape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                throw new ArgumentException($"SetBodyShape: {shape} is not a live shape handle.");
            // Do not wake the body just because its shape changed (activation stays the caller's call).
            _bodyInterface.SetShape(jid, shapeRec.NativeShape, recomputeMass, Activation.DontActivate);
            rec.Shape = shape;
        }

        // Map a hit's SubShapeID to the struck child's UserData for a compound (linkset) body. Jolt puts
        // the child index in the LOW CompoundIndexBits of the SubShapeID (root shape peels first, from
        // the low end - so even a mesh child's own sub-bits sit ABOVE these). Non-compound => 0.
        private uint ResolveChildUserData(JoltBodyRecord? bodyRec, uint subShapeId)
        {
            if (bodyRec == null)
                return 0u;
            if (!_shapes.TryGet(bodyRec.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.CompoundChildUserData == null)
                return 0u;
            uint[] list = shapeRec.CompoundChildUserData;
            int bits = shapeRec.CompoundIndexBits;
            uint mask = bits >= 32 ? uint.MaxValue : (1u << bits) - 1u;
            int idx = (int)(subShapeId & mask);
            return (idx >= 0 && idx < list.Length) ? list[idx] : 0u;
        }

        public void SetBodyMotionType(BodyId body, BodyMotionType motionType, bool activate)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            if (motionType != BodyMotionType.Static && !rec.AllowMotionChange)
                throw new InvalidOperationException(
                    "SetBodyMotionType to a movable type needs a body created Dynamic or Kinematic " +
                    "(a Static body has no MotionProperties to promote). Create it movable up front " +
                    "if it can ever go physical.");
            _bodyInterface.SetMotionType(jid, ToJoltMotion(motionType),
                activate ? Activation.Activate : Activation.DontActivate);
            rec.MotionType = motionType;
        }

        public void SetBodyLayer(BodyId body, PhysicsLayer layer) => throw new NotImplementedException();

        // Move a body IN PLACE - Jolt's BodyInterface repositions the existing body (velocity, contacts,
        // BodyID all preserved); it does NOT destroy/recreate. This is the real reposition (JoltPhysicsSharp
        // 2.19.1 exposes SetPositionAndRotation - the earlier stub was deferred, not a native gap). A no-op
        // on an inert body just sets its transform; activate:true wakes it (used when a live/vehicle body is
        // repositioned so it keeps stepping). Resolve failure (destroyed body) is a safe no-op.
        public void SetBodyTransform(BodyId body, Vector3 position, Quaternion orientation, bool activate)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetPositionAndRotation(jid, position, orientation,
                    activate ? Activation.Activate : Activation.DontActivate);
        }

        public void SetBodyLinearVelocity(BodyId body, Vector3 velocity)
        {
            // Thin seam: this does NOT wake a sleeping body (Jolt-native behaviour - only Apply*
            // impulses activate). A velocity set on a sleeping body takes effect only once something
            // else activates it; that activation policy belongs to the layer above, not here.
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetLinearVelocity(jid, velocity);
        }

        public void SetBodyAngularVelocity(BodyId body, Vector3 velocity)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetAngularVelocity(jid, velocity);
        }

        public void SetBodyMass(BodyId body, float mass)
        {
            if (mass <= 0f || !TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            rec.Mass = mass;
            if (rec.MotionType != BodyMotionType.Dynamic)
                return; // mass is inert for static/kinematic motion; recorded for a later flip to Dynamic.

            // No BodyInterface.SetMass in 2.18.6. Take the shape's geometry-correct mass properties,
            // scale them to the target mass (keeps the inertia tensor's SHAPE, changes only its
            // magnitude), and push them through a body write-lock.
            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                {
                    Body b = lockWrite.Body;
                    MassProperties mp = b.Shape.MassProperties;
                    mp.ScaleToMass(mass);
                    MotionProperties motion = b.MotionProperties;
                    motion.SetMassProperties(motion.AllowedDOFs, mp);
                }
            }
            finally { bli.UnlockWrite(lockWrite); }
        }

        // Read the recorded body mass (set at creation to explicit Mass or ComputeMass = Volume x Density,
        // and updated by SetBodyMass). Read-only - does not touch the simulation. Used for A/B mass parity.
        public float GetBodyMass(BodyId body)
        {
            return TryResolve(body, out JoltBodyRecord rec, out _) ? rec.Mass : 0f;
        }

        // Local principal moments of inertia (diagonal), read from the live MotionProperties.
        // Jolt stores the INVERSE diagonal; invert per component (0 stays 0 - a locked/infinite axis).
        public Vector3 GetBodyInertiaDiagonal(BodyId body)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType != BodyMotionType.Dynamic)
                return Vector3.Zero;

            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockRead(jid, out BodyLockRead lockRead);
            try
            {
                if (!lockRead.Succeeded)
                    return Vector3.Zero;
                Vector3 inv = lockRead.Body.MotionProperties.InverseInertiaDiagonal;
                return new Vector3(
                    inv.X > 0f ? 1f / inv.X : 0f,
                    inv.Y > 0f ? 1f / inv.Y : 0f,
                    inv.Z > 0f ? 1f / inv.Z : 0f);
            }
            finally { bli.UnlockRead(lockRead); }
        }

        // Recompute the dynamic mass from the shape's geometric volume and a PHYSICAL density (kg/m^3),
        // then apply it via the same mass-property scaling path as SetBodyMass. Used so the module can
        // honour SceneObjectPart.Density (x DensityScaleFactor) for BulletSim mass parity.
        public void SetBodyDensity(BodyId body, float physicalDensity)
        {
            if (physicalDensity <= 0f || !TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                {
                    Body b = lockWrite.Body;
                    float mass = MathF.Max(b.Shape.Volume * physicalDensity, 1e-3f);
                    rec.Mass = mass;
                    if (rec.MotionType == BodyMotionType.Dynamic)
                    {
                        MassProperties mp = b.Shape.MassProperties;
                        mp.ScaleToMass(mass);
                        MotionProperties motion = b.MotionProperties;
                        motion.SetMassProperties(motion.AllowedDOFs, mp);
                    }
                }
            }
            finally { bli.UnlockWrite(lockWrite); }
        }

        public void SetBodyFriction(BodyId body, float friction)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetFriction(jid, friction);
        }

        public void SetBodyRestitution(BodyId body, float restitution)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetRestitution(jid, restitution);
        }

        public void SetBodyDamping(BodyId body, float linear, float angular)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType == BodyMotionType.Static)
                return; // no MotionProperties on a static body.

            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                {
                    MotionProperties motion = lockWrite.Body.MotionProperties;
                    motion.LinearDamping = MathF.Max(0f, linear);
                    motion.AngularDamping = MathF.Max(0f, angular);
                }
            }
            finally { bli.UnlockWrite(lockWrite); }
        }

        public void SetBodyGravityFactor(BodyId body, float factor)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType == BodyMotionType.Static)
                return; // static bodies never feel gravity; SetGravityFactor would touch null motion props.
            _bodyInterface.SetGravityFactor(jid, factor);
        }

        public void SetBodyAxisLocks(BodyId body, Vector3 allowedTranslation, Vector3 allowedRotation)
        {
            // Jolt: SixDOFConstraint to world, or MotionProperties mass/inertia
            // scaling. The constraint route is more predictable; the inertia
            // route is cheaper. Start with the constraint and measure.
            throw new NotImplementedException();
        }

        // Apply* only act on DYNAMIC bodies (see TryResolveDynamic). All of these auto-activate a
        // sleeping body - AddForce and AddImpulse were both verified to wake it - which matches SL's
        // wake-on-impulse behaviour. AddForce/AddTorque accumulate and are consumed by the next Step;
        // AddImpulse/AddAngularImpulse change velocity instantly (delta v = impulse / mass).
        public void ApplyForce(BodyId body, Vector3 force)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddForce(jid, force);
        }

        public void ApplyTorque(BodyId body, Vector3 torque)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddTorque(jid, torque);
        }

        public void ApplyImpulse(BodyId body, Vector3 impulse)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddImpulse(jid, impulse);
        }

        public void ApplyImpulseAtPoint(BodyId body, Vector3 impulse, Vector3 worldPoint)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddImpulse(jid, impulse, worldPoint);
        }

        public void ApplyAngularImpulse(BodyId body, Vector3 angularImpulse)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddAngularImpulse(jid, angularImpulse);
        }

        public void ApplyBuoyancy(
            BodyId body, float waterHeight, float buoyancy, float linearDrag, float angularDrag)
        {
            // Jolt: Body.ApplyBuoyancyImpulse(surfacePosition, surfaceNormal,
            //         buoyancy, linearDrag, angularDrag, fluidVelocity,
            //         gravity, deltaTime)
            // Must be called every step while submerged - it is an impulse, not
            // a persistent state. This should replace the hand-rolled lift in
            // the ported boat model.
            throw new NotImplementedException();
        }

        public void ActivateBody(BodyId body)
        {
            // Static bodies are never active; skip so we don't touch a body with no MotionProperties.
            if (TryResolve(body, out JoltBodyRecord rec, out BodyID jid) && rec.MotionType != BodyMotionType.Static)
                _bodyInterface.ActivateBody(jid);
        }

        public void DeactivateBody(BodyId body)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.DeactivateBody(jid);
        }

        // Allow/forbid sleeping (vehicles forbid it while active - Bullet's DISABLE_DEACTIVATION).
        // Needs a body write-lock: AllowSleeping lives on the Body, not the BodyInterface.
        public void SetBodyAllowSleeping(BodyId body, bool allow)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType == BodyMotionType.Static)
                return; // static bodies have no MotionProperties and never sleep/wake.

            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                    lockWrite.Body.SetAllowSleeping(allow);
            }
            finally { bli.UnlockWrite(lockWrite); }
        }

        // Toggle the Persist gate for a LIVE body (subscription happens after CreateBody). Begin/End always
        // report; Persist (the ongoing-touch stream that drives the script `collision` event) is forwarded
        // only when a body in the pair wants events. A prim's collision-script subscription flips this.
        public void SetBodyWantsContactEvents(BodyId body, bool wants)
        {
            if (_bodies.TryGet(body.Value, out JoltBodyRecord rec))
                rec.WantsContactEvents = wants;
        }

        public bool TryGetBodyState(BodyId body, out BodyState state)
        {
            if (!_bodies.TryGet(body.Value, out JoltBodyRecord rec))
            {
                state = default;
                return false;
            }
            var joltId = new BodyID(rec.NativeBodyId);
            state = new BodyState
            {
                Body = body,
                UserData = rec.UserData,
                Position = _bodyInterface.GetPosition(joltId),
                Orientation = _bodyInterface.GetRotation(joltId),
                LinearVelocity = _bodyInterface.GetLinearVelocity(joltId),
                AngularVelocity = _bodyInterface.GetAngularVelocity(joltId),
                Flags = _bodyInterface.IsActive(joltId) ? BodyStateFlags.Active : BodyStateFlags.None,
            };
            return true;
        }

        // =====================================================================
        // Characters
        // =====================================================================

        public CharacterId CreateCharacter(in CharacterDesc desc)
        {
            // CharacterVirtual, not Character: no rigid body in the solve, stepped OUTSIDE _system.Update
            // (see Step), so the movement layer stays in control and stair-stepping / slope handling /
            // moving-platform support come from the controller rather than being rebuilt on a capsule.
            //
            // Z-up adaptation (Jolt's CharacterVirtual defaults are Y-up): Up = +Z, the capsule is
            // rotated to stand along Z, and SupportingVolume is a plane one radius below the centre so
            // the bottom hemisphere counts as ground. The Y-up ExtendedUpdateSettings are remapped in Step.
            if (_system == null)
                throw new InvalidOperationException("CreateCharacter before Initialize.");
            PhysicsSystem system = _system;

            (Shape wrapper, Shape inner) = BuildStandingCapsule(desc.CapsuleHalfHeight, desc.CapsuleRadius);
            var settings = new CharacterVirtualSettings
            {
                Shape = wrapper,
                Up = Vector3.UnitZ,
                SupportingVolume = new Plane(Vector3.UnitZ, -MathF.Max(0.01f, desc.CapsuleRadius)),
                MaxSlopeAngle = desc.MaxSlopeAngle,
                Mass = desc.Mass,
                MaxStrength = MathF.Max(0f, desc.PushStrength) * PushStrengthBaseNewtons,
                // NOTE: PredictiveContactDistance / PenetrationRecoverySpeed are left at Jolt's defaults
                // (0.1 / 1.0). An M6.5 "feet-dip polish" that raised PredictiveContactDistance to 0.15 was
                // REVERTED: on the cone's incline the early look-ahead lifted the capsule off the ground
                // (visible hover) and fought penetration recovery frame-to-frame (a walking hop/bob). The
                // clean-hold walk (defaults) is "mostly SL-like" with only a MINOR feet-dip on transitions,
                // which is preferable to hover+hop. Do not raise the look-ahead without a slope walk-test.
            };

            lock (_characterGate)
            {
                var character = new CharacterVirtual(settings, desc.Position, desc.Orientation, desc.UserData, system);
                character.MaxSlopeAngle = desc.MaxSlopeAngle;
                character.UserData = desc.UserData;

                var rec = new JoltCharacterRecord
                {
                    Character = character,
                    StandingShape = wrapper,
                    InnerCapsule = inner,
                    UserData = desc.UserData,
                    WantsContactEvents = desc.WantsContactEvents,
                    CapsuleHalfHeight = desc.CapsuleHalfHeight,
                    CapsuleRadius = desc.CapsuleRadius,
                    MaxSlopeAngle = desc.MaxSlopeAngle,
                    StepHeight = desc.StepHeight,
                    PushStrength = desc.PushStrength,
                    JumpSpeed = desc.JumpSpeed,
                };
                uint handle = _characters.Add(rec);
                rec.Handle = handle;

                // Avatar as a QUERY CITIZEN (M4.5, resolves #35). A kinematic marker body on the inert
                // AvatarQuery layer (collides with NOTHING - no push, no contacts) carries the avatar's
                // shape + UserData so RayCast/Overlap/ShapeCast can find the avatar. It is synced to the
                // character's position each step (in Step, after ExtendedUpdate, before _system.Update).
                // Distinct from the rejected contact inner body: that failure (CollideKinematicVsNonDynamic
                // HANGS, solid presence changes push) was a SIMULATION-collision problem; a query-only
                // marker never enters the solve, so a query sees it regardless of the collision matrix.
                var markerBcs = new BodyCreationSettings(
                    wrapper, desc.Position, desc.Orientation, MotionType.Kinematic,
                    new ObjectLayer((uint)PhysicsLayer.AvatarQuery));
                markerBcs.UserData = desc.UserData;
                BodyID markerId;
                try { markerId = _bodyInterface.CreateAndAddBody(markerBcs, Activation.DontActivate); }
                finally { markerBcs.Dispose(); }

                var markerRec = new JoltBodyRecord
                {
                    NativeBodyId = markerId.ID,
                    Shape = ShapeId.Invalid,
                    Layer = PhysicsLayer.AvatarQuery,
                    MotionType = BodyMotionType.Kinematic,
                    UserData = desc.UserData,
                    WantsContactEvents = false,
                    IsCharacterMarker = true,
                };
                uint markerHandle = _bodies.Add(markerRec);
                markerRec.Handle = markerHandle;
                _joltToRecord[markerId.ID] = markerRec;
                rec.MarkerBodyId = markerId.ID;
                rec.MarkerRecord = markerRec;

                // Avatar as a COLLISION CITIZEN. Rather than an inner rigid body (which in 2.18.6
                // cannot report kinematic-vs-static/terrain, whose CollideKinematicVsNonDynamic fix
                // HANGS the solver, and which as a solid body perturbs the M3 push behaviour), we
                // forward the CharacterVirtual's OWN contact events. They fire on THIS (step) thread
                // during ExtendedUpdate, cover terrain/static/dynamic/sensor, and - crucially - a
                // standing avatar re-reports its floor contact every step, which is the real thing the
                // #4 gate exists to suppress. Movement is untouched (these are observational). See notes.
                character.OnContactAdded += (CharacterVirtual cv, in BodyID b2, SubShapeID ss, in RVector3 pos, in Vector3 normal, ref CharacterContactSettings s)
                    => PushCharacterBodyContact(rec, b2.ID, ss.Value, ToVec(pos), normal, ContactPhase.Begin);
                character.OnContactPersisted += (CharacterVirtual cv, in BodyID b2, SubShapeID ss, in RVector3 pos, in Vector3 normal, ref CharacterContactSettings s)
                    => PushCharacterBodyContact(rec, b2.ID, ss.Value, ToVec(pos), normal, ContactPhase.Persist);
                character.OnContactRemoved += (CharacterVirtual cv, in BodyID b2, SubShapeID ss)
                    => PushCharacterBodyContact(rec, b2.ID, ss.Value, default, default, ContactPhase.End);

                // Avatar-avatar: register in the shared collision so capsules push/block, and report
                // the contact. otherCharacter.UserData gives the other avatar's id directly.
                if (_charVsChar != null)
                {
                    _charVsChar.Add(character);
                    character.SetCharacterVsCharacterCollision(_charVsChar);
                }
                character.OnCharacterContactAdded += (CharacterVirtual cv, CharacterVirtual other, SubShapeID ss, in RVector3 pos, in Vector3 normal, ref CharacterContactSettings s)
                    => PushCharacterCharacterContact(rec, other, ToVec(pos), normal, ContactPhase.Begin);
                character.OnCharacterContactPersisted += (CharacterVirtual cv, CharacterVirtual other, SubShapeID ss, in RVector3 pos, in Vector3 normal, ref CharacterContactSettings s)
                    => PushCharacterCharacterContact(rec, other, ToVec(pos), normal, ContactPhase.Persist);

                _characterList.Add(rec);
                return new CharacterId(handle);
            }
        }

        private static Vector3 ToVec(in RVector3 d) => new Vector3((float)d.X, (float)d.Y, (float)d.Z);   // 2.19.x renamed Double3 -> RVector3

        // Push an avatar-vs-BODY contact into the ring. Fires on the step thread during ExtendedUpdate.
        // Side A is the avatar (no BodyId - it is not a solver body; UserData carries the avatar id);
        // side B is the touched body, resolved via the reverse map. Persist is gated exactly like body
        // contacts: forwarded only if the avatar or the other body wants events.
        private void PushCharacterBodyContact(JoltCharacterRecord ch, uint otherJoltId, uint otherSubShape, Vector3 point, Vector3 normal, ContactPhase phase)
        {
            _joltToRecord.TryGetValue(otherJoltId, out JoltBodyRecord? other);
            bool wants = ch.WantsContactEvents || (other?.WantsContactEvents ?? false);
            if (phase == ContactPhase.Persist && !wants)
                return;
            _contactListener.Push(new ContactReport
            {
                BodyA = BodyId.Invalid,                 // the avatar is not a rigid body
                BodyB = other != null ? new BodyId(other.Handle) : BodyId.Invalid,
                UserDataA = ch.UserData,
                UserDataB = other?.UserData ?? 0u,
                ChildUserDataA = ch.UserData,           // the avatar has no sub-shapes; itself is the struck part
                ChildUserDataB = ResolveStruckPart(other, otherSubShape),   // the linkset child the avatar touched
                Point = point,
                Normal = normal,                        // character-contact normal (points toward the character)
                Impulse = 0f,                           // controller-resolved contact; no solver impulse available
                Phase = phase,
            });
        }

        // Push an avatar-vs-AVATAR contact. Both sides are avatars (no BodyId); UserData on each.
        private void PushCharacterCharacterContact(JoltCharacterRecord ch, CharacterVirtual other, Vector3 point, Vector3 normal, ContactPhase phase)
        {
            uint otherUserData = other != null ? (uint)other.UserData : 0u;
            if (phase == ContactPhase.Persist && !ch.WantsContactEvents)
                return; // gate on this avatar's flag (the other avatar reports its own side symmetrically)
            _contactListener.Push(new ContactReport
            {
                BodyA = BodyId.Invalid,
                BodyB = BodyId.Invalid,
                UserDataA = ch.UserData,
                UserDataB = otherUserData,
                ChildUserDataA = ch.UserData,           // avatars have no sub-shapes; each side is its own part
                ChildUserDataB = otherUserData,
                Point = point,
                Normal = normal,
                Impulse = 0f,
                Phase = phase,
            });
        }

        // Cook a Z-up standing capsule: Jolt's CapsuleShape axis is Y, so wrap it in a
        // RotatedTranslatedShape rotated Y->Z. Returns (wrapper, inner); the wrapper holds a native ref
        // to the inner, and BOTH are disposed together when the character is removed.
        private static (Shape wrapper, Shape inner) BuildStandingCapsule(float halfHeight, float radius)
        {
            Shape capsule = new CapsuleShape(MathF.Max(0.01f, halfHeight), MathF.Max(0.01f, radius));
            try
            {
                using var rt = new RotatedTranslatedShapeSettings(Vector3.Zero, CapsuleYToZ, capsule);
                return (rt.Create(), capsule);
            }
            catch
            {
                capsule.Dispose();
                throw;
            }
        }

        public void RemoveCharacter(CharacterId character)
        {
            lock (_characterGate)
            {
                if (!_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    return;
                _characterList.Remove(rec);
                if (_charVsChar != null && rec.Character != null)
                    _charVsChar.Remove(rec.Character);

                // Destroy the query marker body first (it native-refs the shared wrapper shape).
                if (rec.MarkerBodyId != 0)
                {
                    _bodyInterface.RemoveAndDestroyBody(new BodyID(rec.MarkerBodyId));
                    _joltToRecord.TryRemove(rec.MarkerBodyId, out _);
                    if (rec.MarkerRecord != null)
                        _bodies.Remove(rec.MarkerRecord.Handle);
                    rec.MarkerBodyId = 0;
                    rec.MarkerRecord = null;
                }

                // Character next (it holds a ref to _system), then the shapes it referenced.
                rec.Character?.Dispose();
                rec.Character = null;
                rec.StandingShape?.Dispose();
                rec.StandingShape = null;
                rec.InnerCapsule?.Dispose();
                rec.InnerCapsule = null;
                _characters.Remove(character.Value);
            }
        }

        public void SetCharacterTransform(CharacterId character, Vector3 position, Quaternion orientation)
        {
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec) && rec.Character != null)
                {
                    rec.Character.Position = position;
                    rec.Character.Rotation = orientation;
                }
            }
        }

        public void ReGroundCharacter(CharacterId character, Vector3 position)
        {
            // Same gate StepCharacter runs under, so the position + velocity write is atomic against the
            // per-step CharacterVirtual update (no half-applied state, no race). Zeroing LinearVelocity is
            // what stops a just-lifted avatar from carrying its accumulated downward fall speed into the
            // next step (which would sink it back into the surface for a frame).
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec) && rec.Character != null)
                {
                    rec.Character.Position = position;
                    rec.Character.LinearVelocity = Vector3.Zero;
                }
            }
        }

        public void SetCharacterShape(CharacterId character, float capsuleHalfHeight, float capsuleRadius)
        {
            lock (_characterGate)
            {
                if (_system == null || !_characters.TryGet(character.Value, out JoltCharacterRecord rec) || rec.Character == null)
                    return;

                (Shape wrapper, Shape inner) = BuildStandingCapsule(capsuleHalfHeight, capsuleRadius);
                // Force the swap (maxPenetrationDepth = MaxValue) - callers resize deliberately; we do not
                // want a silent no-op if the new capsule momentarily overlaps the floor.
                bool ok = rec.Character.SetShape(
                    0f, wrapper, float.MaxValue, new ObjectLayer((uint)PhysicsLayer.Avatar), _system, null, null);
                if (ok)
                {
                    rec.StandingShape?.Dispose();
                    rec.InnerCapsule?.Dispose();
                    rec.StandingShape = wrapper;
                    rec.InnerCapsule = inner;
                    rec.CapsuleHalfHeight = capsuleHalfHeight;
                    rec.CapsuleRadius = capsuleRadius;
                    // NOTE: the SupportingVolume plane still uses the ORIGINAL radius; a large radius change
                    // would want it refreshed too. Minor for M3 (resize is rare) - noted for the terrain/M6 pass.
                }
                else
                {
                    wrapper.Dispose();
                    inner.Dispose();
                }
            }
        }

        public void SetCharacterMovement(CharacterId character, Vector3 desiredVelocity, bool jump, bool flying)
        {
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                {
                    rec.DesiredVelocity = desiredVelocity;
                    rec.JumpRequested = jump;
                    rec.Flying = flying;
                }
            }
        }

        public bool TryGetCharacterState(CharacterId character, out CharacterState state)
        {
            lock (_characterGate)
            {
                if (!_characters.TryGet(character.Value, out JoltCharacterRecord rec) || rec.Character == null)
                {
                    state = default;
                    return false;
                }
                state = BuildCharacterState(rec);
                return true;
            }
        }

        // Snapshot the controller's current kinematic + ground state. Caller holds _characterGate.
        private CharacterState BuildCharacterState(JoltCharacterRecord rec)
        {
            CharacterVirtual ch = rec.Character!;
            GroundState gs = ch.GroundState;
            _joltToRecord.TryGetValue(ch.GroundBodyId, out JoltBodyRecord? groundRec);
            return new CharacterState
            {
                Character = new CharacterId(rec.Handle),
                UserData = rec.UserData,
                Position = ch.Position,
                LinearVelocity = ch.LinearVelocity,
                GroundNormal = ch.GroundNormal,
                GroundBody = groundRec != null ? new BodyId(groundRec.Handle) : BodyId.Invalid,
                IsSupported = ch.IsSupported,
                IsSliding = gs == GroundState.OnSteepGround,
            };
        }

        // Advance one CharacterVirtual. Caller holds _characterGate. Runs BEFORE _system.Update so the
        // controller sees the world at frame start (DESIGN.md). This is the canonical CharacterVirtual
        // velocity model: keep vertical + integrate gravity, adopt ground velocity to ride moving
        // platforms, jump from solid ground, then collide-and-slide via ExtendedUpdate.
        private void StepCharacter(JoltCharacterRecord rec, float dt)
        {
            CharacterVirtual? ch = rec.Character;
            if (ch == null || _system == null)
                return;

            float gz = _settings.Gravity.Z;
            Vector3 desired = rec.DesiredVelocity;
            Vector3 newVel;

            if (rec.Flying)
            {
                // Flying: full 3D control, ground gravity disabled.
                newVel = desired;
            }
            else
            {
                ch.UpdateGroundVelocity(); // refresh GroundVelocity from the (possibly moving) ground body
                GroundState gs = ch.GroundState;
                bool onWalkable = gs == GroundState.OnGround;   // OnGround = slope within MaxSlopeAngle
                float vz = ch.LinearVelocity.Z;

                // On walkable ground and not moving up: adopt the ground's vertical velocity (moving
                // platform) rather than the accumulated fall speed.
                if (onWalkable && vz <= 0f)
                    vz = ch.GroundVelocity.Z;

                // Jump only from walkable ground.
                if (rec.JumpRequested && onWalkable)
                    vz = rec.JumpSpeed;

                // Ground hold / friction (delta #5, "the M6 movement model"): a character SUPPORTED on a
                // WALKABLE slope must NOT slide - SL avatars stand still on inclines within MaxSlopeAngle.
                // We hold by NOT accumulating gravity while firmly on walkable ground (and not jumping):
                // with no downward velocity, ExtendedUpdate's collide-and-slide has nothing to redirect
                // down the slope. Gravity resumes the instant the character is airborne (InAir) or on
                // ground too steep to hold (OnSteepGround) - so ledges still drop and over-steep slopes
                // still slide (IsSliding). This replaces the frictionless "gravity every frame" that let a
                // no-input avatar creep down the cone (M6.5): the harness only tested FLAT ground, so the
                // downslope component never showed until a sloped live spawn exposed it.
                bool heldByGround = onWalkable && !rec.JumpRequested;
                if (!heldByGround)
                    vz += gz * dt;

                // Horizontal = intent, plus the ground's horizontal velocity so we ride a platform that
                // is being pushed sideways. With no input this is zero on static ground - no residual
                // slide velocity carries over frame to frame.
                Vector3 horiz = new Vector3(desired.X, desired.Y, 0f);
                if (onWalkable)
                    horiz += new Vector3(ch.GroundVelocity.X, ch.GroundVelocity.Y, 0f);

                newVel = new Vector3(horiz.X, horiz.Y, vz);
            }

            ch.LinearVelocity = newVel;
            rec.JumpRequested = false;

            // Z-up remap of the (Y-up-defaulted) stair/stick settings. Step-up height = the avatar's
            // StepHeight; stick-to-floor pulls straight down so it tracks steps/ramps without floating.
            var ext = new ExtendedUpdateSettings
            {
                WalkStairsStepUp = new Vector3(0f, 0f, MathF.Max(0f, rec.StepHeight)),
                StickToFloorStepDown = new Vector3(0f, 0f, -MathF.Max(0.05f, rec.StepHeight)),
            };
            ch.ExtendedUpdate(dt, ext, new ObjectLayer((uint)PhysicsLayer.Avatar), _system, null, null);
        }

        // =====================================================================
        // Constraints
        // =====================================================================

        public ConstraintId CreateConstraint(in ConstraintDesc desc)
        {
            // ConstraintKind maps essentially 1:1 onto Jolt's set. The four with
            // no PhysX equivalent - Pulley, Gear, RackAndPinion, Path - are the
            // interesting ones for scripted content, and they are the reason
            // this section is worth exposing to SLua rather than keeping internal.
            throw new NotImplementedException();
        }

        public void RemoveConstraint(ConstraintId constraint) => throw new NotImplementedException();
        public void SetConstraintEnabled(ConstraintId constraint, bool enabled) => throw new NotImplementedException();
        public void SetConstraintMotor(ConstraintId constraint, MotorMode mode, float target, float maxForce) => throw new NotImplementedException();
        public void SetConstraintLimits(ConstraintId constraint, float min, float max) => throw new NotImplementedException();
        public bool IsConstraintBroken(ConstraintId constraint) => throw new NotImplementedException();

        // =====================================================================
        // World
        // =====================================================================

        public void SetGravity(Vector3 gravity)
        {
            if (_system != null)
                _system.Gravity = gravity;
            _settings.Gravity = gravity;
        }

        public void SetTerrain(ShapeId heightFieldShape, Vector3 position)
        {
            if (_system == null)
                throw new InvalidOperationException("SetTerrain before Initialize.");
            if (!_shapes.TryGet(heightFieldShape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                throw new ArgumentException($"SetTerrain: {heightFieldShape} is not a live shape handle.");

            // Replace any existing terrain.
            if (_terrainBody.IsValid)
            {
                RemoveBody(_terrainBody);
                _terrainBody = BodyId.Invalid;
            }

            // Static body in the Terrain layer. The shape is already Z-up-correct (the
            // RotatedTranslatedShape wrapper from CreateHeightFieldShape), so no rotation here.
            var objectLayer = new ObjectLayer((uint)PhysicsLayer.Terrain);
            var bcs = new BodyCreationSettings(
                shapeRec.NativeShape, position, Quaternion.Identity, MotionType.Static, objectLayer);
            try
            {
                bcs.Friction = 0.6f;
                BodyID joltId = _bodyInterface.CreateAndAddBody(bcs, Activation.DontActivate);

                var rec = new JoltBodyRecord
                {
                    NativeBodyId = joltId.ID,
                    Shape = heightFieldShape,
                    Layer = PhysicsLayer.Terrain,
                    MotionType = BodyMotionType.Static,
                    UserData = 0u,
                    WantsContactEvents = false,
                };
                uint handle = _bodies.Add(rec);
                rec.Handle = handle;
                _joltToRecord[joltId.ID] = rec;
                _terrainBody = new BodyId(handle);
            }
            finally { bcs.Dispose(); }
        }

        public void SetWaterHeight(float height) => _waterHeight = height;

        // =====================================================================
        // Queries  (safe concurrent with Step - use the NarrowPhaseQuery)
        // =====================================================================

        public bool RayCast(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, out RayHit hit)
        {
            hit = default;
            if (_system == null)
                return false;

            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return false;

            // Jolt encodes the ray LENGTH in the direction vector's magnitude (not normalized).
            Vector3 rayDir = direction / len * maxDistance;
            var ray = new Ray(origin, rayDir);

            // QueryFilter is now honoured via a per-layer ObjectLayerFilter (cached per filter value).
            if (!_system.NarrowPhaseQuery.CastRay(ray, out RayCastResult result, null, FilterFor(filter), null))
                return false;

            Vector3 point = origin + rayDir * result.Fraction;
            _joltToRecord.TryGetValue(result.BodyID.ID, out JoltBodyRecord? rec);
            hit = new RayHit
            {
                Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                UserData = rec != null ? rec.UserData : 0u,
                ChildUserData = ResolveChildUserData(rec, result.subShapeID2),
                Point = point,
                Normal = SurfaceNormalOf(result.BodyID, result.subShapeID2, point),
                Distance = maxDistance * result.Fraction,
            };
            return true;
        }

        public int RayCastAll(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, Span<RayHit> hits)
        {
            if (_system == null)
                return 0;
            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return 0;

            Vector3 rayDir = direction / len * maxDistance;
            var ray = new Ray(origin, rayDir);
            // AllHitSorted = every hit along the ray, sorted by distance, no duplicates. The collector
            // needs an ICollection; this List is the one query-path allocation (queries run at script
            // rate, not per frame - a thread-local pool is a later optimisation, noted).
            var results = new List<RayCastResult>();
            _system.NarrowPhaseQuery.CastRay(
                ray, new RayCastSettings(), CollisionCollectorType.AllHitSorted, results, null, FilterFor(filter), null, null);

            // Collapse COINCIDENT duplicates: the heightfield's two triangles meeting at the ray XY report
            // two hits at the SAME point on the SAME body - which BulletSim (closest-hit) never produces and
            // scripts counting llCastRay hits do not expect. Drop a hit only when it is the same body AND the
            // same point (within CoincidentEpsilon) as the previous KEPT hit, so a stack of prims (different
            // bodies / different points) or terrain-then-prim (different bodies) is preserved in full,
            // distance-ordered. Results are distance-sorted, so any coincident pair is adjacent.
            int n = 0;
            uint prevBodyId = 0;
            Vector3 prevPoint = default;
            bool havePrev = false;
            for (int i = 0; i < results.Count && n < hits.Length; i++)
            {
                RayCastResult r = results[i];
                Vector3 point = origin + rayDir * r.Fraction;
                if (havePrev && r.BodyID.ID == prevBodyId && Vector3.DistanceSquared(prevPoint, point) < CoincidentEpsilonSq)
                    continue;
                _joltToRecord.TryGetValue(r.BodyID.ID, out JoltBodyRecord? rec);
                hits[n++] = new RayHit
                {
                    Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                    UserData = rec != null ? rec.UserData : 0u,
                    ChildUserData = ResolveChildUserData(rec, r.subShapeID2),
                    Point = point,
                    Normal = SurfaceNormalOf(r.BodyID, r.subShapeID2, point),
                    Distance = maxDistance * r.Fraction,
                };
                prevBodyId = r.BodyID.ID;
                prevPoint = point;
                havePrev = true;
            }
            return n;
        }

        // JoltPhysicsSharp 2.19.x query adaptation (two changes vs 2.18.6; RayCast unaffected):
        //  (1) CollideShape/CastShape now read the COM transform COLUMN-major. System.Numerics builds it
        //      row-major (translation in the last ROW); 2.18.6's wrapper transposed internally, 2.19.x does
        //      NOT - so an un-transposed transform collapses the query shape to ~origin (it then only hits
        //      terrain, never the target). Fix: pass Matrix4x4.Transpose(com). Transposing a row-major matrix
        //      is the equivalent column-major transform for ANY rotation, so this is exact, not identity-only.
        //  (2) The no-settings overloads now pass a ZERO-initialized settings struct (CollisionTolerance=0,
        //      PenetrationTolerance=0), degenerating GJK/EPA. 2.18.6 seeded Jolt's real defaults; restore below.
        private const float JoltCollisionTolerance = 1.0e-4f;   // Jolt cDefaultCollisionTolerance
        private const float JoltPenetrationTolerance = 1.0e-4f; // Jolt cDefaultPenetrationTolerance

        private static CollideShapeSettings DefaultCollideSettings() => new CollideShapeSettings
        {
            CollisionTolerance = JoltCollisionTolerance,
            PenetrationTolerance = JoltPenetrationTolerance,
            MaxSeparationDistance = 0f,
            ActiveEdgeMode = ActiveEdgeMode.CollideOnlyWithActive,  // Jolt's real CollideShapeSettings default
            BackFaceMode = BackFaceMode.IgnoreBackFaces,            // Jolt's real default
        };

        private static ShapeCastSettings DefaultCastSettings() => new ShapeCastSettings
        {
            CollisionTolerance = JoltCollisionTolerance,
            PenetrationTolerance = JoltPenetrationTolerance,
            ActiveEdgeMode = ActiveEdgeMode.CollideWithAll,
            BackFaceModeTriangles = BackFaceMode.IgnoreBackFaces, // a sweep enters through the FRONT face
            BackFaceModeConvex = BackFaceMode.IgnoreBackFaces,
            ReturnDeepestPoint = false,
            UseShrunkenShapeAndConvexRadius = false,
        };

        public int OverlapSphere(Vector3 center, float radius, QueryFilter filter, Span<BodyId> results)
        {
            if (_system == null)
                return 0;
            using var sphere = new SphereShape(MathF.Max(0.001f, radius));
            var found = new List<CollideShapeResult>();
            var cs = DefaultCollideSettings();
            _system.NarrowPhaseQuery.CollideShape(
                sphere, Vector3.One, Matrix4x4.Transpose(Matrix4x4.CreateTranslation(center)), cs, Vector3.Zero,
                CollisionCollectorType.AllHit, found, null, FilterFor(filter), null, null);
            return CollectUniqueBodies(found, results);
        }

        public int OverlapBox(Vector3 center, Vector3 halfExtents, Quaternion orientation, QueryFilter filter, Span<BodyId> results)
        {
            if (_system == null)
                return 0;
            float minHalf = MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z));
            float cr = MathF.Max(0f, MathF.Min(DefaultConvexRadius, minHalf * 0.1f));
            using var box = new BoxShape(halfExtents, cr);
            // A box's centre of mass IS its centre, so the COM transform is just rotate-then-translate.
            Matrix4x4 com = Matrix4x4.CreateFromQuaternion(orientation);
            com.Translation = center;
            var found = new List<CollideShapeResult>();
            var cs = DefaultCollideSettings();
            _system.NarrowPhaseQuery.CollideShape(
                box, Vector3.One, Matrix4x4.Transpose(com), cs, Vector3.Zero,
                CollisionCollectorType.AllHit, found, null, FilterFor(filter), null, null);
            return CollectUniqueBodies(found, results);
        }

        public bool ShapeCast(ShapeId shape, Vector3 origin, Quaternion orientation, Vector3 direction, float maxDistance, QueryFilter filter, out RayHit hit)
        {
            hit = default;
            if (_system == null)
                return false;
            if (!_shapes.TryGet(shape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                return false;
            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return false;

            Vector3 castVec = direction / len * maxDistance; // Jolt encodes cast length in the vector magnitude.
            Matrix4x4 com = Matrix4x4.CreateFromQuaternion(orientation);
            com.Translation = origin;
            var results = new List<ShapeCastResult>();
            var scs = DefaultCastSettings();
            _system.NarrowPhaseQuery.CastShape(
                shapeRec.NativeShape, Matrix4x4.Transpose(com), castVec, scs, Vector3.Zero,
                CollisionCollectorType.ClosestHit, results, null, FilterFor(filter), null, null);
            if (results.Count == 0)
                return false;

            ShapeCastResult r = results[0];
            _joltToRecord.TryGetValue(r.BodyID2.ID, out JoltBodyRecord? rec);
            // PenetrationAxis points from the cast shape into the hit body; the surface normal the caller
            // wants (pointing back out of the struck surface) is its negation, normalised.
            Vector3 axis = r.PenetrationAxis;
            float axisLen = axis.Length();
            Vector3 normal = axisLen > 1e-12f ? -axis / axisLen : default;
            hit = new RayHit
            {
                Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                UserData = rec != null ? rec.UserData : 0u,
                ChildUserData = ResolveChildUserData(rec, r.SubShapeID2.Value),
                Point = r.ContactPointOn2,           // first-contact point on the struck body
                Normal = normal,
                Distance = maxDistance * r.Fraction,
            };
            return true;
        }

        // Surface normal at a hit needs a read-lock on the body (results carry only id/fraction/subshape).
        private Vector3 SurfaceNormalOf(BodyID bodyId, uint subShapeId, Vector3 worldPoint)
        {
            Vector3 normal = default;
            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockRead(bodyId, out BodyLockRead lockRead);
            try
            {
                Body? body = lockRead.Succeeded ? lockRead.Body : null;
                if (body != null)
                    normal = body.GetWorldSpaceSurfaceNormal(new SubShapeID(subShapeId), worldPoint);
            }
            finally { bli.UnlockRead(lockRead); }
            return normal;
        }

        // Flatten CollideShape results (one per touching sub-shape/face - a compound yields several) into
        // a de-duplicated list of Legion BodyIds, stopping at the caller's buffer capacity.
        private int CollectUniqueBodies(List<CollideShapeResult> found, Span<BodyId> results)
        {
            int n = 0;
            for (int i = 0; i < found.Count && n < results.Length; i++)
            {
                if (!_joltToRecord.TryGetValue(found[i].BodyID2.ID, out JoltBodyRecord? rec))
                    continue;
                var id = new BodyId(rec.Handle);
                bool dup = false;
                for (int j = 0; j < n; j++)
                    if (results[j].Equals(id)) { dup = true; break; }
                if (!dup)
                    results[n++] = id;
            }
            return n;
        }

        // ObjectLayerFilter that honours a QueryFilter bitmask. Cached per filter value (below) so we do
        // not allocate a native callback object per query.
        private sealed class LayerQueryFilter : ObjectLayerFilter
        {
            private readonly QueryFilter _filter;
            public LayerQueryFilter(QueryFilter filter) { _filter = filter; }
            protected override bool ShouldCollide(ObjectLayer layer) => QueryFilterAllows(_filter, (PhysicsLayer)layer.Value);
        }

        private static bool QueryFilterAllows(QueryFilter filter, PhysicsLayer layer) => layer switch
        {
            PhysicsLayer.Terrain => (filter & QueryFilter.Terrain) != 0,
            PhysicsLayer.Static => (filter & QueryFilter.Static) != 0,
            PhysicsLayer.Dynamic => (filter & QueryFilter.Dynamic) != 0,
            PhysicsLayer.Avatar => (filter & QueryFilter.Avatar) != 0,
            PhysicsLayer.Sensor => (filter & QueryFilter.Sensor) != 0,
            // The avatar query-marker is found by exactly the filters that name Avatar (llSensor/
            // sit-target). filter=Static/Dynamic/Terrain do NOT return it. This is the ONLY way an
            // avatar surfaces to the query family (M4.5).
            PhysicsLayer.AvatarQuery => (filter & QueryFilter.Avatar) != 0,
            // Debris has no QueryFilter bit - detection queries (llCastRay/llSensor) never return particle
            // debris, so it is excluded from every filter, INCLUDING All.
            PhysicsLayer.Debris => false,
            _ => false,
        };

        // Resolve (and cache) the ObjectLayerFilter for a QueryFilter value. We always use a filter (never
        // null) so Debris is consistently excluded even for QueryFilter.All.
        private ObjectLayerFilter FilterFor(QueryFilter filter)
            => _queryFilters.GetOrAdd(filter, f => new LayerQueryFilter(f));

        // =====================================================================
        // Step
        // =====================================================================

        public StepResult Step(
            float deltaTime,
            Span<BodyState> bodyUpdates,
            Span<CharacterState> characterUpdates,
            Span<ContactReport> contacts)
        {
            _stepTimer.Restart();

            // 1. Step every CharacterVirtual BEFORE the physics update. They are not part of the
            //    solve, so they must see the world as it was at the start of the frame or avatars
            //    jitter against moving prims. (DESIGN.md step ordering - confirmed done here.)
            lock (_characterGate)
            {
                for (int i = 0; i < _characterList.Count; i++)
                {
                    JoltCharacterRecord crec = _characterList[i];
                    StepCharacter(crec, deltaTime);
                    // Sync the query marker to the JUST-stepped position, before _system.Update, so a
                    // query running mid-frame sees the avatar where it now is. DontActivate keeps the
                    // marker out of the active set (it never simulates) - it is only a query target.
                    if (crec.MarkerBodyId != 0 && crec.Character != null)
                        _bodyInterface.SetPositionAndRotation(
                            new BodyID(crec.MarkerBodyId), crec.Character.Position, crec.Character.Rotation, Activation.DontActivate);
                }
            }

            // 2. Advance the simulation (delta #4: 3-arg Update, temp allocation internal).
            if (_system != null && _jobSystem != null)
            {
                int collisionSteps = Math.Max(1, _settings.CollisionSteps);
                _system.Update(deltaTime, collisionSteps, _jobSystem);
            }

            // 3. Fold this frame's queued activation deltas into the step-thread-owned active
            //    set. This is the ONLY place _activeBodies is mutated. Ordered drain so an
            //    activate-then-deactivate within one frame nets out correctly.
            _justActivated.Clear();
            _justDeactivated.Clear();
            _staleActive.Clear();
            while (_activationQueue.TryDequeue(out ActivationDelta delta))
            {
                if (delta.Activated)
                {
                    if (_activeBodies.Add(delta.BodyId))
                        _justActivated.Add(delta.BodyId);
                }
                else
                {
                    _activeBodies.Remove(delta.BodyId);
                    _justActivated.Remove(delta.BodyId);
                    _justDeactivated.Add(delta.BodyId);
                }
            }

            int bodyCount = 0;
            bool bodyOverflow = false;

            // Drain the ACTIVE set: O(active), NOT O(total). foreach over the concrete HashSet
            // uses a struct enumerator - no allocation. For static-only M1 this set is empty and
            // bodyCount stays 0, which is the correct result, not a failure.
            foreach (uint joltId in _activeBodies)
            {
                if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec))
                {
                    _staleActive.Add(joltId); // removed out from under us; clean up after the loop
                    continue;
                }
                if (bodyCount >= bodyUpdates.Length) { bodyOverflow = true; break; }

                var jid = new BodyID(joltId);
                BodyStateFlags flags = BodyStateFlags.Active;
                if (_justActivated.Contains(joltId)) flags |= BodyStateFlags.JustActivated;
                bodyUpdates[bodyCount++] = new BodyState
                {
                    Body = new BodyId(rec.Handle),
                    UserData = rec.UserData,
                    Position = _bodyInterface.GetPosition(jid),
                    Orientation = _bodyInterface.GetRotation(jid),
                    LinearVelocity = _bodyInterface.GetLinearVelocity(jid),
                    AngularVelocity = _bodyInterface.GetAngularVelocity(jid),
                    Flags = flags,
                };
            }
            for (int i = 0; i < _staleActive.Count; i++)
                _activeBodies.Remove(_staleActive[i]);

            // Bodies that slept THIS step get one final state with JustDeactivated set - without
            // it the viewer keeps interpolating and settled objects visibly drift.
            for (int i = 0; i < _justDeactivated.Count && !bodyOverflow; i++)
            {
                uint joltId = _justDeactivated[i];
                if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec))
                    continue; // deactivated AND removed same frame - nothing to emit.
                if (bodyCount >= bodyUpdates.Length) { bodyOverflow = true; break; }

                var jid = new BodyID(joltId);
                bodyUpdates[bodyCount++] = new BodyState
                {
                    Body = new BodyId(rec.Handle),
                    UserData = rec.UserData,
                    Position = _bodyInterface.GetPosition(jid),
                    Orientation = _bodyInterface.GetRotation(jid),
                    LinearVelocity = _bodyInterface.GetLinearVelocity(jid),
                    AngularVelocity = _bodyInterface.GetAngularVelocity(jid),
                    Flags = BodyStateFlags.JustDeactivated,
                };
            }

            // 4. Drain character state (post-ExtendedUpdate position + the ground each one found).
            int charCount = 0;
            lock (_characterGate)
            {
                for (int i = 0; i < _characterList.Count && charCount < characterUpdates.Length; i++)
                {
                    if (_characterList[i].Character == null)
                        continue;
                    characterUpdates[charCount++] = BuildCharacterState(_characterList[i]);
                }
            }

            // 5. Drain contacts from the listener's ring buffer. Fed by the OnContact* handlers
            //    (delta #7); no contacts fire for static-only M1, so this drains empty.
            int contactCount = _contactListener.Drain(contacts, out bool contactOverflow);

            _stepTimer.Stop();

            return new StepResult(
                bodyCount,
                charCount,
                contactCount,
                bodyOverflow,
                contactOverflow,
                activeBodyCount: _activeBodies.Count,
                physicsMs: (float)_stepTimer.Elapsed.TotalMilliseconds);
        }
    }

    // =========================================================================
    // Contact listener
    //
    // Jolt fires contact callbacks FROM WORKER THREADS, mid-solve. Two rules:
    //   - never touch scene state here
    //   - never allocate here
    // Write into a preallocated ring and drain on the step thread. This is the
    // most likely place for a first integration to deadlock or tear.
    // =========================================================================
    internal sealed class LegionContactListener
    {
        private readonly ContactReport[] _ring;
        private int _writeIndex;
        private int _dropped;

        public LegionContactListener(int capacity) => _ring = new ContactReport[Math.Max(1, capacity)];

        // OnContactAdded  -> ContactPhase.Begin    -> LSL collision_start
        // OnContactPersisted -> ContactPhase.Persist -> LSL collision
        // OnContactRemoved -> ContactPhase.End     -> LSL collision_end
        //
        // Persist fires EVERY step for every touching pair. Filter here, not
        // above: a single avatar standing on a floor otherwise generates 45
        // events per second forever. Only forward Persist for pairs whose
        // owning object actually has a collision handler registered.
        internal void Push(in ContactReport report)
        {
            int index = Interlocked.Increment(ref _writeIndex) - 1;
            if (index >= _ring.Length)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _ring[index] = report;
        }

        internal int Drain(Span<ContactReport> destination, out bool overflowed)
        {
            int written = Math.Min(Volatile.Read(ref _writeIndex), _ring.Length);
            int count = Math.Min(written, destination.Length);

            _ring.AsSpan(0, count).CopyTo(destination);

            overflowed = Volatile.Read(ref _dropped) > 0 || written > destination.Length;
            Volatile.Write(ref _writeIndex, 0);
            Volatile.Write(ref _dropped, 0);
            return count;
        }
    }

    // =========================================================================
    // Handle table
    //
    // Generation-tagged slots. The low 24 bits index, the high 8 bits are a
    // generation counter bumped on free. A handle to a destroyed body fails
    // validation instead of silently addressing whatever got allocated in its
    // place - which is precisely the class of bug that makes physics crashes
    // impossible to reproduce.
    // =========================================================================
    internal sealed class HandleTable<T> where T : class
    {
        private const int IndexBits = 24;
        private const uint IndexMask = (1u << IndexBits) - 1u;

        private readonly object _gate = new object();
        private T?[] _slots = new T?[1024];
        private byte[] _generations = new byte[1024];
        private readonly ConcurrentQueue<int> _free = new ConcurrentQueue<int>();
        private int _highWater;

        public uint Add(T item)
        {
            lock (_gate)
            {
                if (!_free.TryDequeue(out int slot))
                {
                    if (_highWater == _slots.Length)
                    {
                        Array.Resize(ref _slots, _slots.Length * 2);
                        Array.Resize(ref _generations, _generations.Length * 2);
                    }
                    slot = _highWater++;
                }

                _slots[slot] = item;
                // Generation 0 is reserved so a zeroed handle is never valid.
                if (_generations[slot] == 0) _generations[slot] = 1;
                return ((uint)_generations[slot] << IndexBits) | (uint)slot;
            }
        }

        public bool TryGet(uint handle, out T item)
        {
            int slot = (int)(handle & IndexMask);
            byte generation = (byte)(handle >> IndexBits);

            if (generation == 0 || slot >= _slots.Length || _generations[slot] != generation)
            {
                item = null!;
                return false;
            }

            T? candidate = _slots[slot];
            item = candidate!;
            return candidate != null;
        }

        public bool IsValid(uint handle) => TryGet(handle, out _);


        public bool Remove(uint handle)
        {
            lock (_gate)
            {
                if (!TryGet(handle, out _)) return false;

                int slot = (int)(handle & IndexMask);
                _slots[slot] = null;
                _generations[slot] = (byte)(_generations[slot] == 255 ? 1 : _generations[slot] + 1);
                _free.Enqueue(slot);
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_slots, 0, _slots.Length);
                _highWater = 0;
                while (_free.TryDequeue(out _)) { }
            }
        }
    }

    // Records hold the native handles plus whatever Legion-side bookkeeping the
    // engine will not remember for us.
    internal sealed class JoltBodyRecord
    {
        public uint Handle;               // our Legion HandleTable handle (for jolt-id -> BodyId)
        public uint NativeBodyId;         // Jolt BodyID.ID
        public ShapeId Shape;
        public PhysicsLayer Layer;
        public BodyMotionType MotionType;
        public uint UserData;
        public bool WantsContactEvents;   // gates Persist forwarding
        public float Mass;                // explicit or Volume x Density; 0 where mass is unused (static)
        public bool AllowMotionChange;    // created movable (AllowDynamicOrKinematic) -> may flip motion type
        public bool IsCharacterMarker;    // a query-only avatar marker (owned by its character; not a real prim)
    }

    internal sealed class JoltShapeRecord
    {
        public Shape? NativeShape;        // the shape this handle represents; disposed at RefCount 0
        public Shape? InnerShape;         // private inner shape OWNED by this wrapper (e.g. the Y-up
                                          // heightfield under a Z-up RotatedTranslatedShape); disposed with it
        public int RefCount;
        public bool IsWrapper;            // decorator wrapper (rotated/translated/scaled) or compound over other shapes
        public ShapeId BaseShape;         // caller-visible wrapped shape (CreateScaledShape); Invalid otherwise
        public uint[]? CompoundChildUserData; // ordered child UserData for a StaticCompound; null otherwise
        public int CompoundIndexBits;     // low bits of a hit SubShapeID that encode the compound child index
    }

    internal sealed class JoltCharacterRecord
    {
        public uint Handle;                   // our Legion HandleTable handle
        public CharacterVirtual? Character;    // the Jolt controller, stepped outside _system.Update
        public Shape? StandingShape;           // Z-up rotated-capsule wrapper we own (disposed on remove)
        public Shape? InnerCapsule;            // the Y-up capsule the wrapper references (disposed with it)
        public uint UserData;
        public bool WantsContactEvents;        // gates Persist forwarding for this avatar's contacts
        public uint MarkerBodyId;              // Jolt BodyID.ID of the query-visible marker (0 = none)
        public JoltBodyRecord? MarkerRecord;   // the marker's body record (in _bodies + _joltToRecord)

        // Tuning knobs captured from CharacterDesc.
        public float CapsuleHalfHeight;
        public float CapsuleRadius;
        public float MaxSlopeAngle;
        public float StepHeight;
        public float PushStrength;
        public float JumpSpeed;

        // Per-frame movement intent (set by SetCharacterMovement, consumed by StepCharacter).
        public Vector3 DesiredVelocity;
        public bool JumpRequested;
        public bool Flying;
    }

    internal sealed class JoltConstraintRecord
    {
        public IntPtr Native;
        public ConstraintKind Kind;
        public BodyId BodyA;
        public BodyId BodyB;
        public float BreakForce;
        public bool Broken;
        public uint UserData;
    }
}
