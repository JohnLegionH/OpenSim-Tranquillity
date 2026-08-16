/*
 * Diff two matrices into one batch of per-listener add/remove changes, and produce a
 * per-listener Replace snapshot. Pure and deterministic - the delta-correctness unit tests
 * assert against these directly.
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public static class DeltaComputer
    {
        /// Per-listener exclusion changes between prev and next. Only listeners whose set
        /// actually changed appear; both directions of a symmetric change are already present in
        /// the matrices, so a boundary crossing yields the full fan-out (both the co-occupant
        /// removals AND the outsider additions - see VoiceStateFeederTests fan-out case).
        public static VisibilityBatch Diff(VisibilityMatrix prev, VisibilityMatrix next, int room)
        {
            var added = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            var removed = new Dictionary<UUID, IReadOnlyCollection<UUID>>();

            var listeners = new HashSet<UUID>();
            foreach (UUID l in prev.Listeners) listeners.Add(l);
            foreach (UUID l in next.Listeners) listeners.Add(l);

            foreach (UUID L in listeners)
            {
                IReadOnlySet<UUID> before = prev.ExcludedFor(L);
                IReadOnlySet<UUID> after = next.ExcludedFor(L);

                List<UUID> add = null;
                foreach (UUID s in after)
                    if (!before.Contains(s))
                        (add ??= new List<UUID>()).Add(s);

                List<UUID> rem = null;
                foreach (UUID s in before)
                    if (!after.Contains(s))
                        (rem ??= new List<UUID>()).Add(s);

                if (add != null) added[L] = add;
                if (rem != null) removed[L] = rem;
            }

            return VisibilityBatch.Delta(room, added, removed);
        }

        /// One listener's full current exclusion set as a Replace snapshot (join/reconnect).
        public static VisibilityBatch SnapshotFor(VisibilityMatrix matrix, UUID listener, int room)
        {
            var full = new List<UUID>(matrix.ExcludedFor(listener));
            return VisibilityBatch.Snapshot(room, listener, full);
        }
    }
}
