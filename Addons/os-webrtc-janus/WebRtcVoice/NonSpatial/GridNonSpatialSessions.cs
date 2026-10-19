/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The region's side of the grid call session store: the same operations as the in-process engine
 * (INonSpatialSessions), answered by the grid session service.
 *
 * Admission stays here, because the group checks need the groups service, which only a region has.
 * The region decides whether to ask; the service decides the cap, the ring latch, idempotency and
 * state. When the service does not answer, a request is REFUSED (refused-store-unavailable). There is
 * no fallback to a region-local store: a region that started its own record of a call would ring the
 * group a second time and count the seat cap on its own, which is what the grid store exists to stop.
 *
 * Sessions returned are copies. Reading them is what the region does; changes go through the operations.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class GridNonSpatialSessions : INonSpatialSessions
    {
        private readonly IVoiceSessionService _service;
        private readonly Dictionary<NonSpatialSessionType, INonSpatialAdmission> _admission = new Dictionary<NonSpatialSessionType, INonSpatialAdmission>();
        private readonly RefusalLogGate _unavailableGate = new RefusalLogGate();
        private readonly Func<DateTime> _clock;

        /// <summary>Raised at most once a minute when the service did not answer. Carries the operation name.</summary>
        public event Action<string> Unavailable;

        public GridNonSpatialSessions(IVoiceSessionService service, IEnumerable<INonSpatialAdmission> admissions, Func<DateTime> clock = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _clock = clock ?? (() => DateTime.UtcNow);
            if (admissions != null)
                foreach (INonSpatialAdmission a in admissions)
                    if (a != null) _admission[a.Type] = a;
        }

        public NonSpatialVoiceSession Get(UUID sessionId, UUID agent = default)
        {
            SessionReply r = Reached(_service.Get(sessionId, agent), "get");
            return r != null && r.Ok ? SessionWire.FromData(r.Session) : null;
        }

        public NonSpatialVoiceSession GetByRoomKey(string roomKey, UUID agent = default)
        {
            SessionReply r = Reached(_service.GetByRoomKey(roomKey, agent), "getbyroomkey");
            return r != null && r.Ok ? SessionWire.FromData(r.Session) : null;
        }

        public SessionOutcome Start(NonSpatialSessionType type, UUID creator, UUID otherOrOwner,
                                    UUID tempSessionId, UUID originRegion, int requestedCap = 0)
        {
            if (type == NonSpatialSessionType.Adhoc)
                throw new ArgumentException("use StartAdhoc: an ad-hoc id is server-owned and keyed on the viewer's temp id", nameof(type));
            List<UUID> invitees = type == NonSpatialSessionType.P2P ? new List<UUID> { otherOrOwner } : new List<UUID>();
            AdmissionVerdict v = Admission(type).CanStart(creator, type == NonSpatialSessionType.Group ? otherOrOwner : UUID.Zero, invitees);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision);
            return Outcome(_service.Start((int)type, creator, otherOrOwner, tempSessionId, originRegion, requestedCap), "start");
        }

        public SessionOutcome StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion,
                                         IReadOnlyList<UUID> invitees = null, int requestedCap = 0)
        {
            AdmissionVerdict v = Admission(NonSpatialSessionType.Adhoc).CanStart(creator, UUID.Zero, invitees ?? new List<UUID>());
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision);
            return Outcome(_service.StartAdhoc(creator, tempSessionId, originRegion, invitees ?? new List<UUID>(), requestedCap), "startadhoc");
        }

        public SessionOutcome CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyCollection<UUID> asked)
            => Outcome(_service.CheckInviteCount(sessionId, inviter, new List<UUID>(asked ?? new List<UUID>())), "checkinvitecount");

        public SessionOutcome Invite(UUID sessionId, UUID inviter, UUID invitee)
        {
            SessionReply found = Reached(_service.Get(sessionId), "invite");
            if (found == null) return SessionOutcome.Fail(SessionOutcome.StoreUnavailable);
            if (!found.Ok) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            NonSpatialVoiceSession snapshot = SessionWire.FromData(found.Session);
            AdmissionVerdict v = Admission(snapshot.Type).CanInvite(snapshot, inviter, invitee);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision, snapshot);
            return Outcome(_service.Invite(sessionId, inviter, invitee), "invite");
        }

        public SessionOutcome Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null)
        {
            SessionReply found = Reached(_service.Get(sessionId), "accept");
            if (found == null) return SessionOutcome.Fail(SessionOutcome.StoreUnavailable);
            if (!found.Ok) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            NonSpatialVoiceSession snapshot = SessionWire.FromData(found.Session);
            AdmissionVerdict v = Admission(snapshot.Type).CanJoin(snapshot, agent);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision, snapshot);
            return Outcome(_service.Accept(sessionId, agent, originRegion, viewerSession), "accept");
        }

        public SessionOutcome MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null)
            => Outcome(_service.MarkPresent(sessionId, agent, originRegion, viewerSession), "markpresent");

        public SessionOutcome Decline(UUID sessionId, UUID agent)
            => Outcome(_service.Decline(sessionId, agent), "decline");

        public SessionOutcome Depart(UUID sessionId, UUID agent, DepartureReason reason)
            => Outcome(_service.Depart(sessionId, agent, (int)reason), "depart");

        public SessionOutcome LeaveChat(UUID sessionId, UUID agent)
            => Outcome(_service.LeaveChat(sessionId, agent), "leavechat");

        public IReadOnlyList<NonSpatialVoiceSession> DepartByViewerSession(UUID agent, string viewerSession, DepartureReason reason)
        {
            if (string.IsNullOrEmpty(viewerSession)) return new List<NonSpatialVoiceSession>();
            return SessionWire.ToSessions(Reached(_service.DepartByViewerSession(agent, viewerSession, (int)reason), "departbyviewer"));
        }

        public IReadOnlyList<NonSpatialVoiceSession> DepartAll(UUID agent, DepartureReason reason)
            => SessionWire.ToSessions(Reached(_service.DepartAll(agent, (int)reason), "departall"));

        /// <summary>
        /// There is nothing to adopt from a ring: every region sees the one record in the service. A ring for a
        /// session the service holds is accepted as already held; a ring for one it does not is refused. State is
        /// never created or changed from what a ring carries.
        /// </summary>
        public SessionOutcome AdoptRemoteRing(UUID groupId, string carriedRoomKey, string token, int cap,
                                              UUID creator, IEnumerable<UUID> alreadyRung,
                                              NonSpatialSessionType type = NonSpatialSessionType.Group)
        {
            SessionReply found = Reached(_service.Get(groupId), "adoptring");
            if (found == null) return SessionOutcome.Fail(SessionOutcome.StoreUnavailable);
            if (!found.Ok || found.Session == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            NonSpatialVoiceSession s = SessionWire.FromData(found.Session);
            if (s == null || s.Type != type) return SessionOutcome.Fail("refused-session-type-mismatch");
            if (!string.Equals(s.RoomKey, carriedRoomKey, StringComparison.Ordinal))
                return SessionOutcome.Fail(string.IsNullOrEmpty(carriedRoomKey) ? "refused-room-key-missing" : "refused-room-key-mismatch");
            return new SessionOutcome(true, "ring-session-held", s);
        }

        public string IssueToken(UUID sessionId, UUID agent)
        {
            SessionReply r = Reached(_service.IssueToken(sessionId, agent), "issuetoken");
            return r != null && r.Ok ? r.Text : null;
        }

        public void MarkInvited(UUID sessionId, IEnumerable<UUID> agents)
        {
            if (agents == null) return;
            Reached(_service.MarkInvited(sessionId, new List<UUID>(agents)), "markinvited");
        }

        /// <summary>False when the agent's bucket is empty, and also when the service did not answer.</summary>
        public bool TryTakeConferenceRequest(UUID agent)
        {
            SessionReply r = Reached(_service.TakeConferenceRequest(agent), "takeconferencerequest");
            return r != null && r.Flag;
        }

        // ---- lifecycle of the region itself -------------------------------------------------------

        /// <summary>
        /// A region process has started: release the seats that name it, which belong to viewers the restart
        /// disconnected. Returns the seats released, or -1 when the service did not answer.
        /// </summary>
        public int DepartRegion(UUID region)
        {
            SessionReply r = Reached(_service.DepartRegion(region), "departregion");
            return r != null && r.Ok ? r.Count : -1;
        }

        /// <summary>Tell the service these agents are still here, so their seats are kept. False when it did not answer.</summary>
        public bool RenewSeats(IReadOnlyList<UUID> agents)
        {
            SessionReply r = Reached(_service.RenewSeats(agents), "renewseats");
            return r != null && r.Ok;
        }

        // ---- helpers -----------------------------------------------------------------------------

        private INonSpatialAdmission Admission(NonSpatialSessionType type)
            => _admission.TryGetValue(type, out INonSpatialAdmission a)
                ? a
                : throw new InvalidOperationException("no admission policy registered for " + type);

        private SessionOutcome Outcome(SessionReply reply, string operation)
            => SessionWire.ToOutcome(Reached(reply, operation));

        private SessionReply Reached(SessionReply reply, string operation)
        {
            if (reply == null && _unavailableGate.ShouldLog(UUID.Zero, _clock()))
                Unavailable?.Invoke(operation);
            return reply;
        }
    }
}
