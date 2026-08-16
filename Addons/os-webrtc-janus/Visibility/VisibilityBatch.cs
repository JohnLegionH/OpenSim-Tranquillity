/*
 * One per-tick unit of change fed toward the mixer - the C# side of the protocol's
 * slvoice_vis message (semantics doc 3.3). A tick emits at most one batch:
 *   - Delta:    per-listener Added / Removed source UUIDs (the frequent movement case).
 *   - Snapshot: one listener's FULL exclusion set as a Replace (reconnect/bootstrap).
 * Wire emission is out of scope here; the (later) sender serializes Added -> op:"add",
 * Removed -> op:"remove", Snapshot -> op:"replace".
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public enum VisOp { Add, Remove, Replace }

    public sealed class VisibilityBatch
    {
        private static readonly IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> EmptyMap
            = new Dictionary<UUID, IReadOnlyCollection<UUID>>();

        public int Room { get; }
        public bool IsSnapshot { get; }
        public IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> Added { get; }
        public IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> Removed { get; }

        private VisibilityBatch(int room, bool isSnapshot,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> added,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> removed)
        {
            Room = room;
            IsSnapshot = isSnapshot;
            Added = added ?? EmptyMap;
            Removed = removed ?? EmptyMap;
        }

        /// A delta carries no per-listener changes at all -> the tick can be dropped.
        /// (A snapshot is never empty: an empty set is a meaningful "clear this listener".)
        public bool IsEmpty => !IsSnapshot && Added.Count == 0 && Removed.Count == 0;

        public static VisibilityBatch Delta(int room,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> added,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> removed)
            => new VisibilityBatch(room, false, added, removed);

        /// A no-change delta - returned on a skipped/failed tick so callers get a non-null batch.
        public static VisibilityBatch EmptyDelta(int room)
            => new VisibilityBatch(room, false, EmptyMap, EmptyMap);

        public static VisibilityBatch Snapshot(int room, UUID listener, IReadOnlyCollection<UUID> fullSet)
        {
            var map = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = fullSet };
            return new VisibilityBatch(room, true, map, EmptyMap);
        }
    }
}
