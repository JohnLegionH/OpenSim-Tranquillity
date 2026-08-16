/*
 * The boundary the (later) Janus sender consumes. The VoiceStateFeeder is the sole producer;
 * no code emits to Janus yet (this task is the producer only).
 */

using System;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public interface IVisibilityFeed
    {
        /// The current full matrix (single source of truth for audio + roster).
        VisibilityMatrix Current { get; }

        /// Raised at most once per tick with the add/remove delta (never for an empty diff).
        event Action<VisibilityBatch> BatchProduced;

        /// A listener's full exclusion set as a Replace snapshot, for join/reconnect recovery.
        VisibilityBatch SnapshotFor(UUID listener);
    }
}
