/*
 * Slice 1 (voice-moderation-design-brief.md) in-memory sticky per-parcel voice-moderation
 * state, keyed by parcel GlobalID. NON-PERSISTENT by decision: a region restart clears every
 * mute. Written by the SpatialVoiceModerationRequest CAP handler (WebRtcVoiceRegionModule) and
 * purged on parcel removal by VoiceVisibilityService.OnLandObjectRemoved so join/delete orphans
 * self-heal.
 *
 * Read by the matrix: FeederWorldFromScene folds IsModerated plus the moderator exemption
 * (VoiceModerationAuth) into the source-side ParcelView.IsVoiceModerated delegate, which
 * VisibilityRules evaluates as rule 2b on every matrix build.
 *
 * Thread-safe: the CAP handler writes on an HTTP worker thread, the purge runs on a sim thread,
 * and the future feeder will read on the tick thread — all under one lock.
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public sealed class VoiceModerationStore
    {
        private sealed class ParcelModeration
        {
            public bool MuteEveryone;
            public readonly HashSet<UUID> MutedAgents = new HashSet<UUID>();
            public bool IsEmpty => !MuteEveryone && MutedAgents.Count == 0;
        }

        private readonly Dictionary<UUID, ParcelModeration> m_byParcel = new Dictionary<UUID, ParcelModeration>();
        private readonly object m_lock = new object();

        /// mute_all / unmute_all — the sticky parcel-wide flag.
        public void SetMuteEveryone(UUID parcelGlobalId, bool muted)
        {
            lock (m_lock)
            {
                if (muted)
                {
                    GetOrCreate(parcelGlobalId).MuteEveryone = true;
                }
                else if (m_byParcel.TryGetValue(parcelGlobalId, out ParcelModeration p))
                {
                    p.MuteEveryone = false;
                    if (p.IsEmpty)
                        m_byParcel.Remove(parcelGlobalId);
                }
            }
        }

        /// mute <agent_id> — add one avatar to the parcel's muted set.
        public void MuteAgent(UUID parcelGlobalId, UUID agentId)
        {
            lock (m_lock)
                GetOrCreate(parcelGlobalId).MutedAgents.Add(agentId);
        }

        /// unmute <agent_id> — remove one avatar from the parcel's muted set.
        public void UnmuteAgent(UUID parcelGlobalId, UUID agentId)
        {
            lock (m_lock)
            {
                if (m_byParcel.TryGetValue(parcelGlobalId, out ParcelModeration p))
                {
                    p.MutedAgents.Remove(agentId);
                    if (p.IsEmpty)
                        m_byParcel.Remove(parcelGlobalId);
                }
            }
        }

        /// Purge all moderation state for a parcel that no longer exists (join / delete), so an
        /// orphaned GlobalID cannot linger. Called from OnLandObjectRemoved.
        public void Remove(UUID parcelGlobalId)
        {
            lock (m_lock)
                m_byParcel.Remove(parcelGlobalId);
        }

        /// True if the given avatar is moderated (muted) on the given parcel — the parcel-wide
        /// mute-everyone flag OR the individual muted set. Takes the SAME lock as the writers, so a
        /// read never observes a torn set. Moderator EXEMPTION is not decided here (this class knows
        /// only state, not permissions) — the adapter applies it, see FeederWorldFromScene.
        public bool IsModerated(UUID parcelGlobalId, UUID agentId)
        {
            lock (m_lock)
            {
                return m_byParcel.TryGetValue(parcelGlobalId, out ParcelModeration p)
                    && (p.MuteEveryone || p.MutedAgents.Contains(agentId));
            }
        }

        private ParcelModeration GetOrCreate(UUID parcelGlobalId)
        {
            if (!m_byParcel.TryGetValue(parcelGlobalId, out ParcelModeration p))
            {
                p = new ParcelModeration();
                m_byParcel[parcelGlobalId] = p;
            }
            return p;
        }
    }
}
