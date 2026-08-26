/*
 * Pure target resolution for the voice-moderation console commands: turn the one token an
 * operator typed into exactly one muted agent, or into an honest refusal.
 *
 * Scene-free and console-free ON PURPOSE — the same reason LandBan and VoiceModerationAuth were
 * extracted. The interesting behaviour here is the ambiguity and absence handling, and that is
 * precisely the part that must be testable without standing up a region.
 *
 * The namespace this searches is the MODERATION STATE ITSELF, not the grid's user directory and
 * not the region's roster. That is deliberate and it is the whole point of the command: the muted
 * avatar has been removed from every roster the operator can click, so resolving against a roster
 * would reproduce the trap the command exists to escape. A grid-wide directory lookup was the
 * other option and was rejected: it can return a user who is not muted anywhere (silently doing
 * nothing), it cannot report ambiguity, and it needs a live user service the console may not have.
 * Resolving against the muted set instead makes both failure modes nameable and exact.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    /// The outcome of resolving one operator-typed token against the muted set.
    public enum VoiceModerationTargetMatch
    {
        /// Exactly one agent. `target` is set.
        Resolved,
        /// A UUID could not be parsed and no muted agent carries that name.
        NotFound,
        /// The name matched more than one distinct muted agent; `ambiguous` lists them so the
        /// operator can re-run with a UUID.
        Ambiguous
    }

    /// One muted agent as the resolver sees it: the id the store holds, and whatever name the
    /// caller managed to resolve for it (null/empty when the scene could not resolve one — such
    /// an entry is still addressable by UUID, just not by name).
    public sealed class VoiceModerationCandidate
    {
        public UUID AgentId { get; }
        public string Name { get; }

        public VoiceModerationCandidate(UUID agentId, string name)
        {
            AgentId = agentId;
            Name = name;
        }
    }

    public static class VoiceModerationTargets
    {
        /// Resolve `token` to a single agent id.
        ///
        /// A parseable non-zero UUID resolves to itself WITHOUT requiring membership in `muted`.
        /// Resolution and outcome are separate questions: a UUID is unambiguous by construction, so
        /// it always resolves, and the caller then reports truthfully that the store held no entry
        /// for it. Folding "not muted" into "could not resolve" would tell an operator their UUID
        /// was malformed when in fact their assumption about the state was wrong.
        ///
        /// Anything else is treated as a name and matched case-insensitively against `muted`,
        /// de-duplicated by agent id first (the same avatar muted on two parcels is ONE candidate,
        /// not an ambiguity).
        public static VoiceModerationTargetMatch Resolve(
            string token,
            IReadOnlyList<VoiceModerationCandidate> muted,
            out UUID target,
            out IReadOnlyList<VoiceModerationCandidate> ambiguous)
        {
            target = UUID.Zero;
            ambiguous = Array.Empty<VoiceModerationCandidate>();

            if (string.IsNullOrWhiteSpace(token))
                return VoiceModerationTargetMatch.NotFound;

            string trimmed = token.Trim();

            // UUID.Zero is rejected rather than resolved: it is what a failed parse elsewhere in
            // this module produces, and unmuting "nobody" on every parcel is not a useful action.
            if (UUID.TryParse(trimmed, out UUID parsed) && parsed != UUID.Zero)
            {
                target = parsed;
                return VoiceModerationTargetMatch.Resolved;
            }

            var byId = new Dictionary<UUID, VoiceModerationCandidate>();
            if (muted is not null)
            {
                foreach (VoiceModerationCandidate c in muted)
                {
                    if (c is null || string.IsNullOrWhiteSpace(c.Name))
                        continue;   // unnamed entries are UUID-addressable only
                    if (!string.Equals(c.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                        continue;
                    byId[c.AgentId] = c;   // dedupe: one avatar muted on several parcels is one hit
                }
            }

            if (byId.Count == 0)
                return VoiceModerationTargetMatch.NotFound;

            if (byId.Count == 1)
            {
                foreach (KeyValuePair<UUID, VoiceModerationCandidate> kv in byId)
                    target = kv.Key;
                return VoiceModerationTargetMatch.Resolved;
            }

            var list = new List<VoiceModerationCandidate>(byId.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.AgentId.ToString(), b.AgentId.ToString()));
            ambiguous = list;
            return VoiceModerationTargetMatch.Ambiguous;
        }
    }
}
