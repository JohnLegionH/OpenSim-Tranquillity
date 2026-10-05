/*
 * Vehicle dynamics ported from InWorldz Halcyon
 * Original Copyright (c) 2015, InWorldz Halcyon Developers
 * Adapted 2026 by Legion Builds
 *
 * A backend-agnostic vehicle controller. The vehicle math follows the Halcyon dynamics as
 * OpenSim's BulletSim vehicle code expresses them (same computations, same order, same
 * constants); the physics engine is reached only through IVehicleBody, per this table:
 *
 *   ControllingPrim.ForceOrientation            -> _body.Orientation
 *   ControllingPrim.ForceVelocity (get/set)     -> _body.LinearVelocity   (same read-back semantics)
 *   ControllingPrim.ForceRotationalVelocity     -> _body.AngularVelocity  (same read-back semantics)
 *   ControllingPrim.ForcePosition               -> _body.Position
 *   ControllingPrim.TotalMass                   -> _body.Mass
 *   ControllingPrim.Inertia                     -> _body.InertiaDiagonal
 *   ControllingPrim.AddForce(true, f)           -> _body.AddForce(f)      (central force, next step)
 *   ControllingPrim.AddAngularForce(true, t)    -> _body.AddTorque(t)     (torque, next step)
 *   ControllingPrim.ActivateIfPhysical(false)   -> _body.KeepAwake()
 *   ControllingPrim.ComputeGravity(buoy)        -> _body.SetGravityFactor(1 - buoy): the engine applies it
 *                                                  (GravModifier = 1)
 *   BSParam.Gravity                             -> _body.Gravity.Z
 *   BSParam.VehicleGroundGravityFudge           -> GroundGravityFactor (the host's setting; 1 leaves gravity whole)
 *   GetTerrainHeight/GetWaterLevel              -> _body.GetTerrainHeight/_body.GetWaterLevel
 *   m_physicsScene.PE.PushUpdate                -> dropped (Bullet-only activation nudge; the host
 *                                                  disables sleeping on an active vehicle body)
 *   VDetailLog                                  -> dropped (logging only)
 *   BSActor/scene-event plumbing                -> the HOST steps an active controller before each
 *                                                  physics step and applies/restores body params
 *
 * Simulated: linear and angular motors with their timescales and decay, linear and angular
 * friction, hover, buoyancy, the vertical attractor, angular and linear deflection, sled
 * movement and banking-to-yaw, plus the shared frame infrastructure (timestep smoothing, velocity
 * anti-jitter, motor reset, stall detection, ground-penetration fix, the vehicle's gravity share, torque
 * accumulator). Not simulated: mouselook steering and wind.
 *
 * The linear and angular motors and friction follow Second Life's documented model rather than the
 * Halcyon formulas: see VehicleMotorSolver and SimulateLinearMotorAndFriction.
 *
 * One evaluation-order note: ApplyGravity's ground fudge test is written here as
 * `IsGroundVehicle && _body.HasCollision` (BulletSim: `HasSomeCollision && IsGroundVehicle`) so
 * the engine query short-circuits away for non-ground vehicles; the result is identical.
 *
 * THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 */

using System;
using OpenMetaverse;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Tests")]

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// Halcyon-derived vehicle dynamics engine, backend-agnostic.
    /// The host owns one instance per vehicle body and calls Step(dt) every frame BEFORE the
    /// physics step while the vehicle is active and physical.
    /// </summary>
    public sealed class VehicleController
    {
        // The neutral physics seam this controller reads/writes through
        private readonly IVehicleBody _body;

        // Vehicle properties and runtime state
        private VehicleProperties _props;

        // Cached mass
        private float m_vehicleMass;

        // The clock the motor reset and the spike checks read: the time since the last step or motor set.
        // The wall clock by default; a host that steps faster or slower than real time (a test harness)
        // supplies a simulated one.
        public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        // Frame counter for periodic operations
        private uint m_frameNum;

        // False until the first step after a type change: the timestep smoothing then starts from that step's length.
        private bool _timestepPrimed;

        // The share of gravity a ground vehicle (car or sled) gets while it touches something: BulletSim's
        // VehicleGroundGravityFudge, 0.2 there. 1 leaves gravity whole. Set by the host from its configuration.
        public float GroundGravityFactor { get; set; } = 1f;

        // =====================================================================
        // Ephemeral per-frame computed values (not persisted)
        // =====================================================================
        private Quaternion _vframe;
        private Quaternion _rotation;
        private Vector3 _worldAngularVel;
        private Vector3 _worldLinearVel;
        private Vector3 _localAngularVel;
        private Vector3 _localLinearVel;

        // The body's velocities as read at the start of the step, before the anti-jitter filter.
        private Vector3 _rawWorldAngularVel;
        private Vector3 _rawWorldLinearVel;
        private Vector3 _rawLocalLinearVel;

        // =====================================================================
        // Stall detection state (per-frame, not persisted)
        // =====================================================================
        private bool _linearMotorStalled;
        private bool _linearMotorStallChecked;

        // =====================================================================
        // Torque accumulator (Halcyon pattern: accumulate per-frame, apply once)
        // =====================================================================
        private Vector3 _accumTorqueVelChange;
        private Vector3 _accumTorqueImpulse;

        // The angular motor's and angular friction's change this step. Applied whole in TorqueFini: the near-zero
        // clean-up there drops a fixed amount per step, which would stop the motor and friction short of their
        // equation by more the shorter the step.
        private Vector3 _motorTorqueVelChange;

        // =====================================================================
        // Constructor
        // =====================================================================
        public VehicleController(IVehicleBody body)
        {
            _body = body;
            _props = new VehicleProperties();
            SetVehicleDefaults(VehicleType.None);
        }

        // =====================================================================
        // Active state. (IsPhysicallyActive is not checked here: the HOST only steps the
        // controller while the body is physical.)
        // =====================================================================
        public bool IsActive
        {
            get { return (_props.Type != VehicleType.None); }
        }

        /// <summary>True once a script has set the linear motor, until the motors are reset.</summary>
        public bool LinearMotorSet => !float.IsPositiveInfinity(_props.Dynamics.LinearDecayIndex);

        public bool IsGroundVehicle
        {
            get { return (_props.Type == VehicleType.Car || _props.Type == VehicleType.Sled); }
        }

        /// <summary>Read a current float vehicle param (preset default + any llSetVehicleFloatParam override).
        /// Introspection for the host / tests - e.g. asserting the boat preset's buoyancy.</summary>
        public float GetFloatParam(VehFloatParam key) => _props.GetFloat(key, 0f);

        /// <summary>Read a current vector vehicle param (preset default + any llSetVehicleVectorParam override).
        /// Introspection for the host / tests - e.g. asserting the car preset's friction/motor timescales.</summary>
        public Vector3 GetVecParam(VehVectorParam key) => _props.GetVec(key);

        #region Vehicle Parameter Setting — routes from LSL Vehicle wire codes to internal enums

        // =================================================================
        // Process vehicle type change from LSL
        // =================================================================
        public void ProcessTypeChange(Vehicle pType)
        {
            VehicleType newType;
            switch (pType)
            {
                case Vehicle.TYPE_NONE:     newType = VehicleType.None; break;
                case Vehicle.TYPE_SLED:     newType = VehicleType.Sled; break;
                case Vehicle.TYPE_CAR:      newType = VehicleType.Car; break;
                case Vehicle.TYPE_BOAT:     newType = VehicleType.Boat; break;
                case Vehicle.TYPE_AIRPLANE: newType = VehicleType.Airplane; break;
                case Vehicle.TYPE_BALLOON:  newType = VehicleType.Balloon; break;
                default:                    newType = VehicleType.None; break;
            }

            _props.Type = newType;
            SetVehicleDefaults(newType);
            _props.Dynamics.Reset();
            // No motor until a script sets one.
            _props.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            _props.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            _timestepPrimed = false;
            _props.Dynamics.LastAccessTOD = Clock();

            // (No scene-event registration or Refresh here; the host reacts to IsActive instead:
            // per-frame Step drive + body physical params.)
        }

        // =================================================================
        // Process float param from LSL
        // =================================================================
        public void ProcessFloatVehicleParam(Vehicle pParam, float pValue)
        {
            // ClampF passes NaN through (Math.Max/Min propagate it), so a non-finite script value
            // would be stored and drive the motors. Ignore it; the stored param is unchanged.
            if (!float.IsFinite(pValue))
                return;
            switch (pParam)
            {
                case Vehicle.ANGULAR_DEFLECTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.ANGULAR_DEFLECTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale] = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    // Scalar set → apply to all 3 axes
                    pValue = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    pValue = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.BANKING_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency] = ClampF(pValue, -1f, 1f);
                    break;
                case Vehicle.BANKING_MIX:
                    _props.ParamsFloat[VehFloatParam.BankingMix] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.BANKING_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.BankingTimescale] = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    break;
                case Vehicle.BUOYANCY:
                    _props.ParamsFloat[VehFloatParam.Buoyancy] = ClampF(pValue, -1f, 1f);
                    break;
                case Vehicle.HOVER_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.HOVER_HEIGHT:
                    _props.ParamsFloat[VehFloatParam.HoverHeight] = ClampF(pValue, VehicleLimits.MinRegionHeight, VehicleLimits.MaxRegionHeight);
                    break;
                case Vehicle.HOVER_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.HoverTimescale] = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxHoverTimescale);
                    break;
                case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale] = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    pValue = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    pValue = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale] = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxAttractTimescale);
                    break;

                // These are vector properties but LSL allows setting them as a single float
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    pValue = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    pValue = ClampF(pValue, -VehicleLimits.MaxAngularVelocity, VehicleLimits.MaxAngularVelocity);
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = new Vector3(pValue, pValue, pValue);
                    MoveAngular(_props.ParamsVec[VehVectorParam.AngularMotorDirection]);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue = ClampF(pValue, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue = ClampF(pValue, -VehicleLimits.MaxLinearVelocity, VehicleLimits.MaxLinearVelocity);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = new Vector3(pValue, pValue, pValue);
                    MoveLinear(_props.ParamsVec[VehVectorParam.LinearMotorDirection]);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue = ClampF(pValue, -VehicleLimits.MaxLinearOffset, VehicleLimits.MaxLinearOffset);
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset] = new Vector3(pValue, pValue, pValue);
                    break;
            }
        }

        // =================================================================
        // Process vector param from LSL
        // =================================================================
        public void ProcessVectorVehicleParam(Vehicle pParam, Vector3 pValue)
        {
            if (!float.IsFinite(pValue.X) || !float.IsFinite(pValue.Y) || !float.IsFinite(pValue.Z))
                return;   // non-finite: see ProcessFloatVehicleParam
            switch (pParam)
            {
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    pValue.X = ClampF(pValue.X, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale] = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    pValue.X = ClampF(pValue.X, -VehicleLimits.MaxAngularVelocity, VehicleLimits.MaxAngularVelocity);
                    pValue.Y = ClampF(pValue.Y, -VehicleLimits.MaxAngularVelocity, VehicleLimits.MaxAngularVelocity);
                    pValue.Z = ClampF(pValue.Z, -VehicleLimits.MaxAngularVelocity, VehicleLimits.MaxAngularVelocity);
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = pValue;
                    MoveAngular(pValue);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue.X = ClampF(pValue.X, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, VehicleLimits.MinPhysicsTimestep, VehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue.X = ClampF(pValue.X, -VehicleLimits.MaxLinearVelocity, VehicleLimits.MaxLinearVelocity);
                    pValue.Y = ClampF(pValue.Y, -VehicleLimits.MaxLinearVelocity, VehicleLimits.MaxLinearVelocity);
                    pValue.Z = ClampF(pValue.Z, -VehicleLimits.MaxLinearVelocity, VehicleLimits.MaxLinearVelocity);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = pValue;
                    MoveLinear(pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue.X = ClampF(pValue.X, -VehicleLimits.MaxLinearOffset, VehicleLimits.MaxLinearOffset);
                    pValue.Y = ClampF(pValue.Y, -VehicleLimits.MaxLinearOffset, VehicleLimits.MaxLinearOffset);
                    pValue.Z = ClampF(pValue.Z, -VehicleLimits.MaxLinearOffset, VehicleLimits.MaxLinearOffset);
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset] = pValue;
                    break;
                case Vehicle.BLOCK_EXIT:
                    // Not implemented in Halcyon — ignore
                    break;
            }
        }

        // =================================================================
        // Process rotation param from LSL
        // =================================================================
        public void ProcessRotationVehicleParam(Vehicle pParam, Quaternion pValue)
        {
            switch (pParam)
            {
                case Vehicle.REFERENCE_FRAME:
                    pValue = Quaternion.Normalize(pValue);
                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = pValue;
                    break;
                case Vehicle.ROLL_FRAME:
                    // Not used in Halcyon — ignore
                    break;
            }
        }

        // =================================================================
        // Process vehicle flags from LSL
        // =================================================================
        public void ProcessVehicleFlags(int pParam, bool remove)
        {
            // Map OpenSim VehicleFlag bits to our internal flags.
            // Standard flags share the same bit positions (1-32768).
            ExtendedVehicleFlags flags = (ExtendedVehicleFlags)pParam;

            if (remove)
                _props.Flags &= ~flags;
            else
                _props.Flags |= flags;
        }

        #endregion // Vehicle Parameter Setting

        #region Step — Main Simulation Entry Point

        /// <summary>
        /// Called every physics frame by the host, BEFORE the engine step.
        /// This is the main simulation entry point, equivalent to Halcyon VehicleDynamics.Simulate().
        /// </summary>
        public void Step(float pTimestep)
        {
            if (!IsActive) return;

            m_frameNum++;
            m_vehicleMass = _body.Mass;

            // -------------------------------------------------------
            // Timestep smoothing (Halcyon pattern). It starts from the first step's own length: started from the
            // reset value (MinPhysicsTimestep) it took a vehicle's first second or so to catch up with a longer
            // step, and the motor under-drove meanwhile, the more the longer the step.
            if (!_timestepPrimed)
            {
                _props.Dynamics.Timestep = pTimestep;
                _timestepPrimed = true;
            }
            float timeStep = _props.Dynamics.Timestep * 0.8f + pTimestep * 0.2f;

            // -------------------------------------------------------
            // Read current state from physics engine
            _vframe = _props.GetRot(VehRotationParam.ReferenceFrame);
            _rotation = _body.Orientation * _vframe;

            // Velocity anti-jitter: per-axis, when velocities are opposing, average to eliminate spike
            Vector3 physAngVel = _body.AngularVelocity;
            _worldAngularVel.X = (_worldAngularVel.X * physAngVel.X >= 0) ? physAngVel.X : _worldAngularVel.X * 0.5f + physAngVel.X * 0.5f;
            _worldAngularVel.Y = (_worldAngularVel.Y * physAngVel.Y >= 0) ? physAngVel.Y : _worldAngularVel.Y * 0.5f + physAngVel.Y * 0.5f;
            _worldAngularVel.Z = (_worldAngularVel.Z * physAngVel.Z >= 0) ? physAngVel.Z : _worldAngularVel.Z * 0.5f + physAngVel.Z * 0.5f;

            Vector3 physLinVel = _body.LinearVelocity;
            _worldLinearVel.X = (_worldLinearVel.X * physLinVel.X >= 0) ? physLinVel.X : _worldLinearVel.X * 0.5f + physLinVel.X * 0.5f;
            _worldLinearVel.Y = (_worldLinearVel.Y * physLinVel.Y >= 0) ? physLinVel.Y : _worldLinearVel.Y * 0.5f + physLinVel.Y * 0.5f;
            _worldLinearVel.Z = (_worldLinearVel.Z * physLinVel.Z >= 0) ? physLinVel.Z : _worldLinearVel.Z * 0.5f + physLinVel.Z * 0.5f;

            // Convert to local coordinates
            Quaternion invRotation = Quaternion.Inverse(_rotation);
            _localAngularVel = _worldAngularVel * invRotation;
            _localLinearVel = _worldLinearVel * invRotation;
            _rawWorldAngularVel = physAngVel;
            _rawWorldLinearVel = physLinVel;
            _rawLocalLinearVel = physLinVel * invRotation;

            // -------------------------------------------------------
            // Reset motors if stale (more than 1 second since last access)
            CheckResetMotors(timeStep);

            // Initialize torque accumulator and stall detection
            TorqueInit();
            ClearLinearMotorStalled();

            _props.Dynamics.Timestep = timeStep;

            // -------------------------------------------------------
            // Ground penetration fix
            float groundHeight = _body.GetTerrainHeight(_body.Position);
            if (_body.Position.Z - groundHeight < VehicleLimits.MaxGroundPenetration)
            {
                Vector3 zforce = Vector3.Zero;
                zforce.Z = 1.0f + Math.Abs(_localLinearVel.Z * 3.0f);
                ApplyLinearVelocityChange(zforce);
            }

            // -------------------------------------------------------
            // Deflection + sled run in the order Angular -> Linear -> Sled, BEFORE hover.
            // Angular deflection — swings the nose toward the velocity direction (weathervane).
            if (VehicleLimits.DoAngularDeflection)
            {
                SimulateAngularDeflection(timeStep);
            }

            // Linear deflection — changing velocity toward the forward axis (the "tracking" bite).
            if (VehicleLimits.DoLinearDeflection)
            {
                SimulateLinearDeflection(timeStep);
            }

            // Sled movement — gravity-assisted slope force, gated on Type == Sled (NEVER a boat). Included
            // for completeness; inert for every non-sled vehicle.
            if (_props.Type == VehicleType.Sled)
            {
                SimulateSledMovement(timeStep);
            }

            // -------------------------------------------------------
            // Hover — maintain target height above terrain/water/global
            SimulateHover(pTimestep);

            // -------------------------------------------------------
            // Vertical attractor and banking
            Vector3 attractionForces = Vector3.Zero;
            if (VehicleLimits.DoVerticalAttractor)
            {
                float angle;
                bool inverted;

                SimulateVerticalAttractor(timeStep, m_frameNum, out attractionForces, out angle, out inverted);
                // Banking runs AFTER the attractor (uses its angle/inverted) and BEFORE the motors: it sets
                // Dynamics.BankingDirection, which the banking-turn block inside SimulateMotors consumes.
                SimulateBankingToYaw(timeStep, angle, inverted);
            }

            // -------------------------------------------------------
            // Motors and friction, linear and angular, then the banking turn motor. They are stepped over the
            // step the engine is about to take (pTimestep), not the smoothed one: each is the exact solution
            // over that step.
            SimulateLinearMotorAndFriction(pTimestep, VehicleLimits.DoMotors, VehicleLimits.DoLinearFriction);
            float angularz = SimulateAngularMotorAndFriction(pTimestep, VehicleLimits.DoMotors, VehicleLimits.DoAngularFriction);
            SimulateBankingTurn(timeStep, VehicleLimits.DoMotors, angularz);

            // Apply gravity manually (same pattern as BSDynamics)
            ApplyGravity();

            // Apply the accumulated torque
            TorqueFini();

            // Save state for next frame
            _props.Dynamics.LastPosition = _body.Position;
            _props.Dynamics.Timestep = timeStep;
            _props.Dynamics.LocalLinearVelocity = _localLinearVel;
            _props.Dynamics.LocalAngularVelocity = _localAngularVel;
        }

        /// <summary>
        /// True when nothing in the vehicle would move a vehicle at rest: on every axis the motor's pull toward its
        /// direction, g(s) * |M|, is below <see cref="IdleMotorSpeed"/> (a motor never set, faded away, or set to
        /// zero, which only brakes), and hover is off or the vehicle is at its hover height. Friction, the attractor
        /// and deflection only slow or turn a vehicle that is already moving, and gravity is held by what it rests
        /// on. A host may then let the body sleep and call <see cref="Rest"/> instead of <see cref="Step"/> while it
        /// sleeps.
        /// </summary>
        public bool IsIdle
            => MotorIdle(_props.Dynamics.LinearDirection, _props.GetVec(VehVectorParam.LinearMotorDecayTimescale), _props.Dynamics.LinearDecayIndex)
            && MotorIdle(_props.Dynamics.AngularDirection, _props.GetVec(VehVectorParam.AngularMotorDecayTimescale), _props.Dynamics.AngularDecayIndex)
            && HoverIdle();

        private bool HoverIdle()
        {
            if (!HoverTarget(out float targetZ, out bool upOnly))
                return true;
            float error = targetZ - _body.Position.Z;
            return Math.Abs(error) < HoverIdleHeight || (upOnly && error <= 0f);
        }

        // A vehicle this close to its hover height (m) has nothing left for hover to do.
        internal const float HoverIdleHeight = 0.01f;

        // A motor pull below this (m/s, or rad/s for the angular motor) cannot move a vehicle at rest; it is under
        // the engine's own sleep threshold (Jolt's point-velocity sleep threshold is 0.03 m/s).
        internal const float IdleMotorSpeed = 0.01f;

        private static bool MotorIdle(Vector3 direction, Vector3 decayTs, float age)
            => VehicleMotorSolver.Grip(age, Math.Max(decayTs.X, VehicleLimits.MinPhysicsTimestep)) * Math.Abs(direction.X) < IdleMotorSpeed
            && VehicleMotorSolver.Grip(age, Math.Max(decayTs.Y, VehicleLimits.MinPhysicsTimestep)) * Math.Abs(direction.Y) < IdleMotorSpeed
            && VehicleMotorSolver.Grip(age, Math.Max(decayTs.Z, VehicleLimits.MinPhysicsTimestep)) * Math.Abs(direction.Z) < IdleMotorSpeed;

        /// <summary>
        /// A step in which the body is asleep and the vehicle idle (<see cref="IsIdle"/>): time passes for the motors'
        /// decay, and the body is left alone. At rest every term of the step is zero (the motors pull toward nothing,
        /// friction has no velocity to slow, gravity is held by the ground), so this is the same as stepping it.
        /// </summary>
        public void Rest(float pTimestep)
        {
            if (!IsActive) return;
            _props.Dynamics.LastAccessTOD = Clock();
            _props.Dynamics.LinearDecayIndex += pTimestep;
            _props.Dynamics.AngularDecayIndex += pTimestep;
            _hoverCarry = null;
        }

        #endregion // Step

        #region Motor Entry Points (called when LSL sets motor direction)

        /// <summary>
        /// Called when VehicleLinearMotorDirection is set.
        /// Faithfully ported from Halcyon VehicleMotor.MoveLinear().
        /// </summary>
        private void MoveLinear(Vector3 direction)
        {
            if (_props.Type == VehicleType.None) return;

            _props.Dynamics.LastAccessTOD = Clock();

            // The motor's grip restarts at full on every set and decays from here (s = 0).
            _props.Dynamics.LinearDecayIndex = 0.0f;

            _props.Dynamics.TargetLinearDelta = direction - _props.Dynamics.LinearDirection;
            _props.Dynamics.LinearDirection = direction;
            _body.KeepAwake();
        }

        /// <summary>
        /// Called when VehicleAngularMotorDirection is set.
        /// Faithfully ported from Halcyon VehicleMotor.MoveAngular().
        /// </summary>
        private void MoveAngular(Vector3 direction)
        {
            if (_props.Type == VehicleType.None) return;

            _props.Dynamics.LastAccessTOD = Clock();

            // The motor's grip restarts at full on every set and decays from here (s = 0).
            _props.Dynamics.AngularDecayIndex = 0.0f;

            _props.Dynamics.TargetAngularDelta = direction - _props.Dynamics.AngularDirection;
            _props.Dynamics.AngularDirection = direction;
            _body.KeepAwake();
        }

        /// <summary>
        /// Check if motors need reset due to stale timestamp.
        /// Returns actual elapsed time.
        /// A motor's grip decays from its set in real time, but the decay clock here advances only while the
        /// controller is stepped; after a gap of over a second (physics off, or just rezzed) the motors are
        /// released rather than resumed at the grip they had when stepping stopped.
        /// </summary>
        private float CheckResetMotors(float timeStep)
        {
            DateTime now = Clock();
            float elapsed = (float)(now - _props.Dynamics.LastAccessTOD).TotalSeconds;
            _props.Dynamics.LastAccessTOD = now;

            if (elapsed > 1.0f)
            {
                // Reset motors — vehicle was idle or just rezzed
                ResetDynamics();
                _body.KeepAwake();
                return timeStep;
            }

            return elapsed;
        }

        /// <summary>
        /// Reset all dynamics state. Ported from Halcyon VehicleMotor.ResetDynamics().
        /// </summary>
        private void ResetDynamics()
        {
            _props.Dynamics.LastAccessTOD = Clock();
            _props.Dynamics.LastPosition = _body.Position;

            _props.Dynamics.LocalLinearVelocity = _localLinearVel;
            _props.Dynamics.LocalAngularVelocity = _localAngularVel;

            _props.Dynamics.LastVerticalAngle = 0.0f;
            _props.Dynamics.VerticalForceAdjust = 1.0f;

            // Motors off: no set to decay from
            _props.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            _props.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            _props.Dynamics.LinearTargetVelocity = Vector3.Zero;
            _props.Dynamics.AngularTargetVelocity = Vector3.Zero;

            // Bank/turn motor off
            _props.Dynamics.BankingDirection = 0;
            _props.Dynamics.BankingTargetVelocity = 0;

            // Deltas zero
            _props.Dynamics.TargetLinearDelta = Vector3.Zero;
            _props.Dynamics.TargetAngularDelta = Vector3.Zero;
        }

        #endregion // Motor Entry Points

        #region Stall Detection

        private void ClearLinearMotorStalled()
        {
            _linearMotorStalled = false;
            _linearMotorStallChecked = false;
        }

        /// <summary>
        /// Detect if the vehicle is jammed against an obstacle.
        /// Ported from Halcyon VehicleMotor.IsLinearMotorStalled().
        /// </summary>
        private bool IsLinearMotorStalled()
        {
            Vector3 currpos = _body.Position;
            Vector3 lastpos = _props.Dynamics.LastPosition;
            Vector3 posdelta = currpos - lastpos;
            float currspeed = Vector3.Mag(_worldLinearVel);
            float timeStep = _props.Dynamics.Timestep;

            if (!_linearMotorStallChecked)
            {
                // Smooth short term position delta
                _props.Dynamics.ShortTermPositionDelta = _props.Dynamics.ShortTermPositionDelta * 0.8f + posdelta * 0.2f;
                _linearMotorStallChecked = true;

                // If either motor is starting fresh, assume not stalled
                if (_props.Dynamics.LinearDecayIndex > VehicleLimits.ThresholdLinearMotorEngaged &&
                    _props.Dynamics.AngularDecayIndex > VehicleLimits.ThresholdAngularMotorEngaged)
                {
                    float stposdelta = Vector3.Mag(_props.Dynamics.ShortTermPositionDelta);
                    float stspeed = stposdelta / timeStep;

                    if (stspeed < currspeed * 0.5f)
                    {
                        if (currspeed >= VehicleLimits.ThresholdLinearMotorUnstuck)
                        {
                            _linearMotorStalled = true;
                        }
                    }
                }
            }

            return _linearMotorStalled;
        }

        #endregion

        #region Gravity

        /// <summary>
        /// The share of the world's gravity the vehicle feels: 1 - buoyancy, times the ground factor for a ground
        /// vehicle touching something. With hover on and HOVER_UP_ONLY, buoyancy vanishes above the hover height
        /// (Linden_Vehicle_Tutorial, "Buoyancy": "the buoyancy effect vanishes when the vehicle is above its hover
        /// height").
        /// </summary>
        private float GravityShare()
        {
            float buoyancy = _props.GetFloat(VehFloatParam.Buoyancy, 0f);
            if (HoverTarget(out float targetZ, out bool upOnly) && upOnly && _body.Position.Z > targetZ)
                buoyancy = 0f;
            float share = 1f - buoyancy;   // ComputeGravity(buoyancy), GravModifier = 1

            // Reduce downward force if vehicle is sitting on ground
            if (IsGroundVehicle && _body.HasCollision)
                share *= GroundGravityFactor;
            return share;
        }

        /// <summary>
        /// The vehicle's gravity: the engine applies its share over the step, spread over the step as for any body.
        /// The linear motor and friction step takes it into its equation and leaves the engine this g h. (Applied
        /// here as a force it was the same, but a force restarts the engine's sleep timer on every step, so a parked
        /// vehicle could never sleep.)
        /// </summary>
        private void ApplyGravity()
        {
            _body.SetGravityFactor(GravityShare());
        }

        #endregion

        #region Torque Accumulator (Halcyon pattern)

        private void TorqueInit()
        {
            _accumTorqueVelChange = Vector3.Zero;
            _accumTorqueImpulse = Vector3.Zero;
            _motorTorqueVelChange = Vector3.Zero;
        }

        /// <summary>
        /// Add torque as a velocity change (mass-independent, instant).
        /// Equivalent to Halcyon AddTorque with ForceMode.VelocityChange.
        /// </summary>
        internal void AddTorqueVelocityChange(Vector3 torque)
        {
            _accumTorqueVelChange += torque;
        }

        /// <summary>
        /// Add torque as an impulse (mass-dependent).
        /// Equivalent to Halcyon AddTorque with ForceMode.Impulse.
        /// </summary>
        internal void AddTorqueImpulse(Vector3 torque)
        {
            _accumTorqueImpulse += torque;
        }

        /// <summary>
        /// Apply all accumulated torques at end of frame.
        /// </summary>
        private void TorqueFini()
        {
            // Clean up near-zero values
            if (Math.Abs(_accumTorqueVelChange.X) < VehicleLimits.MinPhysicsForce) _accumTorqueVelChange.X = 0f;
            if (Math.Abs(_accumTorqueVelChange.Y) < VehicleLimits.MinPhysicsForce) _accumTorqueVelChange.Y = 0f;
            if (Math.Abs(_accumTorqueVelChange.Z) < VehicleLimits.MinPhysicsForce) _accumTorqueVelChange.Z = 0f;

            if (Math.Abs(_accumTorqueImpulse.X) < VehicleLimits.MinPhysicsForce) _accumTorqueImpulse.X = 0f;
            if (Math.Abs(_accumTorqueImpulse.Y) < VehicleLimits.MinPhysicsForce) _accumTorqueImpulse.Y = 0f;
            if (Math.Abs(_accumTorqueImpulse.Z) < VehicleLimits.MinPhysicsForce) _accumTorqueImpulse.Z = 0f;

            // Apply velocity change torques (direct angular velocity modification)
            Vector3 velChange = _accumTorqueVelChange + _motorTorqueVelChange;
            if (velChange != Vector3.Zero)
            {
                _body.AngularVelocity = _body.AngularVelocity + velChange;
            }

            // Apply impulse torques (mass-scaled)
            if (_accumTorqueImpulse != Vector3.Zero)
            {
                _body.AddTorque(_accumTorqueImpulse);
            }

            // (No activation nudge here; the host keeps an active vehicle body from sleeping.)
        }

        #endregion // Torque Accumulator

        #region Adapter Methods — Physics Engine Interface

        /// <summary>
        /// Apply an instant linear velocity change (mass-independent).
        /// Equivalent to Halcyon AddForce with ForceMode.VelocityChange.
        /// </summary>
        private void ApplyLinearVelocityChange(Vector3 deltaV)
        {
            // A non-zero LinearMotorOffset is applied as a CENTRAL velocity change too - the identical
            // net velocity change (true off-center application is not implemented; every stock preset
            // ships offset = zero).
            _body.LinearVelocity = _body.LinearVelocity + deltaV;
        }

        #endregion // Adapter Methods

        #region Deflection (Angular + Linear) + Sled movement (gated Type==Sled)

        /// <summary>
        /// Rotates vehicle toward direction of movement (weathervane: swings the NOSE toward the velocity,
        /// complementary to linear deflection which rotates the velocity toward the nose).
        /// Follows the InWorldz Halcyon vehicle dynamics.
        /// Seam table: all symbols map 1:1 here - _worldLinearVel/_localLinearVel/_worldAngularVel/_rotation,
        /// the limits, the QuatToEuler/RotBetween/AngleBetween utilities, and AddTorqueVelocityChange (the
        /// torque-as-velocity-change seam) already exist on this controller.
        /// </summary>
        private void SimulateAngularDeflection(float timeStep)
        {
            if (Math.Abs(_worldLinearVel.X) >= VehicleLimits.ThresholdDeflectionSpeed ||
                Math.Abs(_worldLinearVel.Y) >= VehicleLimits.ThresholdDeflectionSpeed ||
                Math.Abs(_worldLinearVel.Z) >= VehicleLimits.ThresholdDeflectionSpeed)
            {
                float timescale = Math.Max(_props.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f), timeStep);

                if (timescale < VehicleLimits.MaxTimescale)
                {
                    float timepct = timeStep / timescale;
                    float efficiency = _props.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
                    float speed = Utils.Clamp(Vector3.Mag(_localLinearVel), 0, VehicleLimits.MaxLegacyLinearVelocity);
                    float speedpct = speed / VehicleLimits.MaxLegacyLinearVelocity;

                    // Compute the rotation between the x axis pointing vector and the linear direction
                    Vector3 ahead = new Vector3(1, 0, 0) * _rotation;
                    Quaternion tween = RotBetween(ahead, Vector3.Normalize(_worldLinearVel));
                    float angle = AngleBetween(tween, Quaternion.Identity);
                    Vector3 vtwix = QuatToEuler(tween);

                    // Cheat: if the local X movement is negative, flip the angle
                    if (_localLinearVel.X < 0)
                    {
                        angle = (float)Math.PI - angle;
                    }

                    // Scale the force
                    vtwix = Vector3.Normalize(vtwix) * speedpct * (float)Math.PI * timepct * efficiency * (float)Math.Log(1.0 + angle);

                    // Compute damping
                    Vector3 remvel = Vector3.Zero;
                    if (angle < VehicleLimits.ThresholdDeflectionAngle)
                    {
                        remvel = _worldAngularVel * timepct * efficiency * (float)(Math.Log(1.0 + Math.PI - angle) / Math.Log(1.0 + Math.PI));
                    }

                    vtwix -= remvel;

                    if (IsLinearMotorStalled())
                    {
                        vtwix = Vector3.Zero;
                    }

                    if (Math.Abs(vtwix.X) >= VehicleLimits.ThresholdAngularMotorDeltaV ||
                        Math.Abs(vtwix.Y) >= VehicleLimits.ThresholdAngularMotorDeltaV ||
                        Math.Abs(vtwix.Z) >= VehicleLimits.ThresholdAngularMotorDeltaV)
                    {
                        AddTorqueVelocityChange(vtwix);
                    }
                }
            }
        }

        /// <summary>
        /// Changes velocity toward forward axis.
        /// Follows the InWorldz Halcyon vehicle dynamics.
        /// Seam table: symbols map 1:1 here - _worldLinearVel, _rotation,
        /// _props, the limits, and ApplyLinearVelocityChange are the same names on this controller, so no
        /// substitution is needed inside the body (ApplyLinearVelocityChange already writes _body.LinearVelocity).
        /// </summary>
        private void SimulateLinearDeflection(float timeStep)
        {
            if (Math.Abs(_worldLinearVel.X) >= VehicleLimits.ThresholdLinearMotorDeltaV ||
                Math.Abs(_worldLinearVel.Y) >= VehicleLimits.ThresholdLinearMotorDeltaV ||
                Math.Abs(_worldLinearVel.Z) >= VehicleLimits.ThresholdLinearMotorDeltaV)
            {
                float timescale = Math.Max(_props.GetFloat(VehFloatParam.LinearDeflectionTimescale, 1000f), timeStep);

                if (timescale < VehicleLimits.MaxTimescale)
                {
                    float timePct = timeStep / timescale;
                    float efficiency = _props.GetFloat(VehFloatParam.LinearDeflectionEfficiency, 0f);

                    // Determine the amount of velocity to shift
                  // SL behavior: linear deflection rotates the velocity vector toward
                    // the forward axis, preserving speed. This is what generates lift —
                    // when pitched up, horizontal velocity is redirected upward along
                    // the forward axis.
                    float speed = Vector3.Mag(_worldLinearVel);
                    if (speed < VehicleLimits.ThresholdDeflectionSpeed) return;

                    Vector3 forwardDir = new Vector3(1, 0, 0) * _rotation;
                    float blend = Math.Min(timePct * efficiency, 1.0f);
                    Vector3 worldvel;

                    if ((_props.Flags & ExtendedVehicleFlags.NoDeflectionUp) != 0)
                    {
                        // No deflection upward: deflection turns only the horizontal part of the velocity toward the
                        // nose's horizontal heading, keeping its size, and leaves the vertical part exactly as it was,
                        // so it never adds speed. (Turning the whole velocity and then dropping the upward part of the
                        // change, as before, added forward speed to a vehicle falling nose-level in every step.)
                        worldvel = HorizontalDeflection(_worldLinearVel, forwardDir, blend);
                    }
                    else
                    {
                        // Blend current direction toward forward direction
                        Vector3 currentDir = Vector3.Normalize(_worldLinearVel);
                        Vector3 newDir = Vector3.Normalize(currentDir * (1.0f - blend) + forwardDir * blend);

                        // New velocity = same speed, redirected direction
                        worldvel = newDir * speed - _worldLinearVel;
                    }

                    if (Math.Abs(worldvel.X) > VehicleLimits.ThresholdLinearMotorDeltaV ||
                        Math.Abs(worldvel.Y) > VehicleLimits.ThresholdLinearMotorDeltaV ||
                        Math.Abs(worldvel.Z) > VehicleLimits.ThresholdLinearMotorDeltaV)
                    {
                        ApplyLinearVelocityChange(worldvel);
                    }
                }
            }
        }

        /// <summary>
        /// Gravity-assisted force on slopes for the SLED vehicle type. Follows the InWorldz Halcyon
        /// vehicle dynamics. Included for completeness
        /// only - it is gated Type==Sled in Step, so it NEVER runs for a boat (or any non-sled). Seam table:
        /// BSParam.Gravity -> _body.Gravity.Z, ApplyLinearForce(f) -> _body.AddForce(f); everything else
        /// (_rotation, the limits, IsLinearMotorStalled, m_vehicleMass) maps 1:1.
        /// </summary>
        private void SimulateSledMovement(float timeStep)
        {
            Vector3 force = Vector3.Zero;

            // Compute the percentage of declination -1 (down) to +1 (up)
            Vector3 probe = new Vector3(1f, 0f, 0f);
            probe *= _rotation;

            // If the nose (z-axis) points downward, add some force along the X-axis
            if (Math.Abs(probe.Z) > VehicleLimits.ThresholdDeflectionAngle)
            {
                force = new Vector3(-_body.Gravity.Z * 3.0f, 0f, 0f);   // seam: BSParam.Gravity -> _body.Gravity.Z

                // The sled has a lower force assist going backwards
                if (probe.Z > 0)
                    force *= -0.1f;

                if (!IsLinearMotorStalled())
                {
                    // Modulate the force based on the amount of declination
                    force = force * timeStep * (float)Math.Sqrt(Math.Abs(probe.Z));

                    if (Math.Abs(force.X) >= VehicleLimits.ThresholdLinearMotorDeltaV ||
                        Math.Abs(force.Y) >= VehicleLimits.ThresholdLinearMotorDeltaV ||
                        Math.Abs(force.Z) >= VehicleLimits.ThresholdLinearMotorDeltaV)
                    {
                        force *= _rotation;
                        force *= m_vehicleMass;
                        _body.AddForce(force);   // seam: ApplyLinearForce(f) -> _body.AddForce(f)
                    }
                }
            }
        }

        /// <summary>
        /// The velocity change that turns the horizontal part of a velocity toward the horizontal heading of the nose
        /// by the given blend, keeping the horizontal speed; the vertical part is untouched. Zero when the velocity
        /// has too little horizontal speed or the nose points (nearly) straight up or down.
        /// </summary>
        internal static Vector3 HorizontalDeflection(Vector3 velocity, Vector3 forward, float blend)
        {
            Vector3 horizontal = new Vector3(velocity.X, velocity.Y, 0f);
            Vector3 heading = new Vector3(forward.X, forward.Y, 0f);
            float speed = horizontal.Length();
            if (speed < VehicleLimits.ThresholdDeflectionSpeed || heading.Length() < MinDeflectionHeading)
                return Vector3.Zero;
            Vector3 mix = horizontal / speed * (1.0f - blend) + Vector3.Normalize(heading) * blend;
            if (mix.Length() < 1e-6f)
                return Vector3.Zero;   // exactly opposed halfway: no direction to turn to this step
            return Vector3.Normalize(mix) * speed - horizontal;
        }

        // A nose whose horizontal heading is shorter than this (pointing within about 6 degrees of straight up or
        // down) gives no horizontal direction to deflect toward.
        private const float MinDeflectionHeading = 0.1f;

        #endregion

        #region Hover

        /// <summary>
        /// Hover, as Second Life documents it (Linden_Vehicle_Tutorial, "Hover"; LlSetVehicleFloatParam): the vehicle
        /// springs toward its hover height over terrain, water or the global height, with HOVER_TIMESCALE the period
        /// to achieve it and HOVER_EFFICIENCY a slider from bouncy (0) to critically damped (1). On the height error
        /// e = z - target:
        ///
        ///   e'' = -e / T^2 - 2 eff e' / T
        ///
        /// stepped exactly (<see cref="VehicleSpring"/>) over the step the engine takes. Gravity, buoyancy, friction
        /// and the motors act alongside it in their own blocks; a vehicle without full buoyancy hovers below its
        /// height, where the spring holds its weight, as the tutorial describes.
        /// HOVER_UP_ONLY: hover does not push down; above its height it does nothing (and buoyancy vanishes there,
        /// see <see cref="GravityShare"/>).
        /// </summary>
        private void SimulateHover(float h)
        {
            if (!HoverTarget(out float targetZ, out bool upOnly))
            {
                LetGoOfHover();
                return;
            }
            float timescale = _props.GetFloat(VehFloatParam.HoverTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.HoverEfficiency, 0f);
            double e0 = _body.Position.Z - targetZ;
            if (upOnly && e0 >= 0)
            {
                LetGoOfHover();
                return;
            }

            // The spring's own vertical velocity: the body's, plus what the last step's move left out of it.
            double read = _rawWorldLinearVel.Z;
            double v0 = read + (_hoverCarry ?? 0.0);
            (double move, double carry) = VehicleSpring.MoveOver(e0, v0, timescale, efficiency, h);
            bool crosses = upOnly && VehicleSpring.Step(e0, v0, timescale, efficiency, h).e > 0;
            if (crosses)
            {
                // Up only: the spring lets go where the vehicle passes its height inside the step, and it coasts on
                // from there at the speed it had, with the gravity that buoyancy no longer cancels above the height
                // (the gravity block applies this step's share, set below the height).
                double at = UpOnlyCrossing(e0, v0, timescale, efficiency, h);
                double through = VehicleSpring.Step(e0, v0, timescale, efficiency, at).v;
                double coast = h - at;
                double extraFall = _body.Gravity.Z * (GravityShareAbove() - GravityShare());
                move = (through * coast + 0.5 * extraFall * coast * coast - e0) / h;
                carry = through + extraFall * coast - move;
            }
            double change = move - read;
            // Up only: the spring itself never pushes down (the fall after a crossing is gravity's, not hover's).
            if (upOnly && !crosses && change < 0)
            {
                change = 0;
                carry = 0;
            }
            _hoverCarry = carry;
            ApplyLinearVelocityChange(new Vector3(0f, 0f, (float)change));
        }

        // Hover hands the body back its true vertical velocity (what the last move left out) when it stops acting.
        private void LetGoOfHover()
        {
            if (_hoverCarry is double carry && carry != 0)
                ApplyLinearVelocityChange(new Vector3(0f, 0f, (float)carry));
            _hoverCarry = null;
        }

        // The gravity share over the hover height with HOVER_UP_ONLY, where buoyancy vanishes.
        private float GravityShareAbove()
        {
            float share = 1f;
            if (IsGroundVehicle && _body.HasCollision)
                share *= GroundGravityFactor;
            return share;
        }

        // When, within a step of h, a spring from e0 < 0 that ends above zero first reaches zero (by bisection: the
        // spring is monotonic up to its first crossing).
        private static double UpOnlyCrossing(double e0, double v0, double timescale, double efficiency, double h)
        {
            double lo = 0, hi = h;
            for (int i = 0; i < UpOnlyCrossingIterations; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (VehicleSpring.Step(e0, v0, timescale, efficiency, mid).e < 0) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        private const int UpOnlyCrossingIterations = 40;

        // What the last hover step's move left out of the spring's velocity (VehicleSpring.MoveOver); null when hover
        // did not act in the last step.
        private double? _hoverCarry;

        /// <summary>
        /// The height hover holds the vehicle at, and whether it only pushes up. False when hover is off (its timescale
        /// at the "off" value).
        /// </summary>
        private bool HoverTarget(out float targetZ, out bool upOnly)
        {
            float hoverHeight = _props.GetFloat(VehFloatParam.HoverHeight, 0f);
            float hoverTimescale = _props.GetFloat(VehFloatParam.HoverTimescale, 1000f);
            targetZ = 0f;
            upOnly = (_props.Flags & ExtendedVehicleFlags.HoverUpOnly) != 0;

            // If timescale is effectively disabled, skip
            if (hoverTimescale >= VehicleLimits.MaxHoverTimescale)
                return false;

            Vector3 pos = _body.Position;
            float targetBase;

            // Determine the base height based on hover flags
            if ((_props.Flags & ExtendedVehicleFlags.HoverWaterOnly) != 0)
            {
                targetBase = _body.GetWaterLevel(pos);
            }
            else if ((_props.Flags & ExtendedVehicleFlags.HoverTerrainOnly) != 0)
            {
                targetBase = _body.GetTerrainHeight(pos);
            }
            else if ((_props.Flags & ExtendedVehicleFlags.HoverGlobalHeight) != 0)
            {
                targetBase = 0f; // Global = absolute height, hover height is the target
            }
            else
            {
                // No hover flag set — use max of terrain and water (SL default behavior)
                targetBase = Math.Max(_body.GetTerrainHeight(pos), _body.GetWaterLevel(pos));
            }

            targetZ = targetBase + hoverHeight;
            return true;
        }

        #endregion

        #region Vertical Attractor

        /// <summary>
        /// Points local Z axis to sky with overturn recovery.
        /// Ported from Halcyon VehicleDynamics.SimulateVerticalAttractor().
        /// </summary>
        private void SimulateVerticalAttractor(float timeStep, uint frameNum, out Vector3 attractionForces, out float angle, out bool inverted)
        {
            float timescale = Math.Max(_props.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f), timeStep);
            float efficiency = _props.GetFloat(VehFloatParam.VerticalAttractionEfficiency, 0f);
            inverted = false;
            angle = 0.0f;
            attractionForces = Vector3.Zero;

            if (timescale < VehicleLimits.MaxAttractTimescale)
            {
                // Compute the X and Y axis deflection from vertical
                Vector3 xrot = new Vector3(1, 0, 0) * _rotation;
                Vector3 yrot = new Vector3(0, 1, 0) * _rotation;
                Vector3 xyrot = QuatToEuler(_rotation);

                xyrot.Z = 0;
                angle = Math.Abs(QuatToAngle(Quaternion.CreateFromEulers(xyrot)));

                // Airplanes can fly inverted
                if (_props.Type == VehicleType.Airplane)
                {
                    if (angle > Math.PI / 2)
                    {
                        angle = (float)Math.PI - angle;
                        inverted = true;
                    }
                }

                float apct = angle / (float)Math.PI;

                // Go dormant if in the sweet spot or if no angular changes are happening.
                // Do not go dormant if the vehicle is overturned.
                if (Math.Abs(_props.Dynamics.LastVerticalAngle - angle) >= VehicleLimits.ThresholdAttractorAngle)
                {
                    _props.Dynamics.LastVerticalFrameNumber = frameNum;
                }

                if (angle >= VehicleLimits.ThresholdOverturnAngle ||
                    (frameNum - _props.Dynamics.LastVerticalFrameNumber) < (VehicleLimits.MaxAttractDormancy / timeStep))
                {
                    // Compute restoration force in local coordinates
                    Vector3 vtwix = new Vector3(-yrot.Z, xrot.Z, 0);

                    // Different vehicles have different characteristics
                    vtwix = vtwix * (float)Math.PI * (float)Math.Pow(Math.E, efficiency * 3.0);

                    // Non-airplane vehicles have very strong restorative forces
                    if (_props.Type != VehicleType.Airplane)
                        vtwix *= (1.0f + (float)Math.Pow(1.0 + apct, 4.0));

                    // Zero out y-axis rotation if the limit roll only flag is set
                    if ((_props.Flags & ExtendedVehicleFlags.LimitRollOnly) != 0)
                    {
                        if (_props.Type == VehicleType.Airplane || _props.Type == VehicleType.Balloon)
                            vtwix.Y = 0.0f;
                        else
                            vtwix.Y *= 0.1f;
                    }

                    // If overturned and no progress toward vertical, keep increasing force
                    if (_props.Type != VehicleType.Airplane)
                    {
                        if (angle >= VehicleLimits.ThresholdOverturnAngle && angle >= _props.Dynamics.LastVerticalAngle)
                        {
                            _props.Dynamics.VerticalForceAdjust *= 1.3f;
                            vtwix *= _props.Dynamics.VerticalForceAdjust;
                        }
                        else
                        {
                            _props.Dynamics.VerticalForceAdjust /= 1.1f;
                            if (_props.Dynamics.VerticalForceAdjust < 1.0f)
                                _props.Dynamics.VerticalForceAdjust = 1.0f;
                            vtwix *= _props.Dynamics.VerticalForceAdjust;
                        }
                    }

                    // Apply efficiency damping
                    Vector3 remvel = Vector3.Zero;
                    Vector3 wtensor = _body.InertiaDiagonal;
                    remvel.X = _localAngularVel.X * MovementExpGrowth(efficiency, 1.0f);
                    remvel.Y = _localAngularVel.Y * MovementExpGrowth(efficiency, 1.0f);

                    // Remap tensor to object coordinates
                    vtwix *= (wtensor * _vframe);

                    // Convert local forces to world forces
                    vtwix = vtwix * _rotation;

                    if (vtwix != Vector3.Zero)
                    {
                        attractionForces = vtwix * timeStep / timescale;
                        AddTorqueImpulse(attractionForces);
                    }

                    // Always apply the damping factor while not in the sweet spot
                    if (Vector3.Mag(remvel) > 0)
                    {
                        remvel = remvel * _rotation;
                        AddTorqueVelocityChange(-remvel);
                    }
                    _props.Dynamics.LastVerticalAngle = angle;
                }
            }
        }

        #endregion

        #region Banking (roll -> yaw driver; the consumer is the banking-turn block in SimulateMotors)

        /// <summary>
        /// Converts roll to yaw rotation. Follows the InWorldz Halcyon vehicle dynamics.
        /// Runs AFTER the vertical attractor (whose angle/
        /// inverted it takes) and BEFORE SimulateMotors: it sets Dynamics.BankingDirection, which the
        /// already-present banking-turn block inside SimulateMotors consumes and blends into the angular-Z
        /// torque. Seam table: _localLinearVel/_rotation, the limits, Utils.Clamp and _props.Dynamics are
        /// the same names here, so the body is unchanged.
        /// </summary>
        private void SimulateBankingToYaw(float timeStep, float angle, bool inverted)
        {
            float timescale = Math.Max(_props.GetFloat(VehFloatParam.BankingTimescale, 1000f), timeStep);

            if (timescale < VehicleLimits.MaxAttractTimescale)
            {
                float efficiency = _props.GetFloat(VehFloatParam.BankingEfficiency, 0f);
                float bmodifier = _props.GetFloat(VehFloatParam.InvertedBankingModifier, 1f);

                if (VehicleLimits.DoBanking && timescale < VehicleLimits.MaxTimescale)
                {
                    float bankingmix = _props.GetFloat(VehFloatParam.BankingMix, 0.5f);
                    float xspeed = 0.0f;

                    // Legacy support: use velocity as an on/off switch, proportional and capped
                    if (Math.Abs(_localLinearVel.X) > VehicleLimits.ThresholdAngularMotorDeltaV)
                        xspeed = Utils.Clamp(Math.Abs(_localLinearVel.X), 0, VehicleLimits.MaxLegacyLinearVelocity);
                    float xspeedpct = xspeed / VehicleLimits.MaxLegacyLinearVelocity;

                    // Compute percentage of roll
                    Vector3 erot = new Vector3(0f, 1f, 0f);
                    erot *= _rotation;
                    float xangle = erot.Z;
                    float attitude = (angle > Math.PI / 2.0) ? -1 : 1;

                    // Clamp to current banking range
                    float xmax = _props.GetFloat(VehFloatParam.BankingAzimuth, (float)Math.PI / 2f);
                    xangle = Utils.Clamp(xangle * (float)Math.PI * 0.5f / xmax, -1, 1);

                    // Apply inverted banking modifier
                    if (inverted)
                        efficiency = efficiency * bmodifier;

                    // Apply torque only when above threshold
                    if (Math.Abs(xangle) > VehicleLimits.ThresholdBankAngle)
                    {
                        _props.Dynamics.BankingDirection = -xangle * attitude * efficiency * (1.0f - bankingmix) * (float)Math.PI; // static
                        _props.Dynamics.BankingDirection += -xangle * attitude * efficiency * bankingmix * xspeedpct * (float)Math.PI; // dynamic
                    }
                    else
                    {
                        _props.Dynamics.BankingDirection = 0;
                    }
                }
            }
        }

        #endregion

        #region Motors and friction — Linear + Angular, and the Banking turn motor

        /// <summary>
        /// The linear motor, linear friction and the vehicle's gravity over one step of h seconds. On each axis of
        /// the vehicle frame:
        ///
        ///   dv/dt = g(s) * (M - v) / Tm  -  v / Tf  +  a          g(s) = e^(-s / Td)
        ///
        /// M the motor direction, Tm the motor timescale, Td the motor decay timescale, s the time since the motor
        /// was last set, Tf the friction timescale, a the vehicle's gravity along the axis (see
        /// <see cref="VehicleMotorSolver"/>). The velocity after the step is the exact solution of that equation over
        /// it, so the result does not depend on the step rate. Friction acts on every axis all the time; the motor's
        /// pull fades with its decay and is never cut off.
        ///
        /// The engine applies the gravity itself over the step (<see cref="ApplyGravity"/>); the change written here
        /// is the exact solution less that gravity, so the two together end the step on the exact velocity.
        /// </summary>
        private void SimulateLinearMotorAndFriction(float h, bool motorOn, bool frictionOn)
        {
            Vector3 v0 = _rawLocalLinearVel;
            Vector3 gravityWorld = _body.Gravity * GravityShare();
            Vector3 gravity = gravityWorld * Quaternion.Inverse(_rotation);
            Vector3 motor = _props.Dynamics.LinearDirection;
            Vector3 motorTs = _props.GetVec(VehVectorParam.LinearMotorTimescale);
            Vector3 decayTs = _props.GetVec(VehVectorParam.LinearMotorDecayTimescale);
            Vector3 frictionTs = frictionOn ? _props.GetVec(VehVectorParam.LinearFrictionTimescale) : FrictionOff;

            if (!motorOn)
                _props.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            double age = _props.Dynamics.LinearDecayIndex;

            // Friction and gravity alone over the step, and the motor with them.
            Vector3 frictionOnly = FrictionStep(v0, frictionTs, h, gravity);
            Vector3 v1 = new Vector3(
                (float)VehicleMotorSolver.Step(v0.X, motor.X, motorTs.X, decayTs.X, age, frictionTs.X, h, gravity.X),
                (float)VehicleMotorSolver.Step(v0.Y, motor.Y, motorTs.Y, decayTs.Y, age, frictionTs.Y, h, gravity.Y),
                (float)VehicleMotorSolver.Step(v0.Z, motor.Z, motorTs.Z, decayTs.Z, age, frictionTs.Z, h, gravity.Z));
            v1.X = Utils.Clamp(v1.X, -VehicleLimits.MaxLinearVelocity, VehicleLimits.MaxLinearVelocity);
            v1.Y = Utils.Clamp(v1.Y, -VehicleLimits.MaxLinearVelocity, VehicleLimits.MaxLinearVelocity);
            v1.Z = Utils.Clamp(v1.Z, -VehicleLimits.MaxLinearVelocity, VehicleLimits.MaxLinearVelocity);

            // The step's change in world coordinates: friction's share (with gravity's, less the g h the engine adds),
            // and the motor's share on top of it.
            Vector3 frictionChange = (frictionOnly - v0) * _rotation - gravityWorld * h;
            Vector3 motorChange = (v1 - frictionOnly) * _rotation;

            // VEHICLE_FLAG_LIMIT_MOTOR_UP / LIMIT_MOTOR_DOWN: the motor does not push up (down) in world terms.
            // Friction's share is left whole.
            if ((_props.Flags & ExtendedVehicleFlags.LimitMotorUp) != 0 && motorChange.Z > 0)
                motorChange.Z = 0;
            if ((_props.Flags & ExtendedVehicleFlags.LimitMotorDown) != 0 && motorChange.Z < 0)
                motorChange.Z = 0;

            _props.Dynamics.LinearDecayIndex += h;
            ApplyLinearVelocityChange(frictionChange + motorChange);
        }

        /// <summary>
        /// The angular motor and angular friction over one step of h seconds: the same equation as the linear
        /// motor's (<see cref="SimulateLinearMotorAndFriction"/>) on the angular velocity about each vehicle axis.
        /// With VEHICLE_FLAG_TORQUE_WORLD_Z the third axis is the world's Z instead of the vehicle's, for the motor
        /// and the friction alike. Returns the change about world Z, which the banking turn motor adds to.
        /// </summary>
        private float SimulateAngularMotorAndFriction(float h, bool motorOn, bool frictionOn)
        {
            Vector3 worldVel = _rawWorldAngularVel;
            Vector3 motor = _props.Dynamics.AngularDirection;
            Vector3 motorTs = _props.GetVec(VehVectorParam.AngularMotorTimescale);
            Vector3 decayTs = _props.GetVec(VehVectorParam.AngularMotorDecayTimescale);
            Vector3 frictionTs = frictionOn ? _props.GetVec(VehVectorParam.AngularFrictionTimescale) : FrictionOff;
            bool torqueWorldZ = (_props.Flags & ExtendedVehicleFlags.TorqueWorldZ) != 0;

            // The velocity the equation acts on: about the vehicle's axes, or with TORQUE_WORLD_Z the vehicle's X
            // and Y of the velocity without its world-Z part, and world Z.
            Quaternion invRotation = Quaternion.Inverse(_rotation);
            Vector3 v0;
            if (torqueWorldZ)
            {
                v0 = new Vector3(worldVel.X, worldVel.Y, 0f) * invRotation;
                v0.Z = worldVel.Z;
            }
            else
            {
                v0 = worldVel * invRotation;
            }

            if (!motorOn)
                _props.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            double age = _props.Dynamics.AngularDecayIndex;

            Vector3 v1 = new Vector3(
                (float)VehicleMotorSolver.Step(v0.X, motor.X, motorTs.X, decayTs.X, age, frictionTs.X, h),
                (float)VehicleMotorSolver.Step(v0.Y, motor.Y, motorTs.Y, decayTs.Y, age, frictionTs.Y, h),
                (float)VehicleMotorSolver.Step(v0.Z, motor.Z, motorTs.Z, decayTs.Z, age, frictionTs.Z, h));
            v1.X = Utils.Clamp(v1.X, -VehicleLimits.MaxAngularVelocity, VehicleLimits.MaxAngularVelocity);
            v1.Y = Utils.Clamp(v1.Y, -VehicleLimits.MaxAngularVelocity, VehicleLimits.MaxAngularVelocity);
            v1.Z = Utils.Clamp(v1.Z, -VehicleLimits.MaxAngularVelocity, VehicleLimits.MaxAngularVelocity);

            Vector3 change;
            if (torqueWorldZ)
            {
                Vector3 before = new Vector3(v0.X, v0.Y, 0f) * _rotation + new Vector3(0f, 0f, v0.Z);
                Vector3 after = new Vector3(v1.X, v1.Y, 0f) * _rotation + new Vector3(0f, 0f, v1.Z);
                change = after - before;
            }
            else
            {
                change = (v1 - v0) * _rotation;
            }

            _props.Dynamics.AngularDecayIndex += h;

            // The change about world Z is applied with the banking turn motor's (SimulateBankingTurn).
            float aboutWorldZ = change.Z;
            change.Z = 0;
            _motorTorqueVelChange += change;
            return aboutWorldZ;
        }

        // Friction timescales that turn friction off on every axis.
        private static readonly Vector3 FrictionOff = new Vector3((float)VehicleMotorSolver.FrictionOffTimescale);

        private static Vector3 FrictionStep(Vector3 v0, Vector3 frictionTs, float h, Vector3 accel)
            => new Vector3(
                (float)VehicleMotorSolver.FrictionStep(v0.X, frictionTs.X, h, accel.X),
                (float)VehicleMotorSolver.FrictionStep(v0.Y, frictionTs.Y, h, accel.Y),
                (float)VehicleMotorSolver.FrictionStep(v0.Z, frictionTs.Z, h, accel.Z));

        /// <summary>
        /// The banking turn motor (roll to yaw), and the angular motor's change about world Z with it.
        /// Follows the InWorldz Halcyon vehicle dynamics.
        /// </summary>
        private void SimulateBankingTurn(float timeStep, bool motorOn, float angularz)
        {
            // ================================================================
            // Banking Turn Motor Simulation
            // ================================================================
            float btimescale = _props.GetFloat(VehFloatParam.BankingTimescale, 1000f);
            float bnewvel = 0;
            Vector3 worldvel;

            if (motorOn && _props.Dynamics.BankingDirection != 0 && btimescale < VehicleLimits.MaxTimescale)
            {
                float blastvel = _worldAngularVel.Z;
                float badjvel;
                float bfactor;
                float dirbsign;

                badjvel = blastvel;
                dirbsign = VehicleMath.PosNeg(_props.Dynamics.BankingDirection);

                // Target velocity ensures forward progress
                if (dirbsign * (_props.Dynamics.BankingTargetVelocity - badjvel) > 0) badjvel = _props.Dynamics.BankingTargetVelocity;

                // Compute rampup factor
                bfactor = MotorRampRate(badjvel, _props.Dynamics.BankingDirection, btimescale, timeStep);
                bnewvel = MotorRampStep(badjvel, _props.Dynamics.BankingDirection, btimescale, timeStep, bfactor);

                // If angular motor is engaged, reduce max banking velocity
                if (_props.Dynamics.AngularDecayIndex < VehicleLimits.ThresholdAngularMotorEngaged)
                {
                    if (Math.Abs(bnewvel) > VehicleLimits.MaxLegacyAngularVelocity)
                        bnewvel = VehicleLimits.MaxLegacyAngularVelocity * VehicleMath.PosNeg(bnewvel);
                }

                // Crossover flip
                if (bfactor == VehicleLimits.ThresholdInverseCrossover) bfactor = -bfactor;

                // Avoid zero velocity stiction
                if (bfactor > 0 && Math.Abs(bnewvel) < VehicleLimits.ThresholdAngularMotorDeltaV)
                    bnewvel = dirbsign * VehicleLimits.ThresholdAngularMotorDeltaV * 8;

                // Compute new target banking velocity
                if (_props.Dynamics.BankingDirection * bnewvel < 0 || bfactor < 0)
                    _props.Dynamics.BankingTargetVelocity = bnewvel;
                else
                    _props.Dynamics.BankingTargetVelocity = Utils.Clamp(bnewvel, -Math.Abs(_props.Dynamics.BankingDirection), Math.Abs(_props.Dynamics.BankingDirection));

                // Limit max velocities
                bnewvel = Utils.Clamp(bnewvel, -VehicleLimits.MaxAngularVelocity, VehicleLimits.MaxAngularVelocity);

                // If local velocity exceeds motor speed, motor is not adding power
                if (bfactor >= 0 && (dirbsign * (blastvel - bnewvel) > 0)) bnewvel = blastvel;

                // Kill banking motor when linear motor stalled
                if (IsLinearMotorStalled())
                {
                    _props.Dynamics.BankingTargetVelocity = 0.0f;
                    bnewvel = blastvel;
                }

                // Convert to deltaV
                bnewvel -= blastvel;
            }
            else
            {
                _props.Dynamics.BankingTargetVelocity = 0.0f;
                bnewvel = 0;
            }

            // Blend angular Z and banking forces. The angular motor's and friction's change about world Z is applied
            // whole; the banking motor's only above its threshold, as before.
            worldvel = Vector3.Zero;
            if (Math.Abs(bnewvel) >= VehicleLimits.ThresholdAngularMotorDeltaV)
                worldvel.Z = bnewvel;
            worldvel.Z += angularz;
            _motorTorqueVelChange += worldvel;
        }

        #endregion

        #region Exponential Motor Math (ported from Halcyon VehicleMotor)

        // The ramp below is the banking turn motor's. The linear and angular motors follow VehicleMotorSolver.

        /// <summary>
        /// Soft exponential growth: returns value between 0 and 1.0.
        /// </summary>
        internal float MovementExpGrowth(float timeindex, float timescale)
        {
            return Utils.Clamp((float)Math.Pow(Math.E, (timeindex / timescale) / Math.E) - 1.0f, 0.0f, 1.0f);
        }

        /// <summary>
        /// Compute a growth/decay rate based on an exponential fit between two velocity points.
        /// </summary>
        internal static float GetGrowthRate(float svel, float evel, float timescale)
        {
            float elog;

            if (svel * evel > 0)
            {
                evel = Math.Abs(evel);
                svel = Math.Abs(svel);
                elog = (float)Math.Log(evel / svel);
            }
            else
            {
                if (evel == 0 && svel == 0) return 0;
                if (evel == 0)
                    elog = -(float)Math.Log(1.0 + Math.Abs(svel));
                else if (svel == 0)
                    elog = (float)Math.Log(1.0 + Math.Abs(evel));
                else
                {
                    if (Math.Abs(svel) > CrossoverSpeed)
                        elog = -(float)Math.Log(1.0f + Math.Abs(svel - evel));
                    else
                        return VehicleLimits.ThresholdInverseCrossover;
                }
            }

            return elog / timescale;
        }

        /// <summary>
        /// The motor ramp's rate for one step, applied by <see cref="MotorRampStep"/> as v * e^rate. The ramp is
        /// dv/dt = v * ln(target / v) / timescale (GetGrowthRate gives the log). Its exact solution shrinks
        /// ln(v / target) by e^(-dt / timescale) each step, which is v * (target / v)^(1 - e^(-dt / timescale)):
        /// the same speed at the same time whatever the step. The old step, v + v * ln(target / v) * dt / timescale,
        /// is its first-order form; it gives the same as the step goes to zero, but fell short on a long step.
        /// The crossover marker is returned unchanged (a sign flip, which does not depend on the step).
        /// </summary>
        internal static float MotorRampRate(float svel, float evel, float timescale, float timeStep)
        {
            float elog = GetGrowthRate(svel, evel, 1f);
            if (elog == VehicleLimits.ThresholdInverseCrossover)
                return elog;
            return elog * StepShare(timeStep, timescale);
        }

        /// <summary>
        /// One step of the motor ramp from v toward evel, given the rate MotorRampRate returned for it. Toward a
        /// target of the same sign the ramp is the exact solution above. Toward zero or the other way, the rate
        /// depends on the speed itself (ln(1 + |v|)), which has no closed form, so the step is taken in sub-steps of
        /// at most timescale / RampSubstepsPerTimescale, each with the exact form for its own starting speed.
        /// </summary>
        internal static float MotorRampStep(float v, float evel, float timescale, float timeStep, float rate)
        {
            if (rate == VehicleLimits.ThresholdInverseCrossover)
                return v + v * rate;   // crossover: the velocity flips sign (the marker is -2.01)
            if (v * evel > 0)
                return v * (float)Math.Exp(rate);
            int n = (int)Math.Min(MaxRampSubsteps, Math.Max(1.0, Math.Ceiling(timeStep * RampSubstepsPerTimescale / timescale)));
            float h = timeStep / n;
            for (int i = 0; i < n; i++)
                v = RampSubstep(v, evel, timescale, h);
            return v;
        }

        // One sub-step of a ramp toward zero or the other sign. Toward the other sign the motor brakes until the speed
        // is down to CrossoverSpeed, then flips it (GetGrowthRate's crossover marker): the flip is made at that speed,
        // inside the sub-step, and the rest of the sub-step ramps from there, so when it happens does not depend on
        // where a step or sub-step happens to end.
        private static float RampSubstep(float v, float evel, float timescale, float h)
        {
            float r = MotorRampRate(v, evel, timescale, h);
            if (r == VehicleLimits.ThresholdInverseCrossover)
                return v + v * r;
            float next = v * (float)Math.Exp(r);
            if (v * evel < 0 && Math.Abs(v) > CrossoverSpeed && Math.Abs(next) < CrossoverSpeed)
            {
                float share = (float)Math.Log(CrossoverSpeed / Math.Abs(v)) / r;   // of the sub-step, to the crossover
                float atCrossover = Math.Sign(v) * CrossoverSpeed;
                float flipped = atCrossover + atCrossover * VehicleLimits.ThresholdInverseCrossover;
                float rest = h * (1f - share);
                return MotorRampStep(flipped, evel, timescale, rest, MotorRampRate(flipped, evel, timescale, rest));
            }
            return next;
        }

        // Below this speed a motor driving the other way flips the velocity's sign instead of braking further.
        private const float CrossoverSpeed = 0.3f;


        // The motor ramp toward zero or across it is integrated in sub-steps no longer than a timescale over this
        // (within 0.2% of a far finer integration), and at most this many per step.
        private const float RampSubstepsPerTimescale = 128f;
        private const int MaxRampSubsteps = 1024;

        /// <summary>The share of the way to its end that an exponential with this timescale covers in one step.</summary>
        internal static float StepShare(float timeStep, float timescale)
            => 1f - (float)Math.Exp(-timeStep / timescale);

        #endregion // Motor Math

        #region Quaternion Math Utilities (replaces Halcyon PhysUtil)

        /// <summary>
        /// Convert quaternion to Euler angles (radians). Replaces Halcyon PhysUtil.Rot2Euler().
        /// </summary>
        private static Vector3 QuatToEuler(Quaternion q)
        {
            // Standard quaternion → Euler (X=roll, Y=pitch, Z=yaw)
            float sinr_cosp = 2.0f * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
            float roll = (float)Math.Atan2(sinr_cosp, cosr_cosp);

            float sinp = 2.0f * (q.W * q.Y - q.Z * q.X);
            float pitch;
            if (Math.Abs(sinp) >= 1.0f)
                pitch = (float)Math.PI / 2.0f * Math.Sign(sinp);
            else
                pitch = (float)Math.Asin(sinp);

            float siny_cosp = 2.0f * (q.W * q.Z + q.X * q.Y);
            float cosy_cosp = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
            float yaw = (float)Math.Atan2(siny_cosp, cosy_cosp);

            return new Vector3(roll, pitch, yaw);
        }

        /// <summary>
        /// Get the rotation angle of a quaternion. Replaces Halcyon PhysUtil.Rot2Angle().
        /// </summary>
        private static float QuatToAngle(Quaternion q)
        {
            // 2 * acos(|w|) gives the rotation angle
            float w = Math.Abs(q.W);
            if (w > 1.0f) w = 1.0f;
            return 2.0f * (float)Math.Acos(w);
        }

        /// <summary>
        /// Compute the rotation between two vectors. Replaces Halcyon PhysUtil.RotBetween().
        /// </summary>
        private static Quaternion RotBetween(Vector3 a, Vector3 b)
        {
            a = Vector3.Normalize(a);
            b = Vector3.Normalize(b);
            float dot = Vector3.Dot(a, b);

            if (dot > 0.999999f)
                return Quaternion.Identity;

            if (dot < -0.999999f)
            {
                // 180 degree rotation — find an orthogonal axis
                Vector3 axis = Vector3.Cross(Vector3.UnitX, a);
                if (Vector3.Mag(axis) < 0.0001f)
                    axis = Vector3.Cross(Vector3.UnitY, a);
                axis = Vector3.Normalize(axis);
                return new Quaternion(axis.X, axis.Y, axis.Z, 0f);
            }

            Vector3 cross = Vector3.Cross(a, b);
            float w = (float)Math.Sqrt(a.LengthSquared() * b.LengthSquared()) + dot;
            Quaternion result = new Quaternion(cross.X, cross.Y, cross.Z, w);
            return Quaternion.Normalize(result);
        }

        /// <summary>
        /// Compute the angle between two quaternions. Replaces Halcyon PhysUtil.AngleBetween().
        /// </summary>
        private static float AngleBetween(Quaternion a, Quaternion b)
        {
            Quaternion diff = Quaternion.Inverse(b) * a;
            float w = Math.Abs(diff.W);
            if (w > 1.0f) w = 1.0f;
            return 2.0f * (float)Math.Acos(w);
        }

        #endregion

        #region Vehicle Type Defaults (ported from Halcyon SetVehicleDefaults)

        /// <summary>
        /// Set all vehicle parameters to the defaults for the given type.
        /// Faithfully ported from Halcyon VehicleDynamics.SetVehicleDefaults().
        /// </summary>
        private void SetVehicleDefaults(VehicleType newType)
        {
            switch (newType)
            {
                case VehicleType.None:
                    _props.ParamsVec.Clear();
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat.Clear();
                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 1000f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 1000f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 1000f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 1000f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot.Clear();
                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;

                    _props.Flags = ExtendedVehicleFlags.None;
                    break;

                case VehicleType.Sled:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(1000f, 1f, 1000f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = new Vector3(0f, 0f, -0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(120f, 120f, 120f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(120f, 120f, 120f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 0.3f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 1f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 1f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.1f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 10f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 10f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.LimitRollOnly | ExtendedVehicleFlags.LimitMotorUp;
                    break;

                case VehicleType.Car:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(100f, 0.1f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(100f, 100f, 0.3f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(0.5f, 1f, 1f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(0.2f, 0.2f, 0.05f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(10f, 2f, 2f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.3f, 0.3f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 2f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0.5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 2f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.6f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 2f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = -0.2f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 1f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0.75f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 2.5f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.LimitRollOnly
                                 | ExtendedVehicleFlags.HoverUpOnly | ExtendedVehicleFlags.LimitMotorUp
                                 | ExtendedVehicleFlags.TorqueWorldZ;
                    break;

                case VehicleType.Boat:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(200f, 0.5f, 3f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(10f, 1f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1f, 5f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(0.2f, 2f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(1f, 10f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.3f, 0.3f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0.5f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0.8f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 0.2f;
                    // Buoyancy 1.0 (matches BulletSim's TYPE_BOAT, BSDynamics buoyancy=1.0). Gravity
                    // is fully cancelled (ApplyGravity = gravity*(1-buoyancy) = 0), so a boat CANNOT sink even
                    // on a frame where hover has not run yet - e.g. right after a region reload, before the
                    // controller re-activates. Hover still trims it to the water surface (HoverWaterOnly,
                    // height 0.5); buoyancy holds the baseline, hover positions - the same compose BulletSim uses.
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 3f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0.5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 0.2f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 1f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 0.2f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverWaterOnly
                                 | ExtendedVehicleFlags.LimitMotorUp | ExtendedVehicleFlags.LimitMotorDown
                                 | ExtendedVehicleFlags.TorqueWorldZ;
                    break;

                case VehicleType.Airplane:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(200f, 10f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(1f, 0.1f, 0.5f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(2f, 2f, 2f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(1f, 2f, 1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(60f, 60f, 60f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(8f, 8f, 8f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = new Vector3(0.1f, 0f, 0f);
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = new Vector3(0.05f, 0f, 0f);

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0.5f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 0.5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 1f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 2f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.9f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 2f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 1f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.7f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 1f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.TorqueWorldZ | ExtendedVehicleFlags.LimitRollOnly;
                    break;

                case VehicleType.Balloon:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(1f, 1f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(2f, 0.5f, 1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1f, 5f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(2f, 2f, 0.3f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(60f, 60f, 60f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.3f, 0.3f, 1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = new Vector3(0.1f, 0.1f, 0.1f);
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = new Vector3(0.01f, 0.01f, 0f);

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 5f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0.8f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 10f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 4f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0.05f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 5f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.None;  // Halcyon had ReactToWind only
                    break;
            }
        }

        #endregion // Vehicle Type Defaults

        #region Utility

        private static float ClampF(float val, float min, float max)
        {
            return Math.Max(min, Math.Min(val, max));
        }

        #endregion // Utility

        // =================================================================
        // Public accessor for vehicle type (used by the host's VehicleType getter)
        // =================================================================
        public Vehicle Type
        {
            get
            {
                switch (_props.Type)
                {
                    case VehicleType.None:     return Vehicle.TYPE_NONE;
                    case VehicleType.Sled:     return Vehicle.TYPE_SLED;
                    case VehicleType.Car:      return Vehicle.TYPE_CAR;
                    case VehicleType.Boat:     return Vehicle.TYPE_BOAT;
                    case VehicleType.Airplane: return Vehicle.TYPE_AIRPLANE;
                    case VehicleType.Balloon:  return Vehicle.TYPE_BALLOON;
                    default:                         return Vehicle.TYPE_NONE;
                }
            }
        }
    }
}
