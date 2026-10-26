/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// What the ll.Detected* functions answer for one detected object (a toucher, a collider, a sensed object, a
    /// damage source) of the event a script is handling: a copy made when the event is posted, so it does not change
    /// while the event waits in the script's queue.
    /// </summary>
    internal sealed class DetectedRecord
    {
        public string Key = UUID.Zero.ToString();
        public string Name = string.Empty;
        public string Owner = UUID.Zero.ToString();
        public string Group = UUID.Zero.ToString();
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Velocity;
        public int LinkNumber;
        public int Type;

        /// <summary>llDetectedGrab: the grab offset, set only for touch().</summary>
        public Vector3 OffsetPos;

        /// <summary>llDetectedTouchFace; -1 (TOUCH_INVALID_FACE) when the client sent no surface data.</summary>
        public int TouchFace = -1;
        public Vector3 TouchUV = new Vector3(-1f, -1f, 0f);
        public Vector3 TouchST = new Vector3(-1f, -1f, 0f);
        public Vector3 TouchNormal;
        public Vector3 TouchBinormal;
        public Vector3 TouchPos;

        /// <summary>llDetectedDamage(n) is [Damage, DamageType, OriginalDamage]; AdjustDamage is what llAdjustDamage writes through (on_damage only).</summary>
        public float Damage;
        public int DamageType;
        public float OriginalDamage;
        public Action<float> AdjustDamage;

        /// <summary>One record per DetectParams, in order; null for none.</summary>
        internal static DetectedRecord[] From(DetectParams[] detected)
        {
            return null;
        }
    }
}
