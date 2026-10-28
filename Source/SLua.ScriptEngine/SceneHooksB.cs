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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine
{
    /// <summary>Why a script is held back. A script runs only while none of these is set; each is cleared by its own cause.</summary>
    [Flags]
    internal enum PauseReason
    {
        None = 0,
        /// <summary>The region asked the engine to suspend it (SuspendScript).</summary>
        Core = 1,
        /// <summary>The parcel under its object does not allow scripts.</summary>
        Parcel = 2,
        /// <summary>The region's scripts are switched off (the estate's "disable scripts").</summary>
        Estate = 4,
        /// <summary>Its object is crossing into another region.</summary>
        Crossing = 8,
        /// <summary>Its object arrived with riders and the region holds it for them (Scene.CrossingHold); the region's resume ends it.</summary>
        Arrival = 16,
    }

    /// <summary>Counts of the pauses the policy hooks made, for the status command and the tests.</summary>
    internal sealed class PolicyStats
    {
        private long m_parcelPaused, m_parcelResumed, m_estatePaused, m_estateResumed, m_crossingHeld, m_crossingReleased;

        public long ParcelPaused => System.Threading.Interlocked.Read(ref m_parcelPaused);
        public long ParcelResumed => System.Threading.Interlocked.Read(ref m_parcelResumed);
        public long EstatePaused => System.Threading.Interlocked.Read(ref m_estatePaused);
        public long EstateResumed => System.Threading.Interlocked.Read(ref m_estateResumed);
        public long CrossingHeld => System.Threading.Interlocked.Read(ref m_crossingHeld);
        public long CrossingReleased => System.Threading.Interlocked.Read(ref m_crossingReleased);

        internal void Count(PauseReason reason, bool paused)
        {
            switch (reason)
            {
                case PauseReason.Parcel: System.Threading.Interlocked.Increment(ref (paused ? ref m_parcelPaused : ref m_parcelResumed)); break;
                case PauseReason.Estate: System.Threading.Interlocked.Increment(ref (paused ? ref m_estatePaused : ref m_estateResumed)); break;
                case PauseReason.Crossing: System.Threading.Interlocked.Increment(ref (paused ? ref m_crossingHeld : ref m_crossingReleased)); break;
            }
        }
    }

    /// <summary>A script the engine holds: its item and the prim it is in.</summary>
    internal readonly record struct PolicyScript(UUID ItemID, uint LocalID);

    /// <summary>What the policy hooks need from the engine: its scripts, and a way to hold one back or let it run.</summary>
    internal interface IPolicyHost
    {
        PolicyScript[] AllScripts();

        bool TryGetScript(UUID itemID, out PolicyScript script);

        /// <summary>Sets or clears one reason a script is held back. Unknown items are ignored.</summary>
        void SetPaused(UUID itemID, PauseReason reason, bool paused);

        bool PostScriptEvent(UUID itemID, EventParams parms);
    }

    /// <summary>
    /// What the engine's scripts hold from the region that the policy hooks must tell them of: permission grants,
    /// taken controls, Experience grants and bots. The ll functions that make these are bound in a later step; until
    /// then <see cref="NoGrants"/> answers that nothing is held.
    /// </summary>
    internal interface IScriptGrants
    {

        void ObjectRemoved(SceneObjectGroup group);

        void OwnerChanged(SceneObjectGroup group);

        /// <summary>Controls were released for these items; <paramref name="byRegion"/> when the script did not ask for it.</summary>
        void ControlsReleased(UUID agentId, UUID[] itemIds, bool byRegion);

        void AvatarArrived(UUID agentId);

        void ExperienceLandCheck(UUID agentId);

        void ExperienceGrantEnded(UUID itemId, UUID granterId, UUID experienceId);

        void PermissionsRemoved(UUID itemId);
    }

    /// <summary>The grants of an engine whose scripts cannot make any yet.</summary>
    internal sealed class NoGrants : IScriptGrants
    {
        public static readonly NoGrants Instance = new NoGrants();

        public void ObjectRemoved(SceneObjectGroup group) { }
        public void OwnerChanged(SceneObjectGroup group) { }
        public void ControlsReleased(UUID agentId, UUID[] itemIds, bool byRegion) { }
        public void AvatarArrived(UUID agentId) { }
        public void ExperienceLandCheck(UUID agentId) { }
        public void ExperienceGrantEnded(UUID itemId, UUID granterId, UUID experienceId) { }
        public void PermissionsRemoved(UUID itemId) { }
    }

    /// <summary>
    /// The region's policy events (parcel script rules, estate script switch, crossings, owner changes, controls,
    /// Experience and permission removal, bots) as holds on the engine's scripts and calls to their grants. Each handler
    /// follows what the Phlox engine does with the same event.
    /// </summary>
    internal sealed class SceneHooksB
    {
        private static readonly DetectParams[] s_noDetected = Array.Empty<DetectParams>();

        private readonly Scene m_scene;
        private readonly IPolicyHost m_host;
        private IScriptGrants m_grants;
        private readonly ISchedulerClock m_clock;
        private readonly object m_lock = new object();
        private bool m_subscribed;
        private IEstateModule m_estate;

        // Scripts that run on a no-scripts parcel only because their avatar holds the controls they took.
        private readonly HashSet<UUID> m_controlsExempt = new HashSet<UUID>();

        [ThreadStatic] private static int t_ownControlChange;

        public SceneHooksB(Scene scene, IPolicyHost host, IScriptGrants grants, ISchedulerClock clock = null)
        {
            m_scene = scene;
            m_host = host;
            m_grants = grants ?? NoGrants.Instance;
            m_clock = clock ?? new MonotonicClock();
        }

        /// <summary>What the engine's scripts hold from the region; replaced when the functions that make grants are bound.</summary>
        public IScriptGrants Grants
        {
            set => m_grants = value ?? NoGrants.Instance;
        }

        public bool IsSubscribed
        {
            get { lock (m_lock) return m_subscribed; }
        }

        public void Subscribe()
        {
            lock (m_lock)
            {
                if (m_subscribed)
                    return;
                m_subscribed = true;
            }
            EventManager e = m_scene.EventManager;
            e.OnObjectBeingRemovedFromScene += OnObjectBeingRemovedFromScene;
            e.OnGroupBeginInTransit += OnGroupBeginInTransit;
            e.OnGroupEndInTransit += OnGroupEndInTransit;
            e.OnGroupCrossedToNewParcel += OnGroupCrossedToNewParcel;
            e.OnObjectOwnerOrGroupChanged += OnObjectOwnerOrGroupChanged;
            e.OnLandObjectAdded += OnLandObjectAdded;
            e.OnScriptControlsReleased += OnScriptControlsReleased;
            e.OnRemovePresence += OnRemovePresence;
            e.OnMakeRootAgent += OnMakeRootAgent;
            e.OnAvatarEnteringNewParcel += OnAvatarEnteringNewParcel;
            e.OnExperiencePermissionsRevoked += OnExperiencePermissionsRevoked;
            e.OnScriptPermissionsRemoved += OnScriptPermissionsRemoved;
            e.OnAttach += OnAttach;
        }

        /// <summary>
        /// Follows the estate module's region-settings event, which is where "disable scripts" changes arrive. The estate
        /// module may load after the engine, so the engine calls this once the region has loaded.
        /// </summary>
        public void SubscribeEstate()
        {
            IEstateModule estate;
            lock (m_lock)
            {
                if (m_estate != null)
                    return;
                estate = m_scene.RequestModuleInterface<IEstateModule>();
                if (estate == null)
                    return;
                m_estate = estate;
            }
            estate.OnRegionInfoChange += OnRegionInfoChange;
        }

        public void Unsubscribe()
        {
            IEstateModule estate;
            lock (m_lock)
            {
                if (!m_subscribed)
                    return;
                m_subscribed = false;
                estate = m_estate;
                m_estate = null;
                m_controlsExempt.Clear();
            }
            if (estate != null)
                estate.OnRegionInfoChange -= OnRegionInfoChange;
            EventManager e = m_scene.EventManager;
            e.OnObjectBeingRemovedFromScene -= OnObjectBeingRemovedFromScene;
            e.OnGroupBeginInTransit -= OnGroupBeginInTransit;
            e.OnGroupEndInTransit -= OnGroupEndInTransit;
            e.OnGroupCrossedToNewParcel -= OnGroupCrossedToNewParcel;
            e.OnObjectOwnerOrGroupChanged -= OnObjectOwnerOrGroupChanged;
            e.OnLandObjectAdded -= OnLandObjectAdded;
            e.OnScriptControlsReleased -= OnScriptControlsReleased;
            e.OnRemovePresence -= OnRemovePresence;
            e.OnMakeRootAgent -= OnMakeRootAgent;
            e.OnAvatarEnteringNewParcel -= OnAvatarEnteringNewParcel;
            e.OnExperiencePermissionsRevoked -= OnExperiencePermissionsRevoked;
            e.OnScriptPermissionsRemoved -= OnScriptPermissionsRemoved;
            e.OnAttach -= OnAttach;
        }

        // ---- the parcel rule (hooks 35, 36, 37, 38, 39 and attach) ----

        /// <summary>
        /// Halcyon's rule (EngineInterface.ScriptsCanRun): the object's owner owns the parcel, or the parcel allows other
        /// scripts, or it allows group scripts and the object's group is the parcel's group. No parcel: not allowed. The
        /// group test needs the parcel to have a group, as the region's own start-time check does.
        /// </summary>
        internal static bool ParcelAllowsScripts(LandData land, UUID objectOwner, UUID objectGroup)
        {
            if (land == null)
                return false;
            if (land.OwnerID == objectOwner)
                return true;
            if ((land.Flags & (uint)ParcelFlags.AllowOtherScripts) != 0)
                return true;
            return (land.Flags & (uint)ParcelFlags.AllowGroupScripts) != 0 && land.GroupID.IsNotZero() && land.GroupID == objectGroup;
        }

        /// <summary>
        /// "May this script run here": an attachment may; a script the parcel allows may; a script holding taken controls on
        /// an avatar right now may (<paramref name="onlyByControls"/>); nothing else. No estate-manager or god exemption.
        /// A region with no land module has no parcel rules.
        /// </summary>
        private bool ScriptMayRunHere(SceneObjectPart part, UUID itemID, out bool onlyByControls)
        {
            onlyByControls = false;
            SceneObjectGroup group = part?.ParentGroup;
            if (group == null || group.IsAttachment)
                return true;
            ILandChannel landChannel = m_scene.LandChannel;
            if (landChannel == null)
                return true;

            Vector3 pos = group.AbsolutePosition;
            ILandObject parcel = landChannel.GetLandObjectClippedXY(pos.X, pos.Y);
            if (ParcelAllowsScripts(parcel?.LandData, part.OwnerID, part.GroupID))
                return true;

            if (HoldsControls(part, itemID))
            {
                onlyByControls = true;
                return true;
            }
            return false;
        }

        // Asks the avatar the script's controls were taken on (the permission granter), never a record of the engine's own.
        private bool HoldsControls(SceneObjectPart part, UUID itemID)
        {
            TaskInventoryItem item = part.Inventory.GetInventoryItem(itemID);
            if (item == null || item.PermsGranter.IsZero())
                return false;
            ScenePresence presence = m_scene.GetScenePresence(item.PermsGranter);
            return presence != null && presence.HasScriptControls(itemID);
        }

        private int ParcelLocalIdAt(SceneObjectGroup group)
        {
            Vector3 pos = group.AbsolutePosition;
            return m_scene.LandChannel?.GetLandObjectClippedXY(pos.X, pos.Y)?.LandData?.LocalID ?? -1;
        }

        /// <summary>
        /// The reasons a script that has just been made starts held back: the estate switch and the parcel rule. The engine sets
        /// them before the script is visible or started, so it starts paused and is never refused.
        /// </summary>
        public PauseReason ReasonsFor(UUID itemID, uint localID)
        {
            PauseReason reasons = PauseReason.None;
            if (m_scene.RegionInfo.RegionSettings.DisableScripts)
                reasons |= PauseReason.Estate;
            SceneObjectPart part = m_scene.GetSceneObjectPart(localID);
            if (part?.ParentGroup != null && !part.ParentGroup.IsDeleted)
            {
                bool allowed = ScriptMayRunHere(part, itemID, out bool onlyByControls);
                lock (m_lock)
                {
                    if (onlyByControls) m_controlsExempt.Add(itemID); else m_controlsExempt.Remove(itemID);
                }
                if (!allowed)
                    reasons |= PauseReason.Parcel;
                if (part.ParentGroup.HeldForRiders && !part.ParentGroup.IsAttachment)
                    reasons |= PauseReason.Arrival;
            }
            return reasons;
        }

        /// <summary>A script was made while the settings or the land may have changed under <see cref="ReasonsFor"/>: both are asked again.</summary>
        public void Resync(UUID itemID)
        {
            m_host.SetPaused(itemID, PauseReason.Estate, m_scene.RegionInfo.RegionSettings.DisableScripts);
            EvaluateItem(itemID);
            EvaluateArrival(itemID);
        }

        /// <summary>
        /// The region holds an object that arrived with riders until they have come (Scene.CrossingHold), and its scripts
        /// wait with it: held while the object is held, released by the region's resume once it is not. The engine keeps
        /// no wait of its own.
        /// </summary>
        public void EvaluateArrival(UUID itemID)
        {
            if (!m_host.TryGetScript(itemID, out PolicyScript script))
                return;
            SceneObjectGroup group = m_scene.GetSceneObjectPart(script.LocalID)?.ParentGroup;
            if (group == null)
                return;
            m_host.SetPaused(itemID, PauseReason.Arrival, group.HeldForRiders && !group.IsAttachment && !group.IsDeleted);
        }

        /// <summary>Asks the parcel rule again of one script and pauses or resumes it.</summary>
        public void EvaluateItem(UUID itemID)
        {
            if (!m_host.TryGetScript(itemID, out PolicyScript script))
                return;
            SceneObjectPart part = m_scene.GetSceneObjectPart(script.LocalID);
            if (part?.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;
            bool allowed = ScriptMayRunHere(part, itemID, out bool onlyByControls);
            lock (m_lock)
            {
                if (onlyByControls) m_controlsExempt.Add(itemID); else m_controlsExempt.Remove(itemID);
            }
            m_host.SetPaused(itemID, PauseReason.Parcel, !allowed);
        }

        /// <summary>Re-asks the parcel rule of every script of the object.</summary>
        public void EvaluateGroup(SceneObjectGroup group)
        {
            if (group == null || group.IsDeleted)
                return;
            foreach (UUID item in ItemsOf(group))
                EvaluateItem(item);
        }

        private List<UUID> ItemsOf(SceneObjectGroup group)
        {
            var items = new List<UUID>();
            foreach (PolicyScript script in m_host.AllScripts())
            {
                SceneObjectPart part = m_scene.GetSceneObjectPart(script.LocalID);
                if (part?.ParentGroup != null && ReferenceEquals(part.ParentGroup, group))
                    items.Add(script.ItemID);
            }
            return items;
        }

        // Hook 35: the object moved onto another parcel.
        private void OnGroupCrossedToNewParcel(SceneObjectGroup group, ILandObject oldParcel, ILandObject newParcel) => EvaluateGroup(group);

        // Hook 36: a new owner ends every grant in the object, and the controls the old grants took; the parcel decides again.
        private void OnObjectOwnerOrGroupChanged(SceneObjectGroup group, UUID oldOwner, UUID newOwner, UUID oldGroup, UUID newGroup)
        {
            if (oldOwner != newOwner)
                m_grants.OwnerChanged(group);
            EvaluateGroup(group);
        }

        // Hook 37: a parcel's flags, owner, group or shape changed (the parcel dialog, a join, a subdivide).
        private void OnLandObjectAdded(ILandObject parcel)
        {
            if (parcel?.LandData == null)
                return;
            int parcelId = parcel.LandData.LocalID;
            var onParcel = new Dictionary<UUID, bool>();
            foreach (PolicyScript script in m_host.AllScripts())
            {
                SceneObjectGroup group = m_scene.GetSceneObjectPart(script.LocalID)?.ParentGroup;
                if (group == null || group.IsDeleted || group.IsAttachment)
                    continue;
                if (!onParcel.TryGetValue(group.UUID, out bool on))
                    onParcel[group.UUID] = on = ParcelLocalIdAt(group) == parcelId;
                if (on)
                    EvaluateItem(script.ItemID);
            }
        }

        // Hook 38: the region raises this whenever a registration goes away, the script's own release or the region's
        // (the viewer's release keys, a stand-up, a permission revoke, the avatar leaving). Exactly those scripts are asked
        // again. A release the engine did not ask for, on an avatar still here (not a child, not crossing), is the region's:
        // the grants are told so, to take TAKE_CONTROLS and CONTROL_CAMERA from the script as Halcyon does (handleMustReleaseControls).
        private void OnScriptControlsReleased(UUID agentId, UUID[] scriptItemIds)
        {
            bool byRegion = false;
            if (t_ownControlChange == 0)
            {
                ScenePresence sp = m_scene.GetScenePresence(agentId);
                byRegion = sp != null && !sp.IsDeleted && !sp.IsChildAgent && !sp.IsInTransit;
            }
            m_grants.ControlsReleased(agentId, scriptItemIds, byRegion);
            foreach (UUID item in scriptItemIds)
                EvaluateItem(item);
        }

        /// <summary>Marks a controls change the engine made itself on this thread, so the region's release event is not taken for the region's own.</summary>
        public static IDisposable OwnControlChange()
        {
            t_ownControlChange++;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose() => t_ownControlChange--;
        }

        // Hook 39: a backstop for a presence that goes before its release event; every script that runs only by controls is asked again.
        private void OnRemovePresence(UUID agentId)
        {
            UUID[] exempt;
            lock (m_lock) exempt = new List<UUID>(m_controlsExempt).ToArray();
            foreach (UUID item in exempt)
                EvaluateItem(item);
        }

        // Worn, an object's scripts always run; dropped, the parcel under it decides.
        private void OnAttach(uint localID, UUID itemID, UUID avatarID) => EvaluateGroup(m_scene.GetGroupByPrim(localID));

        // ---- the estate switch ----

        private void OnRegionInfoChange(UUID regionID) => RegionSettingsChanged();

        /// <summary>The region's settings changed: the estate switch ("disable scripts") is read again and every script is paused or resumed to match.</summary>
        public void RegionSettingsChanged()
        {
            bool disabled = m_scene.RegionInfo.RegionSettings.DisableScripts;
            foreach (PolicyScript script in m_host.AllScripts())
                m_host.SetPaused(script.ItemID, PauseReason.Estate, disabled);
        }

        // ---- crossings (hooks 33, 34, 40) ----

        // Hook 33: an object starts to cross into another region: its scripts are held until the crossing ends, keeping the
        // events that reach them. A worn object crosses with its avatar and is not held.
        private void OnGroupBeginInTransit(SceneObjectGroup group)
        {
            if (group == null || group.IsAttachment)
                return;
            foreach (UUID item in ItemsOf(group))
                m_host.SetPaused(item, PauseReason.Crossing, true);
        }

        // Hook 34: a crossing ended. When the object stayed here its scripts carry on with what they kept while held. When
        // it left, the region removes its scripts with it; they stay held until then and are not resumed here.
        private void OnGroupEndInTransit(SceneObjectGroup group, bool crossed)
        {
            if (crossed || group == null || group.IsDeleted || group.IsAttachment)
                return;
            foreach (UUID item in ItemsOf(group))
                m_host.SetPaused(item, PauseReason.Crossing, false);
        }

        // Hook 40: an avatar became a root agent here (a crossing, a teleport, a login). The region itself releases an
        // object held for this avatar; the grants are told so a rider's pending grant can be decided.
        private void OnMakeRootAgent(ScenePresence sp)
        {
            if (sp != null)
                m_grants.AvatarArrived(sp.UUID);
        }

        // ---- grants, bots, Experiences (hooks 32, 41, 42, 43) ----

        // Hook 32: bots the object owned are cleaned up when it leaves the scene.
        private void OnObjectBeingRemovedFromScene(SceneObjectGroup group)
        {
            if (group != null)
                m_grants.ObjectRemoved(group);
        }

        // Hook 41: an avatar entered a parcel: a grant it gave from an Experience ends where the Experience cannot run
        // (https://wiki.secondlife.com/wiki/Experience_permissions_denied).
        private void OnAvatarEnteringNewParcel(ScenePresence sp, int localLandID, UUID regionID)
        {
            if (sp != null && !sp.IsChildAgent)
                m_grants.ExperienceLandCheck(sp.UUID);
        }

        // Hook 42: the region ended a script's Experience grant (the avatar blocked it, or it entered a parcel it cannot run on).
        private void OnExperiencePermissionsRevoked(UUID partId, UUID itemId, UUID granterId, UUID experienceId, int revokedMask, int reason)
        {
            if (m_host.TryGetScript(itemId, out _))
                m_grants.ExperienceGrantEnded(itemId, granterId, experienceId);
        }

        // Hook 43: the region took permissions from a script's item without the script asking (a stand-up, a detach,
        // Release Keys, the viewer's revoke). Nothing is posted to the script.
        private void OnScriptPermissionsRemoved(UUID partId, UUID itemId, UUID granterId, int removedMask)
        {
            if (m_host.TryGetScript(itemId, out _))
                m_grants.PermissionsRemoved(itemId);
        }
    }
}
