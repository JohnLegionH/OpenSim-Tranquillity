/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The per-frame collision set for the Jolt module.
//
// OpenSim's SceneObjectPart.PhysicsCollision diffs each CollisionEventUpdate against the previous one to fire
// collision_start / collision / collision_end. So a subscribed prim gets ONE update per frame listing what it
// touches, and a prim that touched last frame but not this one needs one EMPTY update - that is its
// collision_end. This class owns that accumulate / hold / end decision, with no Scene, so it can be tested.

using System.Collections.Generic;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt
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

        // Top Colliders, ubODE's model - a prim's CollisionScore is the number of Begin/Persist
        // contact reports that named it this frame, counted BEFORE the subscription filter, and reset every frame.
        // _prevScored is last frame's scored set, so the module can zero the prims that dropped out.
        private readonly Dictionary<uint, int> _scores = new Dictionary<uint, int>();
        private readonly List<uint> _prevScored = new List<uint>();

        /// <summary>This frame's contact count per struck prim.</summary>
        internal Dictionary<uint, int> Scores => _scores;

        /// <summary>The prims that had a score last frame (valid after <see cref="BeginFrame"/>).</summary>
        internal List<uint> PreviouslyScored => _prevScored;

        internal void BeginFrame()
        {
            _accum.Clear();
            _prevScored.Clear();
            foreach (uint id in _scores.Keys)
                _prevScored.Add(id);
            _scores.Clear();
        }

        /// <summary>One Begin/Persist report named <paramref name="struckPrim"/> (0 = terrain, not scored).</summary>
        internal void CountContact(uint struckPrim)
        {
            if (struckPrim == 0)
                return;
            _scores.TryGetValue(struckPrim, out int n);
            _scores[struckPrim] = n + 1;
        }

        /// <summary>The <paramref name="cap"/> highest-scored entries, highest first.</summary>
        internal static List<KeyValuePair<uint, float>> TopColliders(List<KeyValuePair<uint, float>> scored, int cap)
        {
            var sorted = new List<KeyValuePair<uint, float>>(scored);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (sorted.Count > cap)
                sorted.RemoveRange(cap, sorted.Count - cap);
            return sorted;
        }

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

            // The contact buffer overflowed, so absence this frame proves nothing - the contact may
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
