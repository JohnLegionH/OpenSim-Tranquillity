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


        // The share of gravity a ground vehicle (car or sled) gets while it touches something: BulletSim's
        // VehicleGroundGravityFudge, 0.2 there. 1 leaves gravity whole. Set by the host from its configuration.
        public float GroundGravityFactor { get; set; } = 1f;

        // The region's vehicle settings (preset set and limits). Set by the host before the first vehicle type is set.
        public VehicleSettings Settings { get; set; } = VehicleSettings.Default;
        private VehicleSettings S => Settings;

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

        /// <summary>The current vehicle flags (preset + any llSetVehicleFlags / llRemoveVehicleFlags). For tests.</summary>
        internal ExtendedVehicleFlags Flags => _props.Flags;

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
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale] = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    // Scalar set → apply to all 3 axes
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxDecayTimescale);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.BANKING_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency] = ClampF(pValue, -1f, 1f);
                    break;
                case Vehicle.BANKING_MIX:
                    _props.ParamsFloat[VehFloatParam.BankingMix] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.BANKING_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.BankingTimescale] = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    break;
                case Vehicle.BUOYANCY:
                    _props.ParamsFloat[VehFloatParam.Buoyancy] = ClampF(pValue, -1f, 1f);
                    break;
                case Vehicle.HOVER_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.HOVER_HEIGHT:
                    _props.ParamsFloat[VehFloatParam.HoverHeight] = ClampF(pValue, S.MinHoverHeight, S.MaxHoverHeight);
                    break;
                case Vehicle.HOVER_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.HoverTimescale] = ClampF(pValue, S.MinTimescale, S.MaxHoverTimescale);
                    break;
                case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale] = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxDecayTimescale);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale] = ClampF(pValue, S.MinTimescale, S.MaxAttractTimescale);
                    break;

                // These are vector properties but LSL allows setting them as a single float
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    pValue = ClampF(pValue, -S.MaxAngularSpeed, S.MaxAngularSpeed);
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = new Vector3(pValue, pValue, pValue);
                    MoveAngular(_props.ParamsVec[VehVectorParam.AngularMotorDirection]);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue = ClampF(pValue, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = new Vector3(pValue, pValue, pValue);
                    MoveLinear(_props.ParamsVec[VehVectorParam.LinearMotorDirection]);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue = ClampF(pValue, -S.MaxMotorOffset, S.MaxMotorOffset);
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
                    pValue.X = ClampF(pValue.X, S.MinTimescale, S.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, S.MinTimescale, S.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale] = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    pValue.X = ClampF(pValue.X, -S.MaxAngularSpeed, S.MaxAngularSpeed);
                    pValue.Y = ClampF(pValue.Y, -S.MaxAngularSpeed, S.MaxAngularSpeed);
                    pValue.Z = ClampF(pValue.Z, -S.MaxAngularSpeed, S.MaxAngularSpeed);
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = pValue;
                    MoveAngular(pValue);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue.X = ClampF(pValue.X, S.MinTimescale, S.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, S.MinTimescale, S.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue.X = ClampF(pValue.X, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                    pValue.Y = ClampF(pValue.Y, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                    pValue.Z = ClampF(pValue.Z, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = pValue;
                    MoveLinear(pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue.X = ClampF(pValue.X, -S.MaxMotorOffset, S.MaxMotorOffset);
                    pValue.Y = ClampF(pValue.Y, -S.MaxMotorOffset, S.MaxMotorOffset);
                    pValue.Z = ClampF(pValue.Z, -S.MaxMotorOffset, S.MaxMotorOffset);
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

            // Every block steps over the step the engine is about to take. (The Halcyon code smoothed it 0.8/0.2 per
            // step, and filtered a velocity whose sign flipped since the last step; both made the result depend on the
            // step rate, and at a steady rate the smoothed step is the step.)
            float timeStep = pTimestep;

            // -------------------------------------------------------
            // Read current state from physics engine
            _vframe = _props.GetRot(VehRotationParam.ReferenceFrame);
            _rotation = _body.Orientation * _vframe;

            Vector3 physAngVel = _body.AngularVelocity;
            Vector3 physLinVel = _body.LinearVelocity;
            _worldAngularVel = physAngVel;
            _worldLinearVel = physLinVel;

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

            // (The sled's slope assist is an acceleration inside the linear motor and friction step: SledAssist.)

            // -------------------------------------------------------
            // Hover — maintain target height above terrain/water/global
            SimulateHover(pTimestep);

            // -------------------------------------------------------
            // Vertical attractor and banking
            float tilt = 0f;
            if (VehicleLimits.DoVerticalAttractor)
                SimulateVerticalAttractor(pTimestep, out tilt, out _);

            // -------------------------------------------------------
            // Motors and friction, linear and angular, then the banking turn motor. They are stepped over the
            // step the engine is about to take (pTimestep), not the smoothed one: each is the exact solution
            // over that step.
            SimulateLinearMotorAndFriction(pTimestep, VehicleLimits.DoMotors, VehicleLimits.DoLinearFriction);
            float angularz = SimulateAngularMotorAndFriction(pTimestep, VehicleLimits.DoMotors, VehicleLimits.DoAngularFriction);
            float banking = VehicleLimits.DoVerticalAttractor ? SimulateBanking(pTimestep, tilt) : 0f;
            _motorTorqueVelChange += new Vector3(0f, 0f, angularz + banking);

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
            && HoverIdle()
            && AttractorIdle();

        /// <summary>
        /// The rest rule: true when the vehicle rests on something (it touched something in the last step), is idle
        /// (<see cref="IsIdle"/>: no motor pulling, hover and the attractor done), moves slower than the rest speed ([Jolt] VehicleRestSpeed; turning slower than that many radians a
        /// second), and the steady speed the motor-and-friction equation gives on each axis from its present pose is
        /// also below the rest speed:
        ///
        ///   v_steady = (g M / Tm + a) / (g / Tm + 1 / Tf)       (g the motor's grip, a the vehicle's gravity along the axis)
        ///
        /// With no motor grip and no friction on an axis, any acceleration along it gives an unbounded steady speed. Of
        /// its three axes, the one nearest the vertical is the one the
        /// support pushes along, so gravity along that axis is held and left out. A host may then hold the vehicle
        /// still (zero its velocity) instead of stepping it, so the engine can let it sleep. The rule does not hold a
        /// vehicle that the equation would start moving: a sled let go on a slope, or a car whose friction lets it roll
        /// down one.
        /// </summary>
        public bool HoldsAtRest
        {
            get
            {
                float rest = S.RestSpeed;
                // A vehicle in the air is not at rest: one swinging through upright under its attractor is idle for a
                // moment and turning slowly, and holding it would stop the swing.
                if (rest <= 0f || !IsActive || !_body.HasCollision || !IsIdle)
                    return false;
                if (_body.LinearVelocity.Length() >= rest || _body.AngularVelocity.Length() >= rest)
                    return false;
                return SteadySpeed().Length() < rest;
            }
        }

        /// <summary>
        /// The steady velocity, on each vehicle axis, that the linear motor-and-friction equation settles at from the
        /// vehicle's present pose and motor grip, with gravity along the axis the vehicle rests on taken by the support
        /// (<see cref="HoldsAtRest"/>). Infinite on an axis with an acceleration and neither grip nor friction.
        /// </summary>
        internal Vector3 SteadySpeed()
        {
            _rotation = _body.Orientation * _props.GetRot(VehRotationParam.ReferenceFrame);
            Vector3 accel = _body.Gravity * GravityShare() * Quaternion.Inverse(_rotation) + SledAssist();
            if (_body.HasCollision)
            {
                // What it rests on takes the gravity along the axis nearest the vertical: the largest share of world up
                // along it.
                Vector3 up = Vector3.UnitZ * Quaternion.Inverse(_rotation);
                float ax = Math.Abs(up.X), ay = Math.Abs(up.Y), az = Math.Abs(up.Z);
                if (az >= ax && az >= ay) accel.Z = 0f;
                else if (ay >= ax) accel.Y = 0f;
                else accel.X = 0f;
            }
            Vector3 motor = _props.Dynamics.LinearDirection;
            Vector3 motorTs = _props.GetVec(VehVectorParam.LinearMotorTimescale);
            Vector3 decayTs = _props.GetVec(VehVectorParam.LinearMotorDecayTimescale);
            Vector3 frictionTs = VehicleLimits.DoLinearFriction ? _props.GetVec(VehVectorParam.LinearFrictionTimescale) : FrictionOff;
            double age = _props.Dynamics.LinearDecayIndex;
            return new Vector3(
                (float)SteadyOnAxis(accel.X, motor.X, motorTs.X, decayTs.X, age, frictionTs.X),
                (float)SteadyOnAxis(accel.Y, motor.Y, motorTs.Y, decayTs.Y, age, frictionTs.Y),
                (float)SteadyOnAxis(accel.Z, motor.Z, motorTs.Z, decayTs.Z, age, frictionTs.Z));
        }

        /// <summary>The size of the steady speed of the motor-and-friction equation on one axis (see <see cref="HoldsAtRest"/>).</summary>
        internal static double SteadyOnAxis(double accel, double motor, double motorTs, double decayTs, double age, double frictionTs)
        {
            double grip = VehicleMotorSolver.Grip(age, Math.Max(decayTs, VehicleMotorSolver.MinTimescale));
            double pull = grip / Math.Max(motorTs, VehicleMotorSolver.MinTimescale);
            double rate = pull + VehicleMotorSolver.FrictionRate(Math.Max(frictionTs, VehicleMotorSolver.MinTimescale));
            double drive = pull * motor + accel;
            if (rate <= 0)
                return drive == 0 ? 0 : double.PositiveInfinity;
            return Math.Abs(drive / rate);
        }

        // The attractor has nothing left to do when it is off, the vehicle is upright to within AttractorIdleAngle, or
        // it rests on something that holds its tilt (a car parked on a slope).
        private bool AttractorIdle()
        {
            if (_props.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f) >= S.MaxAttractTimescale || _body.HasCollision)
                return true;
            Quaternion rotation = _body.Orientation * _props.GetRot(VehRotationParam.ReferenceFrame);
            return (Vector3.UnitZ * rotation).Z >= Math.Cos(AttractorIdleAngle);
        }

        // Upright to within this (radians) the attractor is done.
        internal const double AttractorIdleAngle = 0.002;

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
            _attractorCarry = null;
            _angularDeflectionRate = Vector3.Zero;
        }

        /// <summary>
        /// A step in which the rest rule holds (<see cref="HoldsAtRest"/>): time passes as in <see cref="Rest"/>, the
        /// vehicle's gravity stays on the body, and its velocity is set to zero, so the engine can let it sleep. Whatever
        /// the engine's step then does to it (a body resting on an edge tips) is read on the next step, which steps the
        /// vehicle again if it is no longer at rest.
        /// </summary>
        public void Hold(float pTimestep)
        {
            if (!IsActive) return;
            Rest(pTimestep);
            ApplyGravity();
            _body.LinearVelocity = Vector3.Zero;
            _body.AngularVelocity = Vector3.Zero;
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
                // The position's rate of change, smoothed with a time constant (StallSmoothing): the same smoothing
                // over a span of time whatever the step. (It was 0.8 / 0.2 per step, which is this time constant at
                // the 64 Hz step the code was tuned at.)
                float share = 1f - (float)Math.Exp(-timeStep / StallSmoothing);
                _props.Dynamics.ShortTermPositionDelta += (posdelta / timeStep - _props.Dynamics.ShortTermPositionDelta) * share;
                _linearMotorStallChecked = true;

                // If either motor is starting fresh, assume not stalled
                if (_props.Dynamics.LinearDecayIndex > VehicleLimits.ThresholdLinearMotorEngaged &&
                    _props.Dynamics.AngularDecayIndex > VehicleLimits.ThresholdAngularMotorEngaged)
                {
                    float stspeed = Vector3.Mag(_props.Dynamics.ShortTermPositionDelta);

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

        // The stall check's smoothing time constant (s): -0.0156 / ln(0.8), the old per-step 0.8 / 0.2 at 64 Hz.
        private const float StallSmoothing = 0.07f;

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
            // The linear motor's offset turn is applied separately (MotorOffsetTurn); the velocity change is central.
            _body.LinearVelocity = _body.LinearVelocity + deltaV;
        }

        #endregion // Adapter Methods

        #region Deflection (Angular + Linear) + Sled movement (gated Type==Sled)

        /// <summary>
        /// Angular deflection, as Second Life documents it (Linden_Vehicle_Tutorial, "Linear and Angular Deflection";
        /// LlSetVehicleFloatParam): it reorients the vehicle so that its x axis points the way it is moving, the
        /// timescale being "the time coefficient for exponential decay toward full deflection" and the efficiency a
        /// slider from no deflection (0) to the most (1). The angle a between the nose and the velocity decays as
        ///
        ///   a' = -eff s a / T,   so over a step h the nose turns a (1 - e^(-eff s h / T)) toward the velocity,
        ///
        /// with s = speed / the reference speed ([Jolt] VehicleReferenceSpeed, 30 m/s by default; at most 1), the speed
        /// scaling the existing code had. The
        /// documentation gives no speed term; without it a car slowing to a stop turns its nose toward the jitter of
        /// its contact velocity and drifts, and a falling car noses down hard.
        ///
        /// given to the body as a turning rate for the step (and the last step's taken back, so the deflection is a
        /// rate, not a push that builds up). A vehicle moving backwards turns its tail, not its nose, toward its
        /// velocity. Under ThresholdDeflectionSpeed the velocity has no direction to turn to.
        /// </summary>
        private void SimulateAngularDeflection(float h)
        {
            Vector3 rate = Vector3.Zero;
            float timescale = _props.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
            float speed = _rawWorldLinearVel.Length();
            if (timescale < VehicleLimits.MaxTimescale && efficiency > 0f && speed >= VehicleLimits.ThresholdDeflectionSpeed)
            {
                Vector3 forward = Vector3.UnitX * _rotation;
                Vector3 heading = _rawWorldLinearVel / speed;
                if (Vector3.Dot(forward, heading) < 0f)
                    heading = -heading;
                Vector3 axis = Vector3.Cross(forward, heading);
                float sin = axis.Length();
                if (sin > MinTurnSine)
                {
                    double angle = Math.Atan2(sin, Vector3.Dot(forward, heading));
                    double speedShare = Math.Min(speed, S.ReferenceSpeed) / S.ReferenceSpeed;
                    double turn = angle * (1.0 - Math.Exp(-efficiency * speedShare * h / timescale));
                    rate = axis / sin * (float)(turn / h);
                }
            }
            _motorTorqueVelChange += rate - _angularDeflectionRate;
            _angularDeflectionRate = rate;
        }

        // The turning rate angular deflection gave the body in the last step.
        private Vector3 _angularDeflectionRate;

        // Directions closer than this (the sine of the angle between them) are already aligned.
        private const float MinTurnSine = 1e-6f;

        /// <summary>
        /// Linear deflection, as Second Life documents it (Linden_Vehicle_Tutorial, "Linear and Angular Deflection";
        /// LlSetVehicleFloatParam): it rotates the velocity until it points along the vehicle's x axis, keeping its
        /// speed, with the timescale "the time coefficient for exponential decay toward full deflection" and the
        /// efficiency a slider from none (0) to the most (1). The angle a between the velocity and the nose decays as
        ///
        ///   a' = -eff a / T,   so over a step h the velocity turns a (1 - e^(-eff h / T)) toward the nose.
        ///
        /// With NO_DEFLECTION_UP ("prevents linear deflection parallel to world z-axis") only the horizontal part of
        /// the velocity turns, toward the nose's horizontal heading, and the vertical part is left as it is, so
        /// deflection never adds speed or climbs.
        /// </summary>
        private void SimulateLinearDeflection(float h)
        {
            float timescale = _props.GetFloat(VehFloatParam.LinearDeflectionTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.LinearDeflectionEfficiency, 0f);
            if (timescale >= VehicleLimits.MaxTimescale || efficiency <= 0f)
                return;
            float share = 1f - (float)Math.Exp(-efficiency * h / timescale);
            Vector3 forward = Vector3.UnitX * _rotation;
            Vector3 change = (_props.Flags & ExtendedVehicleFlags.NoDeflectionUp) != 0
                ? HorizontalDeflection(_rawWorldLinearVel, forward, share)
                : Deflection(_rawWorldLinearVel, forward, share);
            if (change != Vector3.Zero)
                ApplyLinearVelocityChange(change);
        }

        /// <summary>
        /// The sled's slope assist (the InWorldz sled movement), as an acceleration along the sled's nose:
        ///
        ///   a = k g sqrt(|sin pitch|)            nose down; nose up, a tenth of it, still pushing down the slope
        ///
        /// k the region's sled assist (<see cref="VehicleSettings.SledAssist"/>, 0.045 by default) and g the world's
        /// gravity. The InWorldz code applied a force of 3 g m sqrt(|sin pitch|) times the step for one step; at the
        /// 15 ms step it was tuned at that is 0.045 g, and at any other step it scaled with the step. As an
        /// acceleration it goes into the linear motor and friction equation (<see cref="SimulateLinearMotorAndFriction"/>),
        /// which steps it exactly with the friction. No assist under ThresholdDeflectionAngle of pitch, or while the
        /// sled is stalled against something (<see cref="IsLinearMotorStalled"/>). Zero for every other vehicle type.
        /// </summary>
        private Vector3 SledAssist()
        {
            if (_props.Type != VehicleType.Sled)
                return Vector3.Zero;
            float noseDown = -(Vector3.UnitX * _rotation).Z;   // sin of the pitch, nose down positive
            if (Math.Abs(noseDown) <= VehicleLimits.ThresholdDeflectionAngle || IsLinearMotorStalled())
                return Vector3.Zero;
            float accel = S.SledAssist * Math.Abs(_body.Gravity.Z) * (float)Math.Sqrt(Math.Abs(noseDown));
            if (noseDown < 0f)
                accel *= -SledUphillShare;
            return new Vector3(accel, 0f, 0f);
        }

        // Nose up, the assist is this share of the nose-down assist, pushing back down the slope.
        private const float SledUphillShare = 0.1f;

        /// <summary>
        /// The velocity change that turns a velocity toward a direction by the given share of the angle between them,
        /// keeping its speed. Zero when the velocity is under ThresholdDeflectionSpeed or already along the direction;
        /// exactly opposed, there is no one way to turn, and it is left for this step.
        /// </summary>
        internal static Vector3 Deflection(Vector3 velocity, Vector3 toward, float share)
        {
            float speed = velocity.Length();
            if (speed < VehicleLimits.ThresholdDeflectionSpeed || toward.Length() < MinDeflectionHeading)
                return Vector3.Zero;
            Vector3 u = velocity / speed, d = Vector3.Normalize(toward);
            Vector3 axis = Vector3.Cross(u, d);
            float sin = axis.Length();
            if (sin < MinTurnSine)
                return Vector3.Zero;
            axis /= sin;
            double turn = Math.Atan2(sin, Vector3.Dot(u, d)) * share;
            // Rodrigues' rotation of u about an axis perpendicular to it.
            Vector3 turned = u * (float)Math.Cos(turn) + Vector3.Cross(axis, u) * (float)Math.Sin(turn);
            return turned * speed - velocity;
        }

        /// <summary>
        /// The velocity change that turns the horizontal part of a velocity toward the horizontal heading of the nose
        /// by the given share of the angle between them, keeping the horizontal speed; the vertical part is untouched.
        /// Zero when the velocity has too little horizontal speed or the nose points (nearly) straight up or down.
        /// </summary>
        internal static Vector3 HorizontalDeflection(Vector3 velocity, Vector3 forward, float share)
        {
            Vector3 horizontal = new Vector3(velocity.X, velocity.Y, 0f);
            Vector3 heading = new Vector3(forward.X, forward.Y, 0f);
            return Deflection(horizontal, heading, share);
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
                // Above the height with HOVER_UP_ONLY hover does nothing, unless the vehicle falls back through the
                // height inside this step: then the spring takes it from there (HoverFromAbove).
                if (!HoverFromAbove(e0, timescale, efficiency, h))
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

        /// <summary>
        /// HOVER_UP_ONLY, a step that starts above the height: the vehicle falls freely (with the gravity buoyancy no
        /// longer cancels above the height) and, if it reaches the height inside the step, the spring acts from that
        /// moment, with the gravity of below the height:
        ///
        ///   e(t) = e0 + v0 t + g_above t^2 / 2   until e(tc) = 0;   then the spring from (0, v0 + g_above tc) for h - tc
        ///
        /// The engine applies this step's gravity share (the one above the height) over the whole step, so the body is
        /// given the velocity that, with it, ends the step where this does, and the rest is carried to the next step.
        /// Returns false (nothing done) when the vehicle stays above the height for the whole step.
        /// </summary>
        private bool HoverFromAbove(double e0, float timescale, float efficiency, float h)
        {
            double read = _rawWorldLinearVel.Z;
            double v0 = read + (_hoverCarry ?? 0.0);
            double gAbove = _body.Gravity.Z * GravityShare();
            if (e0 + v0 * h + 0.5 * gAbove * h * h >= 0)
                return false;

            // The fall reaches the height at tc (by bisection: the fall is past its top or falling, so e(t) crosses
            // zero once in the step).
            double lo = 0, hi = h;
            for (int i = 0; i < UpOnlyCrossingIterations; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (e0 + v0 * mid + 0.5 * gAbove * mid * mid > 0) lo = mid; else hi = mid;
            }
            double tc = 0.5 * (lo + hi);
            double rest = h - tc;

            // From the height: the spring, with the gravity of below the height alongside it as in any step there.
            (double e1, double v1) = VehicleSpring.Step(0.0, v0 + gAbove * tc, timescale, efficiency, rest);
            double gBelow = _body.Gravity.Z * GravityShareBelow();
            e1 += 0.5 * gBelow * rest * rest;
            v1 += gBelow * rest;

            // Less what the engine's gravity (this step's share) adds over the whole step.
            double move = (e1 - 0.5 * gAbove * h * h - e0) / h;
            _hoverCarry = v1 - gAbove * h - move;
            ApplyLinearVelocityChange(new Vector3(0f, 0f, (float)(move - read)));
            return true;
        }

        // The gravity share under the hover height: 1 - buoyancy, times the ground factor for a ground vehicle touching.
        private float GravityShareBelow()
        {
            float share = 1f - _props.GetFloat(VehFloatParam.Buoyancy, 0f);
            if (IsGroundVehicle && _body.HasCollision)
                share *= GroundGravityFactor;
            return share;
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
            if (hoverTimescale >= S.MaxHoverTimescale)
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
        /// The vertical attractor, as Second Life documents it (Linden_Vehicle_Tutorial, "The Vertical Attractor";
        /// LlSetVehicleFloatParam): a spring that brings the vehicle's local z axis to the world's up, with
        /// VERTICAL_ATTRACTION_TIMESCALE setting its period and VERTICAL_ATTRACTION_EFFICIENCY its damping, from
        /// wobbling around up (0) to reaching it with exponential decay (1). Off at a timescale of 500 s or more.
        ///
        /// It acts on the vehicle's roll (the angle about its x axis that would bring its y axis level) and its
        /// pitch (the nose's angle above or below level), each a spring on the angle e and the angular velocity about
        /// that axis:
        ///
        ///   e'' = -s e / T^2 - 2 eff e' / T          s = 1 for roll; for pitch 1, or 0 with LIMIT_ROLL_ONLY
        ///
        /// stepped exactly (<see cref="VehicleSpring"/>) over the step the engine takes; the body is given the angular
        /// velocity that turns it where the spring would at the step's end. Gives the tilt from up (angle), for
        /// banking.
        ///
        /// VEHICLE_FLAG_LIMIT_ROLL_ONLY unlocks the attractor about the pitch axis (Linden_Vehicle_Tutorial: "unlock
        /// the attractor around the pitch axis by setting the VEHICLE_FLAG_LIMIT_ROLL_ONLY bit"; LlSetVehicleFlags:
        /// "For vehicles with vertical attractor that want to be able to climb/dive"), for every vehicle type: the
        /// attractor then corrects roll and leaves pitch alone.
        /// </summary>
        private void SimulateVerticalAttractor(float h, out float angle, out bool inverted)
        {
            float timescale = _props.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.VerticalAttractionEfficiency, 0f);
            Vector3 x = Vector3.UnitX * _rotation, y = Vector3.UnitY * _rotation, z = Vector3.UnitZ * _rotation;
            angle = (float)Math.Acos(Utils.Clamp(z.Z, -1f, 1f));
            inverted = false;
            if (timescale >= S.MaxAttractTimescale)
            {
                LetGoOfAttractor(x, y);
                return;
            }

            double roll = Math.Atan2(y.Z, z.Z);
            double pitch = Math.Asin(Utils.Clamp(-x.Z, -1f, 1f));
            double stiffness = 1.0 / ((double)timescale * timescale);
            double damping = efficiency / (double)timescale;
            bool rollOnly = (_props.Flags & ExtendedVehicleFlags.LimitRollOnly) != 0;

            // The spring's own rates: the body's, plus what the last step's turn left out of them.
            double rollRate = Vector3.Dot(_rawWorldAngularVel, x);
            double pitchRate = Vector3.Dot(_rawWorldAngularVel, y);
            (double rollTurn, double rollCarry) = VehicleSpring.MoveOverGeneral(roll, rollRate + (_attractorCarry?.X ?? 0), stiffness, damping, h);
            // With LIMIT_ROLL_ONLY the pitch is left alone: no spring and no damping on it (any rate the last step's
            // pitch turn left out is handed back).
            double pitchTurn = pitchRate + (_attractorCarry?.Y ?? 0), pitchCarry = 0;
            if (!rollOnly)
                (pitchTurn, pitchCarry) = VehicleSpring.MoveOverGeneral(pitch, pitchTurn, stiffness, damping, h);
            _attractorCarry = new Vector3((float)rollCarry, (float)pitchCarry, 0f);

            // Applied whole with the motors' change (TorqueFini's per-step clean-up would stop it short).
            _motorTorqueVelChange += x * (float)(rollTurn - rollRate) + y * (float)(pitchTurn - pitchRate);
        }

        // What the last attractor step's turn left out of the spring's roll and pitch rates; null when it did not act.
        private Vector3? _attractorCarry;

        // The attractor hands the body back its true roll and pitch rates when it stops acting.
        private void LetGoOfAttractor(Vector3 x, Vector3 y)
        {
            if (_attractorCarry is Vector3 c)
                _motorTorqueVelChange += x * c.X + y * c.Y;
            _attractorCarry = null;
        }

        #endregion

        #region Banking

        /// <summary>
        /// Banking, as Second Life documents it (Linden_Vehicle_Tutorial, "Banking"; LlSetVehicleFloatParam): a roll
        /// about the vehicle's roll axis gives it an angular velocity about the yaw axis, so it turns; positive
        /// efficiency leans into the turn. The yaw rate it asks for is in proportion to the efficiency and the roll,
        /// and with BANKING_MIX toward 1 also to the speed along the roll axis:
        ///
        ///   target = -roll * attitude * eff * pi * ((1 - mix) + mix * speed / Vref)       (roll the y axis' rise,
        ///            scaled to the banking range; attitude -1 when the vehicle is upside down; Vref the
        ///            reference speed, 30 m/s by default)
        ///
        /// and the yaw rate about world z approaches it with BANKING_TIMESCALE, the documented "time it takes for the
        /// banking behavior to defeat a preexisting angular velocity about the world z-axis":
        ///
        ///   w' = (target - w) / T,   stepped exactly: w(h) = target + (w0 - target) e^(-h / T).
        ///
        /// The vertical attractor must be on ("must be enabled in order for the banking behavior to function"); off
        /// at a banking timescale of 500 s or more; under ThresholdBankAngle of roll banking leaves the yaw alone.
        /// Returns the change to the angular velocity about world z.
        /// </summary>
        private float SimulateBanking(float h, float tilt)
        {
            float timescale = _props.GetFloat(VehFloatParam.BankingTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.BankingEfficiency, 0f);
            if (!VehicleLimits.DoBanking || efficiency == 0f || timescale >= S.MaxAttractTimescale
                || _props.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f) >= S.MaxAttractTimescale)
                return 0f;

            float mix = _props.GetFloat(VehFloatParam.BankingMix, 0.5f);
            float forwardSpeed = Math.Abs(_rawLocalLinearVel.X);
            float speedShare = Utils.Clamp(forwardSpeed, 0, S.ReferenceSpeed) / S.ReferenceSpeed;

            // The roll: how far the y axis rises, scaled to the banking range.
            float rise = (Vector3.UnitY * _rotation).Z;
            float range = _props.GetFloat(VehFloatParam.BankingAzimuth, (float)Math.PI / 2f);
            float roll = Utils.Clamp(rise * (float)Math.PI * 0.5f / range, -1, 1);
            if (Math.Abs(roll) <= VehicleLimits.ThresholdBankAngle)
                return 0f;
            float attitude = tilt > Math.PI / 2.0 ? -1f : 1f;

            float target = -roll * attitude * efficiency * (float)Math.PI * ((1f - mix) + mix * speedShare);
            // With the angular motor engaged, banking turns no faster than the legacy angular speed.
            if (_props.Dynamics.AngularDecayIndex < VehicleLimits.ThresholdAngularMotorEngaged)
                target = Utils.Clamp(target, -VehicleLimits.MaxLegacyAngularVelocity, VehicleLimits.MaxLegacyAngularVelocity);
            target = Utils.Clamp(target, -S.MaxAngularSpeed, S.MaxAngularSpeed);

            float w0 = _rawWorldAngularVel.Z;
            float w1 = target + (w0 - target) * (float)Math.Exp(-h / timescale);
            return w1 - w0;
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
        /// was last set, Tf the friction timescale, a the vehicle's gravity along the axis plus a sled's slope
        /// assist (<see cref="SledAssist"/>; see
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
            // The constant accelerations along the vehicle's axes: its gravity, and a sled's slope assist.
            Vector3 gravity = gravityWorld * Quaternion.Inverse(_rotation) + SledAssist();
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
            v1.X = Utils.Clamp(v1.X, -S.MaxLinearSpeed, S.MaxLinearSpeed);
            v1.Y = Utils.Clamp(v1.Y, -S.MaxLinearSpeed, S.MaxLinearSpeed);
            v1.Z = Utils.Clamp(v1.Z, -S.MaxLinearSpeed, S.MaxLinearSpeed);

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
            _motorTorqueVelChange += MotorOffsetTurn(motorChange);
        }

        /// <summary>
        /// VEHICLE_LINEAR_MOTOR_OFFSET: "offset from the center of mass of the vehicle where the linear motor is
        /// applied" (LlSetVehicleVectorParam), so the motor's push also turns the vehicle, as a force at that point
        /// does ("In rockets using this offset gives that very distinctive spiraling out of control",
        /// VEHICLE_LINEAR_MOTOR_OFFSET). The motor's change of velocity this step, dv, is an impulse m dv at the
        /// offset point r (in the vehicle frame); its angular impulse about the centre of mass turns the body by
        ///
        ///   dw = I^-1 (r x m dv)
        ///
        /// with I the body's principal moments of inertia, taken along the body's own axes. dv is the exact motor
        /// share over the step, so the turn over a span of time does not depend on the step either. Friction and
        /// gravity act at the centre of mass and give no turn.
        /// </summary>
        private Vector3 MotorOffsetTurn(Vector3 motorChange)
        {
            Vector3 offset = _props.GetVec(VehVectorParam.LinearMotorOffset);
            if (offset == Vector3.Zero || motorChange == Vector3.Zero)
                return Vector3.Zero;
            Vector3 r = offset * _rotation;
            Vector3 angularImpulse = Vector3.Cross(r, motorChange * m_vehicleMass);

            // To the body's axes, through the inverse of its principal moments, and back to the world.
            Quaternion bodyRotation = _body.Orientation;
            Vector3 local = angularImpulse * Quaternion.Inverse(bodyRotation);
            Vector3 inertia = _body.InertiaDiagonal;
            local = new Vector3(
                inertia.X > 0f ? local.X / inertia.X : 0f,
                inertia.Y > 0f ? local.Y / inertia.Y : 0f,
                inertia.Z > 0f ? local.Z / inertia.Z : 0f);
            return local * bodyRotation;
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
            v1.X = Utils.Clamp(v1.X, -S.MaxAngularSpeed, S.MaxAngularSpeed);
            v1.Y = Utils.Clamp(v1.Y, -S.MaxAngularSpeed, S.MaxAngularSpeed);
            v1.Z = Utils.Clamp(v1.Z, -S.MaxAngularSpeed, S.MaxAngularSpeed);

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

            // The change about world Z is applied in Step, with banking's.
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

        #endregion

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
        /// Set all vehicle parameters to the defaults for the given type: the legacy (InWorldz Halcyon) values, and with
        /// the documented preset set (<see cref="VehicleSettings.Presets"/>) Second Life's documented values over them.
        /// The module's own extension params (wind, mouselook, banking azimuth, motor disabling) are not Second Life
        /// params and keep their legacy values in both sets; none of them is simulated.
        /// </summary>
        private void SetVehicleDefaults(VehicleType newType)
        {
            SetLegacyDefaults(newType);
            if (Settings.Presets == VehiclePresetSet.Documented)
                SetDocumentedDefaults(newType);
        }

        /// <summary>
        /// Second Life's documented defaults for each vehicle type, as the wiki page of each type lists them
        /// (https://wiki.secondlife.com/wiki/VEHICLE_TYPE_SLED, _CAR, _BOAT, _AIRPLANE and _BALLOON). A scalar on the
        /// page sets all three axes of a vector param. The flags are the page's llSetVehicleFlags list; every other
        /// flag is cleared. The pages set no motor offset, so it is zero.
        /// </summary>
        private void SetDocumentedDefaults(VehicleType type)
        {
            const ExtendedVehicleFlags noDeflectionUp = ExtendedVehicleFlags.NoDeflectionUp;
            const ExtendedVehicleFlags rollOnly = ExtendedVehicleFlags.LimitRollOnly;
            const ExtendedVehicleFlags upOnly = ExtendedVehicleFlags.HoverUpOnly;
            const ExtendedVehicleFlags motorUp = ExtendedVehicleFlags.LimitMotorUp;
            const ExtendedVehicleFlags waterOnly = ExtendedVehicleFlags.HoverWaterOnly;
            switch (type)
            {
                case VehicleType.Sled:
                    // The page gives HOVER_EFFICIENCY 10; llSetVehicleFloatParam holds an efficiency to 0..1, so 1.
                    // The page's HOVER_TIMESCALE 10 would turn hover on (any timescale under 300 s does), against its
                    // own comment "no hover"; the sled has hover off (1000 s).
                    SetDocumented(linearFriction: new Vector3(30f, 1f, 1000f), angularFriction: new Vector3(1000f),
                        linearMotorTimescale: 1000f, linearMotorDecay: 120f, angularMotorTimescale: 1000f, angularMotorDecay: 120f,
                        hoverHeight: 0f, hoverEfficiency: 1f, hoverTimescale: 1000f, buoyancy: 0f,
                        linearDeflectionEfficiency: 1f, linearDeflectionTimescale: 1f,
                        angularDeflectionEfficiency: 0f, angularDeflectionTimescale: 10f,
                        attractionEfficiency: 1f, attractionTimescale: 1000f,
                        bankingEfficiency: 0f, bankingMix: 1f, bankingTimescale: 10f,
                        flags: noDeflectionUp | rollOnly | motorUp);
                    break;
                case VehicleType.Car:
                    SetDocumented(linearFriction: new Vector3(100f, 2f, 1000f), angularFriction: new Vector3(1000f),
                        linearMotorTimescale: 1f, linearMotorDecay: 60f, angularMotorTimescale: 1f, angularMotorDecay: 0.8f,
                        hoverHeight: 0f, hoverEfficiency: 0f, hoverTimescale: 1000f, buoyancy: 0f,
                        linearDeflectionEfficiency: 1f, linearDeflectionTimescale: 2f,
                        angularDeflectionEfficiency: 0f, angularDeflectionTimescale: 10f,
                        attractionEfficiency: 1f, attractionTimescale: 10f,
                        bankingEfficiency: -0.2f, bankingMix: 1f, bankingTimescale: 1f,
                        flags: noDeflectionUp | rollOnly | upOnly | motorUp);
                    break;
                case VehicleType.Boat:
                    SetDocumented(linearFriction: new Vector3(10f, 3f, 2f), angularFriction: new Vector3(10f),
                        linearMotorTimescale: 5f, linearMotorDecay: 60f, angularMotorTimescale: 4f, angularMotorDecay: 4f,
                        hoverHeight: 0f, hoverEfficiency: 0.5f, hoverTimescale: 2f, buoyancy: 1f,
                        linearDeflectionEfficiency: 0.5f, linearDeflectionTimescale: 3f,
                        angularDeflectionEfficiency: 0.5f, angularDeflectionTimescale: 5f,
                        attractionEfficiency: 0.5f, attractionTimescale: 5f,
                        bankingEfficiency: -0.3f, bankingMix: 0.8f, bankingTimescale: 1f,
                        flags: noDeflectionUp | waterOnly | upOnly | motorUp);
                    break;
                case VehicleType.Airplane:
                    SetDocumented(linearFriction: new Vector3(200f, 10f, 5f), angularFriction: new Vector3(20f),
                        linearMotorTimescale: 2f, linearMotorDecay: 60f, angularMotorTimescale: 4f, angularMotorDecay: 8f,
                        hoverHeight: 0f, hoverEfficiency: 0.5f, hoverTimescale: 1000f, buoyancy: 0f,
                        linearDeflectionEfficiency: 0.5f, linearDeflectionTimescale: 0.5f,
                        angularDeflectionEfficiency: 1f, angularDeflectionTimescale: 2f,
                        attractionEfficiency: 0.9f, attractionTimescale: 2f,
                        bankingEfficiency: 1f, bankingMix: 0.7f, bankingTimescale: 2f,
                        flags: rollOnly);
                    break;
                case VehicleType.Balloon:
                    SetDocumented(linearFriction: new Vector3(5f), angularFriction: new Vector3(10f),
                        linearMotorTimescale: 5f, linearMotorDecay: 60f, angularMotorTimescale: 6f, angularMotorDecay: 10f,
                        hoverHeight: 5f, hoverEfficiency: 0.8f, hoverTimescale: 10f, buoyancy: 1f,
                        linearDeflectionEfficiency: 0f, linearDeflectionTimescale: 5f,
                        angularDeflectionEfficiency: 0f, angularDeflectionTimescale: 5f,
                        attractionEfficiency: 1f, attractionTimescale: 1000f,
                        bankingEfficiency: 0f, bankingMix: 0.7f, bankingTimescale: 5f,
                        flags: ExtendedVehicleFlags.None);
                    break;
            }
        }

        private void SetDocumented(Vector3 linearFriction, Vector3 angularFriction,
            float linearMotorTimescale, float linearMotorDecay, float angularMotorTimescale, float angularMotorDecay,
            float hoverHeight, float hoverEfficiency, float hoverTimescale, float buoyancy,
            float linearDeflectionEfficiency, float linearDeflectionTimescale,
            float angularDeflectionEfficiency, float angularDeflectionTimescale,
            float attractionEfficiency, float attractionTimescale,
            float bankingEfficiency, float bankingMix, float bankingTimescale,
            ExtendedVehicleFlags flags)
        {
            _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]    = linearFriction;
            _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]   = angularFriction;
            _props.ParamsVec[VehVectorParam.LinearMotorDirection]       = Vector3.Zero;
            _props.ParamsVec[VehVectorParam.AngularMotorDirection]      = Vector3.Zero;
            _props.ParamsVec[VehVectorParam.LinearMotorOffset]          = Vector3.Zero;
            _props.ParamsVec[VehVectorParam.LinearMotorTimescale]       = new Vector3(linearMotorTimescale);
            _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]  = new Vector3(linearMotorDecay);
            _props.ParamsVec[VehVectorParam.AngularMotorTimescale]      = new Vector3(angularMotorTimescale);
            _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale] = new Vector3(angularMotorDecay);

            _props.ParamsFloat[VehFloatParam.HoverHeight]                  = hoverHeight;
            _props.ParamsFloat[VehFloatParam.HoverEfficiency]              = hoverEfficiency;
            _props.ParamsFloat[VehFloatParam.HoverTimescale]               = hoverTimescale;
            _props.ParamsFloat[VehFloatParam.Buoyancy]                     = buoyancy;
            _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]   = linearDeflectionEfficiency;
            _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]    = linearDeflectionTimescale;
            _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]  = angularDeflectionEfficiency;
            _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]   = angularDeflectionTimescale;
            _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency] = attractionEfficiency;
            _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]  = attractionTimescale;
            _props.ParamsFloat[VehFloatParam.BankingEfficiency]            = bankingEfficiency;
            _props.ParamsFloat[VehFloatParam.BankingMix]                   = bankingMix;
            _props.ParamsFloat[VehFloatParam.BankingTimescale]             = bankingTimescale;

            _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
            _props.Flags = flags;
        }

        /// <summary>
        /// The legacy defaults for the given type: the InWorldz Halcyon values.
        /// Faithfully ported from Halcyon VehicleDynamics.SetVehicleDefaults().
        /// </summary>
        private void SetLegacyDefaults(VehicleType newType)
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
