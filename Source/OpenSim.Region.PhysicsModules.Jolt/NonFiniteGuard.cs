// Legion Grid - the non-finite firewall for the Jolt actors (JOLT-2, audit S-2).
//
// ubODE guards every PhysicsActor setter against NaN/Inf; Jolt used to guard only the drained position and
// AddForce/AddAngularForce. SceneObjectPart velocity/impulse calls and Phlox llSetVelocity /
// llSetAngularVelocity / llApplyRotationalImpulse pass NaN straight through, and Release Jolt keeps a NaN
// velocity - the body then poisons every contact it touches. JoltPrim and JoltCharacter call this at every
// setter: a bad value is ignored (the previous one stays) and named in the log, at most once per actor per 10 s.

using System;
using Microsoft.Extensions.Logging;
using OpenMetaverse;

// JOLT-A: the pure helpers (this guard, CollisionFrameTracker, JoltConfig) are tested without an OpenSim Scene.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Tests")]

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal static class NonFiniteGuard
    {
        internal static readonly long LogIntervalTicks = TimeSpan.FromSeconds(10).Ticks;

        // Below this squared length a quaternion carries no rotation and normalising it divides by ~0.
        internal const float MinQuaternionLengthSq = 1e-8f;

        internal static bool Ok(float f) => float.IsFinite(f);

        internal static bool Ok(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        internal static bool Ok(Quaternion q)
            => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W)
               && (q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W) >= MinQuaternionLengthSq;

        /// <summary>A prim size: finite and strictly positive on every axis.</summary>
        internal static bool OkSize(Vector3 v) => Ok(v) && v.X > 0f && v.Y > 0f && v.Z > 0f;

        /// <summary>
        /// The rate limiter: true (and <paramref name="lastTicks"/> advanced) when this actor has not logged in the
        /// last 10 s. <paramref name="lastTicks"/> is the actor's own field, so the limit is per actor.
        /// </summary>
        internal static bool ShouldLog(ref long lastTicks, long nowTicks)
        {
            if (lastTicks != 0 && nowTicks - lastTicks < LogIntervalTicks)
                return false;
            lastTicks = nowTicks;
            return true;
        }

        /// <summary>Record that <paramref name="property"/> on actor <paramref name="localId"/> ignored a bad value.</summary>
        internal static void Rejected(ref long lastTicks, string actorKind, uint localId, string property, string value)
        {
            if (ShouldLog(ref lastTicks, DateTime.UtcNow.Ticks))
                JoltScene.m_log.LogWarning(
                    $"{JoltScene.LogHeader} {actorKind} {localId}: ignored non-finite {property} {value} (kept the previous value; further rejections from this {actorKind} are quiet for 10 s).");
        }
    }
}
