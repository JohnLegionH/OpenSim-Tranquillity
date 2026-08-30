/*
 * Avatar-to-avatar (P2P) voice invitation registry (Docs/voice/a2a-build-plan.md §1.3, slice S-A2A-1).
 *
 * In-memory, per region-server instance, thread-safe: sessionID -> { caller, callee, token, state, created }.
 * The session id is the viewer's XOR of the two agent ids (llimview.cpp:2530-2570 in the wire trace); the
 * sim re-derives it and must find it equal. A record is created at ChatSessionRequest "start p2p voice"
 * (caller = the requesting agent, callee = `params`), the token is minted at the caller's "call" and is what
 * the viewer sends back as provision `credentials`. Unanswered invitations expire on a TTL.
 *
 * This class makes NO authorization decision itself. In S-A2A-1 nothing consults it for admission; the O-29
 * predicate is replaced for "multiagent" only in S-A2A-3, which will ask this registry: does the provision's
 * `channel` name a live session, is the requesting agent one of its two parties, and does `credentials`
 * equal the token. Everything else stays fail-closed.
 *
 * Cross-instance A2A is out of scope for v1 (plan §1.7): a callee on another simulator will not find the
 * record and is refused, not half-provisioned.
 */
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public enum A2ASessionState
    {
        /// <summary>Recorded at "start p2p voice"; the callee has not yet accepted (provisioned).</summary>
        Invited = 0,
        /// <summary>Both parties have provisioned (set by S-A2A-3; unused in S-A2A-1).</summary>
        Active = 1,
    }

    /// <summary>One invitation. Immutable identity fields; mutable token/state/seen under the registry lock.</summary>
    public sealed class A2ASession
    {
        public UUID SessionId { get; }
        public UUID Caller { get; }
        public UUID Callee { get; }
        public DateTime CreatedUtc { get; }

        /// <summary>The per-session secret the viewer echoes as provision <c>credentials</c>. Null until "call".</summary>
        public string Token { get; internal set; }
        public A2ASessionState State { get; internal set; }
        /// <summary>Last activity (record, call, refresh); TTL is measured from here.</summary>
        public DateTime LastSeenUtc { get; internal set; }

        /// <summary>The value the viewer sends as <c>channel</c> on a multiagent provision: the session id as a string.</summary>
        public string ChannelUri => SessionId.ToString();

        internal A2ASession(UUID sessionId, UUID caller, UUID callee, DateTime nowUtc)
        {
            SessionId = sessionId;
            Caller = caller;
            Callee = callee;
            CreatedUtc = nowUtc;
            LastSeenUtc = nowUtc;
            State = A2ASessionState.Invited;
        }

        public bool IsParty(UUID agent) => agent == Caller || agent == Callee;

        public UUID OtherParty(UUID agent) => agent == Caller ? Callee : agent == Callee ? Caller : UUID.Zero;
    }

    public sealed class A2ASessionRegistry
    {
        /// <summary>Default lifetime of an unanswered invitation (plan §1.3: "TTL for unanswered").</summary>
        public static readonly TimeSpan DefaultInviteTtl = TimeSpan.FromMinutes(2);

        /// <summary>Token size in bytes; rendered as lowercase hex (64 chars) so it survives LLSD string round-trips.</summary>
        public const int TokenBytes = 32;

        private readonly object _lock = new object();
        private readonly Dictionary<UUID, A2ASession> _sessions = new Dictionary<UUID, A2ASession>();
        private readonly TimeSpan _inviteTtl;
        private readonly Func<DateTime> _clock;

        public A2ASessionRegistry() : this(DefaultInviteTtl, null) { }

        /// <param name="inviteTtl">Lifetime of an unanswered invitation.</param>
        /// <param name="clock">UTC clock; injectable so TTL is unit-testable without sleeping.</param>
        public A2ASessionRegistry(TimeSpan inviteTtl, Func<DateTime> clock)
        {
            _inviteTtl = inviteTtl > TimeSpan.Zero ? inviteTtl : DefaultInviteTtl;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public TimeSpan InviteTtl => _inviteTtl;

        /// <summary>The viewer's P2P session id: XOR of the two agent ids (symmetric, so both parties derive it).</summary>
        public static UUID ComputeSessionId(UUID a, UUID b)
            => new UUID(a.ulonga ^ b.ulonga, a.ulongb ^ b.ulongb);

        /// <summary>
        /// Record (or refresh) the invitation for caller -> callee. Idempotent for the same pair: a repeated
        /// "start p2p voice" refreshes LastSeen and keeps the token, so a caller that retries does not
        /// invalidate credentials already handed out. A record for the same session id from the OTHER
        /// direction (callee calling back) is the same session and is likewise refreshed, not replaced.
        /// </summary>
        public A2ASession Record(UUID caller, UUID callee, out bool created)
        {
            UUID sessionId = ComputeSessionId(caller, callee);
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                if (_sessions.TryGetValue(sessionId, out A2ASession existing) && existing.IsParty(caller) && existing.IsParty(callee))
                {
                    existing.LastSeenUtc = now;
                    created = false;
                    return existing;
                }
                A2ASession s = new A2ASession(sessionId, caller, callee, now);
                _sessions[sessionId] = s;
                created = true;
                return s;
            }
        }

        /// <summary>
        /// Mint (or return the existing) per-session token for a party's "call". Returns null when the
        /// session is unknown, expired, or the agent is not one of its parties.
        /// </summary>
        public A2ASession IssueToken(UUID sessionId, UUID agent)
        {
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                if (!_sessions.TryGetValue(sessionId, out A2ASession s) || !s.IsParty(agent))
                    return null;
                if (string.IsNullOrEmpty(s.Token))
                    s.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
                s.LastSeenUtc = now;
                return s;
            }
        }

        /// <summary>Lookup by the provision's <c>channel</c> string (the session id). Null when unknown or expired.</summary>
        public A2ASession TryGetByChannel(string channel)
        {
            if (!UUID.TryParse(channel, out UUID sessionId))
                return null;
            return TryGet(sessionId);
        }

        public A2ASession TryGet(UUID sessionId)
        {
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                return _sessions.TryGetValue(sessionId, out A2ASession s) ? s : null;
            }
        }

        /// <summary>Remove a session (decline, both-logout). False when nothing was recorded.</summary>
        public bool Remove(UUID sessionId)
        {
            lock (_lock)
                return _sessions.Remove(sessionId);
        }

        /// <summary>Number of live (unexpired) sessions; sweeps first. Diagnostics / tests.</summary>
        public int Count
        {
            get
            {
                DateTime now = _clock();
                lock (_lock)
                {
                    SweepExpiredLocked(now);
                    return _sessions.Count;
                }
            }
        }

        /// <summary>Drop every Invited session whose LastSeen is older than the TTL. Active sessions (S-A2A-3) never expire here.</summary>
        private void SweepExpiredLocked(DateTime now)
        {
            List<UUID> dead = null;
            foreach (KeyValuePair<UUID, A2ASession> kv in _sessions)
            {
                if (kv.Value.State == A2ASessionState.Invited && now - kv.Value.LastSeenUtc > _inviteTtl)
                    (dead ??= new List<UUID>()).Add(kv.Key);
            }
            if (dead != null)
                foreach (UUID id in dead)
                    _sessions.Remove(id);
        }
    }
}
