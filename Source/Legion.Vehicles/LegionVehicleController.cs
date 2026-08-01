/*
 * Legion Grid — Vehicle Dynamics Port from InWorldz Halcyon
 * Original Copyright (c) 2015, InWorldz Halcyon Developers
 * Adapted for BulletSim physics engine, April 2026.
 *
 * EXTRACTED (M8 Task 2) into this backend-agnostic controller from
 * OpenSim/Region/PhysicsModules/BulletS/LegionVehicleDynamics.cs — which stays untouched as the
 * BulletSim A/B reference. The MATH IS MOVED VERBATIM (same computations, same order, same
 * constants); only the physics-engine seam is substituted, mechanically, per this table:
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
 *   ControllingPrim.ComputeGravity(buoy)        -> _body.Gravity * (1 - buoy)   (GravModifier = 1)
 *   BSParam.Gravity                             -> _body.Gravity.Z
 *   BSParam.VehicleGroundGravityFudge           -> VehicleGroundGravityFudge const (same 0.2 default)
 *   GetTerrainHeight/GetWaterLevel              -> _body.GetTerrainHeight/_body.GetWaterLevel
 *   m_physicsScene.PE.PushUpdate                -> dropped (Bullet-only activation nudge; the host
 *                                                  disables sleeping on an active vehicle body)
 *   VDetailLog                                  -> dropped (logging only)
 *   BSActor/scene-event plumbing                -> the HOST steps an active controller before each
 *                                                  physics step and applies/restores body params
 *
 * SCOPE (M8 Task 2, the BOAT slice): linear motor + timescale/decay, angular motor +
 * timescale/decay, linear friction, angular friction, hover, buoyancy, vertical attractor —
 * plus the shared frame infrastructure (timestep smoothing, velocity anti-jitter, motor reset,
 * stall detection, spike mitigation, ground-penetration fix, manual gravity, torque accumulator).
 * DEFERRED to later slices (calls removed from Step, everything else in place for them):
 * SimulateAngularDeflection, SimulateLinearDeflection, SimulateSledMovement, SimulateBankingToYaw
 * (the banking-turn motor block inside SimulateMotors is retained verbatim; it is inert while
 * BankingDirection stays 0), mouselook, wind.
 *
 * One evaluation-order note: ApplyGravity's ground fudge test is written here as
 * `IsGroundVehicle && _body.HasCollision` (reference: `HasSomeCollision && IsGroundVehicle`) so
 * the engine query short-circuits away for non-ground vehicles; the result is identical.
 *
 * THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 */

using System;
using OpenMetaverse;

namespace Legion.Vehicles
{
    /// <summary>
    /// Halcyon-derived vehicle dynamics engine, backend-agnostic.
    /// The host owns one instance per vehicle body and calls Step(dt) every frame BEFORE the
    /// physics step while the vehicle is active and physical.
    /// </summary>
    public sealed class LegionVehicleController
    {
        // The neutral physics seam this controller reads/writes through
        private readonly IVehicleBody _body;

        // Vehicle properties and runtime state
        private LegionVehicleProperties _props;

        // Cached mass
        private float m_vehicleMass;

        // Frame counter for periodic operations
        private uint m_frameNum;

        // BSParam.VehicleGroundGravityFudge default (ground vehicles only; boats never hit this)
        private const float VehicleGroundGravityFudge = 0.2f;

        // =====================================================================
        // Ephemeral per-frame computed values (not persisted)
        // =====================================================================
        private Quaternion _vframe;
        private Quaternion _rotation;
        private Vector3 _worldAngularVel;
        private Vector3 _worldLinearVel;
        private Vector3 _localAngularVel;
        private Vector3 _localLinearVel;

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

        // =====================================================================
        // Constructor
        // =====================================================================
        public LegionVehicleController(IVehicleBody body)
        {
            _body = body;
            _props = new LegionVehicleProperties();
            SetVehicleDefaults(LegionVehicleType.None);
        }

        // =====================================================================
        // Active state. (The reference also checks IsPhysicallyActive; here the HOST
        // only steps the controller while the body is physical.)
        // =====================================================================
        public bool IsActive
        {
            get { return (_props.Type != LegionVehicleType.None); }
        }

        public bool IsGroundVehicle
        {
            get { return (_props.Type == LegionVehicleType.Car || _props.Type == LegionVehicleType.Sled); }
        }

        /// <summary>Read a current float vehicle param (preset default + any llSetVehicleFloatParam override).
        /// Introspection for the host / tests - e.g. asserting the boat preset's buoyancy.</summary>
        public float GetFloatParam(VehFloatParam key) => _props.GetFloat(key, 0f);

        #region Vehicle Parameter Setting — routes from LSL Vehicle wire codes to internal enums

        // =================================================================
        // Process vehicle type change from LSL
        // =================================================================
        public void ProcessTypeChange(Vehicle pType)
        {
            LegionVehicleType newType;
            switch (pType)
            {
                case Vehicle.TYPE_NONE:     newType = LegionVehicleType.None; break;
                case Vehicle.TYPE_SLED:     newType = LegionVehicleType.Sled; break;
                case Vehicle.TYPE_CAR:      newType = LegionVehicleType.Car; break;
                case Vehicle.TYPE_BOAT:     newType = LegionVehicleType.Boat; break;
                case Vehicle.TYPE_AIRPLANE: newType = LegionVehicleType.Airplane; break;
                case Vehicle.TYPE_BALLOON:  newType = LegionVehicleType.Balloon; break;
                default:                    newType = LegionVehicleType.None; break;
            }

            _props.Type = newType;
            SetVehicleDefaults(newType);
            _props.Dynamics.Reset();
            _props.Dynamics.LastAccessTOD = DateTime.Now;

            // (Reference registers/unregisters BeforeStep scene events and taints a Refresh here;
            // the host reacts to IsActive instead: per-frame Step drive + body physical params.)
        }

        // =================================================================
        // Process float param from LSL
        // =================================================================
        public void ProcessFloatVehicleParam(Vehicle pParam, float pValue)
        {
            switch (pParam)
            {
                case Vehicle.ANGULAR_DEFLECTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.ANGULAR_DEFLECTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale] = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    // Scalar set → apply to all 3 axes
                    pValue = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    pValue = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.BANKING_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency] = ClampF(pValue, -1f, 1f);
                    break;
                case Vehicle.BANKING_MIX:
                    _props.ParamsFloat[VehFloatParam.BankingMix] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.BANKING_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.BankingTimescale] = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    break;
                case Vehicle.BUOYANCY:
                    _props.ParamsFloat[VehFloatParam.Buoyancy] = ClampF(pValue, -1f, 1f);
                    break;
                case Vehicle.HOVER_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.HOVER_HEIGHT:
                    _props.ParamsFloat[VehFloatParam.HoverHeight] = ClampF(pValue, LegionVehicleLimits.MinRegionHeight, LegionVehicleLimits.MaxRegionHeight);
                    break;
                case Vehicle.HOVER_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.HoverTimescale] = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxHoverTimescale);
                    break;
                case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale] = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    pValue = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    pValue = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale] = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxAttractTimescale);
                    break;

                // These are vector properties but LSL allows setting them as a single float
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    pValue = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    pValue = ClampF(pValue, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = new Vector3(pValue, pValue, pValue);
                    MoveAngular(_props.ParamsVec[VehVectorParam.AngularMotorDirection]);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue = ClampF(pValue, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue = ClampF(pValue, -LegionVehicleLimits.MaxLinearVelocity, LegionVehicleLimits.MaxLinearVelocity);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = new Vector3(pValue, pValue, pValue);
                    MoveLinear(_props.ParamsVec[VehVectorParam.LinearMotorDirection]);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue = ClampF(pValue, -LegionVehicleLimits.MaxLinearOffset, LegionVehicleLimits.MaxLinearOffset);
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset] = new Vector3(pValue, pValue, pValue);
                    break;
            }
        }

        // =================================================================
        // Process vector param from LSL
        // =================================================================
        public void ProcessVectorVehicleParam(Vehicle pParam, Vector3 pValue)
        {
            switch (pParam)
            {
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    pValue.X = ClampF(pValue.X, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale] = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    pValue.X = ClampF(pValue.X, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);
                    pValue.Y = ClampF(pValue.Y, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);
                    pValue.Z = ClampF(pValue.Z, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = pValue;
                    MoveAngular(pValue);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue.X = ClampF(pValue.X, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, LegionVehicleLimits.MinPhysicsTimestep, LegionVehicleLimits.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue.X = ClampF(pValue.X, -LegionVehicleLimits.MaxLinearVelocity, LegionVehicleLimits.MaxLinearVelocity);
                    pValue.Y = ClampF(pValue.Y, -LegionVehicleLimits.MaxLinearVelocity, LegionVehicleLimits.MaxLinearVelocity);
                    pValue.Z = ClampF(pValue.Z, -LegionVehicleLimits.MaxLinearVelocity, LegionVehicleLimits.MaxLinearVelocity);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = pValue;
                    MoveLinear(pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue.X = ClampF(pValue.X, -LegionVehicleLimits.MaxLinearOffset, LegionVehicleLimits.MaxLinearOffset);
                    pValue.Y = ClampF(pValue.Y, -LegionVehicleLimits.MaxLinearOffset, LegionVehicleLimits.MaxLinearOffset);
                    pValue.Z = ClampF(pValue.Z, -LegionVehicleLimits.MaxLinearOffset, LegionVehicleLimits.MaxLinearOffset);
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
            LegionVehicleFlags flags = (LegionVehicleFlags)pParam;

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
            // Timestep smoothing (Halcyon pattern)
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

            // -------------------------------------------------------
            // Reset motors if stale (more than 1 second since last access)
            float actualStep = CheckResetMotors(timeStep);

            // Initialize torque accumulator and stall detection
            TorqueInit();
            ClearLinearMotorStalled();

            _props.Dynamics.Timestep = timeStep;

            // -------------------------------------------------------
            // Spike detection and mitigation
            if (LegionVehicleLimits.DoSpikeDetection)
            {
                MitigateVelocitySpiking(actualStep);
            }

            // -------------------------------------------------------
            // Ground penetration fix
            float groundHeight = _body.GetTerrainHeight(_body.Position);
            if (_body.Position.Z - groundHeight < LegionVehicleLimits.MaxGroundPenetration)
            {
                Vector3 zforce = Vector3.Zero;
                zforce.Z = 1.0f + Math.Abs(_localLinearVel.Z * 3.0f);
                ApplyLinearVelocityChange(zforce);
            }

            // -------------------------------------------------------
            // Deflection + sled run in the reference order Angular -> Linear -> Sled, BEFORE hover.
            // Angular deflection — swings the nose toward the velocity direction (weathervane).
            if (LegionVehicleLimits.DoAngularDeflection)
            {
                SimulateAngularDeflection(timeStep);
            }

            // Linear deflection — changing velocity toward the forward axis (the "tracking" bite).
            if (LegionVehicleLimits.DoLinearDeflection)
            {
                SimulateLinearDeflection(timeStep);
            }

            // Sled movement — gravity-assisted slope force, gated on Type == Sled (NEVER a boat). Wired
            // for A/B parity completeness; inert for every non-sled vehicle.
            if (_props.Type == LegionVehicleType.Sled)
            {
                SimulateSledMovement(timeStep);
            }

            // -------------------------------------------------------
            // Hover — maintain target height above terrain/water/global
            SimulateHover(timeStep);

            // -------------------------------------------------------
            // Vertical attractor and banking
            Vector3 attractionForces = Vector3.Zero;
            if (LegionVehicleLimits.DoVerticalAttractor)
            {
                float angle;
                bool inverted;

                SimulateVerticalAttractor(timeStep, m_frameNum, out attractionForces, out angle, out inverted);
                // Banking runs AFTER the attractor (uses its angle/inverted) and BEFORE the motors: it sets
                // Dynamics.BankingDirection, which the banking-turn block inside SimulateMotors consumes.
                SimulateBankingToYaw(timeStep, angle, inverted);
            }

            // -------------------------------------------------------
            // Motors — linear + angular + banking
            if (LegionVehicleLimits.DoMotors)
            {
                SimulateMotors(timeStep, m_frameNum, attractionForces);
            }

            // -------------------------------------------------------
            // Angular friction
            if (LegionVehicleLimits.DoAngularFriction)
            {
                SimulateAngularFriction(timeStep);
            }

            // -------------------------------------------------------
            // Linear friction
            if (LegionVehicleLimits.DoLinearFriction)
            {
                SimulateLinearFriction(timeStep);
            }

            // Apply gravity manually (same pattern as BSDynamics)
            ApplyGravity(timeStep);

            // Apply the accumulated torque
            TorqueFini();

            // Save state for next frame
            _props.Dynamics.LastPosition = _body.Position;
            _props.Dynamics.Timestep = timeStep;
            _props.Dynamics.LocalLinearVelocity = _localLinearVel;
            _props.Dynamics.LocalAngularVelocity = _localAngularVel;
        }

        #endregion // Step

        #region Motor Entry Points (called when LSL sets motor direction)

        /// <summary>
        /// Called when VehicleLinearMotorDirection is set.
        /// Faithfully ported from Halcyon VehicleMotor.MoveLinear().
        /// </summary>
        private void MoveLinear(Vector3 direction)
        {
            if (_props.Type == LegionVehicleType.None) return;

            _props.Dynamics.LastAccessTOD = DateTime.Now;

            // Scale the target if the motor has been decaying.
            if (_props.Dynamics.LinearDecayIndex > LegionVehicleLimits.ThresholdLinearMotorEngaged)
            {
                Vector3 timescale = _props.GetVec(VehVectorParam.LinearMotorDecayTimescale);
                _props.Dynamics.LinearTargetVelocity.X *= MovementExpDecay(_props.Dynamics.LinearDecayIndex, timescale.X);
                _props.Dynamics.LinearTargetVelocity.Y *= MovementExpDecay(_props.Dynamics.LinearDecayIndex, timescale.Y);
                _props.Dynamics.LinearTargetVelocity.Z *= MovementExpDecay(_props.Dynamics.LinearDecayIndex, timescale.Z);
            }

            _props.Dynamics.LinearDecayIndex = 0.0f;

            // SL cheat: ramp and decay should proceed simultaneously, but short decays often prevent
            // motor ramp-up altogether. Delay the decay by a tiny amount.
            float moahfubar = 0.0f;
            Vector3 mts = _props.GetVec(VehVectorParam.LinearMotorTimescale);
            if (Vector3.Mag(mts) < 0.9f)
                moahfubar = 1.0f - Vector3.Mag(mts);
            _props.Dynamics.LinearDecayIndex = LegionVehicleLimits.MinPhysicsTimestep * (1.0f - moahfubar) - LegionVehicleLimits.ThresholdDelayFubar * moahfubar;

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
            if (_props.Type == LegionVehicleType.None) return;

            _props.Dynamics.LastAccessTOD = DateTime.Now;

            // Scale the target if the motor has been decaying.
            if (_props.Dynamics.AngularDecayIndex > LegionVehicleLimits.ThresholdAngularMotorEngaged)
            {
                Vector3 timescale = _props.GetVec(VehVectorParam.AngularMotorDecayTimescale);
                _props.Dynamics.AngularTargetVelocity.X *= MovementExpDecay(_props.Dynamics.AngularDecayIndex, timescale.X);
                _props.Dynamics.AngularTargetVelocity.Y *= MovementExpDecay(_props.Dynamics.AngularDecayIndex, timescale.Y);
                _props.Dynamics.AngularTargetVelocity.Z *= MovementExpDecay(_props.Dynamics.AngularDecayIndex, timescale.Z);
            }

            _props.Dynamics.AngularDecayIndex = 0.0f;

            // SL cheat: delay-fubar for short timescales
            float moahfubar = 0.0f;
            Vector3 mts = _props.GetVec(VehVectorParam.AngularMotorTimescale);
            if (Vector3.Mag(mts) < 0.9f)
                moahfubar = 1.0f - Vector3.Mag(mts);
            _props.Dynamics.AngularDecayIndex = LegionVehicleLimits.MinPhysicsTimestep * (1.0f - moahfubar) - LegionVehicleLimits.ThresholdDelayFubar * moahfubar;

            // SL angular deflection rate limiter (old havok1 bug, institutionalized)
            float ts = _props.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f);
            float de = _props.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
            float sl = (ts + 0.001f) / (de + 0.00001f);
            if (sl < 1.0f)
            {
                direction *= Math.Max(sl, 0.1f);
            }

            _props.Dynamics.TargetAngularDelta = direction - _props.Dynamics.AngularDirection;
            _props.Dynamics.AngularDirection = direction;
            _body.KeepAwake();
        }

        /// <summary>
        /// Check if motors need reset due to stale timestamp.
        /// Returns actual elapsed time.
        /// </summary>
        private float CheckResetMotors(float timeStep)
        {
            DateTime now = DateTime.Now;
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
            _props.Dynamics.LastAccessTOD = DateTime.Now;
            _props.Dynamics.LastPosition = _body.Position;

            _props.Dynamics.LocalLinearVelocity = _localLinearVel;
            _props.Dynamics.LocalAngularVelocity = _localAngularVel;

            _props.Dynamics.LastVerticalAngle = 0.0f;
            _props.Dynamics.VerticalForceAdjust = 1.0f;

            // Motors off
            _props.Dynamics.LinearDecayIndex = LegionVehicleLimits.MaxDecayTimescale * 10.0f;
            _props.Dynamics.AngularDecayIndex = LegionVehicleLimits.MaxDecayTimescale * 10.0f;
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
                if (_props.Dynamics.LinearDecayIndex > LegionVehicleLimits.ThresholdLinearMotorEngaged &&
                    _props.Dynamics.AngularDecayIndex > LegionVehicleLimits.ThresholdAngularMotorEngaged)
                {
                    float stposdelta = Vector3.Mag(_props.Dynamics.ShortTermPositionDelta);
                    float stspeed = stposdelta / timeStep;

                    if (stspeed < currspeed * 0.5f)
                    {
                        if (currspeed >= LegionVehicleLimits.ThresholdLinearMotorUnstuck)
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

        private void ApplyGravity(float timeStep)
        {
            float buoyancy = _props.GetFloat(VehFloatParam.Buoyancy, 0f);
            Vector3 gravity = _body.Gravity * (1f - buoyancy);   // ComputeGravity(buoyancy), GravModifier = 1
            Vector3 appliedGravity = gravity * m_vehicleMass;

            // Reduce downward force if vehicle is sitting on ground
            if (IsGroundVehicle && _body.HasCollision)
                appliedGravity *= VehicleGroundGravityFudge;

            // Apply as a force
            _body.AddForce(appliedGravity);
        }

        #endregion

        #region Torque Accumulator (Halcyon pattern)

        private void TorqueInit()
        {
            _accumTorqueVelChange = Vector3.Zero;
            _accumTorqueImpulse = Vector3.Zero;
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
            if (Math.Abs(_accumTorqueVelChange.X) < LegionVehicleLimits.MinPhysicsForce) _accumTorqueVelChange.X = 0f;
            if (Math.Abs(_accumTorqueVelChange.Y) < LegionVehicleLimits.MinPhysicsForce) _accumTorqueVelChange.Y = 0f;
            if (Math.Abs(_accumTorqueVelChange.Z) < LegionVehicleLimits.MinPhysicsForce) _accumTorqueVelChange.Z = 0f;

            if (Math.Abs(_accumTorqueImpulse.X) < LegionVehicleLimits.MinPhysicsForce) _accumTorqueImpulse.X = 0f;
            if (Math.Abs(_accumTorqueImpulse.Y) < LegionVehicleLimits.MinPhysicsForce) _accumTorqueImpulse.Y = 0f;
            if (Math.Abs(_accumTorqueImpulse.Z) < LegionVehicleLimits.MinPhysicsForce) _accumTorqueImpulse.Z = 0f;

            // Apply velocity change torques (direct angular velocity modification)
            if (_accumTorqueVelChange != Vector3.Zero)
            {
                _body.AngularVelocity = _body.AngularVelocity + _accumTorqueVelChange;
            }

            // Apply impulse torques (mass-scaled)
            if (_accumTorqueImpulse != Vector3.Zero)
            {
                _body.AddTorque(_accumTorqueImpulse);
            }

            // (Reference calls PE.PushUpdate here when changed - a Bullet activation nudge; the host
            // keeps an active vehicle body from sleeping instead.)
        }

        #endregion // Torque Accumulator

        #region Adapter Methods — Physics Engine Interface

        /// <summary>
        /// Apply an instant linear velocity change (mass-independent).
        /// Equivalent to Halcyon AddForce with ForceMode.VelocityChange.
        /// </summary>
        private void ApplyLinearVelocityChange(Vector3 deltaV)
        {
            // The reference branches on LinearMotorOffset here, routing the offset case through a
            // CENTRAL impulse of deltaV * mass - the identical net velocity change (true off-center
            // application is a later slice; every stock preset ships offset = zero).
            _body.LinearVelocity = _body.LinearVelocity + deltaV;
        }

        #endregion // Adapter Methods

        #region Spike Detection

        /// <summary>
        /// Detect and suppress velocity spikes from physics engine.
        /// Ported from Halcyon VehicleDynamics.MitigatePhysxSpiking().
        /// </summary>
        private void MitigateVelocitySpiking(float actualStep)
        {
            // Angular spike detection
            Vector3 xyrot = QuatToEuler(_rotation);
            xyrot.Z = 0;
            float angle = QuatToAngle(Quaternion.CreateFromEulers(xyrot));

            // Diminish spike detection as vehicle goes off X or Y axis
            float accel = (_localAngularVel.Y - _props.Dynamics.LocalAngularVelocity.Y) / actualStep;
            accel = accel * (float)((Math.PI - angle) / Math.PI);

            if (Math.Abs(accel) > 100.0f)
            {
                Vector3 remvel = _body.AngularVelocity * Quaternion.Inverse(_rotation);
                remvel.Z = 0;
                remvel *= _rotation;
                AddTorqueVelocityChange(-remvel);
            }

            // Linear spike detection — only interested in positive (up) accelerations
            accel = (_localLinearVel.Z - _props.Dynamics.LocalLinearVelocity.Z) / actualStep;
            if (accel > 50.0f)
            {
                Vector3 remvel = _body.LinearVelocity * Quaternion.Inverse(_rotation);
                remvel.X = 0;
                remvel.Y *= 0.5f;
                remvel *= _rotation;
                ApplyLinearVelocityChange(-remvel);
            }
        }

        #endregion

        #region Deflection (Angular + Linear) + Sled movement (gated Type==Sled)

        /// <summary>
        /// Rotates vehicle toward direction of movement (weathervane: swings the NOSE toward the velocity,
        /// complementary to linear deflection which rotates the velocity toward the nose).
        /// Ported VERBATIM from the BulletSim reference (LegionVehicleDynamics.SimulateAngularDeflection).
        /// Seam table: all symbols map 1:1 here - _worldLinearVel/_localLinearVel/_worldAngularVel/_rotation,
        /// the limits, the QuatToEuler/RotBetween/AngleBetween utilities, and AddTorqueVelocityChange (the
        /// reference's torque-as-velocity-change seam) already exist on this controller.
        /// </summary>
        private void SimulateAngularDeflection(float timeStep)
        {
            if (Math.Abs(_worldLinearVel.X) >= LegionVehicleLimits.ThresholdDeflectionSpeed ||
                Math.Abs(_worldLinearVel.Y) >= LegionVehicleLimits.ThresholdDeflectionSpeed ||
                Math.Abs(_worldLinearVel.Z) >= LegionVehicleLimits.ThresholdDeflectionSpeed)
            {
                float timescale = Math.Max(_props.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f), timeStep);

                if (timescale < LegionVehicleLimits.MaxTimescale)
                {
                    float timepct = timeStep / timescale;
                    float efficiency = _props.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
                    float speed = Utils.Clamp(Vector3.Mag(_localLinearVel), 0, LegionVehicleLimits.MaxLegacyLinearVelocity);
                    float speedpct = speed / LegionVehicleLimits.MaxLegacyLinearVelocity;

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
                    if (angle < LegionVehicleLimits.ThresholdDeflectionAngle)
                    {
                        remvel = _worldAngularVel * timepct * efficiency * (float)(Math.Log(1.0 + Math.PI - angle) / Math.Log(1.0 + Math.PI));
                    }

                    vtwix -= remvel;

                    if (IsLinearMotorStalled())
                    {
                        vtwix = Vector3.Zero;
                    }

                    if (Math.Abs(vtwix.X) >= LegionVehicleLimits.ThresholdAngularMotorDeltaV ||
                        Math.Abs(vtwix.Y) >= LegionVehicleLimits.ThresholdAngularMotorDeltaV ||
                        Math.Abs(vtwix.Z) >= LegionVehicleLimits.ThresholdAngularMotorDeltaV)
                    {
                        AddTorqueVelocityChange(vtwix);
                    }
                }
            }
        }

        /// <summary>
        /// Changes velocity toward forward axis.
        /// Ported VERBATIM from the BulletSim reference (LegionVehicleDynamics.SimulateLinearDeflection),
        /// itself the Halcyon port. Seam table: reference symbols map 1:1 here - _worldLinearVel, _rotation,
        /// _props, the limits, and ApplyLinearVelocityChange are the same names on this controller, so no
        /// substitution is needed inside the body (ApplyLinearVelocityChange already writes _body.LinearVelocity).
        /// </summary>
        private void SimulateLinearDeflection(float timeStep)
        {
            if (Math.Abs(_worldLinearVel.X) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV ||
                Math.Abs(_worldLinearVel.Y) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV ||
                Math.Abs(_worldLinearVel.Z) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV)
            {
                float timescale = Math.Max(_props.GetFloat(VehFloatParam.LinearDeflectionTimescale, 1000f), timeStep);

                if (timescale < LegionVehicleLimits.MaxTimescale)
                {
                    float timePct = timeStep / timescale;
                    float efficiency = _props.GetFloat(VehFloatParam.LinearDeflectionEfficiency, 0f);

                    // Determine the amount of velocity to shift
                  // SL behavior: linear deflection rotates the velocity vector toward
                    // the forward axis, preserving speed. This is what generates lift —
                    // when pitched up, horizontal velocity is redirected upward along
                    // the forward axis.
                    float speed = Vector3.Mag(_worldLinearVel);
                    if (speed < LegionVehicleLimits.ThresholdDeflectionSpeed) return;

                    Vector3 currentDir = Vector3.Normalize(_worldLinearVel);
                    Vector3 forwardDir = new Vector3(1, 0, 0) * _rotation;

                    // Blend current direction toward forward direction
                    float blend = Math.Min(timePct * efficiency, 1.0f);
                    Vector3 newDir = Vector3.Normalize(currentDir * (1.0f - blend) + forwardDir * blend);

                    // New velocity = same speed, redirected direction
                    Vector3 worldvel = newDir * speed - _worldLinearVel;

                    // Stop any upward deflection
                    if ((_props.Flags & LegionVehicleFlags.NoDeflectionUp) != 0)
                    {
                        if (worldvel.Z > 0) worldvel.Z = 0;
                    }

                    if (Math.Abs(worldvel.X) > LegionVehicleLimits.ThresholdLinearMotorDeltaV ||
                        Math.Abs(worldvel.Y) > LegionVehicleLimits.ThresholdLinearMotorDeltaV ||
                        Math.Abs(worldvel.Z) > LegionVehicleLimits.ThresholdLinearMotorDeltaV)
                    {
                        ApplyLinearVelocityChange(worldvel);
                    }
                }
            }
        }

        /// <summary>
        /// Gravity-assisted force on slopes for the SLED vehicle type. Ported VERBATIM from the BulletSim
        /// reference (LegionVehicleDynamics.SimulateSledMovement). Wired here for A/B PARITY COMPLETENESS
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
            if (Math.Abs(probe.Z) > LegionVehicleLimits.ThresholdDeflectionAngle)
            {
                force = new Vector3(-_body.Gravity.Z * 3.0f, 0f, 0f);   // seam: BSParam.Gravity -> _body.Gravity.Z

                // The sled has a lower force assist going backwards
                if (probe.Z > 0)
                    force *= -0.1f;

                if (!IsLinearMotorStalled())
                {
                    // Modulate the force based on the amount of declination
                    force = force * timeStep * (float)Math.Sqrt(Math.Abs(probe.Z));

                    if (Math.Abs(force.X) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV ||
                        Math.Abs(force.Y) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV ||
                        Math.Abs(force.Z) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV)
                    {
                        force *= _rotation;
                        force *= m_vehicleMass;
                        _body.AddForce(force);   // seam: ApplyLinearForce(f) -> _body.AddForce(f)
                    }
                }
            }
        }

        #endregion

        #region Hover

        /// <summary>
        /// Hover simulation — maintains vehicle at a target height above terrain, water, or global.
        /// Modeled after Halcyon hover behavior and SL vehicle spec.
        /// </summary>
        private void SimulateHover(float timeStep)
        {
            float hoverHeight = _props.GetFloat(VehFloatParam.HoverHeight, 0f);
            float hoverEfficiency = _props.GetFloat(VehFloatParam.HoverEfficiency, 0f);
            float hoverTimescale = _props.GetFloat(VehFloatParam.HoverTimescale, 1000f);

            // If timescale is effectively disabled, skip
            if (hoverTimescale >= LegionVehicleLimits.MaxHoverTimescale)
                return;

            Vector3 pos = _body.Position;
            float currentZ = pos.Z;
            float targetBase;

            // Determine the base height based on hover flags
            if ((_props.Flags & LegionVehicleFlags.HoverWaterOnly) != 0)
            {
                targetBase = _body.GetWaterLevel(pos);
            }
            else if ((_props.Flags & LegionVehicleFlags.HoverTerrainOnly) != 0)
            {
                targetBase = _body.GetTerrainHeight(pos);
            }
            else if ((_props.Flags & LegionVehicleFlags.HoverGlobalHeight) != 0)
            {
                targetBase = 0f; // Global = absolute height, hover height is the target
            }
            else
            {
                // No hover flag set — use max of terrain and water (SL default behavior)
                targetBase = Math.Max(_body.GetTerrainHeight(pos), _body.GetWaterLevel(pos));
            }

            float targetZ = targetBase + hoverHeight;

            // HoverUpOnly — only push up, never pull down
            if ((_props.Flags & LegionVehicleFlags.HoverUpOnly) != 0)
            {
                if (currentZ >= targetZ)
                    return;
            }

            float error = targetZ - currentZ;

            // If we're close enough, don't bother
            if (Math.Abs(error) < 0.01f)
                return;

            // Compute correction velocity
            // Higher efficiency = snappier response, lower = smoother
            float correctionVel = (error / hoverTimescale) * (1f + hoverEfficiency * 2f);

            // Dampen current vertical velocity to prevent oscillation
            float currentVertVel = _worldLinearVel.Z;
            float dampingFactor = 1f - (hoverEfficiency * 0.5f);
            float dampedVel = correctionVel - (currentVertVel * dampingFactor);

            // Apply as a velocity change in world Z
            Vector3 hoverForce = new Vector3(0f, 0f, dampedVel);
            ApplyLinearVelocityChange(hoverForce);
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

            if (timescale < LegionVehicleLimits.MaxAttractTimescale)
            {
                // Compute the X and Y axis deflection from vertical
                Vector3 xrot = new Vector3(1, 0, 0) * _rotation;
                Vector3 yrot = new Vector3(0, 1, 0) * _rotation;
                Vector3 xyrot = QuatToEuler(_rotation);

                xyrot.Z = 0;
                angle = Math.Abs(QuatToAngle(Quaternion.CreateFromEulers(xyrot)));

                // Airplanes can fly inverted
                if (_props.Type == LegionVehicleType.Airplane)
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
                if (Math.Abs(_props.Dynamics.LastVerticalAngle - angle) >= LegionVehicleLimits.ThresholdAttractorAngle)
                {
                    _props.Dynamics.LastVerticalFrameNumber = frameNum;
                }

                if (angle >= LegionVehicleLimits.ThresholdOverturnAngle ||
                    (frameNum - _props.Dynamics.LastVerticalFrameNumber) < (LegionVehicleLimits.MaxAttractDormancy / timeStep))
                {
                    // Compute restoration force in local coordinates
                    Vector3 vtwix = new Vector3(-yrot.Z, xrot.Z, 0);

                    // Different vehicles have different characteristics
                    vtwix = vtwix * (float)Math.PI * (float)Math.Pow(Math.E, efficiency * 3.0);

                    // Non-airplane vehicles have very strong restorative forces
                    if (_props.Type != LegionVehicleType.Airplane)
                        vtwix *= (1.0f + (float)Math.Pow(1.0 + apct, 4.0));

                    // Zero out y-axis rotation if the limit roll only flag is set
                    if ((_props.Flags & LegionVehicleFlags.LimitRollOnly) != 0)
                    {
                        if (_props.Type == LegionVehicleType.Airplane || _props.Type == LegionVehicleType.Balloon)
                            vtwix.Y = 0.0f;
                        else
                            vtwix.Y *= 0.1f;
                    }

                    // If overturned and no progress toward vertical, keep increasing force
                    if (_props.Type != LegionVehicleType.Airplane)
                    {
                        if (angle >= LegionVehicleLimits.ThresholdOverturnAngle && angle >= _props.Dynamics.LastVerticalAngle)
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
        /// Converts roll to yaw rotation. Ported VERBATIM from the BulletSim reference
        /// (LegionVehicleDynamics.SimulateBankingToYaw). Runs AFTER the vertical attractor (whose angle/
        /// inverted it takes) and BEFORE SimulateMotors: it sets Dynamics.BankingDirection, which the
        /// already-present banking-turn block inside SimulateMotors consumes and blends into the angular-Z
        /// torque. Seam table: _localLinearVel/_rotation, the limits, Utils.Clamp and _props.Dynamics are
        /// the same names here, so the body is unchanged.
        /// </summary>
        private void SimulateBankingToYaw(float timeStep, float angle, bool inverted)
        {
            float timescale = Math.Max(_props.GetFloat(VehFloatParam.BankingTimescale, 1000f), timeStep);

            if (timescale < LegionVehicleLimits.MaxAttractTimescale)
            {
                float efficiency = _props.GetFloat(VehFloatParam.BankingEfficiency, 0f);
                float bmodifier = _props.GetFloat(VehFloatParam.InvertedBankingModifier, 1f);

                if (LegionVehicleLimits.DoBanking && timescale < LegionVehicleLimits.MaxTimescale)
                {
                    float bankingmix = _props.GetFloat(VehFloatParam.BankingMix, 0.5f);
                    float xspeed = 0.0f;

                    // Legacy support: use velocity as an on/off switch, proportional and capped
                    if (Math.Abs(_localLinearVel.X) > LegionVehicleLimits.ThresholdAngularMotorDeltaV)
                        xspeed = Utils.Clamp(Math.Abs(_localLinearVel.X), 0, LegionVehicleLimits.MaxLegacyLinearVelocity);
                    float xspeedpct = xspeed / LegionVehicleLimits.MaxLegacyLinearVelocity;

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
                    if (Math.Abs(xangle) > LegionVehicleLimits.ThresholdBankAngle)
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

        #region Motor Simulation — Linear + Angular + Banking

        /// <summary>
        /// Main motor simulation entry point.
        /// Faithfully ported from Halcyon VehicleMotor.Simulate().
        /// </summary>
        private void SimulateMotors(float timeStep, uint frameNum, Vector3 aforces)
        {
            Vector3 rfactor;
            Vector3 dfactor;
            Vector3 lastvel;
            Vector3 adjvel;
            Vector3 newvel;
            Vector3 worldvel;
            Vector3 timescale;
            Vector3 dirsign;

            float buoy = _props.GetFloat(VehFloatParam.Buoyancy, 0f);
            float hoverts = _props.GetFloat(VehFloatParam.HoverTimescale, 1000f);

            // ================================================================
            // Linear Motor Simulation
            // ================================================================
            lastvel = _localLinearVel;
            adjvel = lastvel;

            dirsign.X = VehicleMath.PosNeg(_props.Dynamics.LinearDirection.X);
            dirsign.Y = VehicleMath.PosNeg(_props.Dynamics.LinearDirection.Y);
            dirsign.Z = VehicleMath.PosNeg(_props.Dynamics.LinearDirection.Z);

            // Target velocity ensures forward progress against friction
            if (dirsign.X * (_props.Dynamics.LinearTargetVelocity.X - adjvel.X) >= 0) adjvel.X = _props.Dynamics.LinearTargetVelocity.X;
            if (dirsign.Y * (_props.Dynamics.LinearTargetVelocity.Y - adjvel.Y) >= 0) adjvel.Y = _props.Dynamics.LinearTargetVelocity.Y;
            if (dirsign.Z * (_props.Dynamics.LinearTargetVelocity.Z - adjvel.Z) >= 0) adjvel.Z = _props.Dynamics.LinearTargetVelocity.Z;

            // Compute rampup factors
            timescale = _props.GetVec(VehVectorParam.LinearMotorTimescale);
            rfactor.X = GetGrowthRate(adjvel.X, _props.Dynamics.LinearDirection.X, timescale.X / timeStep);
            rfactor.Y = GetGrowthRate(adjvel.Y, _props.Dynamics.LinearDirection.Y, timescale.Y / timeStep);
            rfactor.Z = GetGrowthRate(adjvel.Z, _props.Dynamics.LinearDirection.Z, timescale.Z / timeStep);

            // Compute decay factor then step to next index
            timescale = _props.GetVec(VehVectorParam.LinearMotorDecayTimescale);
            dfactor.X = MovementExpDecay(_props.Dynamics.LinearDecayIndex, timescale.X);
            dfactor.Y = MovementExpDecay(_props.Dynamics.LinearDecayIndex, timescale.Y);
            dfactor.Z = MovementExpDecay(_props.Dynamics.LinearDecayIndex, timescale.Z);
            _props.Dynamics.LinearDecayIndex += timeStep;

            // If decay hits maximum entropy, no further computation required
            if (Vector3.Mag(dfactor) == 0)
            {
                _props.Dynamics.LinearTargetVelocity = _localLinearVel;
            }
            else
            {
                // Stepwise iteration of a*b^(t*Tau)
                newvel.X = (adjvel.X + adjvel.X * rfactor.X);
                newvel.Y = (adjvel.Y + adjvel.Y * rfactor.Y);
                newvel.Z = (adjvel.Z + adjvel.Z * rfactor.Z);

                // Crossover flip
                if (rfactor.X == LegionVehicleLimits.ThresholdInverseCrossover) rfactor.X = -rfactor.X;
                if (rfactor.Y == LegionVehicleLimits.ThresholdInverseCrossover) rfactor.Y = -rfactor.Y;
                if (rfactor.Z == LegionVehicleLimits.ThresholdInverseCrossover) rfactor.Z = -rfactor.Z;

                // Avoid zero velocity stiction when increasing
                if (rfactor.X > 0 && _props.Dynamics.LinearDirection.X != 0 && Math.Abs(newvel.X) < LegionVehicleLimits.ThresholdLinearMotorDeltaV)
                    newvel.X = dirsign.X * LegionVehicleLimits.ThresholdLinearMotorDeltaV * 8;
                if (rfactor.Y > 0 && _props.Dynamics.LinearDirection.Y != 0 && Math.Abs(newvel.Y) < LegionVehicleLimits.ThresholdLinearMotorDeltaV)
                    newvel.Y = dirsign.Y * LegionVehicleLimits.ThresholdLinearMotorDeltaV * 8;
                if (rfactor.Z > 0 && _props.Dynamics.LinearDirection.Z != 0 && Math.Abs(newvel.Z) < LegionVehicleLimits.ThresholdLinearMotorDeltaV)
                    newvel.Z = dirsign.Z * LegionVehicleLimits.ThresholdLinearMotorDeltaV * 8;

                // Compute new target velocities
                if (_props.Dynamics.LinearDirection.X * newvel.X < 0 || rfactor.X < 0)
                    _props.Dynamics.LinearTargetVelocity.X = newvel.X;
                else
                    _props.Dynamics.LinearTargetVelocity.X = Utils.Clamp(newvel.X, -Math.Abs(_props.Dynamics.LinearDirection.X), Math.Abs(_props.Dynamics.LinearDirection.X));

                if (_props.Dynamics.LinearDirection.Y * newvel.Y < 0 || rfactor.Y < 0)
                    _props.Dynamics.LinearTargetVelocity.Y = newvel.Y;
                else
                    _props.Dynamics.LinearTargetVelocity.Y = Utils.Clamp(newvel.Y, -Math.Abs(_props.Dynamics.LinearDirection.Y), Math.Abs(_props.Dynamics.LinearDirection.Y));

                if (_props.Dynamics.LinearDirection.Z * newvel.Z < 0 || rfactor.Z < 0)
                    _props.Dynamics.LinearTargetVelocity.Z = newvel.Z;
                else
                    _props.Dynamics.LinearTargetVelocity.Z = Utils.Clamp(newvel.Z, -Math.Abs(_props.Dynamics.LinearDirection.Z), Math.Abs(_props.Dynamics.LinearDirection.Z));

                // Limit max velocities
                newvel.X = Utils.Clamp(newvel.X, -LegionVehicleLimits.MaxLinearVelocity, LegionVehicleLimits.MaxLinearVelocity);
                newvel.Y = Utils.Clamp(newvel.Y, -LegionVehicleLimits.MaxLinearVelocity, LegionVehicleLimits.MaxLinearVelocity);
                newvel.Z = Utils.Clamp(newvel.Z, -LegionVehicleLimits.MaxLinearVelocity, LegionVehicleLimits.MaxLinearVelocity);

                // Release motors when delta and direction are both zero
                if (_props.Dynamics.LinearTargetVelocity.X == 0 && _props.Dynamics.LinearDirection.X == 0) newvel.X = lastvel.X;
                if (_props.Dynamics.LinearTargetVelocity.Y == 0 && _props.Dynamics.LinearDirection.Y == 0) newvel.Y = lastvel.Y;
                if (_props.Dynamics.LinearTargetVelocity.Z == 0 && _props.Dynamics.LinearDirection.Z == 0) newvel.Z = lastvel.Z;

                // If local velocity exceeds motor speed, motor is not adding power
                if (rfactor.X >= 0 && (dirsign.X * (lastvel.X - newvel.X) > 0)) dfactor.X = 0;
                if (rfactor.Y >= 0 && (dirsign.Y * (lastvel.Y - newvel.Y) > 0)) dfactor.Y = 0;
                if (rfactor.Z >= 0 && (dirsign.Z * (lastvel.Z - newvel.Z) > 0)) dfactor.Z = 0;

                // Accelerate decay when stalled
                if (Vector3.Mag(dfactor) != 0 && IsLinearMotorStalled())
                {
                    dfactor = Vector3.Zero;
                    _props.Dynamics.LinearDecayIndex = LegionVehicleLimits.MaxDecayTimescale * 100.0f;
                    _props.Dynamics.LinearTargetVelocity = Vector3.Zero;
                }

                // Apply decays to each axis
                newvel.X = (newvel.X * dfactor.X) + lastvel.X * (1.0f - dfactor.X);
                newvel.Y = (newvel.Y * dfactor.Y) + lastvel.Y * (1.0f - dfactor.Y);
                newvel.Z = (newvel.Z * dfactor.Z) + lastvel.Z * (1.0f - dfactor.Z);

                // Switch back to world coords
                worldvel = newvel * _rotation;

                if (Math.Abs(worldvel.X) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV ||
                    Math.Abs(worldvel.Y) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV ||
                    Math.Abs(worldvel.Z) >= LegionVehicleLimits.ThresholdLinearMotorDeltaV)
                {
                    // Convert to deltaV
                    worldvel -= _worldLinearVel;

                    // Limit motor up
                    if ((_props.Flags & LegionVehicleFlags.LimitMotorUp) != 0)
                    {
                        if (worldvel.Z > 0) worldvel.Z = 0;
                    }

                    // Limit motor down
                    if ((_props.Flags & LegionVehicleFlags.LimitMotorDown) != 0)
                    {
                        if (worldvel.Z < 0) worldvel.Z = 0;
                    }

                    // Gravity check: up Z-forces less than effective gravity are nulled
                    if (worldvel.Z > 0)
                    {
                        worldvel.Z += _body.Gravity.Z * (1.0f - buoy);
                        if (worldvel.Z < 0) worldvel.Z = 0;
                    }

                    // Hover moderation: if hover present, accelerate motor decay
                    if (Math.Abs(worldvel.Z) > LegionVehicleLimits.ThresholdLinearMotorDeltaV &&
                        (_props.Flags & (LegionVehicleFlags.HoverGlobalHeight | LegionVehicleFlags.HoverTerrainOnly | LegionVehicleFlags.HoverWaterOnly)) != 0 &&
                        hoverts < LegionVehicleLimits.MaxHoverTimescale)
                    {
                        _props.Dynamics.LinearDecayIndex += 10.0f;
                    }

                    ApplyLinearVelocityChange(worldvel);
                }
            }

            // ================================================================
            // Angular Motor Simulation
            // ================================================================
            worldvel = Vector3.Zero;
            float angularz = 0;

            // Compute decay factor
            timescale = _props.GetVec(VehVectorParam.AngularMotorDecayTimescale);
            dfactor.X = MovementExpDecay(_props.Dynamics.AngularDecayIndex, timescale.X);
            dfactor.Y = MovementExpDecay(_props.Dynamics.AngularDecayIndex, timescale.Y);
            dfactor.Z = MovementExpDecay(_props.Dynamics.AngularDecayIndex, timescale.Z);
            _props.Dynamics.AngularDecayIndex += timeStep;

            if (Vector3.Mag(dfactor) == 0)
            {
                _props.Dynamics.AngularTargetVelocity = Vector3.Zero;
            }
            else
            {
                lastvel = _localAngularVel;
                Vector3 ztorque = Vector3.Zero;

                // Preflight world z-rotation mode
                if ((_props.Flags & LegionVehicleFlags.TorqueWorldZ) != 0)
                {
                    lastvel = _worldAngularVel;
                    ztorque.Z = lastvel.Z;
                    lastvel.Z = 0;
                    lastvel = lastvel * Quaternion.Inverse(_rotation);

                    // Save velocity rotated into local Z-axis, replace Z with world Z
                    ztorque.X = lastvel.Z;
                    lastvel.Z = ztorque.Z;
                }

                adjvel = lastvel;

                dirsign.X = VehicleMath.PosNeg(_props.Dynamics.AngularDirection.X);
                dirsign.Y = VehicleMath.PosNeg(_props.Dynamics.AngularDirection.Y);
                dirsign.Z = VehicleMath.PosNeg(_props.Dynamics.AngularDirection.Z);

                // Target velocity ensures forward progress
                if (dirsign.X * (_props.Dynamics.AngularTargetVelocity.X - adjvel.X) > 0) adjvel.X = _props.Dynamics.AngularTargetVelocity.X;
                if (dirsign.Y * (_props.Dynamics.AngularTargetVelocity.Y - adjvel.Y) > 0) adjvel.Y = _props.Dynamics.AngularTargetVelocity.Y;
                if (dirsign.Z * (_props.Dynamics.AngularTargetVelocity.Z - adjvel.Z) > 0) adjvel.Z = _props.Dynamics.AngularTargetVelocity.Z;

                // Compute rampup factors
                timescale = _props.GetVec(VehVectorParam.AngularMotorTimescale);
                rfactor.X = GetGrowthRate(adjvel.X, _props.Dynamics.AngularDirection.X, timescale.X / timeStep);
                rfactor.Y = GetGrowthRate(adjvel.Y, _props.Dynamics.AngularDirection.Y, timescale.Y / timeStep);
                rfactor.Z = GetGrowthRate(adjvel.Z, _props.Dynamics.AngularDirection.Z, timescale.Z / timeStep);

                // Stepwise iteration
                newvel.X = (adjvel.X + adjvel.X * rfactor.X);
                newvel.Y = (adjvel.Y + adjvel.Y * rfactor.Y);
                newvel.Z = (adjvel.Z + adjvel.Z * rfactor.Z);

                // Crossover flip
                if (rfactor.X == LegionVehicleLimits.ThresholdInverseCrossover) rfactor.X = -rfactor.X;
                if (rfactor.Y == LegionVehicleLimits.ThresholdInverseCrossover) rfactor.Y = -rfactor.Y;
                if (rfactor.Z == LegionVehicleLimits.ThresholdInverseCrossover) rfactor.Z = -rfactor.Z;

                // Avoid zero velocity stiction
                if (rfactor.X > 0 && _props.Dynamics.AngularDirection.X != 0 && Math.Abs(newvel.X) < LegionVehicleLimits.ThresholdAngularMotorDeltaV)
                    newvel.X = dirsign.X * LegionVehicleLimits.ThresholdAngularMotorDeltaV * 8;
                if (rfactor.Y > 0 && _props.Dynamics.AngularDirection.Y != 0 && Math.Abs(newvel.Y) < LegionVehicleLimits.ThresholdAngularMotorDeltaV)
                    newvel.Y = dirsign.Y * LegionVehicleLimits.ThresholdAngularMotorDeltaV * 8;
                if (rfactor.Z > 0 && _props.Dynamics.AngularDirection.Z != 0 && Math.Abs(newvel.Z) < LegionVehicleLimits.ThresholdAngularMotorDeltaV)
                    newvel.Z = dirsign.Z * LegionVehicleLimits.ThresholdAngularMotorDeltaV * 8;

                // --- Vertical attractor interaction clamping ---
                float vtimescale = Math.Max(_props.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f), timeStep);
                float vefficiency = _props.GetFloat(VehFloatParam.VerticalAttractionEfficiency, 0f);
                if (vtimescale < LegionVehicleLimits.MaxAttractTimescale)
                {
                    float velclamp = (float)Math.PI;

                    if (_props.Type == LegionVehicleType.Car)
                        velclamp = (float)Math.PI * 1.1f;
                    else if (_props.Type == LegionVehicleType.Boat)
                        velclamp = (float)Math.PI * 0.95f;
                    else
                        velclamp = (float)Math.PI * 0.8f;

                    if (vtimescale < 1.0f)
                        velclamp = velclamp * vtimescale;

                    if (vtimescale < 1.0f)
                        vtimescale = 1.0f;
                    float voverthrust = (float)(Math.Log(vtimescale + 0.06) / Math.Log(LegionVehicleLimits.MaxAttractTimescale));

                    float vvel;
                    vvel = Utils.Clamp(newvel.X, -velclamp, velclamp);
                    if (newvel.X != vvel) vvel = vvel + (newvel.X - vvel) * voverthrust;
                    newvel.X = vvel;

                    vvel = Utils.Clamp(newvel.Y, -velclamp, velclamp);
                    if (newvel.Y != vvel) vvel = vvel + (newvel.Y - vvel) * voverthrust;
                    newvel.Y = vvel;
                }

                // Compute new target velocities
                if (_props.Dynamics.AngularDirection.X * newvel.X < 0 || rfactor.X < 0)
                    _props.Dynamics.AngularTargetVelocity.X = newvel.X;
                else
                    _props.Dynamics.AngularTargetVelocity.X = Utils.Clamp(newvel.X, -Math.Abs(_props.Dynamics.AngularDirection.X), Math.Abs(_props.Dynamics.AngularDirection.X));

                if (_props.Dynamics.AngularDirection.Y * newvel.Y < 0 || rfactor.Y < 0)
                    _props.Dynamics.AngularTargetVelocity.Y = newvel.Y;
                else
                    _props.Dynamics.AngularTargetVelocity.Y = Utils.Clamp(newvel.Y, -Math.Abs(_props.Dynamics.AngularDirection.Y), Math.Abs(_props.Dynamics.AngularDirection.Y));

                if (_props.Dynamics.AngularDirection.Z * newvel.Z < 0 || rfactor.Z < 0)
                    _props.Dynamics.AngularTargetVelocity.Z = newvel.Z;
                else
                    _props.Dynamics.AngularTargetVelocity.Z = Utils.Clamp(newvel.Z, -Math.Abs(_props.Dynamics.AngularDirection.Z), Math.Abs(_props.Dynamics.AngularDirection.Z));

                // Limit max velocities
                newvel.X = Utils.Clamp(newvel.X, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);
                newvel.Y = Utils.Clamp(newvel.Y, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);
                newvel.Z = Utils.Clamp(newvel.Z, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);

                // Release motors when delta is zero and direction is zero
                if (dfactor.X == 0 && _props.Dynamics.AngularDirection.X == 0) newvel.X = lastvel.X;
                if (dfactor.Y == 0 && _props.Dynamics.AngularDirection.Y == 0) newvel.Y = lastvel.Y;
                if (dfactor.Z == 0 && _props.Dynamics.AngularDirection.Z == 0) newvel.Z = lastvel.Z;

                // If local velocity exceeds motor speed, motor is not adding power
                // Also factor in vertical attractor forces
                if ((aforces.X != 0 && _props.Dynamics.AngularDirection.X == 0) || (rfactor.X >= 0 && (dirsign.X * (lastvel.X - newvel.X) > 0))) dfactor.X = 0;
                if ((aforces.Y != 0 && _props.Dynamics.AngularDirection.Y == 0) || (rfactor.Y >= 0 && (dirsign.Y * (lastvel.Y - newvel.Y) > 0))) dfactor.Y = 0;
                if ((aforces.Z != 0 && _props.Dynamics.AngularDirection.Z == 0) || (rfactor.Z >= 0 && (dirsign.Z * (lastvel.Z - newvel.Z) > 0))) dfactor.Z = 0;

                // Accelerate angular decay when linear motor stalled
                if (Vector3.Mag(dfactor) != 0 && IsLinearMotorStalled())
                {
                    dfactor = Vector3.Zero;
                    _props.Dynamics.AngularDecayIndex = LegionVehicleLimits.MaxDecayTimescale * 100.0f;
                    _props.Dynamics.AngularTargetVelocity = Vector3.Zero;
                }

                // Apply decays to each axis
                newvel.X = (newvel.X * dfactor.X) + lastvel.X * (1.0f - dfactor.X);
                newvel.Y = (newvel.Y * dfactor.Y) + lastvel.Y * (1.0f - dfactor.Y);
                newvel.Z = (newvel.Z * dfactor.Z) + lastvel.Z * (1.0f - dfactor.Z);

                // Handle TorqueWorldZ split
                if ((_props.Flags & LegionVehicleFlags.TorqueWorldZ) != 0)
                {
                    ztorque.Z = newvel.Z;   // This is really world Z-forces
                    newvel.Z = ztorque.X;   // Restore original local Z-forces
                    newvel.Z = 0;
                    ztorque.X = 0;

                    worldvel = newvel * _rotation;
                    worldvel += ztorque;
                }
                else
                {
                    worldvel = newvel * _rotation;
                }

                // Convert to deltaV
                worldvel -= _worldAngularVel;

                // Save Z forces until after banking has been determined
                angularz = worldvel.Z;
                worldvel.Z = 0;

                if (Math.Abs(worldvel.X) >= LegionVehicleLimits.ThresholdAngularMotorDeltaV ||
                    Math.Abs(worldvel.Y) >= LegionVehicleLimits.ThresholdAngularMotorDeltaV ||
                    Math.Abs(worldvel.Z) >= LegionVehicleLimits.ThresholdAngularMotorDeltaV)
                {
                    AddTorqueVelocityChange(worldvel);
                }
            }

            // ================================================================
            // Banking Turn Motor Simulation
            // ================================================================
            float btimescale = _props.GetFloat(VehFloatParam.BankingTimescale, 1000f);
            float bnewvel = 0;

            if (_props.Dynamics.BankingDirection != 0 && btimescale < LegionVehicleLimits.MaxTimescale)
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
                bfactor = GetGrowthRate(badjvel, _props.Dynamics.BankingDirection, btimescale / timeStep);
                bnewvel = (badjvel + badjvel * bfactor);

                // If angular motor is engaged, reduce max banking velocity
                if (_props.Dynamics.AngularDecayIndex < LegionVehicleLimits.ThresholdAngularMotorEngaged)
                {
                    if (Math.Abs(bnewvel) > LegionVehicleLimits.MaxLegacyAngularVelocity)
                        bnewvel = LegionVehicleLimits.MaxLegacyAngularVelocity * VehicleMath.PosNeg(bnewvel);
                }

                // Crossover flip
                if (bfactor == LegionVehicleLimits.ThresholdInverseCrossover) bfactor = -bfactor;

                // Avoid zero velocity stiction
                if (bfactor > 0 && Math.Abs(bnewvel) < LegionVehicleLimits.ThresholdAngularMotorDeltaV)
                    bnewvel = dirbsign * LegionVehicleLimits.ThresholdAngularMotorDeltaV * 8;

                // Compute new target banking velocity
                if (_props.Dynamics.BankingDirection * bnewvel < 0 || bfactor < 0)
                    _props.Dynamics.BankingTargetVelocity = bnewvel;
                else
                    _props.Dynamics.BankingTargetVelocity = Utils.Clamp(bnewvel, -Math.Abs(_props.Dynamics.BankingDirection), Math.Abs(_props.Dynamics.BankingDirection));

                // Limit max velocities
                bnewvel = Utils.Clamp(bnewvel, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);

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

            // Blend angular Z and banking forces
            worldvel = Vector3.Zero;
            worldvel.Z = bnewvel + angularz;

            if (Math.Abs(worldvel.Z) >= LegionVehicleLimits.ThresholdAngularMotorDeltaV)
            {
                AddTorqueVelocityChange(worldvel);
            }
        }

        #endregion

        #region Friction — Angular & Linear

        /// <summary>
        /// Per-axis exponential angular velocity decay.
        /// Ported from Halcyon VehicleDynamics.SimulateAngularFriction().
        /// </summary>
        private void SimulateAngularFriction(float timeStep)
        {
            Vector3 newvel;
            Vector3 worldvel;
            Vector3 frictionTS = _props.GetVec(VehVectorParam.AngularFrictionTimescale);

            if (frictionTS.X < LegionVehicleLimits.MaxTimescale ||
                frictionTS.Y < LegionVehicleLimits.MaxTimescale ||
                frictionTS.Z < LegionVehicleLimits.MaxTimescale)
            {
                frictionTS.X = Math.Max(frictionTS.X, timeStep);
                frictionTS.Y = Math.Max(frictionTS.Y, timeStep);
                frictionTS.Z = Math.Max(frictionTS.Z, timeStep);

                // Normalize to one physics frame time
                frictionTS.X = MovementLimitedGrowth(timeStep, frictionTS.X);
                frictionTS.Y = MovementLimitedGrowth(timeStep, frictionTS.Y);
                frictionTS.Z = MovementLimitedGrowth(timeStep, frictionTS.Z);

                // Compute new target local deltaV
                newvel.X = -_localAngularVel.X * frictionTS.X;
                newvel.Y = -_localAngularVel.Y * frictionTS.Y;
                newvel.Z = -_localAngularVel.Z * frictionTS.Z;

                newvel.X = Utils.Clamp(newvel.X, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);
                newvel.Y = Utils.Clamp(newvel.Y, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);
                newvel.Z = Utils.Clamp(newvel.Z, -LegionVehicleLimits.MaxAngularVelocity, LegionVehicleLimits.MaxAngularVelocity);

                // Clean up when below minimum threshold
                if (Math.Abs(newvel.X) < LegionVehicleLimits.MinPhysicsForce) newvel.X = VehicleMath.PosNeg(newvel.X) * LegionVehicleLimits.MinPhysicsForce;
                if (Math.Abs(newvel.Y) < LegionVehicleLimits.MinPhysicsForce) newvel.Y = VehicleMath.PosNeg(newvel.Y) * LegionVehicleLimits.MinPhysicsForce;
                if (Math.Abs(newvel.Z) < LegionVehicleLimits.MinPhysicsForce) newvel.Z = VehicleMath.PosNeg(newvel.Z) * LegionVehicleLimits.MinPhysicsForce;

                worldvel = newvel * _rotation;

                // Apply only when above sleep threshold
                if (Math.Abs(_localAngularVel.X) >= LegionVehicleLimits.MinPhysicsForce * 2 ||
                    Math.Abs(_localAngularVel.Y) >= LegionVehicleLimits.MinPhysicsForce * 2 ||
                    Math.Abs(_localAngularVel.Z) >= LegionVehicleLimits.MinPhysicsForce * 2)
                {
                    if (Vector3.Mag(_worldAngularVel) < LegionVehicleLimits.ThresholdAngularFrictionDeltaV)
                    {
                        worldvel = -_worldAngularVel;
                    }

                    if (Vector3.Mag(worldvel) > 0)
                    {
                        AddTorqueVelocityChange(worldvel);
                    }
                }
            }
        }

        /// <summary>
        /// Per-axis exponential linear velocity decay.
        /// Ported from Halcyon VehicleDynamics.SimulateLinearFriction().
        /// </summary>
        private void SimulateLinearFriction(float timeStep)
        {
            Vector3 newvel;
            Vector3 worldvel;
            Vector3 frictionTS = _props.GetVec(VehVectorParam.LinearFrictionTimescale);

            if (frictionTS.X < LegionVehicleLimits.MaxTimescale ||
                frictionTS.Y < LegionVehicleLimits.MaxTimescale ||
                frictionTS.Z < LegionVehicleLimits.MaxTimescale)
            {
                frictionTS.X = Math.Max(frictionTS.X, timeStep);
                frictionTS.Y = Math.Max(frictionTS.Y, timeStep);
                frictionTS.Z = Math.Max(frictionTS.Z, timeStep);

                // Normalize to one physics frame time
                frictionTS.X = MovementLimitedGrowth(timeStep, frictionTS.X);
                frictionTS.Y = MovementLimitedGrowth(timeStep, frictionTS.Y);
                frictionTS.Z = MovementLimitedGrowth(timeStep, frictionTS.Z);

                // Compute new target local deltaV
                newvel.X = -_localLinearVel.X * frictionTS.X;
                newvel.Y = -_localLinearVel.Y * frictionTS.Y;
                newvel.Z = -_localLinearVel.Z * frictionTS.Z;

                // Clean up when below minimum threshold
                if (Math.Abs(newvel.X) < LegionVehicleLimits.MinPhysicsForce) newvel.X = VehicleMath.PosNeg(newvel.X) * LegionVehicleLimits.MinPhysicsForce;
                if (Math.Abs(newvel.Y) < LegionVehicleLimits.MinPhysicsForce) newvel.Y = VehicleMath.PosNeg(newvel.Y) * LegionVehicleLimits.MinPhysicsForce;
                if (Math.Abs(newvel.Z) < LegionVehicleLimits.MinPhysicsForce) newvel.Z = VehicleMath.PosNeg(newvel.Z) * LegionVehicleLimits.MinPhysicsForce;

                worldvel = newvel * _rotation;

                // Cheat: Do not fight gravity
                if (worldvel.Z > 0) worldvel.Z = 0;

                // Apply only when above sleep threshold
                if (Math.Abs(_localLinearVel.X) >= LegionVehicleLimits.MinPhysicsForce * 2 ||
                    Math.Abs(_localLinearVel.Y) >= LegionVehicleLimits.MinPhysicsForce * 2 ||
                    Math.Abs(_localLinearVel.Z) >= LegionVehicleLimits.MinPhysicsForce * 2)
                {
                    if (Vector3.Mag(_worldLinearVel) < LegionVehicleLimits.ThresholdLinearFrictionDeltaV)
                    {
                        worldvel = -_worldLinearVel;
                    }

                    if (Vector3.Mag(worldvel) > 0)
                    {
                        ApplyLinearVelocityChange(worldvel);
                    }
                }
            }
        }

        #endregion

        #region Exponential Motor Math (ported from Halcyon VehicleMotor)

        /// <summary>
        /// Exponential decay: Nt = N0 * e^(-t/T). Returns value between 1.0 and ~0.
        /// </summary>
        internal float MovementExpDecay(float timeindex, float timescale)
        {
            if (timeindex <= 0) return 1.0f;
            float factor = (float)Math.Pow(Math.E, (double)(-timeindex / timescale));
            if (factor < LegionVehicleLimits.ThresholdStictionFactor) factor = 0.0f;
            return factor;
        }

        /// <summary>
        /// Limited growth: returns value between 0 and ~1.0.
        /// </summary>
        internal float MovementLimitedGrowth(float timeindex, float timescale)
        {
            return 1.0f - MovementExpDecay(timeindex, timescale);
        }

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
        internal float GetGrowthRate(float svel, float evel, float timescale)
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
                    if (Math.Abs(svel) > 0.3f)
                        elog = -(float)Math.Log(1.0f + Math.Abs(svel - evel));
                    else
                        return LegionVehicleLimits.ThresholdInverseCrossover;
                }
            }

            return elog / timescale;
        }

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
        private void SetVehicleDefaults(LegionVehicleType newType)
        {
            switch (newType)
            {
                case LegionVehicleType.None:
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

                    _props.Flags = LegionVehicleFlags.None;
                    break;

                case LegionVehicleType.Sled:
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
                    _props.Flags = LegionVehicleFlags.NoDeflectionUp | LegionVehicleFlags.LimitRollOnly | LegionVehicleFlags.LimitMotorUp;
                    break;

                case LegionVehicleType.Car:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(100f, 0.1f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(100f, 100f, 1.0f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(0.5f, 1f, 1f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(0.2f, 0.2f, 0.05f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(10f, 2f, 2f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.8f, 0.8f, 0.8f);
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
                    _props.Flags = LegionVehicleFlags.NoDeflectionUp | LegionVehicleFlags.LimitRollOnly
                                 | LegionVehicleFlags.HoverUpOnly | LegionVehicleFlags.LimitMotorUp
                                 | LegionVehicleFlags.TorqueWorldZ;
                    break;

                case LegionVehicleType.Boat:
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
                    // Cause-B: buoyancy 1.0 (matches BulletSim's TYPE_BOAT, BSDynamics buoyancy=1.0). Gravity
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
                    _props.Flags = LegionVehicleFlags.NoDeflectionUp | LegionVehicleFlags.HoverWaterOnly
                                 | LegionVehicleFlags.LimitMotorUp | LegionVehicleFlags.LimitMotorDown
                                 | LegionVehicleFlags.TorqueWorldZ;
                    break;

                case LegionVehicleType.Airplane:
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
                    _props.Flags = LegionVehicleFlags.TorqueWorldZ | LegionVehicleFlags.LimitRollOnly;
                    break;

                case LegionVehicleType.Balloon:
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
                    _props.Flags = LegionVehicleFlags.None;  // Halcyon had ReactToWind only
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
                    case LegionVehicleType.None:     return Vehicle.TYPE_NONE;
                    case LegionVehicleType.Sled:     return Vehicle.TYPE_SLED;
                    case LegionVehicleType.Car:      return Vehicle.TYPE_CAR;
                    case LegionVehicleType.Boat:     return Vehicle.TYPE_BOAT;
                    case LegionVehicleType.Airplane: return Vehicle.TYPE_AIRPLANE;
                    case LegionVehicleType.Balloon:  return Vehicle.TYPE_BALLOON;
                    default:                         return Vehicle.TYPE_NONE;
                }
            }
        }
    }
}
