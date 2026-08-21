/*
 * Slice 1 (voice-moderation-design-brief.md) in-memory sticky per-parcel voice-moderation
 * state, keyed by parcel GlobalID. NON-PERSISTENT by decision: a region restart clears every
 * mute. Written by the SpatialVoiceModerationRequest CAP handler (WebRtcVoiceRegionModule) and
 * purged on parcel removal by VoiceVisibilityService.OnLandObjectRemoved so join/delete orphans
 * self-heal.
 *
 * NOTHING reads this yet. The matrix enforcement rule (a source-side ParcelView predicate) is a
 * later commit; this class deliberately exposes only writes and a purge, no query, so "nothing
 * consumes the store yet" is provable by its surface.
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
