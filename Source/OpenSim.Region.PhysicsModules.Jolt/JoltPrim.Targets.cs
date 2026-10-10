/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// llMoveToTarget and llSetHoverHeight on a physical prim: two controllers that act before every physics step.
//
// Core hands both to the root prim's actor (SceneObjectGroup.MoveToTarget, SetHoverHeight) as PIDTarget, PIDTau and
// PIDActive, and PIDHoverHeight, PIDHoverType, PIDHoverTau and PIDHoverActive. The Second Life wiki says each one
// "critically damps" its target "in tau seconds" (llMoveToTarget, llSetHoverHeight). Both are the critically damped
// spring the Jolt vehicle hover uses at HOVER_EFFICIENCY 1 (VehicleSpring), with tau as its timescale. On the error e
// from the target:
//
//   e'' = -e / tau^2 - 2 e' / tau        so from rest   e(t) = e0 (1 + t / tau) e^(-t / tau)
//
// stepped exactly over the step the engine takes, so the motion is the same at every [Jolt] PhysicsStepRate. Gravity
// is off while one acts, as in ubODE (ODEPrim.Move applies gravity only when neither acts), so a held object stays
// where it is held and buoyancy changes nothing then. A set force and torque still act (ubODE adds them on top too):
// a steady force F holds the object F tau^2 / m from its target, where the spring balances it.
//
// Hover holds the height above the ground, the higher of ground and water, the water, or the region's zero, as
// PIDHoverType says, and follows the ground's height along the object's path, so it holds its height over a slope.
// Move to target wins when both are on, as in ubODE.
//
// Not here: llGroundRepel reaches the actor as the same four hover members, with no flag that tells it apart, so it
// acts as llSetHoverHeight does (it also pulls an object down to its height). A vehicle keeps its own hover and
// motion: neither controller acts on an active vehicle.

using System;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using OpenSim.Region.PhysicsModules.SharedBase;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed partial class JoltPrim
    {
        /// <summary>
        /// The SL wiki, llMoveToTarget: "The smallest functional tau is 0.044444444 (two physics frames, 2/45)." A
        /// smaller tau (and a smaller hover tau) acts as this one.
        /// </summary>
        internal const float MinTau = 2f / 45f;

        /// <summary>The SL wiki, llMoveToTarget: the distance to the target "must be less than 65, or no movement will occur."</summary>
        internal const float MaxTargetDistance = 65f;

        /// <summary>The SL wiki, llSetHoverHeight: "Under SL Server 1.26.2 the limit is 4096 meters above the ground level."</summary>
        internal const float MaxHoverHeight = 4096f;

        /// <summary>
        /// A body asleep this close to where a controller holds it (m) is left asleep. Further away the controller wakes
        /// it.
        /// </summary>
        internal const float HoldTolerance = 0.005f;

        private readonly object _targetLock = new object();
        private Vector3 _pidTarget;
        private float _pidTau;
        private bool _pidActive;
        private float _hoverHeight;
        private PIDHoverType _hoverType;
        private float _hoverTau;
        private bool _hoverActive;
        private bool _targetsChanged;   // a member changed since the last step: wake the body

        // Step thread only.
        private bool _heldByTarget;     // a controller acted in the last step (the body's gravity is off)
        private Vector3 _moveCarry;     // what the last move-to-target step left out of the spring's velocity
        private float _hoverCarry;      // the same for hover (vertical)
        private bool _lastWasMove;
        private BodyId _noSleepBody = BodyId.Invalid;   // the body this has told the engine not to put to sleep

        public override Vector3 PIDTarget
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "PIDTarget", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.PIDTarget = value; return; }
                lock (_targetLock) { _pidTarget = value; _targetsChanged = true; }
            }
        }

        public override float PIDTau
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "PIDTau", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.PIDTau = value; return; }
                lock (_targetLock) { _pidTau = value; _targetsChanged = true; }
            }
        }

        public override bool PIDActive
        {
            get { if (_linkRoot != null) return _linkRoot.PIDActive; lock (_targetLock) return _pidActive; }
            set
            {
                if (_linkRoot != null) { _linkRoot.PIDActive = value; return; }
                lock (_targetLock) { _pidActive = value; _targetsChanged = true; }
                if (value) _module.SetTargeted(this, true);
            }
        }

        public override float PIDHoverHeight
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "PIDHoverHeight", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.PIDHoverHeight = value; return; }
                lock (_targetLock)
                {
                    _hoverHeight = Math.Clamp(value, -MaxHoverHeight, MaxHoverHeight);
                    // "Assigning height a value of zero will have the same effect as llStopHover." (as ubODE's changePIDHoverHeight)
                    if (value == 0f) _hoverActive = false;
                    _targetsChanged = true;
                }
            }
        }

        public override PIDHoverType PIDHoverType
        {
            set
            {
                if (_linkRoot != null) { _linkRoot.PIDHoverType = value; return; }
                lock (_targetLock) { _hoverType = value; _targetsChanged = true; }
            }
        }

        public override float PIDHoverTau
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "PIDHoverTau", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.PIDHoverTau = value; return; }
                lock (_targetLock) { _hoverTau = value; _targetsChanged = true; }
            }
        }

        public override bool PIDHoverActive
        {
            get { if (_linkRoot != null) return _linkRoot.PIDHoverActive; lock (_targetLock) return _hoverActive; }
            set
            {
                if (_linkRoot != null) { _linkRoot.PIDHoverActive = value; return; }
                lock (_targetLock) { _hoverActive = value; _targetsChanged = true; }
                if (value) _module.SetTargeted(this, true);
            }
        }

        /// <summary>
        /// Called by JoltScene before every backend step for a prim that has asked for a target or a hover height.
        /// Returns false once neither is asked for and the body has been let go, so the scene stops stepping it.
        /// </summary>
        internal bool StepTargets(float timeStep)
        {
            Vector3 target;
            float tau, height, hoverTau;
            bool move, hover, changed;
            PIDHoverType type;
            lock (_targetLock)
            {
                target = _pidTarget; tau = _pidTau; move = _pidActive;
                height = _hoverHeight; hoverTau = _hoverTau; type = _hoverType; hover = _hoverActive;
                changed = _targetsChanged;
                _targetsChanged = false;
            }

            // "Only works in physics-enabled objects." The request is kept: "A llMoveToTarget call seems to persist even
            // if physics is turned off." Nothing acts on an active vehicle ("Do not use with vehicles."; ubODE hands a
            // vehicle to its own controller before either of these).
            bool vehicle = _vehicle != null && _vehicle.IsActive;
            if (!_isPhysical || !_body.IsValid || vehicle || !_backend.TryGetBodyState(_body, out BodyState s))
            {
                LetGo(vehicle);
                return move || hover;
            }

            // Selected in the build tool, the object is held still (HoldSelected) and neither controller acts, as ubODE's
            // Move skips a selected prim. The request is kept; let go, the spring starts again from rest where it was held.
            // A change made meanwhile is kept for then, and a body still held here stays stepped so that, if both were
            // stopped meanwhile, it gets its gravity back once it is let go.
            if (_selected)
            {
                _moveCarry = Vector3.Zero;
                _hoverCarry = 0f;
                if (changed)
                    lock (_targetLock) _targetsChanged = true;
                return move || hover || _heldByTarget;
            }

            Vector3 pos = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            Vector3 vel = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            bool awake = (s.Flags & BodyStateFlags.Active) != 0;

            // A tau of 0 or less does nothing ("Calling llMoveToTarget with a tau of 0.0 or less will silently fail");
            // the hover's is refused the same way, as ubODE's is.
            bool moving = move && tau > 0f && (target - pos).Length() < MaxTargetDistance;
            bool hovering = !moving && hover && hoverTau > 0f && height != 0f;
            if (!moving && !hovering)
            {
                LetGo(false);
                return move || hover;
            }

            if (!_heldByTarget || moving != _lastWasMove)
            {
                _moveCarry = Vector3.Zero;
                _hoverCarry = 0f;
            }
            _heldByTarget = true;
            _lastWasMove = moving;
            _backend.SetBodyGravityFactor(_body, 0f);

            if (moving)
                StepMoveToTarget(target, MathF.Max(tau, MinTau), pos, vel, awake, changed, timeStep);
            else
                StepHover(height, type, MathF.Max(hoverTau, MinTau), pos, vel, awake, changed, timeStep);
            return true;
        }

        private void StepMoveToTarget(Vector3 target, float tau, Vector3 pos, Vector3 vel, bool awake, bool changed, float h)
        {
            Vector3 e0 = pos - target;
            if (!awake)
            {
                if (!changed && e0.Length() < HoldTolerance)
                    return;   // asleep where it is held
                _backend.ActivateBody(_body);
                vel = Vector3.Zero;
                _moveCarry = Vector3.Zero;
            }
            Vector3 v0 = vel + _moveCarry;
            KeepAwakeWhile(e0.Length() >= HoldTolerance || v0.Length() > SettledSpeed);
            // A set force's acceleration a is part of the spring's step: e'' = -e / tau^2 - 2 e' / tau + a is the same spring
            // about e = a tau^2.
            Vector3 a = ScriptAcceleration();
            Vector3 shift = a * (tau * tau);
            (double mx, double cx) = VehicleSpring.MoveOver(e0.X - shift.X, v0.X, tau, 1.0, h);
            (double my, double cy) = VehicleSpring.MoveOver(e0.Y - shift.Y, v0.Y, tau, 1.0, h);
            (double mz, double cz) = VehicleSpring.MoveOver(e0.Z - shift.Z, v0.Z, tau, 1.0, h);
            // The spring's velocity at the end of the step, and the velocity that, through the engine's damping and with the
            // force's own share of the motion, moves the body to the spring's position.
            var springEnd = new Vector3((float)(mx + cx), (float)(my + cy), (float)(mz + cz));
            Damping d = DampingOver(h);
            var moveVel = (new Vector3((float)mx, (float)my, (float)mz) - a * (d.ForceMean * h)) / d.Mean;
            _moveCarry = springEnd - moveVel * d.End - a * (d.ForceEnd * h);

            // The engine caps a body's speed ([Jolt] BodyMaxLinearSpeed); past it the spring starts again from the
            // body's own state at the next step.
            float max = _module.BodyMaxLinearSpeed;
            float len = moveVel.Length();
            if (len > max)
            {
                moveVel *= max / len;
                _moveCarry = Vector3.Zero;
            }
            _backend.SetBodyLinearVelocity(_body, ToS(moveVel));
        }

        private void StepHover(float height, PIDHoverType type, float tau, Vector3 pos, Vector3 vel, bool awake, bool changed, float h)
        {
            // Where the target is now, and where it will be under the object at the end of the step: the spring acts
            // on the height above the ground, so on a slope the object rises with the ground as it moves.
            float targetNow = HoverTargetAt(pos.X, pos.Y, height, type);
            float targetNext = HoverTargetAt(pos.X + vel.X * h, pos.Y + vel.Y * h, height, type);
            float e0 = pos.Z - targetNow;
            if (!awake)
            {
                if (!changed && MathF.Abs(e0) < HoldTolerance)
                    return;
                _backend.ActivateBody(_body);
                vel = Vector3.Zero;
                targetNext = targetNow;
                _hoverCarry = 0f;
            }
            float rise = (targetNext - targetNow) / h;
            double ve0 = vel.Z + _hoverCarry - rise;
            KeepAwakeWhile(MathF.Abs(e0) >= HoldTolerance || Math.Abs(ve0) > SettledSpeed);
            float az = ScriptAcceleration().Z;
            (double move, double carry) = VehicleSpring.MoveOver(e0 - az * tau * tau, ve0, tau, 1.0, h);
            Damping d = DampingOver(h);
            float vz = (rise + (float)move - az * d.ForceMean * h) / d.Mean;
            // Next step's error rate starts from the body's vertical velocity plus this, less the ground's rise then.
            _hoverCarry = (float)(move + carry) + rise - vz * d.End - az * d.ForceEnd * h;
            float max = _module.BodyMaxLinearSpeed;
            if (MathF.Abs(vz) > max)
            {
                vz = MathF.CopySign(max, vz);
                _hoverCarry = 0f;
            }
            _backend.SetBodyLinearVelocity(_body, new SVector3(vel.X, vel.Y, vz));
        }

        // A speed (m/s) under which an object at its target has settled there.
        private const float SettledSpeed = 0.01f;

        // While a controller is still bringing the object in, the engine must not put it to sleep: that would stop the
        // spring part way. Once it has settled where it is held it may sleep, and the controller wakes it if it is moved.
        private void KeepAwakeWhile(bool approaching)
        {
            bool forbidden = _noSleepBody.Equals(_body);
            if (approaching == forbidden)
                return;
            _backend.SetBodyAllowSleeping(_body, !approaching);
            _noSleepBody = approaching ? _body : BodyId.Invalid;
        }

        // The acceleration of the script's set force (llSetForce), which the engine applies through this step.
        private Vector3 ScriptAcceleration()
        {
            Vector3 force;
            lock (_scriptForceLock) force = _force;
            if (force == Vector3.Zero)
                return Vector3.Zero;
            float mass = _backend.GetBodyMass(_body);
            return mass > 0f ? force / mass : Vector3.Zero;
        }

        /// <summary>
        /// How a step of h moves a body, per unit of what it is given. Jolt splits the step into n collision sub-steps;
        /// in each it adds the acceleration, damps the velocity, v = (v + a h / n) (1 - c h / n), and moves the body by
        /// that velocity (MotionProperties::ApplyForceTorqueAndDragInternal, then the position integration). A velocity
        /// v set before the step moves the body by v h Mean and leaves it at v End; a steady acceleration a from rest
        /// moves it by a h^2 ForceMean and leaves it at a h ForceEnd.
        /// </summary>
        private readonly record struct Damping(float Mean, float End, float ForceMean, float ForceEnd);

        private Damping DampingOver(float h)
        {
            float c = _backend.GetBodyLinearDamping(_body);
            int n = _module.CollisionSteps;
            double q = 1.0 - Math.Max(c, 0f) * h / n;
            if (q <= 0.0)
                q = 1.0;
            double qi = 1.0, sum = 0.0, u = 0.0, usum = 0.0;
            for (int i = 0; i < n; i++)
            {
                qi *= q;
                sum += qi;
                u = (u + 1.0 / n) * q;
                usum += u;
            }
            return new Damping((float)(sum / n), (float)qi, (float)(usum / n), (float)u);
        }

        /// <summary>
        /// The height hover holds the object's centre at, over (x, y). The SL wiki, llSetHoverHeight: water TRUE means
        /// "hover above water too", the higher of the two (GroundAndWater, as ubODE); "if FALSE ignore water like it
        /// isn't there" (Ground). Water is the water level alone and Absolute the region's zero. "Unless volume detect is
        /// enabled, negative height values when water is FALSE will not move the object below the ground level": no
        /// target is under the ground unless the prim is volume detect.
        /// </summary>
        private float HoverTargetAt(float x, float y, float height, PIDHoverType type)
        {
            float ground = _module.TerrainHeightAt(x, y);
            float water = _module.WaterLevel;
            float baseZ = type switch
            {
                PIDHoverType.GroundAndWater => MathF.Max(ground, water),
                PIDHoverType.Water => water,
                PIDHoverType.Absolute => 0f,
                _ => ground,
            };
            float target = baseZ + height;
            if (!_isVolumeDetect && target < ground)
                target = ground;
            return target;
        }

        // Neither controller acts any more: gravity back (unless a vehicle now decides it), the spring's own velocity
        // handed back to the body, and the body woken so it falls from where it was held.
        private void LetGo(bool vehicle)
        {
            if (!_heldByTarget)
                return;
            _heldByTarget = false;
            // A body that is no longer physical was recreated static, and a physical one is recreated with the script's
            // gravity; only a body still held here needs it back.
            // A vehicle sets its own sleeping rule (JoltPrim.ApplyVehicleBodyParams).
            if (_noSleepBody.Equals(_body) && _body.IsValid && !vehicle)
                _backend.SetBodyAllowSleeping(_body, true);
            _noSleepBody = BodyId.Invalid;
            if (_body.IsValid && _isPhysical && !vehicle)
            {
                _backend.SetBodyGravityFactor(_body, ScriptGravityFactor);
                Vector3 carry = _lastWasMove ? _moveCarry : new Vector3(0f, 0f, _hoverCarry);
                if (carry != Vector3.Zero && _backend.TryGetBodyState(_body, out BodyState s))
                    _backend.SetBodyLinearVelocity(_body, s.LinearVelocity + ToS(carry));
                _backend.ActivateBody(_body);
            }
            _moveCarry = Vector3.Zero;
            _hoverCarry = 0f;
        }
    }
}
