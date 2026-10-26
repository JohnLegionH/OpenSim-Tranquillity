/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
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
    }

    /// <summary>Counts of the pauses the policy hooks made, for the status command and the tests.</summary>
    internal sealed class PolicyStats
    {
        private long m_parcelEvaluated, m_parcelPaused, m_parcelResumed, m_estatePaused, m_estateResumed, m_crossingHeld, m_crossingReleased;

        public long ParcelEvaluated => System.Threading.Interlocked.Read(ref m_parcelEvaluated);
        public long ParcelPaused => System.Threading.Interlocked.Read(ref m_parcelPaused);
        public long ParcelResumed => System.Threading.Interlocked.Read(ref m_parcelResumed);
        public long EstatePaused => System.Threading.Interlocked.Read(ref m_estatePaused);
        public long EstateResumed => System.Threading.Interlocked.Read(ref m_estateResumed);
        public long CrossingHeld => System.Threading.Interlocked.Read(ref m_crossingHeld);
        public long CrossingReleased => System.Threading.Interlocked.Read(ref m_crossingReleased);

        internal void Evaluated() => System.Threading.Interlocked.Increment(ref m_parcelEvaluated);

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
        /// <summary>A script of the object waits for an avatar that has not arrived (a grant from a rider still to come).</summary>
        bool AwaitsRider(SceneObjectGroup group, ISet<UUID> arrived);

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

        public bool AwaitsRider(SceneObjectGroup group, ISet<UUID> arrived) => false;
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
    /// Experience and permission removal, bots) as holds on the engine's scripts and calls to their grants.
    /// </summary>
    internal sealed class SceneHooksB
    {
        /// <summary>How long a crossing object's changed(CHANGED_REGION) waits for riders still to come, in seconds.</summary>
        internal const double ArrivalRiderWaitSeconds = 10;

        private readonly Scene m_scene;
        private readonly IPolicyHost m_host;
        private readonly IScriptGrants m_grants;
        private readonly ISchedulerClock m_clock;

        public SceneHooksB(Scene scene, IPolicyHost host, IScriptGrants grants, ISchedulerClock clock = null)
        {
            m_scene = scene;
            m_host = host;
            m_grants = grants ?? NoGrants.Instance;
            m_clock = clock ?? new MonotonicClock();
        }

        public bool IsSubscribed { get; private set; }

        public void Subscribe() { }

        public void Unsubscribe() { }

        /// <summary>A script has just been made: the estate switch and the parcel rule are applied before it starts.</summary>
        public void EvaluateNew(UUID itemID) { }

        /// <summary>Re-asks the parcel rule of every script of the object.</summary>
        public void EvaluateGroup(SceneObjectGroup group) { }

        public bool AwaitsRider(SceneObjectGroup group, ISet<UUID> arrived) => false;

        /// <summary>Holds a crossing object's changed event until its riders have arrived.</summary>
        public void HoldArrival(UUID itemID, SceneObjectGroup group, int change) { }

        public int HeldArrivalCount => 0;

        /// <summary>Posts the held changed events whose riders have arrived or whose wait is over.</summary>
        public void ProcessHeldArrivals() { }

        /// <summary>The region's settings changed: the estate switch is read again.</summary>
        public void RegionSettingsChanged() { }

        /// <summary>Marks a controls change the engine made itself on this thread, so the region's release event is not taken for the region's own.</summary>
        public static IDisposable OwnControlChange() => new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
