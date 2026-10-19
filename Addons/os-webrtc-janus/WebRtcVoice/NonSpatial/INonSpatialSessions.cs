/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The operations the region module needs from the non-spatial session engine, so the engine can run
 * in the region process (NonSpatialVoiceSessionEngine over an in-memory store) or behind the grid
 * session service (GridNonSpatialSessions). Sessions returned by Get, GetByRoomKey and the outcomes are
 * for reading: the in-process engine hands back its live objects, the grid client hands back copies.
 */
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public interface INonSpatialSessions
    {
        /// <summary>
        /// The session, for reading. <paramref name="agent"/> is who is asking: through the grid service the
        /// call's credential is in the copy only when that agent is a member of the session or was invited
        /// to it. The in-process engine holds the credential itself and ignores it.
        /// </summary>
        NonSpatialVoiceSession Get(UUID sessionId, UUID agent = default);

        NonSpatialVoiceSession GetByRoomKey(string roomKey, UUID agent = default);

        SessionOutcome Start(NonSpatialSessionType type, UUID creator, UUID otherOrOwner,
                             UUID tempSessionId, UUID originRegion, int requestedCap = 0);

        SessionOutcome StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion,
                                  IReadOnlyList<UUID> invitees = null, int requestedCap = 0);

        SessionOutcome CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyCollection<UUID> asked);

        SessionOutcome Invite(UUID sessionId, UUID inviter, UUID invitee);

        SessionOutcome Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null);

        SessionOutcome MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null);

        SessionOutcome Decline(UUID sessionId, UUID agent);

        SessionOutcome Depart(UUID sessionId, UUID agent, DepartureReason reason);

        SessionOutcome LeaveChat(UUID sessionId, UUID agent);

        IReadOnlyList<NonSpatialVoiceSession> DepartByViewerSession(UUID agent, string viewerSession, DepartureReason reason);

        IReadOnlyList<NonSpatialVoiceSession> DepartAll(UUID agent, DepartureReason reason);

        SessionOutcome AdoptRemoteRing(UUID groupId, string carriedRoomKey, string token, int cap,
                                       UUID creator, IEnumerable<UUID> alreadyRung,
                                       NonSpatialSessionType type = NonSpatialSessionType.Group);

        string IssueToken(UUID sessionId, UUID agent);

        void MarkInvited(UUID sessionId, IEnumerable<UUID> agents);

        bool TryTakeConferenceRequest(UUID agent);
    }
}
