/*
 * The per-listener visibility matrix: for each listener, the set of source UUIDs it must not
 * hear. Exclusion is stored per listener (directed) because the mixer applies it per listener;
 * SeeAVs/ban are computed symmetrically so both directions are present. This one object is the
 * single source of truth for future audio exclusion AND roster/join omission (semantics doc 1).
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public sealed class VisibilityMatrix
    {
        private static readonly IReadOnlySet<UUID> EmptySet = new HashSet<UUID>();

        private readonly Dictionary<UUID, HashSet<UUID>> _excluded;

        private VisibilityMatrix(Dictionary<UUID, HashSet<UUID>> excluded)
        {
            _excluded = excluded;
        }

        /// An empty matrix (no agents / no exclusions) - the Diff baseline for bootstrap.
        public static readonly VisibilityMatrix Empty =
            new VisibilityMatrix(new Dictionary<UUID, HashSet<UUID>>());

        /// Listeners that have at least one exclusion.
        public IReadOnlyCollection<UUID> Listeners => _excluded.Keys;

        /// The set of sources excluded for a listener (empty if none).
        public IReadOnlySet<UUID> ExcludedFor(UUID listener)
            => _excluded.TryGetValue(listener, out HashSet<UUID> s) ? s : EmptySet;

        public bool IsExcluded(UUID listener, UUID source)
            => _excluded.TryGetValue(listener, out HashSet<UUID> s) && s.Contains(source);

        /// One code path: enumerate all presences, resolve each parcel once, apply the rules to
        /// every ordered pair. Root parcels via cached GlobalID; child parcels via position.
        public static VisibilityMatrix Build(IFeederWorld world)
        {
            IReadOnlyList<AgentView> agents = world.SnapshotAgents();
            EstateView estate = world.Estate;

            int n = agents.Count;
            var parcels = new ParcelView[n];
            for (int i = 0; i < n; i++)
                parcels[i] = ResolveParcel(world, agents[i]);

            var excluded = new Dictionary<UUID, HashSet<UUID>>(n);
            for (int li = 0; li < n; li++)
            {
                AgentView L = agents[li];
                ParcelView pL = parcels[li];
                HashSet<UUID> set = null;
                for (int si = 0; si < n; si++)
                {
                    if (si == li)
                        continue;
                    if (VisibilityRules.IsExcluded(L, pL, agents[si], parcels[si], estate))
                        (set ??= new HashSet<UUID>()).Add(agents[si].Id);
                }
                if (set != null)
                    excluded[L.Id] = set;
            }
            return new VisibilityMatrix(excluded);
        }

        /// Root: cached currentParcelUUID when known. Child (or root with unknown parcel):
        /// position-derived, since children get no crossing event (baseline 2.2 / #9).
        internal static ParcelView ResolveParcel(IFeederWorld world, in AgentView a)
        {
            if (!a.IsChild && a.CurrentParcelUUID != UUID.Zero)
                return world.GetParcelByGlobalId(a.CurrentParcelUUID);
            return world.GetParcelAt(a.Position);
        }
    }
}
