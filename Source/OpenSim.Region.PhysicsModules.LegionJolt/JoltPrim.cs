// Legion Grid - a prim as a Jolt rigid body (M6.3).
//
// This is the PhysicsActor OpenSim hands back from AddPrimShape. M6.3 Task 1 scope: NON-PHYSICAL
// prims become STATIC Jolt bodies (collision citizens that never move) via the fixed-shape fast path
// (box / sphere / cylinder cook straight to a Jolt primitive - no meshmerizer). Physical dynamics
// (motion updates, forces, mass) are M6.4; collision-event dispatch is M6.6; the IMesher path is
// M6.3 Task 2. Everything dynamics-related below is therefore deliberately inert.
//
// Types: PhysicsActor speaks OpenMetaverse.Vector3/Quaternion (unqualified here); the backend speaks
// System.Numerics (SVector3/SQuaternion). Body orientation is composed in System.Numerics because the
// cylinder axis-correction ordering must be unambiguous (left operand applied first).

using System;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenMetaverse;
using Legion.Physics;
using Legion.Vehicles;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;
using LVehicle = Legion.Vehicles.Vehicle;   // Legion.Vehicles' copy of the LSL wire codes (SharedBase also has a Vehicle enum)

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    internal sealed class JoltPrim : PhysicsActor
    {
        private readonly LegionJoltScene _module;
        private readonly ILegionPhysicsBackend _backend;

        private PrimitiveBaseShape _pbs;
        private Vector3 _position;
        // The position this prim was CONSTRUCTED at; on a region load that is the DB-saved position. Read by
        // the `jolt reloadcheck` console command to report load-time displacement (saved vs settled).
        private readonly Vector3 _birthPos;
        private Vector3 _size;
        private Quaternion _orientation;
        private bool _isPhysical;
        private Vector3 _velocity;              // last drained linear velocity (the SOP reads this for terse updates)
        private Vector3 _rotationalVelocity;    // last drained angular velocity

        // SceneObjectPart.Density (simulator units, default 1000) x DensityScaleFactor = PHYSICAL density
        // (kg/m^3). The same 0.01 conversion BulletSim's BSParam.DensityScaleFactor applies; forwarding it
        // makes Jolt's mass match BulletSim (a 0.5^3 box: 0.125 x 10 = 1.25 kg, not 0.125 x 1000 = 125). 0
        // until AddToPhysics sets pa.Density.
        private const float DensityScaleFactor = 0.01f;
        private float _simDensity;

        private ShapeId _shape = ShapeId.Invalid;   // one handle-ref held for the prim's life
        private BodyId _body = BodyId.Invalid;
        // Assert-buoyancy-on-restart (Balpien): re-assert the vehicle's body params (gravity-cancellation,
        // no-sleep, ...) on this many upcoming LIVE StepVehicle frames. The load-path assertion in the
        // VehicleType restore can be lost because the body was created (GravityFactor=1) with its activation
        // DEFERRED to the step thread, which drains the creation settings AFTER the load-thread SetGravityFactor
        // - so the body starts stepping under full gravity and sinks. Re-asserting from the step thread, on the
        // live body, makes it stick. Set when the vehicle becomes active; runtime llSetVehicleType sets it too
        // (harmless - the body is already live so it sticks first time).
        private int _reassertVehicleFrames;

        // Maps the cooked shape's local axis onto SL's convention. Identity for box/sphere; a +90 deg
        // rotation about X for a cylinder (Jolt's CylinderShape axis is Y, SL cylinders are Z-height).
        // Composed as (correction * primOrientation) so the prim's own rotation still applies.
        private SQuaternion _axisCorrection = SQuaternion.Identity;
        private string _shapeKind = "?";

        private int _subscribedMs;   // collision-event subscription window; stored for M6.6, inert now

        // Linksets (M7). OpenSim adds each prim as its own PhysicsActor then calls child.link(root) per
        // child (SceneObjectGroup). We weld the children into the ROOT's body as a StaticCompoundShape:
        // one rigid body whose sub-shapes are the root + each child at its root-relative offset. The root
        // owns _linkChildren + _compoundShape; a welded child holds _linkRoot and has no body of its own.
        private System.Collections.Generic.List<JoltPrim> _linkChildren;   // root: welded children
        private JoltPrim _linkRoot;                                        // child: our compound root, if welded
        private ShapeId _compoundShape = ShapeId.Invalid;                  // root: the built compound (Invalid = single shape)

        internal BodyId BodyHandle => _body;
        internal string ShapeKind => _shapeKind;
        // Read by the `jolt reloadcheck` / `jolt vehiclestatus` console commands.
        internal Vector3 BirthPos => _birthPos;
        internal Vector3 CurrentPos => _position;
        internal bool IsPhysicalBody => _isPhysical;
        internal bool IsVehicle => _vehicle != null;

        // Live vehicle state for the `jolt vehiclestatus` console command (type / buoyancy / active).
        internal string VehicleInfo()
        {
            if (_vehicle == null) return "no-vehicle";
            return $"type={_vehicle.Type} active={(_vehicle.IsActive ? "Y" : "N")} " +
                   $"buoyancy={_vehicle.GetFloatParam(VehFloatParam.Buoyancy):0.00} " +
                   $"hoverHeight={_vehicle.GetFloatParam(VehFloatParam.HoverHeight):0.00}";
        }

        // Vehicles (M8): the backend-agnostic Halcyon controller + its Jolt seam. Created lazily on
        // the first Vehicle* call; ACTIVE (stepped per-frame, body params applied) only while the
        // controller's type != NONE and the prim is physical. Setting TYPE_NONE destroys it (spec).
        private LegionVehicleController _vehicle;
        private JoltVehicleBody _vehicleBody;

        // Body orientation -> PRIM orientation (undo the cylinder axis-correction; identity for
        // box/sphere/mesh). Same conversion the drain does in ApplyStepState.
        internal Quaternion PrimOrientationOf(SQuaternion bodyOrient)
        {
            SQuaternion prim = SQuaternion.Multiply(SQuaternion.Conjugate(_axisCorrection), bodyOrient);
            return new Quaternion(prim.X, prim.Y, prim.Z, prim.W);
        }

        internal JoltPrim(LegionJoltScene module, ILegionPhysicsBackend backend, uint localid, string name,
                          PrimitiveBaseShape pbs, Vector3 position, Vector3 size, Quaternion rotation, bool isPhysical)
        {
            _module = module;
            _backend = backend;
            LocalID = localid;
            Name = name;
            _pbs = pbs;
            _position = position;
            _birthPos = position;   // saved pos on load; read by `jolt reloadcheck`
            _size = size;
            _orientation = rotation;
            _isPhysical = isPhysical;

            Build();
        }

        private SQuaternion BodyOrientationOf(Quaternion primRot)
            => SQuaternion.Multiply(_axisCorrection, ToS(primRot));   // correction first, then prim

        private void Build()
        {
            _shape = _module.CookPrimShape(_pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            CreateBodyInternal();
        }

        // Create the Jolt body for the CURRENT _isPhysical / _shape / _axisCorrection and cached
        // transform + velocity. Non-physical -> Static (no MotionProperties: the 65k-prim startup guard).
        // Physical -> Dynamic + StartActive (wakes so it falls); mass computed Volume*Density (Density
        // from BodyDesc.Default = 1000). A body that may go physical is created movable ONLY when it is
        // physical (delta #15: Static-born can't be promoted - the toggle recreates instead).
        private void CreateBodyInternal()
        {
            // Cause-A load-time position sanity: never bring a PHYSICAL body up penetrating the terrain. If
            // the saved/current centre is below where it rests on the surface, lift it there and zero its
            // velocity BEFORE the body goes active - so (1) the bad position never drains back + persists, and
            // (2) the native solver doesn't churn resolving a deep penetration (the ~5.8s reload watchdog
            // stall). Only lifts BELOW-terrain bodies: a resting box or a boat FLOATING on water (above the
            // surface) is left exactly where it is. Zeroing velocity also stops the post-lift slide (the boat's
            // 26.5 m horizontal drift). The compound root lifts its whole welded set uniformly (sub-shape
            // offsets are relative to the body origin), so a linkset keeps its shape.
            if (_isPhysical && _module.TryUnburyPhysicalLoad(_position, _size, out Vector3 lifted))
            {
                _position = lifted;
                _velocity = Vector3.Zero;
                _rotationalVelocity = Vector3.Zero;
            }

            BodyDesc desc = BodyDesc.Default;
            desc.Shape = _compoundShape.IsValid ? _compoundShape : _shape;   // linkset root -> the compound
            desc.Position = ToS(_position);
            desc.Orientation = BodyOrientationOf(_orientation);
            desc.LinearVelocity = ToS(_velocity);
            desc.AngularVelocity = ToS(_rotationalVelocity);
            desc.UserData = LocalID;                   // echoed back in every RayHit/contact/update - no lookup
            desc.WantsContactEvents = _subscribedMs > 0;   // keep the Persist gate across a body recreate (weld/reshape)
            if (_isPhysical)
            {
                desc.Layer = PhysicsLayer.Dynamic;
                desc.MotionType = BodyMotionType.Dynamic;
                desc.Mass = 0f;                        // <=0 -> backend computes Volume*Density
                if (_simDensity > 0f)                  // honour the SOP density (BulletSim mass parity, M6.8)
                    desc.Density = _simDensity * DensityScaleFactor;
                // STRUCTURAL PORT of BulletSim's taint-deferred creation: create the body INERT (asleep), never
                // active-on-insert. BulletSim never lets a body be stepped by the engine until ALL taints
                // (create + MakeDynamic + SetVehicle/SetPhysicalGravity) have drained (ProcessTaints runs
                // BEFORE PE.PhysicsStep). Our equivalent barrier: create asleep, then ACTIVATE at the TOP of the
                // next Simulate (RegisterPendingActivation) - by which point AddToPhysics has fully run
                // (AddPrimShape + SetVehicle are synchronous) AND StepVehicles has asserted the vehicle's
                // gravity-cancellation for this frame. So a reloaded boat is NEVER a live gravity body before
                // its buoyancy is in force -> it cannot free-fall during the load / the reload stall.
                desc.StartActive = false;
            }
            else
            {
                desc.Layer = PhysicsLayer.Static;      // non-physical prim = static collision citizen
                desc.MotionType = BodyMotionType.Static;
                desc.Mass = 0f;
                desc.StartActive = false;              // never wake on insert (startup-stall guard)
            }
            _body = _backend.CreateBody(desc);

            if (_isPhysical)
            {
                // Defer activation to the top of the next Simulate (step thread). ActivateBody on a NON-step
                // thread does not reliably reach the step-thread active-set; enqueuing it via the pending set
                // (drained in Simulate before StepVehicles/StepOnce) both fixes that AND gives us BulletSim's
                // configure-before-step barrier - the body is asleep until every load-time property (incl. the
                // vehicle) is applied, so it never free-falls before its buoyancy is asserted.
                _module.RegisterPendingActivation(this);
                if (_backend.TryGetBodyState(_body, out BodyState st))
                    LegionJoltScene.m_log.Debug(
                        $"{LegionJoltScene.LogHeader} physical body id={LocalID} created (deferred activation): active={((st.Flags & BodyStateFlags.Active) != 0)} posZ={st.Position.Z:0.00} shape={_shapeKind}");

                // A recreate (reposition/reshape/weld/physical-toggle) makes a FRESH body with default
                // params; an active vehicle must re-assert its no-friction/no-damping/manual-gravity/
                // never-sleep setup on it (M8).
                ApplyVehicleBodyParams();
            }
        }

        // Called by LegionJoltScene at the top of Simulate (step thread) to activate a physical body that was
        // created inert (deferred activation - the BulletSim configure-before-step barrier). By now every
        // load-time property is applied and the vehicle drive is about to run this frame, so waking the body
        // here means it enters the engine step already configured (gravity cancelled for a vehicle) - it never
        // free-falls. No-op for a non-physical/destroyed body or one already awake.
        internal void ActivatePending()
        {
            if (_body.IsValid && _isPhysical)
                _backend.ActivateBody(_body);
        }

        // Drain: the backend reports this body's post-step transform + velocity. Update the cached values
        // the SOP reads (Position/Orientation/Velocity/RotationalVelocity) and fire the terse update so the
        // viewer sees the motion. The drained Orientation is the BODY orientation (= axisCorrection *
        // primOrientation); undo the correction to hand OpenSim the PRIM orientation (identity for
        // box/sphere/mesh - only cylinders carry a correction). Called once per active body per step, plus
        // one final time when the body sleeps (JustDeactivated) - which is the settle update that stops the
        // viewer interpolating a rested object.
        internal void ApplyStepState(in BodyState s)
        {
            var newPos = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            // A loaded physical linkset can sit PENETRATING the terrain; the solver (CollisionSteps=6) then
            // flings a part to a NaN / far-out-of-region position. Pushing that into the SOP makes OpenSim's
            // terse-update path (PhysicsRequestingTerseUpdate) attempt a REGION CROSSING (there is no
            // neighbour), which spins the heartbeat ~5 s per body - the "all water" boot stall. Drop the
            // glitch update (keep the last good transform) instead of propagating it into a crossing.
            if (!(float.IsFinite(newPos.X) && float.IsFinite(newPos.Y) && float.IsFinite(newPos.Z))
                || MathF.Abs(newPos.X) > 1e5f || MathF.Abs(newPos.Y) > 1e5f || MathF.Abs(newPos.Z) > 1e5f)
            {
                LegionJoltScene.m_log.Warn($"{LegionJoltScene.LogHeader} [physglitch] body {LocalID} implausible pos {newPos} vel {s.LinearVelocity} - update dropped (no crossing)");
                return;
            }
            _position = newPos;
            SQuaternion prim = SQuaternion.Multiply(SQuaternion.Conjugate(_axisCorrection), s.Orientation);
            _orientation = new Quaternion(prim.X, prim.Y, prim.Z, prim.W);
            _velocity = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            _rotationalVelocity = new Vector3(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z);
            RequestPhysicsterseUpdate();
        }

        // Re-cook the shape (resize / shape swap) keeping the same body. Release order mirrors the
        // terrain path: swap the body onto the new shape first, then release the old handle-ref.
        private void Rebuild()
        {
            if (!_body.IsValid) { Build(); return; }
            ShapeId old = _shape;
            _shape = _module.CookPrimShape(_pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            _backend.SetBodyShape(_body, _shape, recomputeMass: false);   // keeps the body at its current transform
            if (old.IsValid)
                _backend.ReleaseShape(old);
        }

        // Called by LegionJoltScene.RemovePrim. RemoveBody drops the body's native shape ref; releasing
        // our handle-ref then frees the shape - no leak, no premature free.
        internal void Destroy()
        {
            // Drop out of the scene's per-frame vehicle drive (no-op if never a vehicle).
            if (_vehicle != null) { _module.UnregisterVehicle(this); _vehicle = null; _vehicleBody = null; }
            // If welded into a parent compound, detach first (parent rebuilds without us).
            if (_linkRoot != null) { JoltPrim r = _linkRoot; _linkRoot = null; r.UnlinkChild(this); }
            // If we are a compound root, orphan our welded children (group teardown removes them anyway).
            if (_linkChildren != null)
            {
                foreach (JoltPrim c in _linkChildren) if (ReferenceEquals(c._linkRoot, this)) c._linkRoot = null;
                _linkChildren.Clear();
            }
            if (_body.IsValid)
                _backend.RemoveBody(_body);
            _body = BodyId.Invalid;
            if (_compoundShape.IsValid) { _backend.ReleaseShape(_compoundShape); _compoundShape = ShapeId.Invalid; }
            if (_shape.IsValid)
                _backend.ReleaseShape(_shape);
            _shape = ShapeId.Invalid;
        }

        private static SVector3 ToS(Vector3 v) => new SVector3(v.X, v.Y, v.Z);
        private static SQuaternion ToS(Quaternion q) => new SQuaternion(q.X, q.Y, q.Z, q.W);

        // ---------------------------------------------------------------------
        // PhysicsActor contract. Real state: Position / Orientation / Size (pushed to the body).
        // The rest is inert this slice (static body).
        // ---------------------------------------------------------------------

        public override Vector3 Position
        {
            get => _position;
            set
            {
                if (_position == value) return;   // the drain writes _position directly; only a real move recreates
                _position = value;
                if (_body.IsValid) RepositionBody();
            }
        }

        public override Quaternion Orientation
        {
            get => _orientation;
            set
            {
                if (_orientation == value) return;
                _orientation = value;
                if (_body.IsValid) RepositionBody();
            }
        }

        // Move the body IN PLACE to the current cached transform via the backend's real reposition
        // (BodyInterface.SetPositionAndRotation, JoltPhysicsSharp 2.19.1). This preserves velocity,
        // contacts, and the BodyID - it does NOT destroy+recreate. That recreate was the root of the
        // every-frame rebuild loop: OpenSim's SOP->physics sync pushes the transform each frame for a
        // moving object, and the old remove+recreate zeroed a never-settling vehicle's velocity + re-inerted
        // it every ~30 ms (the plane became uncontrollable / fell through). Only PURE position/orientation
        // moves reach here (Size/Shape -> Rebuild, physical toggle -> RecreateBody still do full rebuilds).
        //
        // activate:false is deliberate. Jolt's DontActivate leaves an already-active body active (a moving/
        // vehicle body keeps stepping) but does NOT wake an inert one - so a load-time body created inert
        // (deferred activation) stays inert until DrainPendingActivation, preserving the configure-before-
        // step barrier (a reloaded vehicle never free-falls before its gravity-cancel is asserted). Orientation
        // goes through BodyOrientationOf (axis-correction applied), matching CreateBodyInternal.
        private void RepositionBody()
        {
            if (!_body.IsValid) { Build(); return; }

            // Load-time un-bury (restores what the old remove+recreate path did for free). The pre-Fix#1
            // RepositionBody re-ran CreateBodyInternal, so a load-time Position push carrying a saved
            // BELOW-terrain position was lifted (TryUnburyPhysicalLoad) before the body went active. In-place
            // SetBodyTransform skips CreateBodyInternal, so without this a penetrating load position would be
            // pushed straight into the terrain -> the CollisionSteps=6 solver churns resolving the deep
            // penetration -> the ~5.8s boot-stall. Re-apply the SAME un-bury here, reusing the shared helper.
            //
            // GATED on IsRegionLoading ONLY: a LIVE vehicle repositioning (e.g. a car dipping below terrain on
            // a bump mid-drive) must NOT be snapped up - that is real runtime motion, not a bad load position.
            if (_isPhysical && _module.IsRegionLoading
                && _module.TryUnburyPhysicalLoad(_position, _size, out Vector3 lifted))
            {
                _position = lifted;
                _velocity = Vector3.Zero;              // no post-lift slide (matches CreateBodyInternal's un-bury)
                _rotationalVelocity = Vector3.Zero;
                _backend.SetBodyLinearVelocity(_body, ToS(_velocity));
                _backend.SetBodyAngularVelocity(_body, ToS(_rotationalVelocity));
            }

            _backend.SetBodyTransform(_body, ToS(_position), BodyOrientationOf(_orientation), activate: false);
        }

        public override Vector3 Size
        {
            get => _size;
            set
            {
                if (_size == value) return;
                _size = value;
                Rebuild();
            }
        }

        public override PrimitiveBaseShape Shape
        {
            set
            {
                _pbs = value;
                Rebuild();
            }
        }

        public override int PhysicsActorType { get => (int)ActorTypes.Prim; set { } }

        public override bool IsPhysical
        {
            get => _isPhysical;
            set
            {
                if (_isPhysical == value) return;
                _isPhysical = value;
                // Delta #15: a Static-born body has no MotionProperties and CANNOT be promoted
                // (SetBodyMotionType throws), so the toggle RECREATES the body. It also re-cooks the shape:
                // a physical MESH must become a convex hull (mesh Volume=0 -> mass 0), non-physical reverts
                // to a triangle mesh. Transform + velocity carry over. No taint (Jolt is concurrent).
                RecreateBody();
            }
        }

        // Recreate the body for a changed _isPhysical (mesh<->hull, static<->dynamic), preserving
        // transform + velocity. Order mirrors Rebuild: cook new shape, drop old body, create new body,
        // release old shape handle - leak-free.
        private void RecreateBody()
        {
            ShapeId old = _shape;
            _shape = _module.CookPrimShape(_pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            if (_body.IsValid)
                _backend.RemoveBody(_body);
            CreateBodyInternal();
            if (old.IsValid)
                _backend.ReleaseShape(old);
        }

        // The real dynamic mass lives in Jolt (computed Volume x Density at body creation). Read it back
        // so OpenSim/llGetMass and the A/B parity harness see Jolt's assigned mass (M6.8, closes 6.4 gap).
        public override float Mass => _body.IsValid ? _backend.GetBodyMass(_body) : 0f;

        // OpenSim sets pa.Density = SceneObjectPart.Density in AddToPhysics (default 1000). Store it and
        // forward the PHYSICAL density (x DensityScaleFactor) so Jolt's mass matches BulletSim. A live
        // physical body recomputes immediately; a not-yet-physical prim applies it at CreateBodyInternal.
        public override float Density
        {
            get => _simDensity > 0f ? _simDensity : BodyDesc.Default.Density;
            set
            {
                _simDensity = value;
                if (_body.IsValid && _isPhysical)
                    _backend.SetBodyDensity(_body, value * DensityScaleFactor);
            }
        }
        public override bool Stopped => true;

        public override Vector3 GeometricCenter => _position;
        public override Vector3 CenterOfMass => _position;

        // Linear/angular velocity: cached from the drain (SOP reads these for terse updates); a set on a
        // live physical body pushes through so a script llSetVelocity takes effect.
        public override Vector3 Velocity
        {
            get => _velocity;
            set
            {
                Vector3 v = value;
                // Fix-3 (slide): drop restored HORIZONTAL velocity on region LOAD. OpenSim's AddToPhysics
                // replays the DB-saved velocity onto the actor; a vehicle body runs frictionless + never-
                // sleep, so a small saved X/Y coast never bleeds off and drifts the boat metres (the 0.95 m/s
                // slide) - and compounds, since that drift is persisted and replayed next reload. Zeroing it
                // only while the region is LOADING (before the first Simulate) leaves a runtime llSetVelocity
                // untouched. Vertical is left alone (a genuinely falling load body keeps its descent).
                if (_isPhysical && _module.IsRegionLoading)
                {
                    v.X = 0f;
                    v.Y = 0f;
                }
                _velocity = v;
                if (_body.IsValid && _isPhysical) _backend.SetBodyLinearVelocity(_body, ToS(v));
            }
        }
        public override Vector3 RotationalVelocity
        {
            get => _rotationalVelocity;
            set { _rotationalVelocity = value; if (_body.IsValid && _isPhysical) _backend.SetBodyAngularVelocity(_body, ToS(value)); }
        }
        public override Vector3 Torque { get => Vector3.Zero; set { } }
        public override Vector3 Force { get => Vector3.Zero; set { } }
        public override Vector3 Acceleration { get => Vector3.Zero; set { } }
        public override float CollisionScore { get; set; }
        public override bool Kinematic { get => false; set { } }
        public override float Buoyancy { get => 0f; set { } }
        public override bool Flying { get => false; set { } }
        public override bool SetAlwaysRun { get => false; set { } }
        public override bool ThrottleUpdates { get => false; set { } }
        public override bool IsColliding { get; set; }
        public override bool CollidingGround { get; set; }
        public override bool CollidingObj { get; set; }
        public override bool Grabbed { set { } }
        public override bool Selected { set { } }

        public override void CrossingFailure() { }
        // OpenSim calls child.link(root) per child when a physical linkset is formed. Weld this child into
        // the root's compound body.
        public override void link(PhysicsActor obj)
        {
            if (obj is JoltPrim root && !ReferenceEquals(root, this))
                root.LinkChild(this);
        }

        // Detach from the compound and become an independent body again.
        public override void delink()
        {
            if (_linkRoot != null)
            {
                JoltPrim root = _linkRoot;
                _linkRoot = null;
                root.UnlinkChild(this);
            }
            if (!_body.IsValid && _shape.IsValid)
                CreateBodyInternal();   // restore our own body (we were welded into the root)
        }

        // Link/unlink only RECORD membership and mark the root dirty - the compound is (re)built ONCE per
        // frame in RebuildCompoundNow, drained from Simulate. Rebuilding inline per child HUNG the boot-load
        // of a persisted physical linkset: OpenSim fires child.link(root) for every child as the whole set
        // loads at once, and each inline rebuild churned RemoveBody/CreateBody on the live/active root while
        // the load + heartbeat ran concurrently (the repeated root id in the boot log). Deferring coalesces
        // N child-links into ONE rebuild at a controlled point - off the load path, O(N) not O(N^2).
        internal void LinkChild(JoltPrim child)
        {
            _linkChildren ??= new System.Collections.Generic.List<JoltPrim>();
            if (!_linkChildren.Contains(child))
                _linkChildren.Add(child);
            child._linkRoot = this;
            _module.MarkLinksetDirty(this);
        }

        internal void UnlinkChild(JoltPrim child)
        {
            if (_linkChildren != null) _linkChildren.Remove(child);
            _module.MarkLinksetDirty(this);
        }

        private bool _rebuilding;

        // (Re)build the root's body from its own shape + all welded children at their root-relative offsets,
        // ONCE. Called from the module's per-frame dirty-linkset drain (step thread, before the step - safe
        // body ops, no per-child churn). StaticCompoundShape (fast query + per-child UserData for M7 Task 3).
        // Sub-shape transforms are composed in the ROOT BODY frame (BodyOrientationOf carries any cylinder
        // axis-correction); mass/COM/inertia come out of Jolt's compound assembly (mass = sum of child
        // Volume x density, harness [32b]). Re-entrancy- and destroyed-guarded so it can never loop or touch
        // a torn-down prim. No children -> revert to the plain single root shape (never a 1-child compound).
        internal void RebuildCompoundNow()
        {
            if (_rebuilding || !_shape.IsValid) return;   // guard: no re-entrancy, skip a destroyed root
            _rebuilding = true;
            try
            {
                ShapeId oldCompound = _compoundShape;

                if (_linkChildren == null || _linkChildren.Count == 0)
                {
                    _compoundShape = ShapeId.Invalid;   // single member -> plain body, not a degenerate compound
                }
                else
                {
                    // Weld: drop each child's independent body (it lives as a sub-shape of the compound now).
                    foreach (JoltPrim c in _linkChildren)
                        if (c._body.IsValid) { _backend.RemoveBody(c._body); c._body = BodyId.Invalid; }

                    SQuaternion rootBody = BodyOrientationOf(_orientation);
                    SQuaternion invRoot = SQuaternion.Conjugate(rootBody);
                    var kids = new CompoundChild[1 + _linkChildren.Count];
                    kids[0] = new CompoundChild { Shape = _shape, Position = SVector3.Zero, Orientation = SQuaternion.Identity, UserData = LocalID };
                    for (int i = 0; i < _linkChildren.Count; i++)
                    {
                        JoltPrim c = _linkChildren[i];
                        var dWorld = new SVector3(c._position.X - _position.X, c._position.Y - _position.Y, c._position.Z - _position.Z);
                        kids[i + 1] = new CompoundChild
                        {
                            Shape = c._shape,
                            Position = SVector3.Transform(dWorld, invRoot),                                 // world delta -> root frame
                            Orientation = SQuaternion.Multiply(invRoot, c.BodyOrientationOf(c._orientation)),
                            UserData = c.LocalID,
                        };
                    }
                    _compoundShape = _backend.CreateCompoundShape(kids);
                }

                if (_body.IsValid) { _backend.RemoveBody(_body); _body = BodyId.Invalid; }
                CreateBodyInternal();

                if (oldCompound.IsValid) _backend.ReleaseShape(oldCompound);
            }
            catch (Exception e)
            {
                // Never let a linkset rebuild propagate into the heartbeat and wedge the region.
                LegionJoltScene.m_log.Error($"{LegionJoltScene.LogHeader} linkset rebuild EXCEPTION for root {LocalID}: {e}");
            }
            finally { _rebuilding = false; }
        }
        public override void LockAngularMotion(byte axislocks) { }

        // Forces (M8): wired to the backend's accumulate-until-next-Step Apply* (Jolt AddForce/AddTorque
        // == Bullet ApplyCentralForce/ApplyTorque; both auto-activate a sleeping body). BulletSim treats
        // a NON-push AddForce as force-per-second and divides by the frame dt before applying - mirror
        // that so llApplyImpulse/llPushObject land with the same magnitude on both engines.
        public override void AddForce(Vector3 force, bool pushforce)
        {
            if (!_body.IsValid || !_isPhysical || !force.IsFinite())
                return;
            Vector3 f = pushforce ? force : force / _module.LastTimeStep;
            _backend.ApplyForce(_body, ToS(f));
        }

        public override void AddAngularForce(Vector3 force, bool pushforce)
        {
            if (!_body.IsValid || !_isPhysical || !force.IsFinite())
                return;
            _backend.ApplyTorque(_body, ToS(force));   // BulletSim ignores pushforce for angular
        }
        public override void AvatarJump(float forceZ) { }
        public override void SetMomentum(Vector3 momentum) { }

        public override void SetVolumeDetect(int param) { }   // VolumeDetect / phantom-events: M6.6

        // Collision-event subscription: stored so M6.6 can gate Persist forwarding; inert now.
        // A script with a collision handler -> OpenSim calls SubscribeEvents(50). Flip the LIVE body's
        // Persist gate so the ongoing-touch stream (the script `collision` event) reaches the module drain.
        public override void SubscribeEvents(int ms)
        {
            _subscribedMs = ms;
            if (_body.IsValid) _backend.SetBodyWantsContactEvents(_body, ms > 0);
        }
        public override void UnSubscribeEvents()
        {
            _subscribedMs = 0;
            if (_body.IsValid) _backend.SetBodyWantsContactEvents(_body, false);
        }
        public override bool SubscribedEvents() => _subscribedMs > 0;

        // Vehicles (M8): forward the LSL wire params into the backend-agnostic controller. OpenSim's
        // SOP hands us raw ints; the controller keeps the exact Halcyon routing/clamping. Setting a
        // type registers with the scene's per-frame drive + applies the vehicle body params; setting
        // TYPE_NONE unwinds both and destroys the controller.
        public override int VehicleType
        {
            get => _vehicle == null ? 0 : (int)_vehicle.Type;
            set
            {
                EnsureVehicle();
                _vehicle.ProcessTypeChange((LVehicle)value);
                if (_vehicle.IsActive)
                {
                    _module.RegisterVehicle(this);
                    ApplyVehicleBodyParams();
                    // Assert-buoyancy-on-restart (Balpien): the assertion just above can be lost on the load
                    // path (body created GravityFactor=1 with deferred activation drained on the step thread
                    // AFTER this set) - so re-assert it on the next few LIVE step-thread frames, where it sticks.
                    _reassertVehicleFrames = 3;
                }
                else
                {
                    _module.UnregisterVehicle(this);
                    RestoreVehicleBodyParams();
                    _vehicle = null;
                    _vehicleBody = null;
                }
            }
        }

        public override void VehicleFloatParam(int param, float value)
        {
            EnsureVehicle();
            _vehicle.ProcessFloatVehicleParam((LVehicle)param, value);
        }

        public override void VehicleVectorParam(int param, Vector3 value)
        {
            EnsureVehicle();
            _vehicle.ProcessVectorVehicleParam((LVehicle)param, value);
        }

        public override void VehicleRotationParam(int param, Quaternion rotation)
        {
            EnsureVehicle();
            _vehicle.ProcessRotationVehicleParam((LVehicle)param, rotation);
        }

        public override void VehicleFlags(int param, bool remove)
        {
            EnsureVehicle();
            _vehicle.ProcessVehicleFlags(param, remove);
        }

        private void EnsureVehicle()
        {
            if (_vehicle == null)
            {
                _vehicleBody = new JoltVehicleBody(_module, _backend, this);
                _vehicle = new LegionVehicleController(_vehicleBody);
            }
        }

        // Per-frame drive, called by LegionJoltScene.Simulate BEFORE the physics step (the Jolt
        // equivalent of BulletSim's BeforeStep event): snapshot the live body, run the Halcyon math,
        // which pushes velocity changes/forces/torques back through the backend for this step.
        internal void StepVehicle(float timeStep)
        {
            if (_vehicle == null || !_vehicle.IsActive || !_isPhysical || !_body.IsValid)
                return;
            // Assert-buoyancy-on-restart (Balpien): re-assert the vehicle body params on the first live
            // step-thread frames after (re)activation, so the gravity-cancellation that the load-path restore
            // set (but that the deferred body activation clobbered back to GravityFactor=1) actually takes -
            // otherwise a restored boat steps under full engine gravity and sinks despite vehicle=True.
            if (_reassertVehicleFrames > 0)
            {
                _reassertVehicleFrames--;
                ApplyVehicleBodyParams();
                // ★ ZERO the accumulated velocity when buoyancy is (re)asserted. A boat reloaded into the
                // region is born ACTIVE with gravity and can FREE-FALL for the whole load window (incl. the
                // multi-second reload stall) before the controller first steps here - reaching ~60 m/s. Buoyancy
                // only cancels gravity; it does NOT remove that already-accumulated velocity, so without this the
                // boat keeps plunging to the seabed despite vehicle=True. Zeroing (before BeginFrame/hover below)
                // arrests the fall; hover then lifts it from rest to the water surface. A live boat re-activated
                // at runtime is at rest anyway, so zeroing is a no-op for it.
                _backend.SetBodyLinearVelocity(_body, SVector3.Zero);
                _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
                _velocity = Vector3.Zero;
                _rotationalVelocity = Vector3.Zero;
            }
            if (_vehicleBody.BeginFrame())
                _vehicle.Step(timeStep);
        }

        // The BulletSim vehicle body setup (LegionVehicleDynamics.SetPhysicalParameters), translated:
        // the vehicle controls its own friction/damping (BSParam.VehicleFriction/Restitution/
        // AngularDamping all default 0; Jolt's default 0.05 damping would fight the motor math),
        // applies gravity MANUALLY (engine gravity off), and must never sleep (DISABLE_DEACTIVATION).
        // Re-applied after every body recreate (reposition/reshape/weld) while the vehicle is active.
        private void ApplyVehicleBodyParams()
        {
            if (_vehicle == null || !_vehicle.IsActive || !_isPhysical || !_body.IsValid)
                return;
            _backend.SetBodyFriction(_body, 0f);
            _backend.SetBodyRestitution(_body, 0f);
            _backend.SetBodyDamping(_body, 0f, 0f);
            _backend.SetBodyGravityFactor(_body, 0f);
            _backend.SetBodyAllowSleeping(_body, false);
            _backend.ActivateBody(_body);
        }

        private void RestoreVehicleBodyParams()
        {
            if (!_body.IsValid)
                return;
            BodyDesc d = BodyDesc.Default;
            _backend.SetBodyFriction(_body, d.Friction);
            _backend.SetBodyRestitution(_body, d.Restitution);
            _backend.SetBodyDamping(_body, d.LinearDamping, d.AngularDamping);
            _backend.SetBodyGravityFactor(_body, 1f);
            _backend.SetBodyAllowSleeping(_body, true);
        }

        // PID / hover / RotLookAt - physical-motion features (M6.4+).
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
