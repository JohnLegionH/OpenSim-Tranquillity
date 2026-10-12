/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A non-physical prim that keyframed motion (llSetKeyframedMotion) is moving carries what stands on it.
//
// Core moves such an object with plain position and rotation sets, about every 50 ms, from its own timer
// (KeyframeMotion.DoOnTimer), and tells the root prim's actor while it does (PhysicsActor.MovedByKeyframedMotion). A
// fixed body that is set to a new place has no velocity, so an avatar or a physical object on it stays where it was
// and the platform leaves from under it. The Second Life wiki says avatars stand on such an object while it moves
// ("... when standing on a moving platform", llSetKeyframedMotion).
//
// While the hint is on, the prim's body is a kinematic one (layer MovingStatic: it collides as a fixed prim does) and
// each set only records the pose. Once per heartbeat, before the step, the body is moved to the last pose over the time
// that step covers (JoltScene.StepKinematics -> StepKinematic), so it carries the linear and angular velocity of the
// motion: an avatar standing on it takes that velocity from it, and a physical object on it is carried by friction.
//   - A move longer than KinematicJumpDistance in one heartbeat is a jump, not a motion (a script or builder set the
//     object somewhere else): the body is put there with no velocity, so nothing is flung, and what rested on it is woken.
//   - A heartbeat in which no new pose arrived stops the body at the last pose: a kinematic body keeps the velocity it
//     was given, and would carry on past where the motion stopped.
//   - When the hint goes off (the motion stopped, was paused or played out, or the object was selected) the body is made
//     a fixed one again where the object is.
// Phantom and volume-detect prims, physical prims and linkset children are not moved this way.

using System;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed partial class JoltPrim
    {
        // A move longer than this in one heartbeat is a jump (the pose is taken with no velocity), and so is a move faster
        // than KinematicJumpSpeed over a heartbeat longer than this distance allows. Keyframed motion at 3 m/s moves 0.27 m
        // a heartbeat, a lift at 20 m/s 1.8 m, and a plain position set by a script at most 10 m (llSetPos) in one go; 4 m
        // a heartbeat (44 m/s at the default heartbeat) carries every motion a build is likely to make and still tells a
        // set across the parcel from a motion, which would otherwise throw what stands on it at 100 m/s.
        internal const float KinematicJumpDistance = 4f;
        internal const float KinematicJumpSpeed = 40f;

        // Core's hint: keyframed motion is moving this prim's object now.
        private volatile bool _keyframed;

        // The body is kinematic (see KinematicWanted); only the thread that makes the body writes it.
        private volatile bool _kinematicBody;

        // The last pose the object was given, and whether it is new since the last heartbeat's move. Written by whatever
        // thread sets the pose (core's motion timer), read by the step thread.
        private readonly object _kinLock = new object();
        private Vector3 _kinTarget;
        private Quaternion _kinRotation = Quaternion.Identity;
        private bool _kinPending;

        // The body was moved at its last step (so it has a velocity to take away if no new pose comes).
        private bool _kinMoving;

        internal bool IsKinematicBody => _kinematicBody;

        public override bool MovedByKeyframedMotion
        {
            get => _keyframed;
            set
            {
                if (_keyframed == value)
                    return;
                _keyframed = value;
                SyncKinematicBody();
            }
        }

        // A single prim or linkset root that is not physical, phantom or a volume detector, while keyframed motion moves it.
        private bool KinematicWanted
            => _keyframed && !_isPhysical && !_isPhantom && !_isVolumeDetect && _linkRoot == null && _module.KeyframedMotionCarriesRiders;

        // Makes the body the kind the prim now wants: a kinematic one while keyframed motion moves it, a fixed one again
        // after. A static body cannot become kinematic (it has no motion properties; allocating them for every prim is the
        // memory cost the static rule avoids), so the body is made again where the object is. What rests on the prim is woken:
        // the new body has no record of what touched the old one.
        private void SyncKinematicBody()
        {
            if (IsRemoved || KinematicWanted == _kinematicBody)
                return;
            lock (_shapeLock)
            {
                if (IsRemoved || !_body.IsValid || _away != Away.None || IsWeldedChild)
                    return;
                _backend.RemoveBody(_body);
                CreateBodyInternal();   // reads KinematicWanted: layer, motion type and the scene's list of moving prims
            }
            WakeWhatRestsOn(_position, _orientation);
        }

        // A position or rotation set on the kinematic prim: only the pose is kept; StepKinematic moves the body.
        private void NoteKinematicPose(Vector3? position, Quaternion? rotation)
        {
            lock (_kinLock)
            {
                if (position.HasValue) _kinTarget = position.Value;
                if (rotation.HasValue) _kinRotation = rotation.Value;
                _kinPending = true;
            }
        }

        // Called by the scene on the step thread, before the step, with the simulated time the step (or the heartbeat's
        // burst of steps) covers.
        internal void StepKinematic(float seconds)
        {
            if (!_kinematicBody || !_body.IsValid || _linkRoot != null || !(seconds > 0f))
                return;
            bool pending;
            Vector3 target;
            Quaternion rotation;
            lock (_kinLock)
            {
                pending = _kinPending;
                target = _kinTarget;
                rotation = _kinRotation;
                _kinPending = false;
            }
            SQuaternion bodyRotation = BodyOrientationOf(rotation);
            if (pending)
            {
                if (!_backend.TryGetBodyState(_body, out BodyState s))
                    return;
                var from = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
                if (Vector3.Distance(from, target) > MathF.Max(KinematicJumpDistance, KinematicJumpSpeed * seconds))
                {
                    _backend.SetBodyPoseAndVelocity(_body, ToS(target), bodyRotation, SVector3.Zero, SVector3.Zero);
                    _kinMoving = false;
                    WakeWhatRestsOn(from, _orientation);
                }
                else
                {
                    _backend.MoveKinematicBody(_body, ToS(target), bodyRotation, seconds);
                    _kinMoving = true;
                }
            }
            else if (_kinMoving)
            {
                _backend.SetBodyPoseAndVelocity(_body, ToS(target), bodyRotation, SVector3.Zero, SVector3.Zero);
                _kinMoving = false;
            }
        }
    }
}
