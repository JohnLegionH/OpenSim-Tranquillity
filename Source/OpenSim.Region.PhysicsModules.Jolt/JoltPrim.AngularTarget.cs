/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// llRotLookAt and llLookAt on a physical prim: a rotation target that acts before every physics step.
//
// The engine is handed the target (APIDTarget), how hard to go for it (APIDStrength), how long to take to settle
// (APIDDamping) and whether it is on (APIDActive). The Second Life wiki says (llRotLookAt) the object turns "smoothly"
// to the target "with strength resistance at damping force", damping being "seconds to critically damp in", "capped at
// 1.0; greater values are reduced to 1.0", and "the minimum value for strength seems to be 0.0445 for the function to
// have any effect"; (llLookAt) "strength is an inverse value meaning lower values cause the object to turn faster" and
// "damping smooths the objects movements out preventing jitter or sharp movements". Neither page gives a formula.
//
// What is built is a critically damped spring on the rotation, the rotation counterpart of move to target (see
// JoltPrim.Targets.cs). With r the rotation vector (axis x angle, region axes, the short way round) that takes the target
// to the object's rotation, and w the object's angular velocity:
//
//   r'' = -r / T^2 - 2 r' / T        T = max(strength, min(damping, 1)) seconds
//
// from rest r(t) = r0 (1 + t / T) e^(-t / T): it never passes the target. T is the choice the wiki does not settle: both
// numbers are times, so the slower one sets the response. A strength under 0.0445 does nothing (the request is kept), as
// does a strength that is not a number.
//
// The spring is a trajectory, not the body's own state: the controller keeps a virtual rotation and angular velocity that
// follow the spring exactly, and each step sets the body's angular velocity to the one that takes it, over the step the
// engine takes (through its angular damping), from the rotation it really has to the spring's rotation at the end of the
// step. So the motion is the same at every [Jolt] PhysicsStepRate, for any mass or size (a velocity does not depend on
// inertia), and what friction or a bump takes from the body in one step is asked for again in the next: an object resting
// on the ground, which friction holds against a torque, still turns. Contact friction takes up to a fixed share of the
// velocity it is given in each step, whatever it is given, so a body that lags is also asked for what the engine took from
// the last step's command (the estimate halves each step that takes nothing). The virtual state carries on across a new target, so a
// target that moves many times a second (a drag) is followed smoothly. Where the body has strayed further than ResyncAngle
// from it (something else turned it, or the body was recreated) the spring starts again from where the body is.
//
// Gravity and buoyancy are not touched (a move to target turns gravity off while it acts; this turns nothing off). A set
// force or a move to target act as they do without it. A set torque is held against by the same tracking. An angular
// velocity a script sets (llSetAngularVelocity, llTargetOmega on a physical prim) is taken as the speed the spring starts
// from and is brought to the target, not fought. STATUS_ROTATE locks: the spring acts on the axes left free. A vehicle
// keeps its own controller: nothing acts on an active vehicle.
//
// Switching it off sets the angular velocity to zero before the next step, as core does when it stops a look-at
// (SceneObjectPart.StopLookAt): it was turning at the spring's speed and nothing else would stop it, since the body's
// own damping takes about a minute. The rotation it had reached is kept. An angular velocity written since the last step
// is kept instead (core gives a script's llTargetOmega back when a rotate-drag ends, just after it switches the target off).
//
// A body that has settled at its target may sleep; a new target, or a body found asleep away from its target, wakes it.

using System;
using System.Threading;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using OpenSim.Region.PhysicsModules.SharedBase;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed partial class JoltPrim
    {
        /// <summary>The SL wiki, llRotLookAt: "The minimum value for strength seems to be 0.0445 for the function to have any effect."</summary>
        internal const float MinLookAtStrength = 0.0445f;

        /// <summary>The SL wiki, llRotLookAt: "damping seems to be capped at 1.0; greater values are reduced to 1.0".</summary>
        internal const float MaxLookAtDamping = 1f;

        /// <summary>The body this far (rad) from the spring's rotation is taken to have been moved by something else: the spring starts again from it.</summary>
        internal const float ResyncAngle = 0.35f;

        /// <summary>A body asleep this close to its target (rad) is left asleep.</summary>
        internal const float HoldAngle = 0.0035f;

        // A turn to make (rad) under which no allowance is made for what the engine takes from the command: the body is there.
        private const float MinLossTurn = 0.0005f;

        // A spin (rad/s) under which a body at its target has settled there.
        private const float SettledAngularSpeed = 0.01f;

        private SQuaternion _apidTarget = SQuaternion.Identity;   // _targetLock
        private float _apidStrength;                              // _targetLock
        private float _apidDamping;                               // _targetLock
        private bool _apidActive;                                 // _targetLock
        private bool _apidChanged;                                // _targetLock: wake the body
        private int _apidSpunByScript;                            // set by RotationalVelocity, read by the step

        // Step thread only.
        private bool _angActing;           // the controller set the body's angular velocity in the last step
        private bool _angHaveVirtual;
        private SQuaternion _angQ;         // the spring's rotation
        private SVector3 _angW;            // the spring's angular velocity
        private bool _angHavePrev;         // a command was given in the last step, from _angPrevQ
        private SQuaternion _angPrevQ;     // the body's rotation before it
        private SVector3 _angPrevCmd;      // the velocity set then
        private float _angPrevScale;       // the rotation that velocity gives over the step, per unit of it (step x damping mean)
        private float _angLoss;            // what the engine took from the commanded velocity in the last steps (rad/s)

        public override Quaternion APIDTarget
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "APIDTarget", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.APIDTarget = value; return; }
                SQuaternion q = SQuaternion.Normalize(ToS(value));
                lock (_targetLock) { _apidTarget = q; _apidChanged = true; }
            }
        }

        public override float APIDStrength
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "APIDStrength", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.APIDStrength = value; return; }
                lock (_targetLock) { _apidStrength = value; _apidChanged = true; }
            }
        }

        public override float APIDDamping
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "APIDDamping", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.APIDDamping = value; return; }
                lock (_targetLock) { _apidDamping = value; _apidChanged = true; }
            }
        }

        public override bool APIDActive
        {
            set
            {
                if (_linkRoot != null) { _linkRoot.APIDActive = value; return; }
                lock (_targetLock) { _apidActive = value; _apidChanged = true; }
                if (value) _module.SetTargeted(this, true);
            }
        }

        /// <summary>
        /// Called before every backend step (from <see cref="StepTargets"/>). Returns false once the target is off and the
        /// body has been let go.
        /// </summary>
        private bool StepAngularTarget(float h)
        {
            SQuaternion target;
            float strength, damping;
            bool active, changed;
            lock (_targetLock)
            {
                target = _apidTarget; strength = _apidStrength; damping = _apidDamping; active = _apidActive;
                changed = _apidChanged;
                _apidChanged = false;
            }
            bool spun = Interlocked.Exchange(ref _apidSpunByScript, 0) != 0;

            bool vehicle = _vehicle != null && _vehicle.IsActive;
            if (!active || !_isPhysical || !_body.IsValid || vehicle || !_backend.TryGetBodyState(_body, out BodyState s))
            {
                AngularLetGo(vehicle, keepVelocity: spun);
                return active;
            }

            // Selected in the build tool the object is held still and the target does not act (as the move to target); the
            // request is kept, and a change made meanwhile is kept for then.
            if (_selected)
            {
                _angHaveVirtual = false;
                if (changed)
                    lock (_targetLock) _apidChanged = true;
                return true;
            }

            // "The minimum value for strength seems to be 0.0445 for the function to have any effect": under it the
            // request is kept and nothing acts.
            if (!(strength >= MinLookAtStrength))
            {
                AngularLetGo(false);
                return true;
            }
            float timescale = MathF.Max(strength, Math.Clamp(damping, 0f, MaxLookAtDamping));

            SQuaternion goal = SQuaternion.Normalize(BodyOrientationOf(new Quaternion(target.X, target.Y, target.Z, target.W)));
            SQuaternion q = SQuaternion.Normalize(s.Orientation);
            SVector3 w = s.AngularVelocity;
            bool awake = (s.Flags & BodyStateFlags.Active) != 0;
            if (!awake)
            {
                if (!changed && !spun && AngleBetween(q, goal) < HoldAngle)
                    return true;   // asleep at its target
                _backend.ActivateBody(_body);
                w = SVector3.Zero;
                _angHaveVirtual = false;
            }

            if (!_angActing || spun || !_angHaveVirtual || AngleBetween(_angQ, q) > ResyncAngle)
            {
                _angQ = q;
                _angW = w;
                _angHaveVirtual = true;
                _angHavePrev = false;
                _angLoss = 0f;
            }
            _angActing = true;

            // The prim's axes that are locked (STATUS_ROTATE_*) as region axes: the spring acts on the rest.
            SVector3 lockA = SVector3.Zero, lockB = SVector3.Zero, lockC = SVector3.Zero;
            int locks = 0;
            if (_bodyLockX) lockA = SVector3.Transform(SVector3.UnitX, q);
            if (_bodyLockY) lockB = SVector3.Transform(SVector3.UnitY, q);
            if (_bodyLockZ) lockC = SVector3.Transform(SVector3.UnitZ, q);
            if (_bodyLockX || _bodyLockY || _bodyLockZ) locks = 1;
            SVector3 Free(SVector3 v)
            {
                if (locks == 0) return v;
                if (_bodyLockX) v -= SVector3.Dot(v, lockA) * lockA;
                if (_bodyLockY) v -= SVector3.Dot(v, lockB) * lockB;
                if (_bodyLockZ) v -= SVector3.Dot(v, lockC) * lockC;
                return v;
            }

            // The spring over this step, on the rotation vector from the target to the spring's rotation.
            SVector3 r = Free(RotationVector(SQuaternion.Normalize(SQuaternion.Multiply(_angQ, SQuaternion.Conjugate(goal)))));
            SVector3 v0 = Free(_angW);
            (double mx, double cx) = VehicleSpring.MoveOver(r.X, v0.X, timescale, 1.0, h);
            (double my, double cy) = VehicleSpring.MoveOver(r.Y, v0.Y, timescale, 1.0, h);
            (double mz, double cz) = VehicleSpring.MoveOver(r.Z, v0.Z, timescale, 1.0, h);
            var move = new SVector3((float)mx, (float)my, (float)mz);          // the error's mean rate over the step
            var springEnd = new SVector3((float)(mx + cx), (float)(my + cy), (float)(mz + cz));
            SQuaternion next = SQuaternion.Normalize(SQuaternion.Multiply(FromRotationVector(move * h), _angQ));

            // The angular velocity that takes the body from where it is to the spring's rotation at the end of the step,
            // through the engine's angular damping.
            SVector3 turn = Free(RotationVector(SQuaternion.Normalize(SQuaternion.Multiply(next, SQuaternion.Conjugate(q)))));
            Damping d = DampingFor(_module.PrimAngularDamping, h);
            float scale = h * d.Mean;

            // What the last command gave: the turn it was meant to make and the turn the body made.
            if (_angHavePrev && _angPrevCmd.LengthSquared() > 1e-12f)
            {
                SVector3 u = SVector3.Normalize(_angPrevCmd);
                SVector3 made = RotationVector(SQuaternion.Normalize(SQuaternion.Multiply(q, SQuaternion.Conjugate(_angPrevQ))));
                float missing = (_angPrevCmd.Length() * _angPrevScale - SVector3.Dot(made, u)) / _angPrevScale;
                _angLoss = missing > 0f ? missing : _angLoss * 0.5f;
            }
            else
                _angLoss *= 0.5f;

            SVector3 cmd = turn / scale;
            float turnLen = turn.Length();
            if (turnLen > MinLossTurn && _angLoss > 0f)
                cmd += turn * (_angLoss / turnLen);
            float max = _module.BodyMaxAngularSpeed;
            float len = cmd.Length();
            if (len > max)
            {
                cmd *= max / len;
                _angHaveVirtual = false;   // the body cannot keep up: the spring starts again from it
            }
            _angQ = next;
            _angW = Free(springEnd);
            _angHavePrev = true;
            _angPrevQ = q;
            _angPrevCmd = cmd;
            _angPrevScale = scale;
            _backend.SetBodyAngularVelocity(_body, cmd);

            _angApproaching = r.Length() >= HoldAngle || v0.Length() > SettledAngularSpeed;
            ApplySleepRule();
            return true;
        }

        private bool _bodyLockX, _bodyLockY, _bodyLockZ;   // the body's own axes with an infinite inertia (ApplyRotationLocks)

        // The target is off, not usable, or the body is not one it acts on: the spin it set is ended, the body may sleep.
        // An angular velocity written since the last step (core applies a script's llTargetOmega again when a rotate-drag is
        // let go, right after it switches the target off) is the one the body keeps.
        private void AngularLetGo(bool vehicle, bool keepVelocity = false)
        {
            _angHaveVirtual = false;
            if (!_angActing)
                return;
            _angActing = false;
            _angApproaching = false;
            if (vehicle || !_body.IsValid || !_isPhysical)
            {
                if (!_linApproaching) _noSleepBody = BodyId.Invalid;
                return;
            }
            if (!keepVelocity)
                _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
            ApplySleepRule();
        }

        /// <summary>The angle (rad) between two rotations, the short way.</summary>
        private static float AngleBetween(SQuaternion a, SQuaternion b)
        {
            float dot = MathF.Min(1f, MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W));
            return 2f * MathF.Acos(dot);
        }

        /// <summary>The rotation as axis x angle, the angle in [0, pi].</summary>
        private static SVector3 RotationVector(SQuaternion q)
        {
            if (q.W < 0f)
                q = new SQuaternion(-q.X, -q.Y, -q.Z, -q.W);
            var v = new SVector3(q.X, q.Y, q.Z);
            float s = v.Length();
            if (s < 1e-7f)
                return v * 2f;
            return v * (2f * MathF.Atan2(s, q.W) / s);
        }

        private static SQuaternion FromRotationVector(SVector3 r)
        {
            float angle = r.Length();
            if (angle < 1e-9f)
                return SQuaternion.Identity;
            return SQuaternion.CreateFromAxisAngle(r / angle, angle);
        }
    }
}
