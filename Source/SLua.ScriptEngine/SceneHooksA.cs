/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine
{
    /// <summary>Where the scene hooks post the events they translate: the two entry points the engine offers other engines.</summary>
    internal interface IEventPoster
    {
        bool PostObjectEvent(uint localID, EventParams parms);

        bool PostScriptEvent(UUID itemID, EventParams parms);
    }

    /// <summary>
    /// The region's object and avatar events (touch, changed, collision, land collision, attach, moving, targets,
    /// control, death and damage, frame time) as LSL events posted to the scripts concerned, with the detected data
    /// the Detected functions read. The translation follows what the Phlox engine does with the same hooks
    /// (Halcyon's EventRouter for the detected data; the SL wiki pages named at each handler for the rest); the
    /// SLua handler name is the LSL event name.
    /// </summary>
    internal sealed class SceneHooksA
    {
        // llDetected* "touch" repeat: a held touch is reported at most this often per prim and toucher (Halcyon TOUCH_INTERVAL, 100 ms).
        internal const double TouchIntervalSeconds = 0.1;

        private static readonly DetectParams[] s_noDetected = Array.Empty<DetectParams>();

        private readonly Scene m_scene;
        private readonly IEventPoster m_poster;
        private readonly bool m_physicsThrottle;
        private readonly ISchedulerClock m_clock;
        private readonly object m_subscribeLock = new object();
        private bool m_subscribed;

        // When a touch was last reported, by prim and toucher; an entry goes with the touch_end.
        private readonly Dictionary<(uint Prim, UUID Agent), double> m_lastTouch = new Dictionary<(uint, UUID), double>();

        // The last ten physics frame times in milliseconds (Halcyon's PhysicsScene.SimulationFrameTimeAvg).
        private readonly int[] m_frameTimes = new int[10];
        private int m_frameCount;
        private int m_frameNext;
        private readonly object m_frameLock = new object();

        public SceneHooksA(Scene scene, IEventPoster poster, bool physicsThrottle, ISchedulerClock clock = null)
        {
            m_scene = scene;
            m_poster = poster;
            m_physicsThrottle = physicsThrottle;
            m_clock = clock ?? new MonotonicClock();
        }

        /// <summary>Average of the last ten physics frame times in milliseconds, fed once per frame while the physics throttle is on.</summary>
        public int PhysicsFrameTimeAvg
        {
            get
            {
                lock (m_frameLock)
                {
                    if (m_frameCount == 0)
                        return 0;
                    int total = 0;
                    for (int i = 0; i < m_frameCount; i++)
                        total += m_frameTimes[i];
                    return total / m_frameCount;
                }
            }
        }

        /// <summary>True between Subscribe and Unsubscribe.</summary>
        public bool IsSubscribed
        {
            get { lock (m_subscribeLock) return m_subscribed; }
        }

        public void Subscribe()
        {
            lock (m_subscribeLock)
            {
                if (m_subscribed)
                    return;
                m_subscribed = true;
            }
            EventManager e = m_scene.EventManager;
            e.OnObjectGrab += OnObjectGrab;
            e.OnObjectGrabbing += OnObjectGrabbing;
            e.OnObjectDeGrab += OnObjectDeGrab;
            e.OnScriptChangedEvent += OnScriptChangedEvent;
            e.OnAvatarKilled += OnAvatarKilled;
            e.OnAvatarDamage += OnAvatarDamage;
            e.OnAvatarDamageApplied += OnAvatarDamageApplied;
            e.OnScriptControlEvent += OnScriptControlEvent;
            e.OnScriptColliderStart += OnScriptColliderStart;
            e.OnScriptColliding += OnScriptColliding;
            e.OnScriptCollidingEnd += OnScriptCollidingEnd;
            e.OnScriptLandColliderStart += OnScriptLandColliderStart;
            e.OnScriptLandColliding += OnScriptLandColliding;
            e.OnScriptLandColliderEnd += OnScriptLandColliderEnd;
            e.OnAttach += OnAttach;
            e.OnScriptMovingStartEvent += OnScriptMovingStartEvent;
            e.OnScriptMovingEndEvent += OnScriptMovingEndEvent;
            e.OnScriptAtTargetEvent += OnScriptAtTargetEvent;
            e.OnScriptNotAtTargetEvent += OnScriptNotAtTargetEvent;
            e.OnScriptAtRotTargetEvent += OnScriptAtRotTargetEvent;
            e.OnScriptNotAtRotTargetEvent += OnScriptNotAtRotTargetEvent;
            if (m_physicsThrottle)
                e.OnFrame += OnFrame;
        }

        public void Unsubscribe()
        {
            lock (m_subscribeLock)
            {
                if (!m_subscribed)
                    return;
                m_subscribed = false;
            }
            EventManager e = m_scene.EventManager;
            e.OnObjectGrab -= OnObjectGrab;
            e.OnObjectGrabbing -= OnObjectGrabbing;
            e.OnObjectDeGrab -= OnObjectDeGrab;
            e.OnScriptChangedEvent -= OnScriptChangedEvent;
            e.OnAvatarKilled -= OnAvatarKilled;
            e.OnAvatarDamage -= OnAvatarDamage;
            e.OnAvatarDamageApplied -= OnAvatarDamageApplied;
            e.OnScriptControlEvent -= OnScriptControlEvent;
            e.OnScriptColliderStart -= OnScriptColliderStart;
            e.OnScriptColliding -= OnScriptColliding;
            e.OnScriptCollidingEnd -= OnScriptCollidingEnd;
            e.OnScriptLandColliderStart -= OnScriptLandColliderStart;
            e.OnScriptLandColliding -= OnScriptLandColliding;
            e.OnScriptLandColliderEnd -= OnScriptLandColliderEnd;
            e.OnAttach -= OnAttach;
            e.OnScriptMovingStartEvent -= OnScriptMovingStartEvent;
            e.OnScriptMovingEndEvent -= OnScriptMovingEndEvent;
            e.OnScriptAtTargetEvent -= OnScriptAtTargetEvent;
            e.OnScriptNotAtTargetEvent -= OnScriptNotAtTargetEvent;
            e.OnScriptAtRotTargetEvent -= OnScriptAtRotTargetEvent;
            e.OnScriptNotAtRotTargetEvent -= OnScriptNotAtRotTargetEvent;
            e.OnFrame -= OnFrame;
            lock (m_lastTouch) m_lastTouch.Clear();
        }

        // ---- touch (hooks 10 to 12) ----

        // The region has already chosen the prim (Scene.ProcessObjectGrab): the touched prim when it handles the touch,
        // the root as well on llPassTouches. originalID is the touched prim when the root takes a child's touch. Each
        // touch goes to the scripts of that one prim (SL llPassTouches).

        private void OnObjectGrab(uint localID, uint originalID, Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_scene.GetSceneObjectPart(localID);
            if (part == null)
                return;
            // SL llDetectedGrab (https://wiki.secondlife.com/wiki/LlDetectedGrab) is usable only inside touch().
            DetectParams dp = BuildTouchDetectParams(TouchedPart(part, originalID), remoteClient, Vector3.Zero, surfaceArgs);
            NoteTouch(part.LocalId, remoteClient.AgentId);
            m_poster.PostObjectEvent(part.LocalId, new EventParams("touch_start", new object[] { 1 }, new[] { dp }));
        }

        /// <summary>
        /// A grab update while the mouse is held: reported as touch(), but a touch reported less than the touch interval
        /// ago absorbs it, so a viewer that sends many updates does not flood the script's queue.
        /// </summary>
        private void OnObjectGrabbing(uint localID, uint originalID, Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_scene.GetSceneObjectPart(localID);
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;
            var key = (part.LocalId, remoteClient.AgentId);
            double now = m_clock.Now;
            lock (m_lastTouch)
            {
                if (m_lastTouch.TryGetValue(key, out double last) && now - last < TouchIntervalSeconds)
                    return;
                m_lastTouch[key] = now;
            }
            DetectParams dp = BuildTouchDetectParams(TouchedPart(part, originalID), remoteClient, offsetPos, surfaceArgs);
            m_poster.PostObjectEvent(part.LocalId, new EventParams("touch", new object[] { 1 }, new[] { dp }));
        }

        private void OnObjectDeGrab(uint localID, uint originalID, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_scene.GetSceneObjectPart(localID);
            if (part == null)
                return;
            DetectParams dp = BuildTouchDetectParams(TouchedPart(part, originalID), remoteClient, Vector3.Zero, surfaceArgs);
            lock (m_lastTouch) m_lastTouch.Remove((part.LocalId, remoteClient.AgentId));
            m_poster.PostObjectEvent(part.LocalId, new EventParams("touch_end", new object[] { 1 }, new[] { dp }));
        }

        private void NoteTouch(uint prim, UUID agent)
        {
            double now = m_clock.Now;
            lock (m_lastTouch)
            {
                if (m_lastTouch.Count > 256)
                {
                    // A toucher who left without a touch_end: forget touches older than a few seconds.
                    var stale = new List<(uint, UUID)>();
                    foreach (KeyValuePair<(uint Prim, UUID Agent), double> kv in m_lastTouch)
                        if (now - kv.Value > 10)
                            stale.Add(kv.Key);
                    foreach ((uint, UUID) k in stale)
                        m_lastTouch.Remove(k);
                }
                m_lastTouch[(prim, agent)] = now;
            }
        }

        /// <summary>The prim the avatar touched: the one the region names in originalID when the root takes a child's touch.</summary>
        private SceneObjectPart TouchedPart(SceneObjectPart target, uint originalID)
            => originalID != 0 && originalID != target.LocalId ? m_scene.GetSceneObjectPart(originalID) ?? target : target;

        /// <summary>
        /// The toucher's detect data as Halcyon's EventRouter built it (DetectParams.Populate): name, position,
        /// rotation, velocity and active group, and llDetectedType AGENT, plus ACTIVE while the avatar moves (SL
        /// llDetectedType). llDetectedLinkNumber is the touched prim's. Surface data comes only through the
        /// write-only SurfaceTouchArgs setter; null leaves TOUCH_INVALID_FACE and TOUCH_INVALID_TEXCOORD.
        /// </summary>
        private DetectParams BuildTouchDetectParams(SceneObjectPart touched, IClientAPI remoteClient, Vector3 offsetPos, SurfaceTouchEventArgs surfaceArgs)
        {
            var dp = new DetectParams { Key = remoteClient.AgentId };
            ScenePresence sp = m_scene.GetScenePresence(remoteClient.AgentId);
            if (sp != null)
                dp.Populate(m_scene);
            else
            {
                dp.Name = remoteClient.Name;
                dp.Owner = remoteClient.AgentId;
            }
            // Populate types an NPC 0x20; a touch comes from an avatar, a bot's too (Halcyon: AGENT)
            dp.Type = DetectParams.AGENT | (sp != null && sp.Velocity != Vector3.Zero ? DetectParams.ACTIVE : 0);
            dp.LinkNum = touched.LinkNum;
            dp.OffsetPos = new LSL_Types.Vector3(offsetPos.X, offsetPos.Y, offsetPos.Z);
            dp.SurfaceTouchArgs = surfaceArgs;
            return dp;
        }

        // ---- changed (hook 13) ----

        // CHANGED_LINK (sit, stand, link, unlink), CHANGED_SCALE, CHANGED_SHAPE and the others the region raises. The
        // local id is the part that changed: only that part's scripts hear it.
        private void OnScriptChangedEvent(uint localID, uint change, object data)
            => m_poster.PostObjectEvent(localID, new EventParams("changed", new object[] { (int)change }, s_noDetected));

        // ---- death and damage (hooks 14 to 16) ----

        /// <summary>
        /// on_death (https://wiki.secondlife.com/wiki/On_death): every script on every part of every attachment the dead
        /// avatar wears, with no arguments. The killer's local id is not part of the SL event.
        /// </summary>
        private void OnAvatarKilled(uint killerLocalId, ScenePresence dead)
        {
            if (dead == null)
                return;
            foreach (uint local in AttachmentParts(dead))
                m_poster.PostObjectEvent(local, new EventParams("on_death", Array.Empty<object>(), null));
        }

        /// <summary>
        /// on_damage (https://wiki.secondlife.com/wiki/On_damage), raised before the damage is applied: one detected
        /// entry per pending damage, llDetectedDamage(n) being [amount, type, original] and llAdjustDamage(n, v)
        /// writing the entry's amount. It is posted without waiting for the handlers, so an adjustment made by a
        /// script lands only if the handler has run before the region applies the batch.
        /// </summary>
        private void OnAvatarDamage(ScenePresence presence, List<DamageEntry> batch)
        {
            if (presence == null || batch == null || batch.Count == 0)
                return;
            DetectParams[] det = DamageDetectParams(batch, adjustable: true);
            foreach (uint local in AttachmentParts(presence))
                m_poster.PostObjectEvent(local, new EventParams("on_damage", new object[] { batch.Count }, det));
        }

        /// <summary>final_damage: what landed, to the same scripts.</summary>
        private void OnAvatarDamageApplied(ScenePresence presence, List<DamageEntry> batch)
        {
            if (presence == null || batch == null || batch.Count == 0)
                return;
            DetectParams[] det = DamageDetectParams(batch, adjustable: false);
            foreach (uint local in AttachmentParts(presence))
                m_poster.PostObjectEvent(local, new EventParams("final_damage", new object[] { batch.Count }, det));
        }

        private DetectParams[] DamageDetectParams(List<DamageEntry> batch, bool adjustable)
        {
            var det = new DetectParams[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                DamageEntry entry = batch[i];
                SceneObjectPart src = entry.SourceObject.IsZero() ? null : m_scene.GetSceneObjectPart(entry.SourceObject);
                det[i] = new DetectParams
                {
                    Key = entry.SourceObject,
                    Owner = entry.SourceOwner,
                    Group = src?.GroupID ?? UUID.Zero,
                    Name = src?.Name ?? string.Empty,
                    Type = src == null ? 0 : (src.ParentGroup.ContainsScripts() ? DetectParams.SCRIPTED | DetectParams.ACTIVE : DetectParams.PASSIVE),
                    Position = src == null ? new LSL_Types.Vector3() : new LSL_Types.Vector3(src.AbsolutePosition.X, src.AbsolutePosition.Y, src.AbsolutePosition.Z),
                    Damage = entry.Amount,
                    DamageType = entry.DamageType,
                    OriginalDamage = entry.OriginalDamage,
                    AdjustDamage = adjustable ? (v => entry.Amount = v) : null,
                };
            }
            return det;
        }

        /// <summary>The local id of every part of every attachment the avatar wears.</summary>
        private static List<uint> AttachmentParts(ScenePresence presence)
        {
            var parts = new List<uint>();
            foreach (SceneObjectGroup attachment in presence.GetAttachments())
            {
                if (attachment == null || attachment.IsDeleted)
                    continue;
                foreach (SceneObjectPart part in attachment.Parts)
                    parts.Add(part.LocalId);
            }
            return parts;
        }

        // ---- control (hook 17) ----

        private void OnScriptControlEvent(UUID itemID, UUID agentID, uint held, uint change)
            => m_poster.PostScriptEvent(itemID, new EventParams("control", new object[] { agentID, (int)held, (int)change }, null));

        // ---- collisions (hooks 19 to 21) ----

        private void OnScriptColliderStart(uint localID, ColliderArgs col) => PostCollision("collision_start", localID, col);

        private void OnScriptColliding(uint localID, ColliderArgs col) => PostCollision("collision", localID, col);

        private void OnScriptCollidingEnd(uint localID, ColliderArgs col) => PostCollision("collision_end", localID, col);

        private void PostCollision(string eventName, uint localID, ColliderArgs col)
        {
            DetectParams[] det = FilteredColliders(localID, col);
            if (det.Length == 0)
                return;
            m_poster.PostObjectEvent(localID, new EventParams(eventName, new object[] { det.Length }, det));
        }

        /// <summary>
        /// The colliders the host part's llCollisionFilter lets through, as DetectParams. The region applies
        /// SceneObjectPart.CollisionFilteredOut before it raises the event; applying it again here means the filter
        /// holds for a collision that arrives another way, and the count a script sees is the count it may see.
        /// </summary>
        private DetectParams[] FilteredColliders(uint localID, ColliderArgs col)
        {
            if (col?.Colliders == null || col.Colliders.Count == 0)
                return s_noDetected;
            SceneObjectPart host = m_scene.GetSceneObjectPart(localID);
            var det = new List<DetectParams>(col.Colliders.Count);
            foreach (DetectedObject detobj in col.Colliders)
            {
                if (host != null && host.CollisionFilteredOut(detobj.keyUUID, detobj.nameStr))
                    continue;
                var d = new DetectParams { Key = detobj.keyUUID };
                d.Populate(m_scene, detobj);
                // A collider that left the region between the physics step and now: Populate finds nothing, so the
                // entry keeps what physics saw, as Halcyon's DetectParams.FromDetectedObject copied it.
                if (string.IsNullOrEmpty(d.Name) && detobj.keyUUID.IsNotZero() && m_scene.GetScenePresence(detobj.keyUUID) == null
                    && m_scene.GetSceneObjectPart(detobj.keyUUID) == null)
                    FromDetectedObject(d, detobj);
                det.Add(d);
            }
            return det.ToArray();
        }

        private static void FromDetectedObject(DetectParams d, DetectedObject detobj)
        {
            d.Name = detobj.nameStr ?? string.Empty;
            d.Owner = detobj.ownerUUID;
            d.Group = detobj.groupUUID;
            d.Position = new LSL_Types.Vector3(detobj.posVector);
            d.Rotation = new LSL_Types.Quaternion(detobj.rotQuat);
            d.Velocity = new LSL_Types.Vector3(detobj.velVector);
            d.LinkNum = detobj.linkNumber;
            d.Type = detobj.colliderType;
        }

        // ---- land collisions (hooks 22 to 24): one event per contact point, with its position ----

        private void OnScriptLandColliderStart(uint localID, ColliderArgs col) => PostLandCollision("land_collision_start", localID, col);

        private void OnScriptLandColliding(uint localID, ColliderArgs col) => PostLandCollision("land_collision", localID, col);

        private void OnScriptLandColliderEnd(uint localID, ColliderArgs col) => PostLandCollision("land_collision_end", localID, col);

        private void PostLandCollision(string eventName, uint localID, ColliderArgs col)
        {
            if (col?.Colliders == null)
                return;
            foreach (DetectedObject detobj in col.Colliders)
                m_poster.PostObjectEvent(localID, new EventParams(eventName, new object[] { detobj.posVector }, s_noDetected));
        }

        // ---- attach (hook 25): the wearer, or the null key when the object is taken off ----

        private void OnAttach(uint localID, UUID itemID, UUID avatarID)
            => m_poster.PostObjectEvent(localID, new EventParams("attach", new object[] { avatarID }, s_noDetected));

        // ---- moving and targets (hooks 26 to 31) ----

        private void OnScriptMovingStartEvent(uint localID)
            => m_poster.PostObjectEvent(localID, new EventParams("moving_start", Array.Empty<object>(), s_noDetected));

        private void OnScriptMovingEndEvent(uint localID)
            => m_poster.PostObjectEvent(localID, new EventParams("moving_end", Array.Empty<object>(), s_noDetected));

        private void OnScriptAtTargetEvent(UUID scriptID, uint handle, Vector3 targetpos, Vector3 atpos)
            => m_poster.PostScriptEvent(scriptID, new EventParams("at_target", new object[] { (int)handle, targetpos, atpos }, s_noDetected));

        private void OnScriptNotAtTargetEvent(UUID scriptID)
            => m_poster.PostScriptEvent(scriptID, new EventParams("not_at_target", Array.Empty<object>(), s_noDetected));

        private void OnScriptAtRotTargetEvent(UUID scriptID, uint handle, Quaternion targetrot, Quaternion atrot)
            => m_poster.PostScriptEvent(scriptID, new EventParams("at_rot_target", new object[] { (int)handle, targetrot, atrot }, s_noDetected));

        private void OnScriptNotAtRotTargetEvent(UUID scriptID)
            => m_poster.PostScriptEvent(scriptID, new EventParams("not_at_rot_target", Array.Empty<object>(), s_noDetected));

        // ---- frame (hook 44) ----

        // Fed once per heartbeat frame from the scene's own timings (physics update plus physics sync, which the sim
        // stats add up as the physics milliseconds), while the physics throttle is on.
        private void OnFrame()
        {
            int ms = m_scene.MonitorPhysicsUpdateTime + m_scene.MonitorPhysicsSyncTime;
            lock (m_frameLock)
            {
                m_frameTimes[m_frameNext] = ms;
                m_frameNext = (m_frameNext + 1) % m_frameTimes.Length;
                if (m_frameCount < m_frameTimes.Length)
                    m_frameCount++;
            }
        }
    }
}
