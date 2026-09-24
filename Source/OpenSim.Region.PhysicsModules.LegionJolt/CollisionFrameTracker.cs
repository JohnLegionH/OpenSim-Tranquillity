// Legion Grid - the per-frame collision set for the Jolt module (M7 Task 3; extracted by JOLT-4).
//
// OpenSim's SceneObjectPart.PhysicsCollision diffs each CollisionEventUpdate against the previous one to fire
// collision_start / collision / collision_end. So a subscribed prim gets ONE update per frame listing what it
// touches, and a prim that touched last frame but not this one needs one EMPTY update - that is its
// collision_end. This class owns that accumulate / hold / end decision, with no Scene, so it can be tested.

using System.Collections.Generic;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    internal sealed class CollisionFrameTracker
    {
        private readonly Dictionary<uint, CollisionEventUpdate> _accum = new Dictionary<uint, CollisionEventUpdate>();
        private readonly HashSet<uint> _collidedLastFrame = new HashSet<uint>();
        private readonly List<uint> _ended = new List<uint>();

        /// <summary>This frame's accumulated updates, keyed by the struck prim's LocalID.</summary>
        internal Dictionary<uint, CollisionEventUpdate> Current => _accum;

        /// <summary>The prims that reported collisions last frame (the collision_end candidates).</summary>
        internal HashSet<uint> CollidedLastFrame => _collidedLastFrame;

        internal bool IsTracked(uint localId) => _collidedLastFrame.Contains(localId);

        internal void BeginFrame() => _accum.Clear();

        /// <summary>Record that subscribed prim <paramref name="prim"/> touches <paramref name="collider"/> this frame.</summary>
        internal void AddCollider(uint prim, uint collider, ContactPoint contact)
        {
            if (!_accum.TryGetValue(prim, out CollisionEventUpdate u))
            {
                u = new CollisionEventUpdate();
                _accum[prim] = u;
            }
            u.AddCollider(collider, contact);
        }

        /// <summary>
        /// Close the frame. Returns the prims that collided last frame and not this one - each gets one empty update
        /// (collision_end) - and rolls the last-frame set forward. The list is reused; read it before the next call.
        /// </summary>
        internal List<uint> EndFrame(bool contactsOverflowed)
        {
            _ended.Clear();

            // JOLT-4 (I-2): the contact buffer overflowed, so absence this frame proves nothing - the contact may
            // simply not have fit. End nobody (no false collision_end, and no false collision_start next frame
            // when the contact reappears); keep last frame's prims tracked alongside this frame's.
            if (contactsOverflowed)
            {
                foreach (uint id in _accum.Keys)
                    _collidedLastFrame.Add(id);
                return _ended;
            }

            foreach (uint id in _collidedLastFrame)
                if (!_accum.ContainsKey(id))
                    _ended.Add(id);
            _collidedLastFrame.Clear();
            foreach (uint id in _accum.Keys)
                _collidedLastFrame.Add(id);
            return _ended;
        }
    }
}
