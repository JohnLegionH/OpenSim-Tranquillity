/*
 * P1.2G item 4: the "call" handler for a GROUP session.
 *
 * This is the EXISTING call shape, reused, not new machinery. The viewer's group voice channel does
 * exactly what the P2P one does:
 *     LLVoiceChannelGroup::activate -> requestChannelInfo (llvoicechannel.cpp:492-502)
 *       -> voiceCallCapCoro posts {method:"call", session-id, alt_params} (:631)
 *       -> setChannelInfo(result["voice_credentials"]) (:687)
 * and for a group `session-id` IS the group id, because computeSessionID slams it
 * (llimview.cpp:2535-2538). So the only difference from A2A is which authority answers: the group
 * service rather than the pair registry.
 *
 * WHY THIS IS A WRAPPER AND NOT AN EDIT TO ChatSessionRequestLogic. The live A2A path has to stay
 * provably unchanged this slice (P1.6 migrates it). So the module tries this handler FIRST, and it
 * declines -- returning false, touching nothing -- unless every one of these holds:
 *     group voice is enabled, the method is "call", and the session-id names a group this agent is
 *     a member of.
 * On a decline the module calls ChatSessionRequestLogic.Decide exactly as before, byte for byte.
 * A non-member's "call" therefore still reaches the A2A arm and still 404s there, which is the
 * pre-slice behaviour for an unknown session and is what the viewer already knows how to survive.
 *
 * WHAT IT HANDS BACK is the same map the A2A arm builds (ChatSessionRequestLogic.cs:228-235), with
 * one deliberate difference: channel_uri is NonSpatialRoomKey.Derive(grid, Group, groupId), NOT the
 * session id. The viewer treats it as opaque (P1-VERIFY), and the mixer hashes it as the multiagent
 * channel, so the group's room is distinct from every A2A room and from every parcel room without
 * any change to JanusAudioBridge.
 */
using System;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    public static class GroupVoiceChatSession
    {
        public const string MethodCall = "call";

        public const string DecisionCallAdmitted = "group-call-admitted";
        public const string DecisionNotMember = "group-refused-not-a-member";
        public const string DecisionNoPower = "group-refused-no-power";
        public const string DecisionFull = "group-refused-capacity";
        public const string InstrumentTag = "[GROUP VOICE]";

        /// <summary>
        /// Try to answer a ChatSessionRequest as a group voice call. Returns false -- having done
        /// NOTHING -- when this is not one, so the caller falls through to the A2A path unchanged.
        /// </summary>
        public static bool TryHandleCall(OSDMap reqmap, UUID agentID, GroupVoicePolicy policy,
                                         NonSpatialVoiceSessionEngine engine, UUID originRegion,
                                         out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (policy == null || !policy.IsUsable || engine == null || reqmap == null)
                return false;
            if (!reqmap.TryGetString("method", out string method) || !string.Equals(method, MethodCall, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!reqmap.TryGetUUID("session-id", out UUID groupID) || groupID == UUID.Zero)
                return false;
            // The discriminator: a group session-id is a group this agent belongs to. Anything else
            // is not ours and must fall through untouched.
            if (!policy.IsMember(agentID, groupID))
                return false;

            outcome = Call(agentID, groupID, policy, engine, originRegion);
            return true;
        }

        private static ChatSessionOutcome Call(UUID agentID, UUID groupID, GroupVoicePolicy policy,
                                               NonSpatialVoiceSessionEngine engine, UUID originRegion)
        {
            // The power gate. A member without it is a 403, which the viewer shows as VoiceNotAllowed
            // (llvoicechannel.cpp:658-663) -- the correct, survivable answer for "you may not".
            if (!policy.HasRequiredPowers(agentID, groupID))
                return Refuse(HttpStatusCode.Forbidden, agentID, groupID, DecisionNoPower, "missing GP_SESSION_JOIN/GP_SESSION_VOICE");

            // Start is idempotent by derivation (the session id IS the group id), so the first caller
            // opens the room and every later one finds it.
            SessionOutcome start = engine.Start(NonSpatialSessionType.Group, agentID, groupID, groupID, originRegion, policy.Cap);
            if (!start.Ok)
                return Refuse(HttpStatusCode.Forbidden, agentID, groupID, "group-refused-" + start.Decision, start.Decision);

            // Take the seat HERE, at "call", not at provision: with no invitation step this is the
            // first moment the grid knows this agent intends to be in the room, and refusing here
            // means the viewer never builds a PeerConnection it is going to be denied.
            SessionOutcome seat = engine.Accept(start.Session.SessionId, agentID, originRegion);
            if (!seat.Ok)
            {
                // Capacity is the one refusal with a required status: 409 Conflict, which the viewer
                // maps to ERROR_CHANNEL_FULL, the same thing the mixer's own 495 produces.
                HttpStatusCode code = seat.Decision == SessionOutcome.Capacity
                    ? (HttpStatusCode)NonSpatialCaps.CapacityHttpStatus
                    : HttpStatusCode.Forbidden;
                string word = seat.Decision == SessionOutcome.Capacity ? DecisionFull : "group-refused-" + seat.Decision;
                return Refuse(code, agentID, groupID, word, seat.Decision);
            }

            string token = engine.IssueToken(start.Session.SessionId, agentID);
            OSDMap creds = new OSDMap
            {
                ["voice_server_type"] = OSD.FromString(A2AInvitation.VoiceServerType),
                ["channel_uri"] = OSD.FromString(start.Session.RoomKey),
                ["channel_credentials"] = OSD.FromString(token),
            };
            return new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                Body = new OSDMap { ["voice_credentials"] = creds },
                Instrument = Line(agentID, groupID, DecisionCallAdmitted,
                                  $"room={start.Session.RoomKey} seats={engine.Store.Get(start.Session.SessionId)?.SeatsHeld}/{start.Session.Cap}"),
            };
        }

        private static ChatSessionOutcome Refuse(HttpStatusCode status, UUID agentID, UUID groupID, string decision, string detail)
            => new ChatSessionOutcome
            {
                Status = status,
                Instrument = Line(agentID, groupID, decision, detail),
            };

        /// <summary>One greppable line per decision, the house instrument convention. No token is ever logged.</summary>
        public static string Line(UUID agentID, UUID groupID, string decision, string detail)
            => $"{InstrumentTag} agent={agentID} group={groupID} decision={decision}" +
               (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
