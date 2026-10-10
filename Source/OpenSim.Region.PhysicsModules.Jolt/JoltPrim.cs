/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A prim as a Jolt rigid body.
//
// This is the PhysicsActor OpenSim hands back from AddPrimShape. NON-PHYSICAL prims become STATIC
// Jolt bodies (collision citizens that never move); physical prims become DYNAMIC bodies. Simple
// box / sphere / cylinder shapes cook straight to a Jolt primitive via the fixed-shape fast path (no
// meshmerizer); other shapes go through the region's IMesher. PhysicsActor members this class does not
// support are deliberately inert.
//
// Types: PhysicsActor speaks OpenMetaverse.Vector3/Quaternion (unqualified here); the backend speaks
// System.Numerics (SVector3/SQuaternion). Body orientation is composed in System.Numerics because the
// cylinder axis-correction ordering must be unambiguous (left operand applied first).

using System;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;
using VehicleCode = OpenSim.Region.PhysicsModules.Jolt.Vehicles.Vehicle;   // OpenSim.Region.PhysicsModules.Jolt.Vehicles' copy of the LSL wire codes (SharedBase also has a Vehicle enum)

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class JoltPrim : PhysicsActor
    {
        private readonly JoltScene _module;
        private readonly IPhysicsBackend _backend;

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

        // The physics material: llSetPhysicsMaterial and PRIM_MATERIAL. Ranges from the SL wiki (llSetPhysicsMaterial):
        // gravity_multiplier "range [-1.0, +28.0], default: 1.0", restitution "range [0.0, 1.0]", friction "range [0.0,
        // 255.0]", density "range [1.0, 22587.0] kg/m^3, default: 1000.0". A value outside its range is clamped to it; a
        // non-finite one is refused. Core sets all four on every new actor (SceneObjectPart.AddToPhysics); until then a
        // prim has BodyDesc's friction and restitution.
        private float _friction = BodyDesc.Default.Friction;
        private float _restitution = BodyDesc.Default.Restitution;
        private float _gravityModifier = 1f;
        internal const float MaxFriction = 255f;
        internal const float MinGravityModifier = -1f;
        internal const float MaxGravityModifier = 28f;
        internal const float MinDensity = 1f;
        internal const float MaxDensity = 22587f;

        // PRIM_MATERIAL_STONE (0) to PRIM_MATERIAL_LIGHT (7): friction and restitution as the SL wiki lists them
        // (llSetPhysicsMaterial's notes and PRIM_MATERIAL's constants table). "Using PRIM_MATERIAL to set the material type
        // will reset the values for friction and restitution to that material's defaults."
        internal static readonly (float Friction, float Restitution)[] MaterialTable =
        {
            (0.8f, 0.4f),   // stone
            (0.3f, 0.4f),   // metal
            (0.2f, 0.7f),   // glass
            (0.6f, 0.5f),   // wood
            (0.9f, 0.3f),   // flesh
            (0.4f, 0.7f),   // plastic
            (0.9f, 0.9f),   // rubber
            (0.6f, 0.5f),   // light
        };

        // The linear and angular damping of a physical prim that is not a vehicle, per second: [Jolt] PrimLinearDamping and
        // PrimAngularDamping, 0.05 by default (Jolt's own). The SL wiki gives no figure (llSetPhysicsMaterial: a collision of
        // two objects with restitution 1.0 "will still not be perfectly elastic due to damping in the physics engine";
        // llSetAngularVelocity: a spun cube with gravity 0 "slows down over time"). ubODE's, as a rate, are 0.1001 and 0.0250
        // (ODEPrim: dBodySetDamping(Body, .002f, .0005f), which ODE applies as v x (1 - scale) every 0.020 s step). Jolt
        // applies v x (1 - rate x step) each step, the same decay per second at any step rate. A vehicle has none (its
        // controller does its own friction).
        private float PrimLinearDamping => _module.PrimLinearDamping;
        private float PrimAngularDamping => _module.PrimAngularDamping;

        // Set while a new physical body waits for its first activation (RegisterPendingActivation): a property set before
        // then must not wake it, or it would step before everything about it is set up.
        private bool _activationPending;

        private ShapeId _shape = ShapeId.Invalid;   // one handle-ref held for the prim's life
        private BodyId _body = BodyId.Invalid;
        // Assert buoyancy on restart: re-assert the vehicle's body params (gravity-cancellation,
        // no-sleep, ...) on every LIVE StepVehicle step that starts within this many seconds (the time left of
        // ReassertVehicleSeconds). The load-path assertion in the
        // VehicleType restore can be lost because the body was created (GravityFactor=1) with its activation
        // DEFERRED to the step thread, which drains the creation settings AFTER the load-thread SetGravityFactor
        // - so the body starts stepping under full gravity and sinks. Re-asserting from the step thread, on the
        // live body, makes it stick. Set when the vehicle becomes active; runtime llSetVehicleType sets it too
        // (harmless - the body is already live so it sticks first time).
        private float _reassertVehicleTime;

        // How long after a vehicle becomes active its body params are re-asserted and, with no linear motor set, its
        // velocity zeroed: the steps that start within this time, the same span at any step rate. 0.27 s is the three
        // heartbeats it was at the default FrameTime (0.0909 s).
        internal const float ReassertVehicleSeconds = 0.27f;

        // Maps the cooked shape's local axis onto SL's convention. Identity for box/sphere; a +90 deg
        // rotation about X for a cylinder (Jolt's CylinderShape axis is Y, SL cylinders are Z-height).
        // Composed as (correction * primOrientation) so the prim's own rotation still applies.
        private SQuaternion _axisCorrection = SQuaternion.Identity;
        private string _shapeKind = "?";

        private int _subscribedMs;   // collision-event subscription window; gates Persist forwarding

        // This prim's last non-finite-rejection log line (NonFiniteGuard rate limit, one per 10 s).
        private long _nonFiniteLogTicks;

        // Linksets. OpenSim adds each prim as its own PhysicsActor then calls child.link(root) per
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
        // The mass of this prim's body, the one the engine moves (a welded linkset's root: the whole linkset's); 0 without
        // a body. Read by the console test commands.
        internal float BodyMass => _body.IsValid ? _backend.GetBodyMass(_body) : 0f;

        // Live vehicle state for the `jolt vehiclestatus` console command (type / buoyancy / active).
        internal string VehicleInfo()
        {
            if (_vehicle == null) return "no-vehicle";
            return $"type={_vehicle.Type} active={(_vehicle.IsActive ? "Y" : "N")} " +
                   $"buoyancy={_vehicle.GetFloatParam(VehFloatParam.Buoyancy):0.00} " +
                   $"hoverHeight={_vehicle.GetFloatParam(VehFloatParam.HoverHeight):0.00}";
        }

        // Vehicles: the backend-agnostic vehicle controller + its Jolt seam. Created lazily on
        // the first Vehicle* call; ACTIVE (stepped per-frame, body params applied) only while the
        // controller's type != NONE and the prim is physical. Setting TYPE_NONE destroys it (spec).
        private VehicleController _vehicle;
        private JoltVehicleBody _vehicleBody;

        // Body orientation -> PRIM orientation (undo the cylinder axis-correction; identity for
        // box/sphere/mesh). Same conversion the drain does in ApplyStepState.
        internal Quaternion PrimOrientationOf(SQuaternion bodyOrient)
        {
            SQuaternion prim = SQuaternion.Multiply(SQuaternion.Conjugate(_axisCorrection), bodyOrient);
            return new Quaternion(prim.X, prim.Y, prim.Z, prim.W);
        }

        internal JoltPrim(JoltScene module, IPhysicsBackend backend, uint localid, string name,
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
            _shape = _module.CookPrimShape(_backend, _pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            _ownMass = null;
            CreateBodyInternal();
        }

        // Create the Jolt body for the CURRENT _isPhysical / _shape / _axisCorrection and cached
        // transform + velocity. Non-physical -> Static (no MotionProperties: the 65k-prim startup guard).
        // Physical -> Dynamic + StartActive (wakes so it falls); mass computed Volume*Density (Density
        // from BodyDesc.Default = 1000). A body that may go physical is created movable ONLY when it is
        // physical (a Static-born body can't be promoted - the toggle recreates instead).
        private void CreateBodyInternal()
        {
            // Load-time position sanity: never bring a PHYSICAL body up penetrating the terrain. If
            // the saved/current centre is below where it rests on the surface, lift it there and zero its
            // velocity BEFORE the body goes active - so (1) the bad position never drains back + persists, and
            // (2) the native solver doesn't churn resolving a deep penetration (a multi-second load
            // stall). Only lifts BELOW-terrain bodies: a resting box or a boat FLOATING on water (above the
            // surface) is left exactly where it is. Zeroing velocity also stops the post-lift slide (a boat's
            // horizontal drift). The compound root lifts its whole welded set uniformly (sub-shape
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
            desc.Friction = _friction;                 // a compound's children get their own just below
            desc.Restitution = _restitution;
            if (_isPhysical)
            {
                desc.Layer = PhysicsLayer.Dynamic;
                desc.MotionType = BodyMotionType.Dynamic;
                desc.Mass = 0f;                        // <=0 -> backend computes Volume*Density
                desc.Density = PhysicalDensity;        // honour the SOP density (BulletSim mass parity)
                desc.LinearDamping = PrimLinearDamping;
                desc.AngularDamping = PrimAngularDamping;
                desc.GravityFactor = ScriptGravityFactor;   // llSetBuoyancy (a vehicle sets its own just below)
                // Same structure as BulletSim's taint-deferred creation: create the body INERT (asleep), never
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

            // A linkset: each child's own friction and restitution, and a mass that sums each part's volume x its own
            // density ("Can individual prims in a linked set have different Physics settings? Yes.", SL wiki, Physics
            // Material Settings test).
            if (_body.IsValid && _compoundShape.IsValid && _linkChildren != null)
            {
                foreach (JoltPrim c in _linkChildren)
                    _backend.SetBodyPartMaterial(_body, c.LocalID, c._friction, c._restitution);
                if (_isPhysical)
                    _backend.SetBodyMass(_body, LinksetMass());
            }

            if (_isPhysical)
            {
                _activationPending = true;
                // Defer activation to the top of the next Simulate (step thread). ActivateBody on a NON-step
                // thread does not reliably reach the step-thread active-set; enqueuing it via the pending set
                // (drained in Simulate before StepVehicles/StepOnce) both fixes that AND gives us BulletSim's
                // configure-before-step barrier - the body is asleep until every load-time property (incl. the
                // vehicle) is applied, so it never free-falls before its buoyancy is asserted.
                _module.RegisterPendingActivation(this);
                if (_backend.TryGetBodyState(_body, out BodyState st))
                    JoltScene.m_log.LogDebug(
                        $"{JoltScene.LogHeader} physical body id={LocalID} created (deferred activation): active={((st.Flags & BodyStateFlags.Active) != 0)} posZ={st.Position.Z:0.00} shape={_shapeKind}");

                // A recreate (reposition/reshape/weld/physical-toggle) makes a FRESH body with default
                // params; an active vehicle must re-assert its no-friction/no-damping/manual-gravity/
                // never-sleep setup on it.
                ApplyVehicleBodyParams();
                // A new body starts free to turn about every axis: re-apply the script's STATUS_ROTATE locks.
                if (_rotationLocks != 0)
                    ApplyRotationLocks();
                // A new body of an object that is selected (a relink, resize, physics toggle or unlink while it is being
                // edited) starts held, as the old one was.
                if (_selected && _linkRoot == null)
                    HoldSelected();
            }
        }

        // Called by JoltScene at the top of Simulate (step thread) to activate a physical body that was
        // created inert (deferred activation - the BulletSim configure-before-step barrier). By now every
        // load-time property is applied and the vehicle drive is about to run this frame, so waking the body
        // here means it enters the engine step already configured (gravity cancelled for a vehicle) - it never
        // free-falls. No-op for a non-physical/destroyed body or one already awake.
        internal void ActivatePending()
        {
            _activationPending = false;
            if (_body.IsValid && _isPhysical && !_selected)
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
            // neighbour), which spins the heartbeat ~5 s per body - a boot stall. Drop the
            // glitch update (keep the last good transform) instead of propagating it into a crossing.
            if (!(float.IsFinite(newPos.X) && float.IsFinite(newPos.Y) && float.IsFinite(newPos.Z))
                || MathF.Abs(newPos.X) > 1e5f || MathF.Abs(newPos.Y) > 1e5f || MathF.Abs(newPos.Z) > 1e5f)
            {
                JoltScene.m_log.LogWarning($"{JoltScene.LogHeader} [physglitch] body {LocalID} implausible pos {newPos} vel {s.LinearVelocity} - update dropped (no crossing)");
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
            _shape = _module.CookPrimShape(_backend, _pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            _ownMass = null;
            _backend.SetBodyShape(_body, _shape, recomputeMass: false);   // keeps the body at its current transform
            // The new shape's mass (and inertia, keeping any rotation locks): volume x density, as at creation. Without
            // this a resized physical prim kept the mass of its old size, and llGetMass with it.
            if (_isPhysical)
                _backend.SetBodyDensity(_body, _simDensity > 0f ? _simDensity * DensityScaleFactor : BodyDesc.Default.Density);
            if (old.IsValid)
                _backend.ReleaseShape(old);
        }

        // Called by JoltScene.RemovePrim. RemoveBody drops the body's native shape ref; releasing
        // our handle-ref then frees the shape - no leak, no premature free.
        internal void Destroy()
        {
            // Drop out of the scene's per-frame vehicle drive (no-op if never a vehicle).
            if (_vehicle != null) { _module.UnregisterVehicle(this); _vehicle = null; _vehicleBody = null; }
            _module.SetScriptForced(this, false);
            // If welded into a parent compound, detach first (parent rebuilds without us).
            _welded = false;
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
        // Members not wired to the body below are inert.
        // ---------------------------------------------------------------------

        public override Vector3 Position
        {
            get => _position;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Position", value.ToString()); return; }
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
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Orientation", value.ToString()); return; }
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
        // it every ~30 ms (an airplane vehicle became uncontrollable / fell through). Only PURE position/orientation
        // moves reach here (Size/Shape -> Rebuild, physical toggle -> RecreateBody still do full rebuilds).
        //
        // A move or turn wakes a sleeping physical body, as ubODE's does (ODEPrim changePosition and changeOrientation
        // enable a disabled body), so it carries on from where it was put and is never left hanging asleep in the air.
        // Not while the region loads or before the body's first activation: a load-time body created inert (deferred
        // activation) stays inert until DrainPendingActivation, preserving the configure-before-step barrier (a reloaded
        // vehicle never free-falls before its gravity-cancel is asserted). Not while the object is selected either: it is
        // held where the build tool puts it. Orientation goes through BodyOrientationOf (axis-correction applied),
        // matching CreateBodyInternal.
        private void RepositionBody()
        {
            if (!_body.IsValid) { Build(); return; }

            // Load-time un-bury. A remove+recreate reposition would re-run CreateBodyInternal, so a load-time
            // Position push carrying a saved BELOW-terrain position would be lifted (TryUnburyPhysicalLoad)
            // before the body went active. In-place
            // SetBodyTransform skips CreateBodyInternal, so without this a penetrating load position would be
            // pushed straight into the terrain -> the CollisionSteps=6 solver churns resolving the deep
            // penetration -> a multi-second boot stall. Re-apply the SAME un-bury here, reusing the shared helper.
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

            _backend.SetBodyTransform(_body, ToS(_position), BodyOrientationOf(_orientation), activate: MayWake);
        }

        public override Vector3 Size
        {
            get => _size;
            set
            {
                if (!NonFiniteGuard.OkSize(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Size", value.ToString()); return; }
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
                // A Static-born body has no MotionProperties and CANNOT be promoted
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
            _shape = _module.CookPrimShape(_backend, _pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            _ownMass = null;
            if (_body.IsValid)
                _backend.RemoveBody(_body);
            CreateBodyInternal();
            if (old.IsValid)
                _backend.ReleaseShape(old);
        }

        // This prim's own mass: core adds up every part's (SceneObjectGroup.GetMass) for llGetMass and llGetObjectMass, as
        // ubODE's per-part mass (ODEPrim.Mass, density x volume for every prim) is added up. A lone physical prim reads its
        // body's mass, the one the engine moves; any other prim (non-physical, a linkset's root or a welded child) its own
        // volume x density. The SL wiki gives a non-physical object a mass like any other ("Returns a float that is the
        // mass of object", llGetMass), and "mass = density * volume" (Physics Material Settings test).
        public override float Mass
        {
            get
            {
                if (_isPhysical && _body.IsValid && !_compoundShape.IsValid && _linkRoot == null)
                    return _backend.GetBodyMass(_body);
                OwnMassData own = OwnMass();
                return own == null ? 0f : MathF.Max(own.Volume * PhysicalDensity, 1e-3f);
            }
        }

        // The volume and centre of mass of this prim's shape as it is when physical, so neither reading changes when
        // physics is switched on or off. A non-physical prim that is not a box, sphere or cylinder collides as its triangle
        // mesh, which has no volume; its physical shape (the convex hull) is cooked once to read them, then released.
        private sealed class OwnMassData
        {
            public float Volume;
            public SVector3 CenterOfMass;          // in the shape's frame
            public SQuaternion AxisCorrection;     // that shape's axis correction
        }
        private OwnMassData _ownMass;              // null until read; cleared whenever the shape is cooked again

        private OwnMassData OwnMass()
        {
            OwnMassData own = _ownMass;
            if (own != null)
                return own;
            ShapeId shape = _shape;
            if (!shape.IsValid)
                return null;
            own = new OwnMassData { AxisCorrection = _axisCorrection };
            if (_isPhysical || _backend.GetShapeVolume(shape) > 0f)
            {
                own.Volume = _backend.GetShapeVolume(shape);
                own.CenterOfMass = _backend.GetShapeCenterOfMass(shape);
            }
            else
            {
                ShapeId hull = _module.CookPrimShape(_backend, _pbs, _size, true, out SQuaternion correction, out _);
                try
                {
                    own.Volume = _backend.GetShapeVolume(hull);
                    own.CenterOfMass = _backend.GetShapeCenterOfMass(hull);
                    own.AxisCorrection = correction;
                }
                finally
                {
                    if (hull.IsValid)
                        _backend.ReleaseShape(hull);
                }
            }
            _ownMass = own;
            return own;
        }

        // OpenSim sets pa.Density = SceneObjectPart.Density in AddToPhysics (default 1000). Store it and
        // forward the PHYSICAL density (x DensityScaleFactor) so Jolt's mass matches BulletSim. A live
        // physical body recomputes immediately; a not-yet-physical prim applies it at CreateBodyInternal.
        // The SL wiki: "mass = density * volume. That volume is the true volume of the shape." (Physics Material Settings
        // test), and llGetMass is in lindograms, kg / 100. A welded child's density counts in its root's mass.
        public override float Density
        {
            get => _simDensity > 0f ? _simDensity : BodyDesc.Default.Density;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Density", value.ToString()); return; }
                value = Math.Clamp(value, MinDensity, MaxDensity);
                if (_simDensity == value) return;
                _simDensity = value;
                (_linkRoot ?? this).ApplyMass();
            }
        }

        // The density the backend works in (kg/m^3): the SOP density x DensityScaleFactor.
        private float PhysicalDensity => _simDensity > 0f ? _simDensity * DensityScaleFactor : BodyDesc.Default.Density;

        // A welded linkset's mass: each part's shape volume x its own density.
        private float LinksetMass()
        {
            float mass = _backend.GetShapeVolume(_shape) * PhysicalDensity;
            foreach (JoltPrim c in _linkChildren)
                mass += _backend.GetShapeVolume(c._shape) * c.PhysicalDensity;
            return MathF.Max(mass, 1e-3f);
        }

        // The body's mass from its density (its parts' densities for a welded linkset), and the body woken: a heavier or
        // lighter body moves differently under the same forces.
        private void ApplyMass()
        {
            if (!_body.IsValid || !_isPhysical)
                return;
            if (_compoundShape.IsValid && _linkChildren != null && _linkChildren.Count > 0)
                _backend.SetBodyMass(_body, LinksetMass());
            else
                _backend.SetBodyDensity(_body, PhysicalDensity);
            WakeBody();
        }

        // Wakes the body after a script changed how it moves. Not while the region loads, nor before a new body's first
        // activation: it is woken then, once everything about it is set up (CreateBodyInternal). Not while the object is
        // selected: it wakes when it is let go.
        private void WakeBody()
        {
            if (MayWake && _body.IsValid)
                _backend.ActivateBody(_body);
        }

        private bool MayWake => _isPhysical && !_selected && !_activationPending && !_module.IsRegionLoading;

        // llSetPhysicsMaterial FRICTION and RESTITUTION, and PRIM_MATERIAL (SetMaterial). Each prim of a linkset has its own
        // (SL wiki, Physics Material Settings test: "Can individual prims in a linked set have different Physics settings?
        // Yes."); a contact uses the struck prim's. Two touching surfaces combine as ubODE combines them, friction
        // sqrt(f1 x f2) and restitution r1 x r2 (JoltPhysicsBackend.CombineMaterials); the SL wiki gives no rule.
        public override float Friction
        {
            get => _friction;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Friction", value.ToString()); return; }
                SetContactMaterial(Math.Clamp(value, 0f, MaxFriction), _restitution);
            }
        }

        public override float Restitution
        {
            get => _restitution;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Restitution", value.ToString()); return; }
                SetContactMaterial(_friction, Math.Clamp(value, 0f, 1f));
            }
        }

        // PRIM_MATERIAL: the material's friction and restitution (MaterialTable). Core calls this when the material changes,
        // and on every new actor before it sets Friction and Restitution (SceneObjectPart.AddToPhysics). Any other value is
        // ignored.
        public override void SetMaterial(int material)
        {
            if (material < 0 || material >= MaterialTable.Length)
                return;
            SetContactMaterial(MaterialTable[material].Friction, MaterialTable[material].Restitution);
        }

        private void SetContactMaterial(float friction, float restitution)
        {
            if (_friction == friction && _restitution == restitution)
                return;
            _friction = friction;
            _restitution = restitution;
            // A vehicle sets its own contact friction and restitution (ApplyVehicleBodyParams); these come back when it
            // stops being one.
            JoltPrim root = _linkRoot ?? this;
            if (!root._body.IsValid || (root._vehicle != null && root._vehicle.IsActive))
                return;
            _backend.SetBodyPartMaterial(root._body, LocalID, friction, restitution);
            root.WakeBody();
        }

        // llSetPhysicsMaterial GRAVITY_MULTIPLIER: the body's gravity is (1 - buoyancy) x the multiplier x the region's, as
        // ubODE computes it (ODEPrim.Move). The root prim's applies to the whole linkset, as in ubODE; a child's is kept.
        public override float GravModifier
        {
            get => _gravityModifier;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "GravModifier", value.ToString()); return; }
                value = Math.Clamp(value, MinGravityModifier, MaxGravityModifier);
                if (_gravityModifier == value) return;
                _gravityModifier = value;
                if (_linkRoot == null)
                    ApplyScriptGravity();
            }
        }
        public override bool Stopped => true;

        public override Vector3 GeometricCenter => _position;

        // llGetCenterOfMass, in region coordinates. The SL wiki: "Returns the vector position of the object's center of
        // mass in region coordinates." and "If called from a child prim, the child's center of mass is returned instead".
        // Core reads a physical object's from its root's actor and a child's from the child's actor; for a non-physical
        // object it takes the mass-weighted mean of every part's (SceneObjectGroup.GetCenterOfMass). So a physical
        // linkset's root reports the whole linkset's, each part's weighted by its own volume x density, and every other
        // prim its own part's, as ubODE does (ODEPrim.CenterOfMass: the body's position, or the prim's own centre).
        public override Vector3 CenterOfMass
        {
            get
            {
                if (_isPhysical && _linkRoot == null && _compoundShape.IsValid && _linkChildren != null && _linkChildren.Count > 0)
                    return LinksetCenterOfMass();
                return OwnCenterOfMass(out _);
            }
        }

        private Vector3 LinksetCenterOfMass()
        {
            Vector3 sum = OwnCenterOfMass(out float total);
            sum *= total;
            JoltPrim[] children = _linkChildren.ToArray();
            foreach (JoltPrim c in children)
            {
                Vector3 com = c.OwnCenterOfMass(out float m);
                sum += com * m;
                total += m;
            }
            return total > 0f ? sum / total : _position;
        }

        // This prim's own centre of mass in region coordinates, and its own mass. A welded child is placed by its root's
        // body, which is all the engine moves: the child sits where it was welded (WeldOffset) in the root's frame.
        private Vector3 OwnCenterOfMass(out float mass)
        {
            OwnMassData own = OwnMass();
            mass = own == null ? 0f : MathF.Max(own.Volume * PhysicalDensity, 1e-3f);
            SVector3 bodyPos;
            SQuaternion primRot;
            JoltPrim root = _linkRoot;
            if (root != null && _welded)
            {
                SQuaternion rootBody = root.BodyOrientationOf(root._orientation);
                bodyPos = ToS(root._position) + SVector3.Transform(_weldPosition, rootBody);
                // The inverse of BodyOrientationOf: body = correction x prim.
                primRot = SQuaternion.Multiply(SQuaternion.Conjugate(_axisCorrection), SQuaternion.Multiply(rootBody, _weldOrientation));
            }
            else
            {
                bodyPos = ToS(_position);
                primRot = ToS(_orientation);
            }
            if (own == null)
                return new Vector3(bodyPos.X, bodyPos.Y, bodyPos.Z);
            SVector3 com = bodyPos + SVector3.Transform(own.CenterOfMass, SQuaternion.Multiply(own.AxisCorrection, primRot));
            return new Vector3(com.X, com.Y, com.Z);
        }

        // Where a welded child sits in its root's body: the offset and body orientation its compound sub-shape was given.
        private bool _welded;
        private SVector3 _weldPosition;
        private SQuaternion _weldOrientation = SQuaternion.Identity;

        // Linear/angular velocity: cached from the drain (SOP reads these for terse updates); a set on a
        // live physical body pushes through so a script llSetVelocity takes effect.
        public override Vector3 Velocity
        {
            get => _velocity;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Velocity", value.ToString()); return; }
                Vector3 v = value;
                // Drop restored HORIZONTAL velocity on region LOAD. OpenSim's AddToPhysics
                // replays the DB-saved velocity onto the actor; a vehicle body runs frictionless + never-
                // sleep, so a small saved X/Y coast never bleeds off and drifts the boat metres - and
                // compounds, since that drift is persisted and replayed next reload. Zeroing it
                // only while the region is LOADING (before the first Simulate) leaves a runtime llSetVelocity
                // untouched. Vertical is left alone (a genuinely falling load body keeps its descent).
                if (_isPhysical && _module.IsRegionLoading)
                {
                    v.X = 0f;
                    v.Y = 0f;
                }
                if (_selected) return;   // held still while selected, as ubODE's changevelocity
                _velocity = v;
                if (_body.IsValid && _isPhysical) _backend.SetBodyLinearVelocity(_body, ToS(v));
            }
        }
        public override Vector3 RotationalVelocity
        {
            get => _rotationalVelocity;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "RotationalVelocity", value.ToString()); return; }
                if (_selected) return;
                _rotationalVelocity = value;
                if (_body.IsValid && _isPhysical) _backend.SetBodyAngularVelocity(_body, ToS(value));
            }
        }
        // Script forces: llSetForce and llSetTorque (persistent, region axes: core turns a local vector into region axes
        // before it gets here), llSetBuoyancy and the STATUS_ROTATE_* locks. Core hands these to the root prim's actor; a
        // welded child hands any that reach it on to its root, so a linkset is one object (the SL wiki, llSetBuoyancy:
        // "The most recent call of llSetBuoyancy in any child prim appears to set the global buoyancy level for the
        // object."). They are kept on the prim and survive a body recreate (resize, relink, physics toggle).
        private readonly object _scriptForceLock = new object();
        private Vector3 _force;        // N, region axes; applied before every backend step until set to zero
        private Vector3 _torque;       // N m, region axes; the same
        private float _buoyancy;       // the body feels (1 - buoyancy) of the region's gravity
        private byte _rotationLocks;   // SceneObjectGroup.axisSelect bits: 0x02 X, 0x04 Y, 0x08 Z locked

        public override Vector3 Torque
        {
            get { if (_linkRoot != null) return _linkRoot.Torque; lock (_scriptForceLock) return _torque; }
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Torque", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.Torque = value; return; }
                bool on;
                lock (_scriptForceLock) { _torque = value; on = _force != Vector3.Zero || _torque != Vector3.Zero; }
                _module.SetScriptForced(this, on);
            }
        }

        public override Vector3 Force
        {
            get { if (_linkRoot != null) return _linkRoot.Force; lock (_scriptForceLock) return _force; }
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Force", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.Force = value; return; }
                bool on;
                lock (_scriptForceLock) { _force = value; on = _force != Vector3.Zero || _torque != Vector3.Zero; }
                _module.SetScriptForced(this, on);
            }
        }

        // Called by JoltScene before every backend step for a prim with a force or torque set: the force and torque act
        // for that step, so they hold at every step rate until the script sets them to zero. Jolt's AddForce and
        // AddTorque wake a sleeping body. Vehicles keep their own forces: ubODE does not add a vehicle's set force or
        // torque (ODEPrim.Move hands a vehicle to its controller before it adds them) and core does not hand them to a
        // vehicle when it sets its physics up (SceneObjectPart.ApplyPhysics).
        internal void StepScriptForces(float timeStep)
        {
            if (!_isPhysical || !_body.IsValid || _selected || (_vehicle != null && _vehicle.IsActive))
                return;
            Vector3 force, torque;
            lock (_scriptForceLock) { force = _force; torque = _torque; }
            if (force != Vector3.Zero)
                _backend.ApplyForce(_body, LimitForce(ToS(force), timeStep));
            if (torque != Vector3.Zero)
                _backend.ApplyTorque(_body, LimitTorque(ToS(torque), timeStep));
        }

        // An absurd but finite force or torque is cut to the one that brings the body from rest to the engine's speed cap
        // ([Jolt] BodyMaxLinearSpeed, BodyMaxAngularSpeed) in one step. The engine caps the speed there anyway, so this
        // changes nothing a script can see; it keeps the engine's arithmetic finite (a 1e30 N force on a 1 g body would
        // overflow the velocity to infinity, and the cap would then turn it into NaN).
        private SVector3 LimitForce(SVector3 force, float timeStep)
        {
            float max = _module.BodyMaxLinearSpeed * _backend.GetBodyMass(_body) / timeStep;
            float len = force.Length();
            return len > max && len > 0f ? force * (max / len) : force;
        }

        private SVector3 LimitTorque(SVector3 torque, float timeStep)
        {
            SVector3 inertia = _backend.GetBodyInertiaDiagonal(_body);
            float smallest = float.MaxValue;
            if (inertia.X > 0f) smallest = MathF.Min(smallest, inertia.X);
            if (inertia.Y > 0f) smallest = MathF.Min(smallest, inertia.Y);
            if (inertia.Z > 0f) smallest = MathF.Min(smallest, inertia.Z);
            if (smallest == float.MaxValue)
                return torque;   // every axis locked: nothing turns it
            float max = _module.BodyMaxAngularSpeed * smallest / timeStep;
            float len = torque.Length();
            return len > max && len > 0f ? torque * (max / len) : torque;
        }

        public override Vector3 Acceleration { get => Vector3.Zero; set { } }
        public override float CollisionScore { get; set; }
        public override bool Kinematic { get => false; set { } }

        // llSetBuoyancy as the SL wiki describes it: "A buoyancy value of 0.0 disables the effect", "when buoyancy is
        // < 1.0, the object sinks", "when buoyancy equals 1.0 it floats", "when buoyancy is > 1.0 the object rises". The
        // body's gravity is (1 - buoyancy) of the region's, as ubODE computes it (ODEPrim.Move). A vehicle decides its own
        // gravity (VEHICLE_BUOYANCY); ubODE leaves llSetBuoyancy out for a vehicle too, and the value is kept for when the
        // vehicle is removed.
        public override float Buoyancy
        {
            get => _linkRoot != null ? _linkRoot.Buoyancy : _buoyancy;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Buoyancy", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.Buoyancy = value; return; }
                if (_buoyancy == value) return;
                _buoyancy = value;
                ApplyScriptGravity();
            }
        }

        // The script's gravity (buoyancy and gravity multiplier) on the body. A changed gravity wakes the body, as ubODE's
        // changeBuoyancy does.
        private void ApplyScriptGravity()
        {
            if (!_isPhysical || !_body.IsValid || (_vehicle != null && _vehicle.IsActive))
                return;
            _backend.SetBodyGravityFactor(_body, ScriptGravityFactor);
            WakeBody();
        }

        // The body's gravity factor for the script's buoyancy and gravity multiplier. Its size is cut to what takes a body
        // from rest to the engine's speed cap in one 1 ms step: no larger value gives a different motion, and the engine's
        // arithmetic stays finite for any finite buoyancy.
        private float ScriptGravityFactor
        {
            get
            {
                float max = _module.BodyMaxLinearSpeed * JoltConfig.MaxPhysicsStepRate / MathF.Max(_module.DefaultGravity.Length(), 1f);
                return Math.Clamp((1f - _buoyancy) * _gravityModifier, -max, max);
            }
        }
        public override bool Flying { get => false; set { } }
        public override bool SetAlwaysRun { get => false; set { } }
        public override bool ThrottleUpdates { get => false; set { } }
        public override bool IsColliding { get; set; }
        public override bool CollidingGround { get; set; }
        public override bool CollidingObj { get; set; }
        public override bool Grabbed { set { } }

        // Selection in the build tool. Core hands the same value to the root's actor and every part's
        // (SceneObjectGroup.IsSelected). A selected physical object stops where it is and stays: no gravity, no drift, no
        // script force, impulse, velocity or vehicle step, and nothing that hits it moves it. Let go, it carries on from
        // rest. That is ubODE's (ODEPrim.DoSelectedStatus stops and disables the body; Move skips a selected prim). ubODE
        // also stops a selected object colliding; here it stays solid, as BulletSim's selected object (BSPrim.IsStatic).
        // The root holds the whole welded linkset; a welded child's flag is kept for when it is unlinked.
        private volatile bool _selected;

        public override bool Selected
        {
            set
            {
                if (_selected == value)
                    return;
                _selected = value;
                if (_linkRoot != null || !_isPhysical || !_body.IsValid)
                    return;
                if (value)
                    HoldSelected();
                else
                    ReleaseSelected();
            }
        }

        // Held: the body is made kinematic (it keeps its place, nothing it touches moves it, gravity does not act) and
        // stopped, and the simulator is told it stopped.
        private void HoldSelected()
        {
            _backend.SetBodyLinearVelocity(_body, SVector3.Zero);
            _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
            _backend.SetBodyMotionType(_body, BodyMotionType.Kinematic, activate: false);
            _backend.DeactivateBody(_body);
            _velocity = Vector3.Zero;
            _rotationalVelocity = Vector3.Zero;
            RequestPhysicsterseUpdate();
        }

        // Let go: dynamic again with its mass (and rotation locks) as before, at rest, and awake so it falls or settles
        // from where it was left. A vehicle gets its body setup back.
        private void ReleaseSelected()
        {
            _backend.SetBodyMotionType(_body, BodyMotionType.Dynamic, activate: false);
            _backend.SetBodyLinearVelocity(_body, SVector3.Zero);
            _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
            ApplyMass();
            ApplyVehicleBodyParams();
            WakeBody();
        }

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
            _welded = false;
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
        // loads at once, and each inline rebuild churned RemoveBody/CreateBody on the active root body while
        // the load + heartbeat ran concurrently. Deferring coalesces
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
        // body ops, no per-child churn). StaticCompoundShape (fast query + per-child UserData for per-child collision identity).
        // Sub-shape transforms are composed in the ROOT BODY frame (BodyOrientationOf carries any cylinder
        // axis-correction); mass/COM/inertia come out of Jolt's compound assembly (mass = sum of child
        // Volume x density). Re-entrancy- and destroyed-guarded so it can never loop or touch
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
                        c._weldPosition = kids[i + 1].Position;
                        c._weldOrientation = kids[i + 1].Orientation;
                        c._welded = true;
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
                JoltScene.m_log.LogError($"{JoltScene.LogHeader} linkset rebuild EXCEPTION for root {LocalID}: {e}");
            }
            finally { _rebuilding = false; }
        }
        // llSetStatus STATUS_ROTATE_X/_Y/_Z: "Can turn along this axis (physical objects only)" (SL wiki, llSetStatus).
        // Core hands the root's actor SceneObjectGroup.axisSelect bits, set = locked (0x02 X, 0x04 Y, 0x08 Z), the bits
        // ubODE reads (ODEPrim.createAMotor). Each locked axis is one of the prim's own axes; the body gets an infinite
        // inertia about it (SetBodyRotationLocks), so nothing turns the object about that axis, and locking stops its
        // turning, as ubODE's does. ubODE fixes the locked axes in the region where they point when they are locked; here
        // they turn with the object about its free axes. The two agree with all three locked and with one axis free.
        public override void LockAngularMotion(byte axislocks)
        {
            if (_linkRoot != null) { _linkRoot.LockAngularMotion(axislocks); return; }
            _rotationLocks = (byte)(axislocks & 0x0E);
            ApplyRotationLocks();
        }

        private void ApplyRotationLocks()
        {
            if (!_isPhysical || !_body.IsValid)
                return;
            // The prim's axes in the body's own frame: the same axes, but for a cylinder, whose body is turned by the axis
            // correction.
            SQuaternion toBody = SQuaternion.Multiply(SQuaternion.Conjugate(BodyOrientationOf(_orientation)), ToS(_orientation));
            bool lockX = false, lockY = false, lockZ = false;
            for (int axis = 0; axis < 3; axis++)
            {
                if ((_rotationLocks & (0x02 << axis)) == 0)
                    continue;
                SVector3 v = SVector3.Transform(axis == 0 ? SVector3.UnitX : axis == 1 ? SVector3.UnitY : SVector3.UnitZ, toBody);
                float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
                if (ax >= ay && ax >= az) lockX = true;
                else if (ay >= az) lockY = true;
                else lockZ = true;
            }
            _backend.SetBodyRotationLocks(_body, lockX, lockY, lockZ);
        }

        // Forces: wired to the backend's accumulate-until-next-Step Apply* (Jolt AddForce/AddTorque
        // == Bullet ApplyCentralForce/ApplyTorque; both auto-activate a sleeping body). BulletSim treats
        // a NON-push AddForce as force-per-second and divides by the frame dt before applying - mirror
        // that so llApplyImpulse/llPushObject land with the same magnitude on both engines. The force acts for the
        // next backend step only: with [Jolt] PhysicsStepRate on, LastTimeStep is that step's length and a push is
        // scaled by heartbeat / step, so both impulses are the same as with one step per heartbeat.
        public override void AddForce(Vector3 force, bool pushforce)
        {
            if (_linkRoot != null) { _linkRoot.AddForce(force, pushforce); return; }
            if (!_body.IsValid || !_isPhysical || _selected || !force.IsFinite())
                return;
            Vector3 f = pushforce ? force * _module.PushForceScale : force / _module.LastTimeStep;
            _backend.ApplyForce(_body, ToS(f));   // wakes a sleeping body
            _pushedSinceVehicleStep = true;
        }

        // A non-push angular force is llApplyRotationalImpulse's impulse (SceneObjectGroup.ApplyAngularImpulse): it acts
        // as a torque for the next backend step, divided by that step, so the angular impulse delivered is the one given
        // at every step rate, as ubODE delivers it (ODEPrim.changeAddAngularImpulse divides by its step). A push (the
        // viewer's grab spin) is applied as it comes, as before.
        public override void AddAngularForce(Vector3 force, bool pushforce)
        {
            if (_linkRoot != null) { _linkRoot.AddAngularForce(force, pushforce); return; }
            if (!_body.IsValid || !_isPhysical || _selected || !force.IsFinite())
                return;
            if (pushforce)
                _backend.ApplyTorque(_body, ToS(force));
            else
                _backend.ApplyTorque(_body, LimitTorque(ToS(force) / _module.LastTimeStep, _module.LastTimeStep));
        }
        public override void AvatarJump(float forceZ) { }
        public override void SetMomentum(Vector3 momentum) { }

        public override void SetVolumeDetect(int param) { }   // VolumeDetect / phantom-events: not implemented

        // Collision-event subscription.
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

        // Vehicles: forward the LSL wire params into the backend-agnostic controller. OpenSim's
        // SOP hands us raw ints; the controller routes them as Halcyon did and holds each value to the
        // region's VehicleSettings limits. Setting a
        // type registers with the scene's per-frame drive + applies the vehicle body params; setting
        // TYPE_NONE unwinds both and destroys the controller.
        public override int VehicleType
        {
            get => _vehicle == null ? 0 : (int)_vehicle.Type;
            set
            {
                EnsureVehicle();
                _vehicle.ProcessTypeChange((VehicleCode)value);
                if (_vehicle.IsActive)
                {
                    _module.RegisterVehicle(this);
                    ApplyVehicleBodyParams();
                    // Assert buoyancy on restart: the assertion just above can be lost on the load
                    // path (body created GravityFactor=1 with deferred activation drained on the step thread
                    // AFTER this set) - so re-assert it on the step-thread steps of the next
                    // ReassertVehicleSeconds, where it sticks.
                    _reassertVehicleTime = ReassertVehicleSeconds;
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
            _vehicle.ProcessFloatVehicleParam((VehicleCode)param, value);
            WakeVehicle();
        }

        public override void VehicleVectorParam(int param, Vector3 value)
        {
            EnsureVehicle();
            _vehicle.ProcessVectorVehicleParam((VehicleCode)param, value);
            WakeVehicle();
        }

        public override void VehicleRotationParam(int param, Quaternion rotation)
        {
            EnsureVehicle();
            _vehicle.ProcessRotationVehicleParam((VehicleCode)param, rotation);
            WakeVehicle();
        }

        public override void VehicleFlags(int param, bool remove)
        {
            EnsureVehicle();
            _vehicle.ProcessVehicleFlags(param, remove);
            WakeVehicle();
        }

        private void EnsureVehicle()
        {
            if (_vehicle == null)
            {
                _vehicleBody = new JoltVehicleBody(_module, _backend, this);
                _vehicle = new VehicleController(_vehicleBody);
                _vehicle.GroundGravityFactor = _module.VehicleGroundGravityFactor;
                _vehicle.Settings = _module.VehicleSettings;
                Func<DateTime> clock = _module.ControllerClock;
                if (clock != null)
                    _vehicle.Clock = clock;
            }
        }

        // Per-frame drive, called by JoltScene.Simulate BEFORE the physics step (the Jolt
        // equivalent of BulletSim's BeforeStep event): snapshot the live body, run the controller,
        // which pushes velocity changes/forces/torques back through the backend for this step.
        internal void StepVehicle(float timeStep)
        {
            if (_vehicle == null || !_vehicle.IsActive || !_isPhysical || !_body.IsValid || _selected)
                return;
            // Assert buoyancy on restart: re-assert the vehicle body params on the step-thread steps
            // of the first ReassertVehicleSeconds after (re)activation, so the gravity-cancellation that the load-path restore
            // set (but that the deferred body activation clobbered back to GravityFactor=1) actually takes -
            // otherwise a restored boat steps under full engine gravity and sinks despite vehicle=True.
            if (_reassertVehicleTime > 0f)
            {
                _reassertVehicleTime -= timeStep;
                ApplyVehicleBodyParams();
                // ★ ZERO the accumulated velocity when buoyancy is (re)asserted. A boat reloaded into the
                // region is born ACTIVE with gravity and can FREE-FALL for the whole load window (incl. the
                // multi-second reload stall) before the controller first steps here - reaching ~60 m/s. Buoyancy
                // only cancels gravity; it does NOT remove that already-accumulated velocity, so without this the
                // boat keeps plunging to the seabed despite vehicle=True. Zeroing (before BeginFrame/hover below)
                // arrests the fall; hover then lifts it from rest to the water surface. A live boat re-activated
                // at runtime is at rest anyway, so zeroing is a no-op for it.
                // Not once a script has set the linear motor: the vehicle is then being driven, and zeroing would
                // throw away the motor's first frames, a span that is longer the slower the step rate.
                if (!_vehicle.LinearMotorSet)
                {
                    _backend.SetBodyLinearVelocity(_body, SVector3.Zero);
                    _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
                    _velocity = Vector3.Zero;
                    _rotationalVelocity = Vector3.Zero;
                }
            }
            if (!_vehicleBody.BeginFrame())
                return;
            // The vehicle's ground check (BulletSim's HasSomeCollision): touching anything in the last step. The engine
            // reports no contacts for a sleeping body, which has not moved since it last touched what it rests on, so
            // asleep it keeps the state it went to sleep with (otherwise a car parked slightly tilted would stop being
            // idle, its attractor no longer held by the ground, and wake on the next step).
            if (_vehicleBody.IsAwake)
                IsColliding = _backend.BodyHadContact(_body);
            // A parked vehicle sleeps like any other body. The engine may put it to sleep only while nothing in the
            // vehicle would move it (a vehicle drifting slowly toward its hover height must not be stopped: the engine
            // zeroes a body's velocity when it sleeps). Asleep and idle, the controller only lets time pass for it
            // (forces or velocity writes would wake it). A script setting a motor or any vehicle param wakes it
            // (WakeVehicle), as does a collision.
            bool idle = _vehicle.IsIdle;
            // A push or impulse since the last step (AddForce) is not yet in the body's velocity: the rest rule must not
            // hold the vehicle still against it, or put it to sleep with the force still waiting.
            bool pushed = _pushedSinceVehicleStep;
            _pushedSinceVehicleStep = false;
            if (idle != _vehicleMaySleep)
            {
                _backend.SetBodyAllowSleeping(_body, idle);
                _vehicleMaySleep = idle;
            }
            if (!_vehicleBody.IsAwake && idle)
            {
                _vehicleHeldTime = 0f;
                _vehicle.Rest(timeStep);
            }
            else if (idle && !pushed && _vehicle.HoldsAtRest)
            {
                // The rest rule ([Jolt] VehicleRestSpeed): nothing in the vehicle would move it and the equation would
                // not start it moving from where it is, so it is held still. Without this, a vehicle with no contact
                // friction resting a fraction of a degree tilted inside the engine's penetration allowance creeps
                // along the tilt at a few centimetres a second and never sleeps.
                // Held for the engine's time before sleep, it is put to sleep. Held still, there is nothing left for the
                // engine's own sleep test to wait for (in the harness it took about 2.5 s longer), and the time is then
                // the same at every step rate and on every platform.
                _vehicle.Hold(timeStep);
                _vehicleHeldTime += timeStep;
                if (_vehicleHeldTime >= HeldBeforeSleep)
                    _backend.DeactivateBody(_body);
            }
            else
            {
                _vehicleHeldTime = 0f;
                _vehicle.Step(timeStep);
            }
        }

        // How long the rest rule has held the vehicle still (s), and how long it holds it before putting it to sleep:
        // Jolt's own time before sleep (PhysicsSettings.mTimeBeforeSleep, 0.5 s).
        private float _vehicleHeldTime;
        private const float HeldBeforeSleep = 0.5f;

        // Whether the vehicle body is allowed to sleep: only while its controller is idle (StepVehicle).
        private bool _vehicleMaySleep;

        // AddForce since the last StepVehicle (set on a script thread, read on the heartbeat).
        private volatile bool _pushedSinceVehicleStep;

        // A vehicle param, flag or type set by a script: the body wakes so the controller steps with it.
        private void WakeVehicle()
        {
            if (_isPhysical && _body.IsValid)
                _backend.ActivateBody(_body);
        }

        // The BulletSim vehicle body setup (BSDynamics.SetPhysicalParameters), translated:
        // the vehicle controls its own friction/damping (BSParam.VehicleFriction/Restitution/
        // AngularDamping all default 0; Jolt's default 0.05 damping would fight the motor math),
        // decides the vehicle's gravity itself (engine gravity off until its first step sets the vehicle's share).
        // It starts unable to sleep (BulletSim's DISABLE_DEACTIVATION); StepVehicle lets it sleep while nothing in the
        // vehicle would move it.
        // It has continuous collision detection: a car at 18 m/s moves 1.6 m in an 11 Hz step, more than its own height,
        // and two cars closing head on 3.2 m. Without it the engine can miss the contact before the step by a few
        // millimetres, end the step with the bodies deeply overlapped and push them apart along the shortest way out,
        // which is up: one car rode over the other and drove on at its motor's speed.
        // Re-applied after every body recreate (reposition/reshape/weld) while the vehicle is active.
        private void ApplyVehicleBodyParams()
        {
            if (_vehicle == null || !_vehicle.IsActive || !_isPhysical || !_body.IsValid)
                return;
            _backend.SetBodyFriction(_body, _vehicle.Settings.ContactFriction);
            _backend.SetBodyRestitution(_body, 0f);
            _backend.SetBodyDamping(_body, 0f, 0f);
            _backend.SetBodyGravityFactor(_body, 0f);
            _backend.SetBodyAllowSleeping(_body, false);
            _backend.SetBodyContinuousCollision(_body, true);
            _vehicleMaySleep = false;
            _backend.ActivateBody(_body);
        }

        private void RestoreVehicleBodyParams()
        {
            if (!_body.IsValid)
                return;
            BodyDesc d = BodyDesc.Default;
            // Back to the prims' own physics material, and a prim's damping.
            _backend.SetBodyFriction(_body, _friction);
            _backend.SetBodyRestitution(_body, _restitution);
            if (_compoundShape.IsValid && _linkChildren != null)
                foreach (JoltPrim c in _linkChildren)
                    _backend.SetBodyPartMaterial(_body, c.LocalID, c._friction, c._restitution);
            _backend.SetBodyDamping(_body, PrimLinearDamping, PrimAngularDamping);
            _backend.SetBodyGravityFactor(_body, ScriptGravityFactor);   // back to the script's buoyancy and gravity multiplier
            _backend.SetBodyAllowSleeping(_body, true);
            _backend.SetBodyContinuousCollision(_body, d.UseCcd);
        }

        // PID / hover / RotLookAt - physical-motion features, not implemented (no-ops).
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
