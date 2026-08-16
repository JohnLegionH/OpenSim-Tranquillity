/*
 * Pure builder for the peer_ctl_batch request body (mixer-feed-protocol.md §3.3). Produces the
 * ROOM-LESS inner request { "request":"peer_ctl_batch", "op":..., "excl":{ L:[S,...], ... } };
 * the sink stamps "room" (correction: room lives sink-side, feeder/orchestrator stay room-agnostic).
 *
 * It is the SINGLE producer of the wire body, so format is guarded HERE with an invariant. That is
 * deliberate: the mixer's Ok does not mean applied (an unknown room / a malformed batch both read as
 * janus:"success", §3.3.1), so we cannot detect a format error by inspecting responses — we prevent
 * it at the source instead. Dependency-free beyond OpenMetaverse; directly unit-testable.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    public static class PeerCtlBatchSerializer
    {
        public static string OpString(VisOp op)
        {
            switch (op)
            {
                case VisOp.Add: return "add";
                case VisOp.Remove: return "remove";
                case VisOp.Replace: return "replace";
                default: throw new ArgumentOutOfRangeException(nameof(op));
            }
        }

        /// <summary>Build the room-less peer_ctl_batch request. Format invariant: every listener and
        /// source is a non-zero UUID; a violation throws (the caller guards, never inspects responses).</summary>
        public static OSDMap BuildRequest(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl)
        {
            if (excl == null)
                throw new ArgumentNullException(nameof(excl));

            var exclMap = new OSDMap();
            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in excl)
            {
                if (kv.Key == UUID.Zero)
                    throw new InvalidOperationException("peer_ctl_batch invariant: zero listener UUID");
                var arr = new OSDArray();
                foreach (UUID s in kv.Value)
                {
                    if (s == UUID.Zero)
                        throw new InvalidOperationException("peer_ctl_batch invariant: zero source UUID");
                    arr.Add(new OSDString(s.ToString()));
                }
                exclMap[kv.Key.ToString()] = arr;
            }

            return new OSDMap
            {
                ["request"] = new OSDString("peer_ctl_batch"),
                ["op"] = new OSDString(OpString(op)),
                ["excl"] = exclMap
            };
        }

        /// <summary>Added and Removed must be disjoint per (listener, source) pair. A pair in BOTH is
        /// a feeder bug (it added and removed the same exclusion in one tick) — do not resolve it by
        /// ordering; throw naming the first overlap. The caller runs this inside its never-throw guard.</summary>
        public static void EnsureDisjoint(
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> added,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> removed)
        {
            if (added == null || removed == null)
                return;
            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in added)
            {
                if (!removed.TryGetValue(kv.Key, out IReadOnlyCollection<UUID> rem))
                    continue;
                foreach (UUID s in kv.Value)
                {
                    foreach (UUID r in rem)
                    {
                        if (s == r)
                            throw new InvalidOperationException(
                                $"peer_ctl_batch: (listener {kv.Key}, source {s}) is in BOTH add and remove — feeder bug");
                    }
                }
            }
        }
    }
}
