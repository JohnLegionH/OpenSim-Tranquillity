/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

/*
 * Vehicle dynamics ported from InWorldz Halcyon (InWorldz.PhysxPhysics/Vehicle/VehicleDynamics.cs),
 * through OpenSim's BulletSim vehicle code.
 * Adapted 2026 by Legion Builds
 *
 * A backend-agnostic vehicle controller. The behaviours Second Life documents (the motors and
 * friction, hover, buoyancy, the vertical attractor, banking and deflection) follow its documented
 * vehicle model (Linden_Vehicle_Tutorial and the llSetVehicle* pages of the SL wiki; each
 * Simulate method names its source). The sled's slope assist is not a documented Second Life
 * behaviour: it is carried over from the InWorldz Halcyon sled movement (SledAssist). The frame
 * structure, the parameter routing and the legacy type presets come from the InWorldz Halcyon
 * vehicle code, as OpenSim's BulletSim vehicle code expresses it; the legacy values are kept
 * behind VehiclePresets = legacy (VehicleSettings). Six behaviours of the Halcyon code that differ from the documented
 * model (the pitch righting kept under LIMIT_ROLL_ONLY, hover up only acting with buoyancy, friction that would push up
 * dropped, a quick motor's delayed decay, the stronger and escalating self-righting, and the buoyancy adjustment) are
 * on only where the region chooses VehicleSettings.Behaviour = InWorldz.
 * The physics engine is reached only through IVehicleBody, per this table (BulletSim's names
 * on the left):
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
 * accumulator), and mouselook steering and banking (VEHICLE_FLAG_MOUSELOOK_STEER, _BANK) toward the driver's camera
 * the host supplies (DriverCamera). Not simulated: wind.
 *
 * The motors and friction are solved by VehicleMotorSolver, hover and the vertical attractor by
 * VehicleSpring, each as the exact solution over the step the engine takes.
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
    /// The vehicle controller, backend-agnostic: Second Life's documented vehicle behaviours and the sled's
    /// slope assist from the Halcyon sled movement, on a frame structure derived from the Halcyon vehicle code.
    /// The host owns one instance per vehicle body and calls Step(dt) every frame BEFORE the
    /// physics step while the vehicle is active and physical.
    /// </summary>
    /// <summary>
    /// What the vehicle knows of its driver's camera: the world direction it looks along, and whether the driver is in
    /// mouselook (the viewer's AGENT_CONTROL_MOUSELOOK control flag, as the simulator's CameraData.MouseLook).
    /// </summary>
    public readonly record struct DriverView(Vector3 At, bool InMouselook);

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

        // The driver's camera (the simulator's CameraData: CameraAtAxis and MouseLook), for VEHICLE_FLAG_MOUSELOOK_STEER
        // and VEHICLE_FLAG_MOUSELOOK_BANK; null, or no function, when no driver's camera is known. Read on the physics
        // step. Set by the host.
        public Func<DriverView?> DriverCamera { get; set; }

        // The region's vehicle settings (preset set and limits). Set by the host before the first vehicle type is set.
        public VehicleSettings Settings { get; set; } = VehicleSettings.Default;
        private VehicleSettings S => Settings;

        // The region follows InWorldz's vehicle behaviour in six places (VehicleSettings.Behaviour). Each is written so
        // that with Second Life's behaviour, the default, the arithmetic is what it was without the choice.
        private bool InWorldz => S.Behaviour == VehicleBehaviour.InWorldz;

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
            // A motorcycle is a car where InWorldz tells the two apart (VehicleDynamics.cs:2826).
            get { return (_props.Type == VehicleType.Car || _props.Type == VehicleType.Motorcycle || _props.Type == VehicleType.Sled); }
        }

        /// <summary>Read a current float vehicle param (preset default + any llSetVehicleFloatParam override).
        /// Introspection for the host / tests - e.g. asserting the boat preset's buoyancy.</summary>
        public float GetFloatParam(VehFloatParam key) => _props.GetFloat(key, 0f);

        /// <summary>Read a current vector vehicle param (preset default + any llSetVehicleVectorParam override).
        /// Introspection for the host / tests - e.g. asserting the car preset's friction/motor timescales.</summary>
        public Vector3 GetVecParam(VehVectorParam key) => _props.GetVec(key);

        /// <summary>The current vehicle flags (preset + any llSetVehicleFlags / llRemoveVehicleFlags). For tests.</summary>
        internal ExtendedVehicleFlags Flags => _props.Flags;

        /// <summary>The current reference frame (llSetVehicleRotationParam VEHICLE_REFERENCE_FRAME). For tests.</summary>
        internal Quaternion ReferenceFrame => _props.GetRot(VehRotationParam.ReferenceFrame);

        /// <summary>True once a script has set the angular motor, until the motors are reset. For tests.</summary>
        internal bool AngularMotorSet => !float.IsPositiveInfinity(_props.Dynamics.AngularDecayIndex);

        /// <summary>The seconds since each motor was set, as its decay clock reads (negative while a short motor's decay is
        /// held back under InWorldz's behaviour). For tests.</summary>
        internal float LinearMotorAge => _props.Dynamics.LinearDecayIndex;
        internal float AngularMotorAge => _props.Dynamics.AngularDecayIndex;

        /// <summary>The factor the attractor's strength has grown to while the vehicle is overturned (InWorldz's behaviour;
        /// 1 otherwise). For tests.</summary>
        internal float AttractorEscalation => _props.Dynamics.VerticalForceAdjust;

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
                case Vehicle.TYPE_SAILBOAT:    newType = VehicleType.Sailboat; break;
                case Vehicle.TYPE_MOTORCYCLE:  newType = VehicleType.Motorcycle; break;
                default:                    newType = VehicleType.None; break;
            }

            _props.Type = newType;
            SetVehicleDefaults(newType);
            _props.Dynamics.Reset();
            // No motor until a script sets one.
            _props.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            _props.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            _props.Dynamics.LastAccessTOD = Clock();
            _bankingAirTime = 0;

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
            bool hoverWasOn = InWorldz && HoverCommanded();
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
                    pValue = ClampF(pValue, S.MinTimescale, S.EffectiveMaxDecayTimescale);
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
                // InWorldz's parameters, held to the ranges of the InWorldz simulator's ClampFloatParam
                // (VehicleDynamics.cs:164-218); the disable-motors pair has no range there.
                case Vehicle.MOUSELOOK_AZIMUTH:
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth] = ClampF(pValue, VehicleLimits.ThresholdMouselookAngle, (float)Math.PI);
                    break;
                case Vehicle.MOUSELOOK_ALTITUDE:
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude] = ClampF(pValue, VehicleLimits.ThresholdMouselookAngle, (float)Math.PI);
                    break;
                case Vehicle.BANKING_AZIMUTH:
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth] = ClampF(pValue, VehicleLimits.ThresholdBankAngle, (float)Math.PI);
                    break;
                case Vehicle.DISABLE_MOTORS_ABOVE:
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove] = pValue;
                    break;
                case Vehicle.DISABLE_MOTORS_AFTER:
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter] = pValue;
                    break;
                case Vehicle.INVERTED_BANKING_MODIFIER:
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier] = ClampF(pValue, -10f, 10f);
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
                    pValue = ClampF(pValue, S.MinTimescale, S.EffectiveMaxDecayTimescale);
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
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = HeldAngularMotor(new Vector3(pValue, pValue, pValue));
                    if (!ScriptMotorIgnored())
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

            // InWorldz: "Special case for legacy scripts: height<=0 nd eff=0 means no hover". A hover that was on is cleared
            // and its timescale put to the off value (VehicleDynamics.cs:1562-1571).
            if (hoverWasOn && HoverOffByLegacyRule())
                _props.ParamsFloat[VehFloatParam.HoverTimescale] = S.MaxHoverTimescale;
        }

        // Hover is commanded: its timescale is below the off value and the legacy rule does not switch it off.
        private bool HoverCommanded()
            => _props.GetFloat(VehFloatParam.HoverTimescale, 1000f) < S.MaxHoverTimescale && !HoverOffByLegacyRule();

        // InWorldz: a hover height of zero or below with efficiency zero is no hover (VehicleDynamics.cs:1563-1564).
        private bool HoverOffByLegacyRule()
            => _props.GetFloat(VehFloatParam.HoverHeight, 0f) <= 0f && _props.GetFloat(VehFloatParam.HoverEfficiency, 0f) == 0f;

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
                    pValue = HeldAngularMotor(pValue);
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = pValue;
                    if (!ScriptMotorIgnored())
                        MoveAngular(pValue);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue.X = ClampF(pValue.X, S.MinTimescale, S.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, S.MinTimescale, S.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue = HeldLinearMotor(pValue);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = pValue;
                    MoveLinear(pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue.X = ClampF(pValue.X, -S.MaxMotorOffset, S.MaxMotorOffset);
                    pValue.Y = ClampF(pValue.Y, -S.MaxMotorOffset, S.MaxMotorOffset);
                    pValue.Z = ClampF(pValue.Z, -S.MaxMotorOffset, S.MaxMotorOffset);
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset] = pValue;
                    break;
                // InWorldz takes a value for each axis of the four motor timescales (llSetVehicleVectorParam with codes 30,
                // 31, 34 and 35; Halcyon's VectorParams.cs:45-52 and VehicleDynamics.cs:406-427), each held to the same
                // range the float setter gives all three. Second Life documents these as floats and the call is ignored.
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.LinearMotorTimescale] = HeldTimescales(pValue, S.MaxTimescale);
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.AngularMotorTimescale] = HeldTimescales(pValue, S.MaxTimescale);
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale] = HeldTimescales(pValue, S.EffectiveMaxDecayTimescale);
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale] = HeldTimescales(pValue, S.EffectiveMaxDecayTimescale);
                    break;
                case Vehicle.BLOCK_EXIT:
                    // Not simulated; ignored, as Halcyon did
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
                    // A rotation with a NaN or infinite component is not a rotation: refuse it and keep the frame, as
                    // the float and vector parameters do. (Normalize turns a NaN into the identity and an infinity
                    // into NaN, either of which would turn the whole vehicle.)
                    if (!float.IsFinite(pValue.X) || !float.IsFinite(pValue.Y) || !float.IsFinite(pValue.Z) || !float.IsFinite(pValue.W))
                        return;
                    pValue = Quaternion.Normalize(in pValue);
                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = pValue;
                    break;
                case Vehicle.ROLL_FRAME:
                    // Not simulated; ignored, as Halcyon did
                    break;
            }
        }

        // A timescale on each axis, held to the region's shortest timescale and the given longest.
        private Vector3 HeldTimescales(Vector3 v, float longest)
            => new Vector3(ClampF(v.X, S.MinTimescale, longest), ClampF(v.Y, S.MinTimescale, longest), ClampF(v.Z, S.MinTimescale, longest));

        // A motor direction held to the region's limits, each axis.
        private Vector3 HeldLinearMotor(Vector3 v)
            => new Vector3(ClampF(v.X, -S.MaxLinearSpeed, S.MaxLinearSpeed), ClampF(v.Y, -S.MaxLinearSpeed, S.MaxLinearSpeed),
                           ClampF(v.Z, -S.MaxLinearSpeed, S.MaxLinearSpeed));

        // The angular motor direction. Second Life documents the cap on its magnitude ("The magnitude of the angular motor
        // is capped to be no more than two rotations per second (4 * PI radians/sec)"), so the vector is scaled down with
        // its direction kept. The InWorldz simulator holds each axis instead (VehicleDynamics.cs:394-398), which lets a
        // diagonal direction reach the cap times the square root of 3.
        private Vector3 HeldAngularMotor(Vector3 v)
        {
            if (InWorldz)
                return new Vector3(ClampF(v.X, -S.MaxAngularSpeed, S.MaxAngularSpeed), ClampF(v.Y, -S.MaxAngularSpeed, S.MaxAngularSpeed),
                                   ClampF(v.Z, -S.MaxAngularSpeed, S.MaxAngularSpeed));
            // In double, so that a huge component does not overflow the length.
            double length = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y + (double)v.Z * v.Z);
            if (!(length > S.MaxAngularSpeed))
                return v;
            float scale = (float)(S.MaxAngularSpeed / length);
            return new Vector3(v.X * scale, v.Y * scale, v.Z * scale);
        }

        // =================================================================
        // Process vehicle flags from LSL
        // =================================================================
        // As on ubODE (ODEDynamics.ProcessVehicleFlags): set these flags, or remove them; remove with -1 removes every
        // flag, and set with -1 sets every flag.
        public void ProcessVehicleFlags(int pParam, bool remove)
        {
            ExtendedVehicleFlags flags = FromScriptFlags(pParam);

            if (remove)
                _props.Flags &= ~flags;
            else
                _props.Flags |= flags;
        }

        // =================================================================
        // A saved vehicle handed back (the host's PhysicsActor.SetVehicle)
        // =================================================================
        /// <summary>A saved vehicle's flags, in place of every flag it has (its type's).</summary>
        public void RestoreFlags(int flags)
        {
            _props.Flags = FromScriptFlags(flags);
        }

        // A script's flag number, and the number a saved record keeps, as the engine's flags. The bits 0x1 to 0x8000 are the
        // same in both. The InWorldz extensions are numbered differently: a script sends REACT_TO_CURRENTS 0x10000,
        // REACT_TO_WIND 0x20000, LIMIT_MOTOR_DOWN 0x40000, TORQUE_WORLD_Z 0x80000, MOUSEPOINT_STEER 0x100000 and
        // MOUSEPOINT_BANK 0x200000 (the InWorldz simulator's VehicleFlags.cs), where the engine's enum has them at
        // 1 << 16 to 1 << 21 in another order. A bit above 0x200000 has no flag and is kept where it is, so -1 is still
        // every bit.
        internal static ExtendedVehicleFlags FromScriptFlags(int script)
        {
            ExtendedVehicleFlags flags = (ExtendedVehicleFlags)(script & (0xFFFF | ~0x3FFFFF));
            if ((script & 0x10000) != 0) flags |= ExtendedVehicleFlags.ReactToCurrents;
            if ((script & 0x20000) != 0) flags |= ExtendedVehicleFlags.ReactToWind;
            if ((script & 0x40000) != 0) flags |= ExtendedVehicleFlags.LimitMotorDown;
            if ((script & 0x80000) != 0) flags |= ExtendedVehicleFlags.TorqueWorldZ;
            if ((script & 0x100000) != 0) flags |= ExtendedVehicleFlags.MousePointSteer;
            if ((script & 0x200000) != 0) flags |= ExtendedVehicleFlags.MousePointBank;
            return flags;
        }

        /// <summary>Both motors and the banking turn off, their directions kept as settings: a failed region crossing, as
        /// ubODE's ODEDynamics.Stop (motor effect 0), so a vehicle put back inside the edge does not drive out again until a
        /// script sets a motor.</summary>
        public void StopMotors()
        {
            _props.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            _props.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            _props.Dynamics.LinearTargetVelocity = Vector3.Zero;
            _props.Dynamics.AngularTargetVelocity = Vector3.Zero;
            _props.Dynamics.BankingDirection = 0;
            _props.Dynamics.BankingTargetVelocity = 0;
        }

        /// <summary>A saved vehicle's motor directions, held to the region's limits as a script's are, kept as its settings
        /// without starting either motor: the motors run once a script sets them again, as on ubODE, whose
        /// ODEDynamics.DoSetVehicle stores the directions with no motor effect.</summary>
        public void RestoreMotorDirections(Vector3 linear, Vector3 angular)
        {
            if (float.IsFinite(linear.X) && float.IsFinite(linear.Y) && float.IsFinite(linear.Z))
                _props.ParamsVec[VehVectorParam.LinearMotorDirection] = HeldLinearMotor(linear);
            if (float.IsFinite(angular.X) && float.IsFinite(angular.Y) && float.IsFinite(angular.Z))
                _props.ParamsVec[VehVectorParam.AngularMotorDirection] = HeldAngularMotor(angular);
        }

        #endregion // Vehicle Parameter Setting

        #region Step — Main Simulation Entry Point

        /// <summary>
        /// Called every physics frame by the host, BEFORE the engine step.
        /// This is the main simulation entry point, in the place of Halcyon's VehicleDynamics.Simulate().
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

            // InWorldz's camera steering sets the angular motor from the driver's camera before the motors act.
            if (InWorldz)
                SimulateMouselookInput();

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
            if (S.DoAngularDeflection)
            {
                SimulateAngularDeflection(timeStep);
            }

            // Linear deflection — changing velocity toward the forward axis (the "tracking" bite).
            if (S.DoLinearDeflection)
            {
                SimulateLinearDeflection(timeStep);
            }

            // (The sled's slope assist is an acceleration inside the linear motor and friction step: SledAssist.)

            // -------------------------------------------------------
            // Hover — maintain target height above terrain/water/global
            SimulateHover(pTimestep);

            // -------------------------------------------------------
            // Vertical attractor and banking
            _attractorChange = Vector3.Zero;
            float tilt = 0f;
            if (S.DoVerticalAttractor)
                SimulateVerticalAttractor(pTimestep, out tilt, out _);

            // -------------------------------------------------------
            // Motors and friction, linear and angular, then the banking turn motor. They are stepped over the
            // step the engine is about to take (pTimestep), not the smoothed one: each is the exact solution
            // over that step.
            SimulateLinearMotorAndFriction(pTimestep, S.DoMotors, S.DoLinearFriction);
            float angularz = SimulateAngularMotorAndFriction(pTimestep, S.DoMotors, S.DoAngularFriction);
            float banking = S.DoVerticalAttractor ? SimulateBanking(pTimestep, tilt) : 0f;
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
        /// sleeps. A vehicle with VEHICLE_FLAG_MOUSELOOK_STEER or _BANK is never idle: a sleeping vehicle would read no
        /// camera, and the InWorldz simulator keeps such a vehicle awake for that reason (its sleep threshold is 0;
        /// VehicleDynamics.cs:784-789). Under InWorldz's behaviour, where the two MOUSEPOINT flags steer and bank from the
        /// camera too, the same holds for them.
        /// </summary>
        public bool IsIdle
            => (_props.Flags & (ExtendedVehicleFlags.MouselookSteer | ExtendedVehicleFlags.MouselookBank)) == 0
            && !(InWorldz && (_props.Flags & (ExtendedVehicleFlags.MousePointSteer | ExtendedVehicleFlags.MousePointBank)) != 0)
            && MotorIdle(_props.Dynamics.LinearDirection, _props.GetVec(VehVectorParam.LinearMotorDecayTimescale), _props.Dynamics.LinearDecayIndex)
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
            Vector3 frictionTs = S.DoLinearFriction ? _props.GetVec(VehVectorParam.LinearFrictionTimescale) : FrictionOff;
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
        /// Derived from Halcyon VehicleMotor.MoveLinear(); the decay restarts as Second Life documents it.
        /// </summary>
        private void MoveLinear(Vector3 direction)
        {
            if (_props.Type == VehicleType.None) return;

            _props.Dynamics.LastAccessTOD = Clock();

            // InWorldz: "Scale the target if the motor has been decaying" (VehicleDynamics.cs:3054-3062).
            if (InWorldz && _props.Dynamics.LinearDecayIndex > VehicleLimits.ThresholdLinearMotorEngaged)
            {
                Vector3 decayTs = _props.GetVec(VehVectorParam.LinearMotorDecayTimescale);
                float index = _props.Dynamics.LinearDecayIndex;
                _props.Dynamics.LinearTargetVelocity = new Vector3(
                    _props.Dynamics.LinearTargetVelocity.X * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.X),
                    _props.Dynamics.LinearTargetVelocity.Y * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.Y),
                    _props.Dynamics.LinearTargetVelocity.Z * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.Z));
            }

            // The motor's grip restarts at full on every set and decays from here (s = 0).
            _props.Dynamics.LinearDecayIndex = MotorDecayStart(VehVectorParam.LinearMotorTimescale);

            _props.Dynamics.TargetLinearDelta = direction - _props.Dynamics.LinearDirection;
            _props.Dynamics.LinearDirection = direction;
            _body.KeepAwake();
        }

        /// <summary>
        /// Called when VehicleAngularMotorDirection is set.
        /// Derived from Halcyon VehicleMotor.MoveAngular(); the decay restarts as Second Life documents it.
        /// </summary>
        private void MoveAngular(Vector3 direction)
        {
            if (_props.Type == VehicleType.None) return;

            _props.Dynamics.LastAccessTOD = Clock();

            // InWorldz: "Scale the target if the motor has been decaying" (VehicleDynamics.cs:3087-3095): the velocity the
            // ramp works from is the target it reached, less the decay since.
            if (InWorldz && _props.Dynamics.AngularDecayIndex > VehicleLimits.ThresholdAngularMotorEngaged)
            {
                Vector3 decayTs = _props.GetVec(VehVectorParam.AngularMotorDecayTimescale);
                float index = _props.Dynamics.AngularDecayIndex;
                _props.Dynamics.AngularTargetVelocity = new Vector3(
                    _props.Dynamics.AngularTargetVelocity.X * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.X),
                    _props.Dynamics.AngularTargetVelocity.Y * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.Y),
                    _props.Dynamics.AngularTargetVelocity.Z * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.Z));
            }

            // The motor's grip restarts at full on every set and decays from here (s = 0).
            _props.Dynamics.AngularDecayIndex = MotorDecayStart(VehVectorParam.AngularMotorTimescale);

            direction = LimitedAngularRate(direction);
            _props.Dynamics.TargetAngularDelta = direction - _props.Dynamics.AngularDirection;
            _props.Dynamics.AngularDirection = direction;
            _body.KeepAwake();
        }

        /// <summary>
        /// The angular rate limiter. InWorldz scales the angular motor direction it is given by
        ///
        ///   s = (angular deflection timescale + 0.001) / (angular deflection efficiency + 0.00001)
        ///
        /// when s is below 1, but by no less than 0.1: "SL induces an angular rate limiter based on the angular
        /// deflection timescale. Basically angular deflection is enabled even when the vehicle is not moving"
        /// (VehicleDynamics.cs:3107-3116, read, not run). A vehicle whose deflection is strong (efficiency up) and fast
        /// (timescale down) is turned more slowly by its motor. The floor is [Jolt] VehicleAngularLimiterFloor
        /// (<see cref="VehicleSettings.AngularLimiterFloor"/>); with 1 there is no limiter. The scale is taken from the
        /// deflection settings at the moment the direction is set, as InWorldz does; the direction a script set is kept
        /// as it was (GetVecParam) and the motor works on the scaled one. Under both behaviours.
        /// </summary>
        private Vector3 LimitedAngularRate(Vector3 direction)
        {
            float timescale = _props.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
            float scale = (timescale + VehicleLimits.AngularLimiterTimescaleBias) / (efficiency + VehicleLimits.AngularLimiterEfficiencyBias);
            if (scale < 1.0f)
                direction *= Math.Max(scale, S.AngularLimiterFloor);
            return direction;
        }

        /// <summary>The angular motor direction the motor works on: the script's, after the angular rate limiter. For tests.</summary>
        internal Vector3 AngularMotorDirectionInEffect => _props.Dynamics.AngularDirection;

        // InWorldz holds the decay of a quick motor back by a few hundredths of a second, "SL cheat, ramp and decay
        // should proceed simultaneously, but short decays often will prevent motor ramp-up altogether. The cheat is to
        // delay the decay by a tiny amount" (VehicleDynamics.cs:3066-3072 for the linear motor, 3099-3105 for the angular
        // one). A motor whose timescale vector is shorter than 0.9 (its length) starts its decay clock at
        // 0.0156 (1 - f) - 0.06 f, with f = 1 - length: negative, so the grip stays whole for that long (up to 0.06 s,
        // for a motor timescale near 0); any other motor starts it at 0.0156 s. Second Life's decay starts at the set.
        private float MotorDecayStart(VehVectorParam motorTimescale)
        {
            if (!InWorldz)
                return 0.0f;
            float length = _props.GetVec(motorTimescale).Length();
            float f = length < MotorDecayDelayBelow ? 1.0f - length : 0.0f;
            return VehicleLimits.MinPhysicsTimestep * (1.0f - f) - VehicleLimits.ThresholdDelayFubar * f;
        }

        // A motor timescale vector shorter than this (m) has its decay delayed (VehicleDynamics.cs:3070, 3103).
        internal const float MotorDecayDelayBelow = 0.9f;

        // The motor step on one axis. Where the decay clock reads negative (InWorldz's delay) the motor holds its whole
        // grip until the clock reaches zero and decays from there; everywhere else it is VehicleMotorSolver.Step.
        private static double MotorStep(double v0, double motor, double motorTimescale, double decayTimescale, double age,
                                        double frictionTimescale, double h, double accel = 0.0)
        {
            if (age < 0)
            {
                double delay = -age;
                if (h <= delay)
                    return VehicleMotorSolver.StepFullGrip(v0, motor, motorTimescale, frictionTimescale, h, accel);
                double v = VehicleMotorSolver.StepFullGrip(v0, motor, motorTimescale, frictionTimescale, delay, accel);
                return VehicleMotorSolver.Step(v, motor, motorTimescale, decayTimescale, 0.0, frictionTimescale, h - delay, accel);
            }
            return VehicleMotorSolver.Step(v0, motor, motorTimescale, decayTimescale, age, frictionTimescale, h, accel);
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
        /// Reset all dynamics state. Derived from Halcyon VehicleMotor.ResetDynamics().
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

        // The buoyancy the vehicle has: its VEHICLE_BUOYANCY, except that InWorldz makes a value from 0.977 up to (not
        // including) 1 into 1, "Legacy grid hack" (PhysxPrim.cs:2963-2965, reached from the vehicle's buoyancy through
        // VehicleDynamics.cs:1546-1548): a vehicle set to 0.98 floats, where Second Life's would sink slowly.
        private float Buoyancy()
        {
            float buoyancy = _props.GetFloat(VehFloatParam.Buoyancy, 0f);
            if (InWorldz && buoyancy >= LegacyFloatBuoyancy && buoyancy < 1f)
                return 1f;
            return buoyancy;
        }

        internal const float LegacyFloatBuoyancy = 0.977f;

        /// <summary>
        /// The share of the world's gravity the vehicle feels: 1 - buoyancy, times the ground factor for a ground
        /// vehicle touching something. With hover on and HOVER_UP_ONLY, buoyancy vanishes above the hover height
        /// (Linden_Vehicle_Tutorial, "Buoyancy": "the buoyancy effect vanishes when the vehicle is above its hover
        /// height").
        /// </summary>
        private float GravityShare()
        {
            float buoyancy = Buoyancy();
            // (InWorldz's hover has no such rule: its buoyancy stays above the hover height.)
            if (HoverTarget(out float targetZ, out bool upOnly) && upOnly && !InWorldz && _body.Position.Z > targetZ)
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
        /// Angular deflection, as Second Life documents it (wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial, "Linear and
        /// Angular Deflection"; LlSetVehicleFloatParam): it reorients the vehicle so that its x axis points the way it is
        /// moving, the timescale being the time constant of an exponential decay toward full deflection and the efficiency a
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
            if (InWorldz)
            {
                SimulateAngularDeflectionInWorldz(h);
                return;
            }
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

        /// <summary>
        /// InWorldz's angular deflection (VehicleDynamics.cs:1114-1169, read, not run), in place of the documented law
        /// above: a push of angular velocity each step that is not a share of the remaining angle but speedShare x pi x
        /// (h / T) x efficiency x ln(1 + angle), along the normalised Euler angles of the rotation from the nose to the
        /// velocity, with a damping term within pi / 64 of aligned (<see cref="HalcyonVehicleLaws.AngularDeflectionPush"/>);
        /// the push is zero while the linear motor is stalled against something (<see cref="IsLinearMotorStalled"/>), and it is
        /// applied only when one of its components is at least pi / 512, as an accumulated velocity change. It is a push, not a
        /// rate, so nothing of the last step is taken back.
        /// </summary>
        private void SimulateAngularDeflectionInWorldz(float h)
        {
            float timescale = _props.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
            Vector3 push = HalcyonVehicleLaws.AngularDeflectionPush(_rotation, _worldLinearVel, _localLinearVel, _worldAngularVel,
                timescale, efficiency, h, S.ReferenceSpeed);
            if (push == Vector3.Zero)
                return;
            if (IsLinearMotorStalled())
                return;
            if (Math.Abs(push.X) >= VehicleLimits.ThresholdAngularMotorDeltaV ||
                Math.Abs(push.Y) >= VehicleLimits.ThresholdAngularMotorDeltaV ||
                Math.Abs(push.Z) >= VehicleLimits.ThresholdAngularMotorDeltaV)
                AddTorqueVelocityChange(push);
        }

        // The turning rate angular deflection gave the body in the last step.
        private Vector3 _angularDeflectionRate;

        // Directions closer than this (the sine of the angle between them) are already aligned.
        private const float MinTurnSine = 1e-6f;

        /// <summary>
        /// Linear deflection, as Second Life documents it (wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial, "Linear and
        /// Angular Deflection"; LlSetVehicleFloatParam): it rotates the velocity until it points along the vehicle's x axis,
        /// keeping its speed, with the timescale the time constant of an exponential decay toward full deflection and the
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
            Vector3 u = velocity / speed, d = Vector3.Normalize(in toward);
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
            float share = 1f - Buoyancy();
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
            if (InWorldz)
                return GravityShareBelow();   // buoyancy does not vanish above the height
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

            // InWorldz: with HOVER_UP_ONLY the hover force acts only "if diff > 0 || Buoyancy > 0" (diff the height still
            // to climb): below its height the vehicle is lifted as ever, but above it hover acts only while buoyancy is
            // positive, and then pushes the vehicle down to its height as an ordinary hover does ("bug for bug compatible
            // with standard vehicles; unless there is some positive buoyancy, hover does not work", PhysxPrim.pid.cs:323-325;
            // the flag reaches it from VehicleDynamics.cs:1596-1597). So a buoyant up-only hover is a plain hover.
            if (upOnly && InWorldz && Buoyancy() > 0f)
                upOnly = false;

            // If timescale is effectively disabled, skip
            if (hoverTimescale >= S.MaxHoverTimescale)
                return false;

            // InWorldz: a height of zero or below with efficiency zero is no hover, "legacy scripts" (VehicleDynamics.cs:1563-1564).
            if (InWorldz && HoverOffByLegacyRule())
                return false;

            Vector3 pos = _body.Position;
            float targetBase;

            // Determine the base height based on hover flags. The global height comes first: with HOVER_GLOBAL_HEIGHT
            // set the vehicle hovers at that height rather than above the ground or water (the flag's page on the
            // Second Life wiki), whatever other hover flag is set with it; InWorldz's hover does the same ("Global hover
            // overrides water and ground", VehicleDynamics.cs:1575-1576).
            if ((_props.Flags & ExtendedVehicleFlags.HoverGlobalHeight) != 0)
            {
                targetBase = 0f; // Global = absolute height, hover height is the target
            }
            else if ((_props.Flags & ExtendedVehicleFlags.HoverWaterOnly) != 0
                     && (_props.Flags & ExtendedVehicleFlags.HoverTerrainOnly) == 0)
            {
                targetBase = _body.GetWaterLevel(pos);
            }
            else if ((_props.Flags & ExtendedVehicleFlags.HoverTerrainOnly) != 0
                     && (_props.Flags & ExtendedVehicleFlags.HoverWaterOnly) == 0)
            {
                targetBase = _body.GetTerrainHeight(pos);
            }
            else
            {
                // Neither flag, or both: the higher of terrain and water. The Second Life page for the flags leaves
                // both set undefined; InWorldz hovers over both ("both flags set or both clear means hover both",
                // VehicleDynamics.cs:1580-1588, height taken as above the higher of the two, PhysxPrim.pid.cs:372-378).
                targetBase = Math.Max(_body.GetTerrainHeight(pos), _body.GetWaterLevel(pos));
            }

            targetZ = targetBase + hoverHeight;
            return true;
        }

        #endregion

        #region Vertical Attractor

        /// <summary>
        /// The vertical attractor, as Second Life documents it (wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial, "The
        /// Vertical Attractor"; LlSetVehicleFloatParam): a spring that brings the vehicle's local z axis to the world's up, with
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
        /// VEHICLE_FLAG_LIMIT_ROLL_ONLY unlocks the attractor about the pitch axis (the tutorial's section on the vertical
        /// attractor, and LlSetVehicleFlags, which offers the flag to vehicles that must be able to climb and dive), for
        /// every vehicle type: the attractor then corrects roll and leaves pitch alone.
        ///
        /// Ported from Halcyon VehicleDynamics.SimulateVerticalAttractor(), then changed to the documented damped spring
        /// above; what is kept from Halcyon's version is described where it is applied below.
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

            // InWorldz: the attractor goes dormant once the tilt has stopped changing.
            if (InWorldz && AttractorDormant(angle, h))
            {
                LetGoOfAttractor(x, y);
                return;
            }

            double roll = Math.Atan2(y.Z, z.Z);
            double pitch = Math.Asin(Utils.Clamp(-x.Z, -1f, 1f));
            double stiffness = 1.0 / ((double)timescale * timescale);
            double damping = efficiency / (double)timescale;
            bool rollOnly = (_props.Flags & ExtendedVehicleFlags.LimitRollOnly) != 0;

            // InWorldz: a stronger spring, and with LIMIT_ROLL_ONLY a weak pitch spring for cars, boats and sleds.
            double rollStiffness = stiffness, pitchStiffness = stiffness;
            bool pitchSprung = !rollOnly;
            if (InWorldz)
            {
                rollStiffness = pitchStiffness = stiffness * AttractorStrength(angle, h);
                if (rollOnly && _props.Type != VehicleType.Airplane && _props.Type != VehicleType.Balloon)
                {
                    pitchStiffness *= RollOnlyPitchShare;
                    pitchSprung = true;
                }
            }

            // The spring's own rates: the body's, plus what the last step's turn left out of them.
            double rollRate = Vector3.Dot(_rawWorldAngularVel, x);
            double pitchRate = Vector3.Dot(_rawWorldAngularVel, y);
            (double rollTurn, double rollCarry) = VehicleSpring.MoveOverGeneral(roll, rollRate + (_attractorCarry?.X ?? 0), rollStiffness, damping, h);
            // With LIMIT_ROLL_ONLY the pitch is left alone: no spring and no damping on it (any rate the last step's
            // pitch turn left out is handed back).
            double pitchTurn = pitchRate + (_attractorCarry?.Y ?? 0), pitchCarry = 0;
            if (pitchSprung)
                (pitchTurn, pitchCarry) = VehicleSpring.MoveOverGeneral(pitch, pitchTurn, pitchStiffness, damping, h);
            _attractorCarry = new Vector3((float)rollCarry, (float)pitchCarry, 0f);

            // Applied whole with the motors' change (TorqueFini's per-step clean-up would stop it short).
            _attractorChange = x * (float)(rollTurn - rollRate) + y * (float)(pitchTurn - pitchRate);
            _motorTorqueVelChange += _attractorChange;
        }

        // The change of angular velocity the attractor gave the body this step (world); zero when it did not act. InWorldz's
        // angular motor leaves an axis alone while the attractor acts on it and the motor has no direction there.
        private Vector3 _attractorChange;

        // What the last attractor step's turn left out of the spring's roll and pitch rates; null when it did not act.
        private Vector3? _attractorCarry;

        /// <summary>
        /// InWorldz's attractor dormancy (VehicleDynamics.cs:1255-1264, read, not run): "Go dormant if in the sweet spot or
        /// if no angular changes are happening. Do not go dormant if the vehicle is overturned." The attractor acts while the
        /// tilt has changed by at least pi / 256 (0.7 degrees) between two steps within the last
        /// <see cref="VehicleLimits.MaxAttractDormancy"/> seconds (1.5 s), or while the tilt is at least
        /// <see cref="VehicleLimits.ThresholdOverturnAngle"/> (0.6 pi). The tilt it is compared with is the one of the last step
        /// the attractor acted on, so a dormant attractor wakes once the tilt has moved 0.7 degrees from where it went to
        /// sleep. Halcyon counts frames of about 15 ms; here the change that wakes it is taken per
        /// <see cref="HalcyonStep"/> of time while the attractor acts (the tilt it is compared with is then the last step's),
        /// so the step rate does not decide how long it stays awake; once it has been dormant for a step the tilt it is
        /// compared with is the stale one and the change is the displacement itself. An airplane is measured from the nearer of upright and
        /// inverted, as there.
        /// </summary>
        private bool AttractorDormant(float tilt, float h)
        {
            VehicleDynamicsState d = _props.Dynamics;
            bool wasDormant = d.AttractorQuietTime >= VehicleLimits.MaxAttractDormancy;
            d.AttractorQuietTime += h;
            double threshold = VehicleLimits.ThresholdAttractorAngle * (wasDormant ? 1.0 : h / HalcyonStep);
            if (Math.Abs(d.LastVerticalAngle - tilt) >= threshold)
                d.AttractorQuietTime = 0f;
            float measured = _props.Type == VehicleType.Airplane && tilt > Math.PI / 2.0 ? (float)Math.PI - tilt : tilt;
            return measured < VehicleLimits.ThresholdOverturnAngle && d.AttractorQuietTime >= VehicleLimits.MaxAttractDormancy;
        }

        // ---- InWorldz's attractor (VehicleDynamics.cs, SimulateVerticalAttractor 1217-1334) ----
        //
        // Halcyon scales the attractor's restoring force by a factor that depends on the tilt and on how long the vehicle
        // has been overturned; here that factor scales the stiffness of the spring (the damping is left as it is, as
        // Halcyon leaves its damping term). Its force baseline, pi e^(3 efficiency), is not taken: the spring keeps
        // the timescale and efficiency Second Life documents.
        //   - Vehicles other than airplanes "have very strong restorative forces": x (1 + (1 + tilt / pi)^4) (1272-1274).
        //   - Other than airplanes, while overturned (tilt at least 0.6 pi) and not improving (the tilt no smaller than
        //     the step before's) the factor grows by 1.3 every step; otherwise it falls by 1.1 every step, to 1 (1288-1302).
        //     Halcyon's step is 15 ms (PhysxScene.cs:55-56); the growth and fall are taken per 15 ms of time, so they
        //     do not depend on the step rate here.
        //   - Halcyon has no limit on the growth (its PhysX body's angular speed limit bounds the effect); here the factor
        //     stops at MaxAttractorEscalation, because beyond about that the exact spring rings from one step to the
        //     next and the vehicle no longer rights itself smoothly.
        // The tilt is the angle between the vehicle's z axis and up; Halcyon takes it from the Euler roll and pitch,
        // which agree for the tilts of a vehicle that is righting itself.

        /// <summary>InWorldz's strength factor for a tilt of <paramref name="tilt"/> radians: 1 + (1 + tilt / pi)^4.</summary>
        internal static float InWorldzAttractorBoost(float tilt)
            => 1f + (float)Math.Pow(1.0 + tilt / Math.PI, 4.0);

        // With LIMIT_ROLL_ONLY, the share of its pitch spring a car, boat or sled keeps: "vtwix.Y *= 0.1f"
        // (VehicleDynamics.cs:1278-1284, "Other vehicles need a tad more stablization because of stronger PhysX collisions").
        internal const double RollOnlyPitchShare = 0.1;

        // Halcyon's physics step, over which its strength factor grows by 1.3 or falls by 1.1.
        internal const double HalcyonStep = 0.015;
        internal const double EscalationGrowth = 1.3, EscalationFall = 1.1;
        internal const float MaxAttractorEscalation = 10f;

        private float AttractorStrength(float tilt, float h)
        {
            VehicleDynamicsState d = _props.Dynamics;
            float last = d.LastVerticalAngle;
            d.LastVerticalAngle = tilt;
            if (_props.Type == VehicleType.Airplane)
                return 1f;

            double steps = h / HalcyonStep;
            if (tilt >= VehicleLimits.ThresholdOverturnAngle && tilt >= last)
                d.VerticalForceAdjust = Math.Min(d.VerticalForceAdjust * (float)Math.Pow(EscalationGrowth, steps), MaxAttractorEscalation);
            else
                d.VerticalForceAdjust = Math.Max(d.VerticalForceAdjust / (float)Math.Pow(EscalationFall, steps), 1f);
            return InWorldzAttractorBoost(tilt) * d.VerticalForceAdjust;
        }

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
        /// efficiency leans into the turn, negative leans out of it. "The magnitude of the yaw effect will be
        /// proportional to the VEHICLE_BANKING_EFFICIENCY, the angle of the roll rotation, and sometimes the vehicle's
        /// velocity along its preferred axis of motion", the last with BANKING_MIX toward 1:
        ///
        ///   target = -roll * attitude * eff * pi * ((1 - mix) + mix * speed / Vref)       (roll the angle about the
        ///            vehicle's x axis, as the vertical attractor measures it, whatever the pitch; upside down, measured
        ///            from the nearer side, with attitude -1; Vref the reference speed, 30 m/s by default)
        ///
        /// and the yaw rate about world z approaches it with BANKING_TIMESCALE, the documented "time it takes for the
        /// banking behavior to defeat a preexisting angular velocity about the world z-axis":
        ///
        ///   w' = (s target - w) / T,   stepped exactly: w(h) = s target + (w0 - s target) e^(-h / T).
        ///
        /// s is the banking's strength: 1, but with VEHICLE_FLAG_LIMIT_MOTOR_UP in the air (LlSetVehicleFlags: "the
        /// strength of the banking will decay when the vehicle no longer experiences collisions. The decay timescale is
        /// the same as VEHICLE_BANKING_TIMESCALE") s = e^(-t_air / T), t_air the time since it last touched something.
        /// It is whole again on the step after the vehicle touches something. With s fading over the step the
        /// equation's exact step is w(h) = (w0 + s0 target h / T) e^(-h / T), s0 its strength at the step's start.
        ///
        /// The vertical attractor must be on ("must be enabled in order for the banking behavior to function"); off
        /// at a banking timescale of 500 s or more; under ThresholdBankAngle of roll banking leaves the yaw alone.
        /// Returns the change to the angular velocity about world z.
        /// </summary>
        private float SimulateBanking(float h, float tilt)
        {
            // The time in the air at the start of this step, for LIMIT_MOTOR_UP's fade; kept whether banking acts or not.
            double airTime = _bankingAirTime;
            _bankingAirTime = _body.HasCollision ? 0 : _bankingAirTime + h;

            float timescale = _props.GetFloat(VehFloatParam.BankingTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.BankingEfficiency, 0f);
            if (!S.DoBanking || efficiency == 0f || timescale >= S.MaxAttractTimescale
                || _props.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f) >= S.MaxAttractTimescale)
                return 0f;

            float mix = _props.GetFloat(VehFloatParam.BankingMix, 0.5f);
            float forwardSpeed = Math.Abs(_rawLocalLinearVel.X);
            float speedShare = Utils.Clamp(forwardSpeed, 0, S.ReferenceSpeed) / S.ReferenceSpeed;

            // The roll: the angle about the x axis that would bring the y axis level (the attractor's roll), upside down
            // measured from the nearer side (so that it turns the way the y axis' rise did), scaled to the banking range.
            const float HalfPi = (float)(Math.PI / 2.0);
            float angle = (float)Math.Atan2((Vector3.UnitY * _rotation).Z, (Vector3.UnitZ * _rotation).Z);
            if (angle > HalfPi)
                angle = (float)Math.PI - angle;
            else if (angle < -HalfPi)
                angle = -(float)Math.PI - angle;
            // InWorldz lets a script set the range (VEHICLE_BANKING_AZIMUTH); Second Life's roll range is the whole pi / 2.
            float range = InWorldz ? _props.GetFloat(VehFloatParam.BankingAzimuth, HalfPi) : HalfPi;
            float roll = Utils.Clamp(angle * HalfPi / range, -HalfPi, HalfPi);
            if (Math.Abs(roll) <= VehicleLimits.ThresholdBankAngle)
                return 0f;
            float attitude = tilt > Math.PI / 2.0 ? -1f : 1f;
            // InWorldz: an airplane flying inverted banks by the inverted banking modifier (VehicleDynamics.cs:1241-1247,
            // 1376-1377; only an airplane is marked inverted there).
            if (InWorldz && _props.Type == VehicleType.Airplane && tilt > Math.PI / 2.0)
                efficiency *= _props.GetFloat(VehFloatParam.InvertedBankingModifier, 1f);

            float target = -roll * attitude * efficiency * (float)Math.PI * ((1f - mix) + mix * speedShare);
            // With the angular motor engaged, banking turns no faster than the legacy angular speed.
            if (_props.Dynamics.AngularDecayIndex < VehicleLimits.ThresholdAngularMotorEngaged)
                target = Utils.Clamp(target, -VehicleLimits.MaxLegacyAngularVelocity, VehicleLimits.MaxLegacyAngularVelocity);
            target = Utils.Clamp(target, -S.MaxAngularSpeed, S.MaxAngularSpeed);

            float w0 = _rawWorldAngularVel.Z;
            float w1;
            if ((_props.Flags & ExtendedVehicleFlags.LimitMotorUp) != 0 && !_body.HasCollision)
                w1 = (float)((w0 + target * Math.Exp(-airTime / timescale) * h / timescale) * Math.Exp(-h / timescale));
            else
                w1 = target + (w0 - target) * (float)Math.Exp(-h / timescale);
            return w1 - w0;
        }

        // How long the vehicle has been out of touch with anything (s), for LIMIT_MOTOR_UP's banking fade.
        private double _bankingAirTime;

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
        /// pull fades with its decay and is never cut off. Under InWorldz the motor is Halcyon's instead
        /// (<see cref="HalcyonLinearMotorChange"/>), a change on top of friction's.
        ///
        /// The engine applies the gravity itself over the step (<see cref="ApplyGravity"/>); the change written here
        /// is the exact solution less that gravity, so the two together end the step on the exact velocity.
        /// </summary>
        // The seconds InWorldz puts a linear motor's decay clock on while hover moderates it (VehicleDynamics.cs:2719).
        internal const float HoverModerationSeconds = 10.0f;

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
            Vector3 motorChange;
            if (InWorldz)
            {
                motorChange = HalcyonLinearMotorChange(h, v0, motor, motorTs, decayTs);
            }
            else
            {
                Vector3 v1 = new Vector3(
                    (float)MotorStep(v0.X, motor.X, motorTs.X, decayTs.X, age, frictionTs.X, h, gravity.X),
                    (float)MotorStep(v0.Y, motor.Y, motorTs.Y, decayTs.Y, age, frictionTs.Y, h, gravity.Y),
                    (float)MotorStep(v0.Z, motor.Z, motorTs.Z, decayTs.Z, age, frictionTs.Z, h, gravity.Z));
                v1.X = Utils.Clamp(v1.X, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                v1.Y = Utils.Clamp(v1.Y, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                v1.Z = Utils.Clamp(v1.Z, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                motorChange = (v1 - frictionOnly) * _rotation;
            }

            // The step's change in world coordinates: friction's share (with gravity's, less the g h the engine adds),
            // and the motor's share on top of it.
            Vector3 frictionChange = (frictionOnly - v0) * _rotation - gravityWorld * h;

            // InWorldz: "Cheat: Do not fight gravity." Friction that would push the vehicle up in world z is dropped
            // (VehicleDynamics.cs:1494-1495), so a falling vehicle is not slowed by its friction and one rising is. What
            // friction does to the step is the change it makes to the velocity the vehicle would have with no friction
            // (its velocity and the gravity over the step); where that change is upward in world z it is taken out,
            // leaving the vehicle to the engine's gravity on that axis.
            if (InWorldz)
            {
                float frictionUp = ((frictionOnly - (v0 + gravity * h)) * _rotation).Z;
                if (frictionUp > 0f)
                    frictionChange.Z -= frictionUp;
            }

            // VEHICLE_FLAG_LIMIT_MOTOR_UP / LIMIT_MOTOR_DOWN: the motor does not push up (down) in world terms.
            // Friction's share is left whole.
            if ((_props.Flags & ExtendedVehicleFlags.LimitMotorUp) != 0 && motorChange.Z > 0)
                motorChange.Z = 0;
            if ((_props.Flags & ExtendedVehicleFlags.LimitMotorDown) != 0 && motorChange.Z < 0)
                motorChange.Z = 0;

            bool hoverFights = false;
            if (InWorldz)
            {
                // "Gravity check. Any up Z-forces less than the effective gravity are nulled" (VehicleDynamics.cs:2707-2712,
                // read, not run): the motor's upward change of velocity this step has gravity times the share of it not
                // carried by buoyancy added to it (gravity is negative), and is dropped if that leaves it below zero. It is
                // compared as a change of velocity against an acceleration, as there.
                if (motorChange.Z > 0f)
                {
                    motorChange.Z += _body.Gravity.Z * (1f - _props.GetFloat(VehFloatParam.Buoyancy, 0f));
                    if (motorChange.Z < 0f)
                        motorChange.Z = 0f;
                }

                // "Hover moderation. If hover is present, accelerate the motor decay" (2714-2720): a motor that still moves the
                // vehicle up or down while a hover flag is set and the hover is on has its decay clock put on ten seconds.
                hoverFights = Math.Abs(motorChange.Z) > VehicleLimits.ThresholdLinearMotorDeltaV
                    && (_props.Flags & (ExtendedVehicleFlags.HoverGlobalHeight | ExtendedVehicleFlags.HoverTerrainOnly | ExtendedVehicleFlags.HoverWaterOnly)) != 0
                    && _props.GetFloat(VehFloatParam.HoverTimescale, 1000f) < S.MaxHoverTimescale;
            }

            _props.Dynamics.LinearDecayIndex += h;
            if (hoverFights)
                _props.Dynamics.LinearDecayIndex += HoverModerationSeconds;
            ApplyLinearVelocityChange(frictionChange + motorChange);
            _motorTorqueVelChange += MotorOffsetTurn(motorChange);
        }

        /// <summary>
        /// The change of world velocity InWorldz's linear motor makes in one step of h seconds (VehicleDynamics.cs:2576-2725,
        /// read, not run), in place of the documented equation: the ramp toward the direction with the motor's target velocity
        /// and the decay blend (<see cref="HalcyonVehicleLaws.LinearMotor"/>), from the velocity at the start of the step, as a
        /// change on top of friction's, which is worked out from the same start velocity. Nothing is applied when the new
        /// velocity is under 0.005 m/s on every world axis ("let the vehicle eventually sleep"), or when the motor has
        /// nothing left to add. The limit-motor flags, the gravity check and the hover moderation act on the change in the
        /// caller, as they do on Halcyon's.
        /// </summary>
        private Vector3 HalcyonLinearMotorChange(float h, Vector3 lastvel, Vector3 motor, Vector3 motorTs, Vector3 decayTs)
        {
            float index = _props.Dynamics.LinearDecayIndex;
            Vector3 target = _props.Dynamics.LinearTargetVelocity;
            Vector3 local = HalcyonVehicleLaws.LinearMotor(h, lastvel, motor, motorTs, decayTs, ref index, ref target,
                S.MaxLinearSpeed, IsLinearMotorStalled);
            _props.Dynamics.LinearDecayIndex = index;
            _props.Dynamics.LinearTargetVelocity = target;
            if (local == lastvel)
                return Vector3.Zero;

            Vector3 world = local * _rotation;
            if (Math.Abs(world.X) < VehicleLimits.ThresholdLinearMotorDeltaV &&
                Math.Abs(world.Y) < VehicleLimits.ThresholdLinearMotorDeltaV &&
                Math.Abs(world.Z) < VehicleLimits.ThresholdLinearMotorDeltaV)
                return Vector3.Zero;

            return world - _rawWorldLinearVel;
        }

        /// <summary>
        /// VEHICLE_LINEAR_MOTOR_OFFSET: the offset from the vehicle's centre of mass at which the linear motor acts
        /// (wiki.secondlife.com/wiki/LlSetVehicleVectorParam), so the motor's push also turns the vehicle, as a force at that
        /// point does (the page notes that rockets built with this offset spin out of control; the parameter's own page,
        /// VEHICLE_LINEAR_MOTOR_OFFSET, says the same). The motor's change of velocity this step, dv, is an impulse m dv at the
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
            Vector3 motorTs = _props.GetVec(VehVectorParam.AngularMotorTimescale);
            Vector3 motor = MouselookMotor(_props.Dynamics.AngularDirection, motorTs);
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

            if (InWorldz)
                return SimulateAngularMotorAndFrictionInWorldz(h, v0, motor, motorTs, decayTs, frictionTs, torqueWorldZ);

            Vector3 v1 = new Vector3(
                (float)MotorStep(v0.X, motor.X, motorTs.X, decayTs.X, age, frictionTs.X, h),
                (float)MotorStep(v0.Y, motor.Y, motorTs.Y, decayTs.Y, age, frictionTs.Y, h),
                (float)MotorStep(v0.Z, motor.Z, motorTs.Z, decayTs.Z, age, frictionTs.Z, h));
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

        /// <summary>
        /// InWorldz's angular motor and friction (VehicleDynamics.cs:2733-2960 and SimulateAngularFriction, 1402-1458; read,
        /// not run), in place of the documented equation above. Each is a change of angular velocity worked out from the
        /// velocity at the start of the step, and the two are added: the motor sets the velocity it would have (see
        /// <see cref="HalcyonAngularMotor"/>), and friction takes the share 1 - e^(-h / Tf) of that start velocity away on
        /// top. So a friction timescale shorter than the motor's does not hold the motor down, as the documented equation
        /// would. The friction is the one of the documented solver (the exact exponential, off on an axis at 1000 s or more).
        /// The motor's change is applied only when it is at least pi / 512 about the world's x or y axis (the z part is
        /// always kept, for banking), as Halcyon's is.
        /// </summary>
        private float SimulateAngularMotorAndFrictionInWorldz(float h, Vector3 v0, Vector3 motor, Vector3 motorTs, Vector3 decayTs,
                                                              Vector3 frictionTs, bool torqueWorldZ)
        {
            Vector3 motorChange = HalcyonAngularMotor(h, v0, motor, motorTs, decayTs);
            Vector3 frictionChange = new Vector3(
                (float)VehicleMotorSolver.FrictionStep(v0.X, frictionTs.X, h) - v0.X,
                (float)VehicleMotorSolver.FrictionStep(v0.Y, frictionTs.Y, h) - v0.Y,
                (float)VehicleMotorSolver.FrictionStep(v0.Z, frictionTs.Z, h) - v0.Z);

            Vector3 motorWorld = ChangeToWorld(motorChange, torqueWorldZ);
            Vector3 frictionWorld = ChangeToWorld(frictionChange, torqueWorldZ);
            if (Math.Abs(motorWorld.X) < VehicleLimits.ThresholdAngularMotorDeltaV && Math.Abs(motorWorld.Y) < VehicleLimits.ThresholdAngularMotorDeltaV)
            {
                motorWorld.X = 0f;
                motorWorld.Y = 0f;
            }

            _props.Dynamics.AngularDecayIndex += h;

            // The change about world Z is applied in Step, with banking's.
            Vector3 change = motorWorld + frictionWorld;
            float aboutWorldZ = change.Z;
            change.Z = 0;
            _motorTorqueVelChange += change;
            return aboutWorldZ;
        }

        // A change of angular velocity in the motor's frame (the vehicle's axes, or with TORQUE_WORLD_Z the vehicle's x and
        // y and the world's z) as a change of world angular velocity.
        private Vector3 ChangeToWorld(Vector3 change, bool torqueWorldZ)
            => torqueWorldZ ? new Vector3(change.X, change.Y, 0f) * _rotation + new Vector3(0f, 0f, change.Z) : change * _rotation;

        /// <summary>
        /// The change of angular velocity InWorldz's angular motor makes in one step of h seconds, in the motor's frame, from
        /// the velocity <paramref name="lastvel"/> (VehicleDynamics.cs:2733-2960, read, not run). On each axis the motor
        /// ramps the velocity toward its direction M by a per-step growth, "a stepwise iteration of the continuous
        /// a*b^(t*Tau) function" (<see cref="HalcyonVehicleLaws.GrowthRate"/>):
        ///
        ///   next = a (1 + ln(|M| / |a|) / (Tm / h)),   a = the velocity, or the motor's target if that is further along
        ///
        /// (from rest, from pi / 64 rad/s, a stiction start; toward zero, by -ln(1 + |a|) / (Tm / h); across zero, to zero and
        /// then the other way), kept as the motor's target for the next step, and the result blended with the velocity
        /// by the motor's grip e^(-s / Td) (<see cref="HalcyonVehicleLaws.MovementExpDecay"/>), s the time since the
        /// direction was set: result = next g + v (1 - g). The grip is dropped (the motor adds nothing) on an axis where the
        /// velocity is already past the motor's, and on an axis with no direction while the vertical attractor is acting;
        /// and on every axis while the linear motor is stalled against something, which also ends the motor.
        ///
        /// While the vertical attractor is on, the roll and pitch speeds the ramp gives are first clamped against it
        /// (<see cref="HalcyonVehicleLaws.AttractorClamp"/>, VehicleDynamics.cs:2809-2856). The ramp's timescale is held to at least the step (steps per timescale at least 1): Halcyon's step
        /// never exceeded its shortest timescale, so that a step longer than a quick motor's timescale does not overshoot
        /// the direction.
        /// </summary>
        private Vector3 HalcyonAngularMotor(float h, Vector3 lastvel, Vector3 motor, Vector3 motorTs, Vector3 decayTs)
        {
            VehicleDynamicsState d = _props.Dynamics;
            float[] last = { lastvel.X, lastvel.Y, lastvel.Z };
            float[] dir = { motor.X, motor.Y, motor.Z };
            float[] ts = { motorTs.X, motorTs.Y, motorTs.Z };
            float[] decay = { HalcyonVehicleLaws.MovementExpDecay(d.AngularDecayIndex, decayTs.X),
                              HalcyonVehicleLaws.MovementExpDecay(d.AngularDecayIndex, decayTs.Y),
                              HalcyonVehicleLaws.MovementExpDecay(d.AngularDecayIndex, decayTs.Z) };
            float[] target = { d.AngularTargetVelocity.X, d.AngularTargetVelocity.Y, d.AngularTargetVelocity.Z };
            float[] attractor = { _attractorChange.X, _attractorChange.Y, _attractorChange.Z };

            // If the decay hits maximum entropy no further computations are required.
            if (decay[0] == 0 && decay[1] == 0 && decay[2] == 0)
            {
                d.AngularTargetVelocity = Vector3.Zero;
                return Vector3.Zero;
            }

            float[] newvel = new float[3];
            float[] ramp = new float[3];
            float max = S.MaxAngularSpeed;

            // The attractor's timescale as the clamp reads it, held to at least the step; with the attractor switched off for
            // the region there is nothing to overpower, and the clamp is out.
            float attractTimescale = S.DoVerticalAttractor
                ? Math.Max(_props.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f), h)
                : float.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                float dirsign = VehicleMath.PosNeg(dir[i]);

                // A target velocity is used where the velocity is lower, so that friction at low speed cannot hold the
                // motor down: "The target ensures forward progress."
                float adjvel = last[i];
                if (dirsign * (target[i] - adjvel) > 0)
                    adjvel = target[i];

                ramp[i] = HalcyonVehicleLaws.GrowthRate(adjvel, dir[i], Math.Max(ts[i] / h, 1f));
                newvel[i] = adjvel + adjvel * ramp[i];

                // At the crossover point, flip to indicate the velocity is growing.
                if (ramp[i] == VehicleLimits.ThresholdInverseCrossover)
                    ramp[i] = -ramp[i];

                // Avoid zero velocity stiction when the new velocity is increasing.
                if (ramp[i] > 0 && dir[i] != 0 && Math.Abs(newvel[i]) < VehicleLimits.ThresholdAngularMotorDeltaV)
                    newvel[i] = dirsign * VehicleLimits.ThresholdAngularMotorDeltaV * 8;

                // The clamp against the vertical attractor, on the roll and pitch velocities.
                if (i < 2 && attractTimescale < S.MaxAttractTimescale)
                    newvel[i] = HalcyonVehicleLaws.AttractorClamp(newvel[i], _props.Type, attractTimescale);

                // The new target: the new velocity while ramping toward zero, else held to the direction.
                if (dir[i] * newvel[i] < 0 || ramp[i] < 0)
                    target[i] = newvel[i];
                else
                    target[i] = Utils.Clamp(newvel[i], -Math.Abs(dir[i]), Math.Abs(dir[i]));

                newvel[i] = Utils.Clamp(newvel[i], -max, max);

                // Release the motor when it has decayed and has no direction.
                if (decay[i] == 0 && dir[i] == 0)
                    newvel[i] = last[i];

                // If the velocity on the axis is already past the motor's (the motor adds no power), or the attractor is acting
                // on an axis the motor has no direction on, use the velocity.
                if ((attractor[i] != 0 && dir[i] == 0) || (ramp[i] >= 0 && dirsign * (last[i] - newvel[i]) > 0))
                    decay[i] = 0;
            }

            d.AngularTargetVelocity = new Vector3(target[0], target[1], target[2]);

            // Accelerate the angular decay when the linear motor is stalled.
            if ((decay[0] != 0 || decay[1] != 0 || decay[2] != 0) && IsLinearMotorStalled())
            {
                decay[0] = decay[1] = decay[2] = 0;
                d.AngularDecayIndex = VehicleLimits.MaxDecayTimescale * 100.0f;
                d.AngularTargetVelocity = Vector3.Zero;
            }

            // Apply the decay to each axis.
            return new Vector3(
                newvel[0] * decay[0] + last[0] * (1.0f - decay[0]) - last[0],
                newvel[1] * decay[1] + last[1] * (1.0f - decay[1]) - last[1],
                newvel[2] * decay[2] + last[2] * (1.0f - decay[2]) - last[2]);
        }

        /// <summary>
        /// The angular motor's direction with VEHICLE_FLAG_MOUSELOOK_STEER or VEHICLE_FLAG_MOUSELOOK_BANK set, a
        /// driver's camera known and the driver in mouselook (llSetVehicleFlags):
        ///
        /// - STEER: "make the angular motor try to make the vehicle turn such that its local x-axis points in the same
        ///   direction as the client-side camera". The yaw (z) and pitch (y) axes aim at the camera.
        /// - BANK: "Same as above, but relies on banking. It remaps left-right motions of the client camera (also known
        ///   as "yaw") to rotations about the vehicle's local x-axis". The pitch axis aims as with STEER; the camera's yaw
        ///   aims the roll (x) axis instead, leaning into the turn (a camera to the left rolls the vehicle left), and
        ///   banking turns the vehicle. With both flags set, BANK's remap is used.
        ///
        /// Each aimed axis asks for the turning rate that would close its angle a to the camera in one motor timescale,
        /// held to that axis of the script's motor direction, as the flags "only turn the vehicle up to the current
        /// VEHICLE_ANGULAR_MOTOR_DIRECTION; If VEHICLE_ANGULAR_MOTOR_DIRECTION is set to ZERO_VECTOR, then the vehicle
        /// will not turn":
        ///
        ///   M = clamp(a / Tm, -|M_script|, |M_script|)
        ///
        /// The motor equation then acts on it as on any motor direction, with its timescale and decay. An axis not aimed
        /// keeps the script's direction. Without a driver's camera, or with the driver not in mouselook, the flags change
        /// nothing: the vehicle behaves as if they were clear, as in InWorldz, where steering and banking from the camera
        /// need the camera's mouselook state (VehicleDynamics.cs:813-816, 479-480).
        /// </summary>
        private Vector3 MouselookMotor(Vector3 motor, Vector3 motorTs)
        {
            const ExtendedVehicleFlags mouselook = ExtendedVehicleFlags.MouselookSteer | ExtendedVehicleFlags.MouselookBank;
            // InWorldz sets the motor from the camera in SimulateMouselookInput instead.
            if (InWorldz)
                return motor;
            if ((_props.Flags & mouselook) == 0 || DriverCamera?.Invoke() is not DriverView { InMouselook: true, At: Vector3 camera }
                || !float.IsFinite(camera.X) || !float.IsFinite(camera.Y) || !float.IsFinite(camera.Z)
                || camera.LengthSquared() < MinCameraAxis)
                return motor;

            // Where the camera looks in the vehicle's frame: its yaw from the x axis (about +z, positive to the left) and
            // its pitch (about +y, positive nose down).
            Vector3 d = Vector3.Normalize(in camera) * Quaternion.Inverse(_rotation);
            double yaw = Math.Atan2(d.Y, d.X);
            double pitch = -Math.Atan2(d.Z, Math.Sqrt(d.X * d.X + d.Y * d.Y));
            motor.Y = Aim(pitch, motor.Y, motorTs.Y);
            if ((_props.Flags & ExtendedVehicleFlags.MouselookBank) != 0)
                motor.X = Aim(-yaw, motor.X, motorTs.X);
            else
                motor.Z = Aim(yaw, motor.Z, motorTs.Z);
            return motor;
        }

        /// <summary>
        /// InWorldz's camera steering (VehicleDynamics.cs:792-861 and 813-856, read, not run), which sets the angular motor
        /// from the driver's camera on every step in place of <see cref="MouselookMotor"/>'s aim. For each axis the sideways
        /// part of the camera's direction in the vehicle's frame (the yaw's sine, and for the pitch the vertical part) is
        /// taken as a share of the range the vehicle gives it (times pi / 2, over MOUSELOOK_AZIMUTH for yaw, over
        /// MOUSELOOK_ALTITUDE for pitch, pi / 4 by default, held to -1..1; <see cref="MouselookShare"/>), which is 0 within
        /// pi / 32 of the heading and otherwise shaped "into a soft exponential: (e^x-1) / (e-1)". STEERING (the mouselook
        /// steer flag with the driver in mouselook, or MOUSEPOINT_STEER in any view) sets the angular motor to
        /// (0, pitch share * the script's y, yaw share * the script's z) when either share is not 0; BANKING (the bank flag
        /// in mouselook, or MOUSEPOINT_BANK in any view) to (-yaw share * the script's x, 0, 0) when the yaw share is not 0.
        /// Both set, banking is the later and stands. Each is an ordinary set of the motor, so its decay restarts and the
        /// angular rate limiter applies; with the camera inside the zone nothing is set and the motor goes on as it was,
        /// fading by its decay. The script's direction is the one stored (<see cref="ScriptMotorIgnored"/> keeps a script's
        /// own set from moving the motor while this acts). LIMIT_ROLL_ONLY leaves the pitch share at 0.
        /// InWorldz reads the camera once in eight steps (frame number mod 8); here it is read on every step. The camera
        /// reaches the simulator some ten to twenty times a second and the motor's timescale is about a second, so a read
        /// on every step only makes the turn answer the camera sooner by under a fifth of a second; the pacing is not taken.
        /// InWorldz measures the pitch as the vertical part of the camera's own y axis (its sideways axis, which a camera
        /// that is not rolled keeps level); the camera's direction is all the host gives the vehicle, and the pitch of
        /// the camera is what a driver aims with, so the pitch share here is the camera's pitch.
        /// </summary>
        private void SimulateMouselookInput()
        {
            const ExtendedVehicleFlags any = ExtendedVehicleFlags.MouselookSteer | ExtendedVehicleFlags.MouselookBank
                | ExtendedVehicleFlags.MousePointSteer | ExtendedVehicleFlags.MousePointBank;
            ExtendedVehicleFlags flags = _props.Flags;
            if ((flags & any) == 0 || DriverCamera?.Invoke() is not DriverView { At: Vector3 camera } view
                || !float.IsFinite(camera.X) || !float.IsFinite(camera.Y) || !float.IsFinite(camera.Z)
                || camera.LengthSquared() < MinCameraAxis)
                return;

            bool steering = ((flags & ExtendedVehicleFlags.MouselookSteer) != 0 && view.InMouselook) || (flags & ExtendedVehicleFlags.MousePointSteer) != 0;
            bool banking = ((flags & ExtendedVehicleFlags.MouselookBank) != 0 && view.InMouselook) || (flags & ExtendedVehicleFlags.MousePointBank) != 0;
            if (!steering && !banking)
                return;

            Vector3 d = Vector3.Normalize(in camera) * Quaternion.Inverse(_rotation);
            float xcam = MouselookShare(d.Y, VehFloatParam.MouselookAzimuth);
            float ycam = (flags & ExtendedVehicleFlags.LimitRollOnly) != 0 ? 0f : MouselookShare(-d.Z, VehFloatParam.MouselookAltitude);
            Vector3 direction = _props.GetVec(VehVectorParam.AngularMotorDirection);
            if (steering && (xcam != 0f || ycam != 0f))
                MoveAngular(new Vector3(0f, ycam * direction.Y, xcam * direction.Z));
            if (banking && xcam != 0f)
                MoveAngular(new Vector3(-xcam * direction.X, 0f, 0f));
        }

        /// <summary>
        /// A camera's offset on one axis as InWorldz shapes it: the component of the camera's direction (in the vehicle's
        /// frame) times pi / 2 over the range, held to -1..1; 0 while that is no more than pi / 32 (0.098, "Angle below which
        /// mouselook steer/bank actions do not occur", <see cref="VehicleLimits.ThresholdMouselookAngle"/>), which with the
        /// default range of pi / 4 is a camera within 2.8 degrees of the heading; outside it the sign times
        /// (e^|x| - 1) / (e - 1), a curve that runs from 0 to 1 and is 6 percent of the way at the edge of the zone, 39
        /// percent at 15 degrees and whole from 30 degrees.
        /// </summary>
        private float MouselookShare(float component, VehFloatParam range)
        {
            float most = _props.GetFloat(range, (float)(Math.PI / 4.0));
            if (!(most > 0f))
                return 0f;
            float share = Utils.Clamp(component * (float)(Math.PI / 2.0) / most, -1f, 1f);
            float size = Math.Abs(share);
            if (size <= VehicleLimits.ThresholdMouselookAngle)
                return 0f;
            return Math.Sign(share) * (float)((Math.Exp(size) - 1.0) / (Math.E - 1.0));
        }

        // InWorldz: "Skip angular motor action if in camera mouselook mode and mouselook steer or bank are enabled"
        // (VehicleDynamics.cs:478-482): while the driver is in mouselook with either flag set, a script's set of the angular
        // motor direction is kept as the direction the camera's share is taken of, and does not move the motor.
        private bool ScriptMotorIgnored()
            => InWorldz && (_props.Flags & (ExtendedVehicleFlags.MouselookSteer | ExtendedVehicleFlags.MouselookBank)) != 0
            && DriverCamera?.Invoke() is DriverView { InMouselook: true };

        private static float Aim(double angle, float most, float timescale)
        {
            double limit = Math.Abs(most);
            return (float)Math.Clamp(angle / Math.Max(timescale, VehicleMotorSolver.MinTimescale), -limit, limit);
        }

        // A camera axis shorter than this (squared) gives no direction.
        private const float MinCameraAxis = 1e-6f;

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
            a = Vector3.Normalize(in a);
            b = Vector3.Normalize(in b);
            float dot = Vector3.Dot(a, b);

            if (dot > 0.999999f)
                return Quaternion.Identity;

            if (dot < -0.999999f)
            {
                // 180 degree rotation — find an orthogonal axis
                Vector3 axis = Vector3.Cross(Vector3.UnitX, a);
                if (Vector3.Mag(axis) < 0.0001f)
                    axis = Vector3.Cross(Vector3.UnitY, a);
                axis = Vector3.Normalize(in axis);
                return new Quaternion(axis.X, axis.Y, axis.Z, 0f);
            }

            Vector3 cross = Vector3.Cross(a, b);
            float w = (float)Math.Sqrt(a.LengthSquared() * b.LengthSquared()) + dot;
            Quaternion result = new Quaternion(cross.X, cross.Y, cross.Z, w);
            return Quaternion.Normalize(in result);
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

        #region Vehicle Type Defaults (documented, and legacy ported from Halcyon SetVehicleDefaults)

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
                    // InWorldz's car, value for value and flag for flag (VehicleDynamics.cs:1767-1804). It is not the
                    // documented car: its friction, motor and decay timescales, angular deflection and attractor
                    // differ from the page's, and, like the page's car, it has no TORQUE_WORLD_Z.
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
                                 | ExtendedVehicleFlags.HoverUpOnly | ExtendedVehicleFlags.LimitMotorUp;
                    break;

                // The two InWorldz vehicle types, which Second Life does not have: the same values with either preset set
                // (there is no documented set for them), InWorldz's own, value for value and flag for flag.
                case VehicleType.Motorcycle:
                    // VehicleDynamics.cs:1806-1844.
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(100f, 0.1f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(3f, 0.2f, 10f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = new Vector3(0f, 0f, -0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(0.5f, 1f, 1f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(0.1f, 0.1f, 0.05f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(10f, 1f, 1f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.2f, 0.8f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 2f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0.8f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 2f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 1f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0.95f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = -0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 0.1f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 1.5f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 2.5f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverUpOnly | ExtendedVehicleFlags.LimitMotorUp
                                 | ExtendedVehicleFlags.LimitMotorDown | ExtendedVehicleFlags.LimitRollOnly | ExtendedVehicleFlags.TorqueWorldZ;
                    break;

                case VehicleType.Sailboat:
                    // VehicleDynamics.cs:1886-1924. Wind and water currents are stored (the two efficiency vectors and
                    // the REACT_TO_WIND and REACT_TO_CURRENTS flags) but not simulated: no vehicle here reacts to either.
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(200f, 0.5f, 3f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(10f, 1f, 0.2f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1f, 5f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(2f, 2f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(1f, 10f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.3f, 0.3f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = new Vector3(0.02f, 0.001f, 0f);
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = new Vector3(0.1f, 0.01f, 0f);

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0.0001f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0.8f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 0.5f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 3f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0.5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 0.3f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0.8f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = -0.2f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 0.5f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverWaterOnly | ExtendedVehicleFlags.LimitMotorUp
                                 | ExtendedVehicleFlags.LimitMotorDown | ExtendedVehicleFlags.ReactToWind | ExtendedVehicleFlags.ReactToCurrents
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
                    case VehicleType.Sailboat:    return Vehicle.TYPE_SAILBOAT;
                    case VehicleType.Motorcycle:  return Vehicle.TYPE_MOTORCYCLE;
                    default:                         return Vehicle.TYPE_NONE;
                }
            }
        }
    }
}
