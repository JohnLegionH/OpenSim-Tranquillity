/*
 * P1.1: the engine. Start / Invite / Accept / Decline / Depart, plus the sweep.
 *
 * NOTHING IS WIRED. No cap handler calls this, no transport, no deploy. The live A2A path
 * (ChatSessionRequestLogic + A2ASessionRegistry + A2AProvisionAdmission) is untouched and keeps
 * running exactly as it does today; P1.6 migrates it onto this engine.
 *
 * IDEMPOTENCY (item 6) is not sprinkled through the methods -- it falls out of two rules:
 *   - identity is DERIVED, never generated, everywhere it can be (DeriveSessionId), so the same
 *     request always addresses the same session. ADHOC is the one exception and it is handled by
 *     keying the creation on the viewer's temp id (see StartAdhoc);
 *   - every read-modify-write goes through INonSpatialSessionStore.StartOrGet / Mutate, which are
 *     atomic, so concurrent duplicates collapse instead of racing.
 * That is what makes a repeated start, a retried invite, a double accept, a reconnect and a region
 * crossing all no-ops rather than duplicates.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>What an engine operation did. Decision is the greppable instrument word.</summary>
    public readonly struct SessionOutcome
    {
        public bool Ok { get; }
        public string Decision { get; }
        public NonSpatialVoiceSession Session { get; }
        public bool Created { get; }

        /// <summary>
        /// P1.2G-b: this operation took the room from 0 seats to 1 -- the moment a group voice call
        /// BEGINS, and the only moment the fan-out fires. Decided inside the store mutation, so two
        /// simultaneous first-joiners cannot both claim it and double-ring the group.
        /// </summary>
        public bool StartedRinging { get; }

        public SessionOutcome(bool ok, string decision, NonSpatialVoiceSession session, bool created = false,
                              bool startedRinging = false)
        {
            Ok = ok;
            Decision = decision;
            Session = session;
            Created = created;
            StartedRinging = startedRinging;
        }

        public static SessionOutcome Fail(string decision, NonSpatialVoiceSession s = null) => new SessionOutcome(false, decision, s);

        public const string NoSuchSession = "refused-no-session";
        public const string Capacity = "refused-capacity";
        public const string AlreadyDeparted = "refused-departed";
    }

    public sealed class NonSpatialVoiceSessionEngine
    {
        /// <summary>An unanswered invitation's lifetime. Matches the live A2A invite TTL for consistency.</summary>
        public static readonly TimeSpan DefaultInviteTtl = TimeSpan.FromMinutes(2);

        /// <summary>Idle backstop for a formed session; the same reasoning as A2ASessionRegistry.DefaultActiveIdleTtl.</summary>
        public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromHours(8);

        private readonly INonSpatialSessionStore _store;
        private readonly Dictionary<NonSpatialSessionType, INonSpatialAdmission> _admission;
        private readonly string _gridId;
        private readonly Func<DateTime> _clock;
        private readonly TimeSpan _inviteTtl;
        private readonly TimeSpan _idleTtl;

        public NonSpatialVoiceSessionEngine(INonSpatialSessionStore store,
                                            IEnumerable<INonSpatialAdmission> admissions,
                                            string gridId,
                                            Func<DateTime> clock = null,
                                            TimeSpan? inviteTtl = null,
                                            TimeSpan? idleTtl = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _admission = new Dictionary<NonSpatialSessionType, INonSpatialAdmission>();
            if (admissions != null)
                foreach (INonSpatialAdmission a in admissions)
                    _admission[a.Type] = a;
            _gridId = gridId ?? string.Empty;
            _clock = clock ?? (() => DateTime.UtcNow);
            _inviteTtl = inviteTtl ?? DefaultInviteTtl;
            _idleTtl = idleTtl ?? DefaultIdleTtl;
        }

        public INonSpatialSessionStore Store => _store;
        public TimeSpan InviteTtl => _inviteTtl;
        public TimeSpan IdleTtl => _idleTtl;

        // ---- identity ------------------------------------------------------------------------

        /// <summary>
        /// The AUTHORITATIVE session id. Derived wherever the viewer already agrees, so the start
        /// reply is a no-op re-key and a repeat addresses the same session:
        ///   P2P   the XOR of the two agents -- what the viewer computed (llimview.cpp:2551-2570)
        ///         and what the live path already re-derives (A2ASessionRegistry.ComputeSessionId).
        ///   GROUP the group id -- the viewer slams it (llimview.cpp:2535-2538).
        ///   ADHOC server-owned and RANDOM, because the viewer's id is itself random
        ///         (llimview.cpp:2542) and means nothing to anyone else. This is the only type that
        ///         needs the re-key, and giving the server the id is the point: it is what lets a
        ///         conference be addressed grid-wide by something other than one viewer's guess.
        /// </summary>
        public static UUID DeriveSessionId(NonSpatialSessionType type, UUID creator, UUID otherOrOwner)
        {
            switch (type)
            {
                case NonSpatialSessionType.P2P:
                    return new UUID(creator.ulonga ^ otherOrOwner.ulonga, creator.ulongb ^ otherOrOwner.ulongb);
                case NonSpatialSessionType.Group:
                    return otherOrOwner;
                case NonSpatialSessionType.Adhoc:
                    return UUID.Random();
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "unknown non-spatial session type");
            }
        }

        // ---- start ---------------------------------------------------------------------------

        /// <summary>
        /// Open (or re-find) a P2P or GROUP session. Idempotent by derivation: the id is a function
        /// of the parties, so a repeat -- including the other party starting it simultaneously --
        /// lands on the same record through StartOrGet.
        /// </summary>
        public SessionOutcome Start(NonSpatialSessionType type, UUID creator, UUID otherOrOwner,
                                    UUID tempSessionId, UUID originRegion, int requestedCap = 0)
        {
            if (type == NonSpatialSessionType.Adhoc)
                throw new ArgumentException("use StartAdhoc: an ad-hoc id is server-owned and keyed on the viewer's temp id", nameof(type));

            List<UUID> invitees = type == NonSpatialSessionType.P2P ? new List<UUID> { otherOrOwner } : new List<UUID>();
            AdmissionVerdict v = Admission(type).CanStart(creator, type == NonSpatialSessionType.Group ? otherOrOwner : UUID.Zero, invitees);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision);

            UUID sessionId = DeriveSessionId(type, creator, otherOrOwner);
            return StartWithId(type, sessionId, tempSessionId == UUID.Zero ? sessionId : tempSessionId,
                               creator, type == NonSpatialSessionType.Group ? otherOrOwner : UUID.Zero,
                               originRegion, requestedCap,
                               type == NonSpatialSessionType.P2P ? new List<UUID> { otherOrOwner } : null);
        }

        /// <summary>
        /// Open (or re-find) an ADHOC conference. The authoritative id is server-owned, so
        /// idempotency cannot come from derivation -- it comes from the (creator, tempSessionId)
        /// pair the viewer retries with. A viewer that re-POSTs "start conference" after a timeout
        /// sends the SAME temp id (it is minted once, at session creation), so the second call finds
        /// the first session instead of opening a second room and a second start reply.
        /// </summary>
        public SessionOutcome StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion,
                                         IReadOnlyList<UUID> invitees = null, int requestedCap = 0)
        {
            AdmissionVerdict v = Admission(NonSpatialSessionType.Adhoc).CanStart(creator, UUID.Zero, invitees ?? new List<UUID>());
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision);

            NonSpatialVoiceSession prior = FindAdhocByTemp(creator, tempSessionId);
            if (prior != null)
            {
                Touch(prior.SessionId);
                return new SessionOutcome(true, "start-idempotent", prior, created: false);
            }
            return StartWithId(NonSpatialSessionType.Adhoc, UUID.Random(), tempSessionId, creator, UUID.Zero,
                               originRegion, requestedCap, invitees);
        }

        private SessionOutcome StartWithId(NonSpatialSessionType type, UUID sessionId, UUID tempSessionId,
                                           UUID creator, UUID owner, UUID originRegion, int requestedCap,
                                           IReadOnlyList<UUID> invitees)
        {
            DateTime now = _clock();
            int cap = NonSpatialCaps.Effective(type, requestedCap);
            string roomKey = NonSpatialRoomKey.Derive(_gridId, type, sessionId);

            NonSpatialVoiceSession s = _store.StartOrGet(sessionId, () =>
            {
                NonSpatialVoiceSession made = new NonSpatialVoiceSession(type, sessionId, tempSessionId, roomKey,
                                                                         owner, creator, cap, now);
                // the creator takes its seat at start; it is the one party that never needs inviting
                made.Upsert(creator, MemberState.Accepted, originRegion, now);
                if (invitees != null)
                    foreach (UUID a in invitees)
                        if (a != UUID.Zero && a != creator)
                            made.Upsert(a, MemberState.Invited, UUID.Zero, now);
                return made;
            }, out bool created);

            if (!created)
            {
                // A repeat start refreshes and re-seats the ORIGINAL creator (it may have departed
                // and come back) but never duplicates the session, the members or the room.
                //
                // P1.2G found the sharp edge here: for GROUP, every member calls Start, not just the
                // opener, so seating `creator` unconditionally would hand a seat to an arbitrary
                // caller WITHOUT the cap check that lives in Accept -- the 51st member would walk
                // straight in. Two guards: only the session's own creator is re-seated here, and
                // only when there is room. Everyone else takes their seat through Accept, which is
                // where admission and the cap are decided.
                _store.Mutate<object>(sessionId, sess =>
                {
                    sess.LastSeenUtc = now;
                    NonSpatialMember me = sess.Find(creator);
                    if (me == null || me.State == MemberState.Departed)
                    {
                        if (sess.Creator == creator && !sess.IsFull)
                            sess.Upsert(creator, MemberState.Accepted, originRegion, now);
                    }
                    else
                        me.LastSeenUtc = now;
                    if (invitees != null)
                        foreach (UUID a in invitees)
                            if (a != UUID.Zero && a != creator && sess.Find(a) == null)
                                sess.Upsert(a, MemberState.Invited, UUID.Zero, now);
                    return null;
                });
            }
            // The creator took the first seat inside the factory (or was re-seated above), so THIS
            // is where a group call begins for the agent that opens it.
            bool ring = ClaimRing(sessionId);
            return new SessionOutcome(true, created ? "started" : "start-idempotent", s, created, ring);
        }

        // ---- invite / accept / decline ---------------------------------------------------------

        /// <summary>
        /// Invite an agent. Idempotent: a repeated invite of the same agent refreshes the existing
        /// invitation rather than adding a second membership, and an invite of someone who already
        /// holds a seat is a no-op rather than a demotion back to Invited.
        /// </summary>
        public SessionOutcome Invite(UUID sessionId, UUID inviter, UUID invitee)
        {
            DateTime now = _clock();
            NonSpatialVoiceSession s = _store.Get(sessionId);
            if (s == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);

            AdmissionVerdict v = Admission(s.Type).CanInvite(s, inviter, invitee);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision, s);

            // An invitation reserves no seat, but inviting INTO a room that is already full is a
            // refusal now rather than a disappointment later.
            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember existing = sess.Find(invitee);
                if (existing != null && existing.HoldsSeat)
                {
                    existing.LastSeenUtc = now;
                    sess.LastSeenUtc = now;
                    return new SessionOutcome(true, "invite-idempotent-seated", sess);
                }
                if (sess.IsFull)
                    return SessionOutcome.Fail(SessionOutcome.Capacity, sess);
                bool repeat = existing != null;
                sess.Upsert(invitee, MemberState.Invited, existing?.OriginRegion ?? UUID.Zero, now);
                sess.LastSeenUtc = now;
                return new SessionOutcome(true, repeat ? "invite-idempotent" : "invited", sess);
            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        /// <summary>
        /// Take a seat. This is the cap's decision point and it is made INSIDE the store mutation,
        /// so N simultaneous accepts at the boundary produce exactly Cap seats and the rest a
        /// capacity refusal -- never Cap+1 (item 6).
        /// </summary>
        public SessionOutcome Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null)
        {
            DateTime now = _clock();
            NonSpatialVoiceSession snapshot = _store.Get(sessionId);
            if (snapshot == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);

            AdmissionVerdict v = Admission(snapshot.Type).CanJoin(snapshot, agent);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision, snapshot);

            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m != null && m.HoldsSeat)
                {
                    // Repeat accept, reconnect, or the same agent arriving from a new region: refresh
                    // in place. The region is re-pointed, NOT duplicated (O-110).
                    m.OriginRegion = originRegion;
                    m.LastSeenUtc = now;
                    if (!string.IsNullOrEmpty(viewerSession)) m.ViewerSession = viewerSession;
                    sess.LastSeenUtc = now;
                    return new SessionOutcome(true, "accept-idempotent", sess);
                }
                if (sess.IsFull)
                    return SessionOutcome.Fail(SessionOutcome.Capacity, sess);
                bool wasEmpty = sess.SeatsHeld == 0;
                NonSpatialMember seated = sess.Upsert(agent, MemberState.Accepted, originRegion, now);
                if (!string.IsNullOrEmpty(viewerSession)) seated.ViewerSession = viewerSession;
                sess.LastSeenUtc = now;
                // P1.2G-b: 0 -> 1 is the moment the call begins. Claimed INSIDE the mutation and
                // latched by RingSent, so two simultaneous first-joiners cannot both ring the group.
                bool ring = wasEmpty && sess.SeatsHeld == 1 && !sess.RingSent;
                if (ring) sess.RingSent = true;
                return new SessionOutcome(true, "accepted", sess, created: false, startedRinging: ring);

            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        /// <summary>The member is in the media room (its provision was admitted). Accepted -> Present.</summary>
        public SessionOutcome MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null)
        {
            DateTime now = _clock();
            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m == null || m.State == MemberState.Departed || m.State == MemberState.Declined)
                    return SessionOutcome.Fail(SessionOutcome.AlreadyDeparted, sess);
                if (!m.HoldsSeat && sess.IsFull)
                    return SessionOutcome.Fail(SessionOutcome.Capacity, sess);
                m.State = MemberState.Present;
                m.OriginRegion = originRegion;
                m.LastSeenUtc = now;
                if (!string.IsNullOrEmpty(viewerSession)) m.ViewerSession = viewerSession;
                sess.LastSeenUtc = now;
                return new SessionOutcome(true, "present", sess);
            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        /// <summary>
        /// Decline an invitation -- O-108 lifecycle (2). Separate from departing a session the agent
        /// had joined: declining never touches a seat, because an invitation never held one.
        /// </summary>
        public SessionOutcome Decline(UUID sessionId, UUID agent)
            => DepartInternal(sessionId, agent, DepartureReason.InvitationDeclined, MemberState.Declined);

        /// <summary>
        /// Leave, from any of the lifecycles. The engine does not care which one fired and never
        /// waits for the others: whichever arrives first releases the seat.
        /// </summary>
        public SessionOutcome Depart(UUID sessionId, UUID agent, DepartureReason reason)
            => DepartInternal(sessionId, agent, reason, MemberState.Departed);

        private SessionOutcome DepartInternal(UUID sessionId, UUID agent, DepartureReason reason, MemberState terminal)
        {
            DateTime now = _clock();
            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession, sess);
                if (m.State == terminal)
                {
                    sess.LastSeenUtc = now;
                    return new SessionOutcome(true, "depart-idempotent", sess);
                }
                m.State = terminal;
                m.Departure = reason;
                m.LastSeenUtc = now;
                m.ViewerSession = null;
                sess.LastSeenUtc = now;
                // P1.2G-b: the room emptied, so the ring cycle is over. Clearing it is what makes a
                // LATER start ring the group again; without it a group is rung once per process.
                if (sess.SeatsHeld == 0) sess.EndRingCycle();
                // P1.4b: a DECLINE also forgets that this agent was rung, so a later invite can ring
                // them again. A departure does not: leaving a call you were in is not an invitation
                // to be re-rung by every subsequent joiner.
                else if (terminal == MemberState.Declined) sess.ClearInvited(agent);
                return new SessionOutcome(true, terminal == MemberState.Declined ? "declined" : "departed", sess);
            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        /// <summary>
        /// Release the seat the agent holds UNDER THIS VIEWER SESSION, and only that one. This is the
        /// voice-teardown lifecycle's entry point: the viewer's logout provision carries
        /// {logout, viewer_session} and no channel, so the viewer session is the only thing that
        /// identifies which seat to free.
        ///
        /// P1.2G found this missing: the logout arm released A2A records and nothing else, so a group
        /// member who hung up kept their seat until the idle TTL -- and with the 50 cap load-bearing,
        /// enough hang-ups would have locked a group out of its own room for eight hours.
        ///
        /// A null or empty viewer session matches NOTHING here on purpose. Departing every session an
        /// agent holds is <see cref="DepartAll"/>, which is the presence-close lifecycle, not this one.
        /// </summary>
        public IReadOnlyList<NonSpatialVoiceSession> DepartByViewerSession(UUID agent, string viewerSession, DepartureReason reason)
        {
            List<NonSpatialVoiceSession> touched = new List<NonSpatialVoiceSession>();
            if (string.IsNullOrEmpty(viewerSession))
                return touched;
            foreach (NonSpatialVoiceSession s in _store.All())
            {
                NonSpatialMember m = s.Find(agent);
                if (m == null || !m.HoldsSeat || !string.Equals(m.ViewerSession, viewerSession, StringComparison.Ordinal))
                    continue;
                if (Depart(s.SessionId, agent, reason).Ok)
                    touched.Add(s);
            }
            return touched;
        }

        /// <summary>
        /// The agent left every session it was in, for one reason -- the shape a presence close or a
        /// grid-wide logout needs. Returns the sessions actually touched.
        /// </summary>
        public IReadOnlyList<NonSpatialVoiceSession> DepartAll(UUID agent, DepartureReason reason)
        {
            List<NonSpatialVoiceSession> touched = new List<NonSpatialVoiceSession>();
            foreach (NonSpatialVoiceSession s in _store.All())
            {
                NonSpatialMember m = s.Find(agent);
                if (m == null || m.State == MemberState.Departed || m.State == MemberState.Declined) continue;
                SessionOutcome o = Depart(s.SessionId, agent, reason);
                if (o.Ok) touched.Add(s);
            }
            return touched;
        }

        /// <summary>
        /// P1.2G-c: adopt a group session this instance learned about from an INCOMING RING rather
        /// than from a local start. Needed because the accept that follows a remote ring lands HERE,
        /// and TryHandleAcceptInvitation can only admit into a session this store knows.
        ///
        /// The room key is DERIVED locally, not taken from the wire, and compared against the one the
        /// ring carried. They must agree: the derivation is (gridId, tag, sessionId) and gridId is the
        /// shared GatekeeperURI, so a mismatch means the two instances disagree about the grid id --
        /// a misconfiguration that would silently put the two halves of a call in different mixer
        /// rooms. Better to refuse loudly here than to be inaudible in production.
        ///
        /// Born with RingSent LATCHED and the invited set pre-seeded, which is what makes send-once
        /// hold across instances (P1.2G-c item 2): this instance must never re-ring a call it did not
        /// start. It carries NO seats -- the ringing instance owns those, and this instance's seat
        /// count is its own slice only (the stated per-instance cap limitation).
        /// </summary>
        public SessionOutcome AdoptRemoteRing(UUID groupId, string carriedRoomKey, string token, int cap,
                                              UUID creator, IEnumerable<UUID> alreadyRung,
                                              NonSpatialSessionType type = NonSpatialSessionType.Group)
        {
            if (groupId == UUID.Zero)
                return SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            string local = NonSpatialRoomKey.Derive(_gridId, type, groupId);
            if (!string.IsNullOrEmpty(carriedRoomKey) && !string.Equals(local, carriedRoomKey, StringComparison.Ordinal))
                return SessionOutcome.Fail("refused-room-key-mismatch");

            DateTime now = _clock();
            int effective = NonSpatialCaps.Effective(type, cap);
            // Owner is the group for a group session; an ad-hoc conference has no owning object.
            UUID owner = type == NonSpatialSessionType.Group ? groupId : UUID.Zero;
            NonSpatialVoiceSession s = _store.StartOrGet(groupId, () =>
            {
                NonSpatialVoiceSession made = new NonSpatialVoiceSession(type, groupId, groupId,
                                                                          local, owner, creator, effective, now);
                made.Token = token;                 // the ring carries the credential the viewer will echo
                made.RingSent = true;               // never re-ring a call this instance did not start
                if (alreadyRung != null)
                    foreach (UUID a2 in alreadyRung) made.MarkInvited(a2);
                SeedAdoptedMembership(made, type, alreadyRung, now);
                return made;
            }, out bool created);

            if (!created)
            {
                _store.Mutate<object>(groupId, sess =>
                {
                    sess.LastSeenUtc = now;
                    if (string.IsNullOrEmpty(sess.Token) && !string.IsNullOrEmpty(token)) sess.Token = token;
                    sess.RingSent = true;
                    if (alreadyRung != null)
                        foreach (UUID a2 in alreadyRung) sess.MarkInvited(a2);
                    SeedAdoptedMembership(sess, type, alreadyRung, now);
                    return null;
                });
            }
            return new SessionOutcome(true, created ? "ring-adopted" : "ring-adopt-idempotent", s, created);
        }

        /// <summary>
        /// P1.4b: an ADOPTED ad-hoc session must carry the membership, because AdhocAdmission.CanJoin
        /// admits on the member record alone -- an ad-hoc conference has no roster to fall back on.
        /// Without this, a member rung across instances would be refused at the accept that lands
        /// here, which is exactly the cross-instance case this slice exists to make work.
        ///
        /// Group is deliberately left alone: its CanJoin re-checks membership and powers against the
        /// groups service, so records here would buy nothing and would change what
        /// GroupVoiceInvite.Targets sees on the adopting instance.
        /// </summary>
        private static void SeedAdoptedMembership(NonSpatialVoiceSession sess, NonSpatialSessionType type,
                                                  IEnumerable<UUID> rung, DateTime now)
        {
            if (type != NonSpatialSessionType.Adhoc || rung == null) return;
            foreach (UUID a in rung)
            {
                if (a == UUID.Zero) continue;
                if (sess.Find(a) == null) sess.Upsert(a, MemberState.Invited, UUID.Zero, now);
            }
        }

        // ---- credentials -----------------------------------------------------------------------

        /// <summary>Token size in bytes, rendered as lowercase hex. Matches A2ASessionRegistry.TokenBytes.</summary>
        public const int TokenBytes = 32;

        /// <summary>
        /// The session's credential, minted on first call and stable afterwards so a retried "call"
        /// does not invalidate credentials already in a viewer's hands. Returns null when the agent
        /// is not a member of the session, so a token can never be handed to a stranger.
        /// </summary>
        public string IssueToken(UUID sessionId, UUID agent)
        {
            DateTime now = _clock();
            return _store.Mutate<string>(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m == null || m.State == MemberState.Departed || m.State == MemberState.Declined)
                    return null;
                if (string.IsNullOrEmpty(sess.Token))
                    sess.Token = NewToken();
                sess.LastSeenUtc = now;
                return sess.Token;
            });
        }

        /// <summary>
        /// Constant-time-ish comparison of a presented credential against the session's. False for a
        /// session with no token yet, so a provision can never arrive before its "call".
        /// </summary>
        public static bool TokenMatches(NonSpatialVoiceSession session, string presented)
        {
            string held = session?.Token;
            if (string.IsNullOrEmpty(held) || string.IsNullOrEmpty(presented) || held.Length != presented.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < held.Length; i++) diff |= held[i] ^ presented[i];
            return diff == 0;
        }

        private static string NewToken()
        {
            byte[] raw = new byte[TokenBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(raw);
            return Convert.ToHexString(raw).ToLowerInvariant();
        }

        // ---- sweep ---------------------------------------------------------------------------

        /// <summary>
        /// Expire unanswered invitations and collect dead sessions. This is the backstop that makes
        /// "departure never waits on a POST that does not come" true even when NO lifecycle fires --
        /// a viewer that is simply gone leaves an invitation that ages out and a session that dies.
        /// Returns the ids removed.
        /// </summary>
        public IReadOnlyList<UUID> Sweep()
        {
            DateTime now = _clock();
            List<UUID> removed = new List<UUID>();
            foreach (NonSpatialVoiceSession s in _store.All())
            {
                _store.Mutate<object>(s.SessionId, sess =>
                {
                    foreach (NonSpatialMember m in sess.Members)
                        if (m.State == MemberState.Invited && now - m.InvitedUtc >= _inviteTtl)
                        {
                            m.State = MemberState.Departed;
                            m.Departure = DepartureReason.Expired;
                            m.LastSeenUtc = now;
                        }
                    return null;
                });
                NonSpatialVoiceSession after = _store.Get(s.SessionId);
                if (after == null) continue;
                if (after.IsDead || now - after.LastSeenUtc >= _idleTtl)
                {
                    if (_store.Remove(after.SessionId)) removed.Add(after.SessionId);
                }
            }
            return removed;
        }

        // ---- helpers -------------------------------------------------------------------------

        /// <summary>
        /// Record that these agents have been rung for the current ring cycle, so a later joiner's
        /// fan-out skips them (GroupVoiceInvite.Targets). Called after delivery whatever the outcome:
        /// a member we could not reach is then not re-rung on every subsequent join, which would be
        /// worse than one missed ring.
        /// </summary>
        public void MarkInvited(UUID sessionId, IEnumerable<UUID> agents)
        {
            if (agents == null) return;
            _store.Mutate<object>(sessionId, sess =>
            {
                foreach (UUID a in agents) sess.MarkInvited(a);
                return null;
            });
        }

        private INonSpatialAdmission Admission(NonSpatialSessionType type)
            => _admission.TryGetValue(type, out INonSpatialAdmission a)
                ? a
                : throw new InvalidOperationException("no admission policy registered for " + type);

        /// <summary>
        /// Claim the ring for this session if it is the first seat of a cycle. Atomic under the
        /// store, and latched by RingSent so exactly one caller ever claims it per cycle.
        ///
        /// P1.2G-b found this the hard way: the 0 -> 1 transition does NOT happen in Accept for the
        /// agent that opens the room, because Start seats the creator itself. Claiming only in
        /// Accept meant the opener never rang the group -- which is the one case that always matters.
        /// </summary>
        private bool ClaimRing(UUID sessionId)
            => _store.Mutate(sessionId, sess =>
            {
                if (sess.SeatsHeld != 1 || sess.RingSent) return false;
                sess.RingSent = true;
                return true;
            }, false);

        private void Touch(UUID sessionId)
        {
            DateTime now = _clock();
            _store.Mutate<object>(sessionId, s => { s.LastSeenUtc = now; return null; });
        }

        private NonSpatialVoiceSession FindAdhocByTemp(UUID creator, UUID tempSessionId)
        {
            if (tempSessionId == UUID.Zero) return null;
            foreach (NonSpatialVoiceSession s in _store.All())
                if (s.Type == NonSpatialSessionType.Adhoc && s.TempSessionId == tempSessionId && s.Creator == creator)
                    return s;
            return null;
        }
    }
}
