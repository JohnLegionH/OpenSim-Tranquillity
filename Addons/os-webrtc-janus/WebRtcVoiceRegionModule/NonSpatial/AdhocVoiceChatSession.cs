/*
 * P1.4a: "start conference" and the event-queue ChatterBoxSessionStartReply.
 *
 * THIS IS THE FIX FOR THE POPUP JOHN HIT: "Unable to start a new chat session with Multi-person
 * chat. The session initialization is timed out." We answer "start conference" with 200 and no body
 * (O-107, ChatSessionRequestLogic.cs:167-175), so the viewer's session never initialises and
 * LLSessionTimeoutTimer fires that message. A body would not have helped: startConferenceCoro reads
 * NOTHING from the response (llimview.cpp:576-624) -- it only checks the status. The authoritative
 * id arrives on the EVENT QUEUE.
 *
 * ADHOC IS THE ONE TYPE THAT GENUINELY NEEDS THE RE-KEY. For a group the viewer slams the session id
 * to the group id, and for P2P it is the XOR, so both already agree with the server. For a
 * conference the viewer mints session_id.generate() (llimview.cpp:2542) -- a random id the server
 * has never seen -- and only adopts ours when the reply arrives:
 *     LLViewerChatterBoxSessionStartReply::post reads success / temp_session_id / session_id
 *         (llimview.cpp:4877-4886)
 *     -> processSessionInitializedReply re-keys mId2SessionMap, sets mSessionID and updates the
 *        voice channel id (:1705-1737, :1165-1177)
 *     -> and fires the QUEUED CALL if mStartCallOnInitialize (:1731-1734)
 * That last line is why this slice must come before ad-hoc voice: without the re-key the queued call
 * never starts.
 *
 * Modelled on GroupsMessagingModule.ChatterBoxSessionStartReplyViaCaps (:686-707), which has been
 * shipping this event for group chat for years. The module already knows how to send it -- the
 * ChatSessionOutcome.Reply path (WebRtcVoiceRegionModule.cs:1163-1185) -- so this arm only has to
 * produce the two ids and let the existing plumbing do the rest.
 */
using System;
using System.Collections.Generic;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    public static class AdhocVoiceChatSession
    {
        public const string MethodStartConference = "start conference";

        public const string DecisionStarted = "adhoc-conference-started";
        public const string DecisionIdempotent = "adhoc-conference-idempotent";
        public const string DecisionNoParams = "adhoc-refused-no-invitees";
        public const string InstrumentTag = "[ADHOC VOICE]";

        /// <summary>
        /// Answer "start conference": open (or re-find) the ADHOC session and hand back the two ids
        /// the viewer needs. Returns false -- untouched -- for anything else, so every other method
        /// and the whole A2A path behave exactly as before.
        ///
        /// Idempotent by the viewer's own temp id: a retried start finds the first session rather
        /// than opening a second room and sending a second reply. The viewer mints its temp id once,
        /// at session creation, so a retry carries the same one.
        /// </summary>
        public static bool TryHandleStartConference(OSDMap reqmap, UUID agentID, NonSpatialVoiceSessionEngine engine,
                                                    UUID originRegion, bool enabled, out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (!enabled || engine is null || reqmap is null)
                return false;
            if (!reqmap.TryGetString("method", out string method)
                || !string.Equals(method, MethodStartConference, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!reqmap.TryGetUUID("session-id", out UUID tempSessionId) || tempSessionId == UUID.Zero)
                return false;

            List<UUID> invitees = ParseInvitees(reqmap);

            SessionOutcome start = engine.StartAdhoc(agentID, tempSessionId, originRegion, invitees);
            if (!start.Ok)
            {
                outcome = new ChatSessionOutcome
                {
                    Status = HttpStatusCode.Forbidden,
                    Instrument = Line(agentID, tempSessionId, "adhoc-refused-" + start.Decision, null),
                };
                return true;
            }

            outcome = new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                // The module's existing Reply path turns this into the event-queue
                // ChatterBoxSessionStartReply carrying {success, temp_session_id, session_id}.
                Reply = new ChatSessionOutcome.StartReply
                {
                    SessionId = start.Session.SessionId,
                    TempSessionId = start.Session.TempSessionId,
                },
                Instrument = Line(agentID, tempSessionId,
                                  start.Created ? DecisionStarted : DecisionIdempotent,
                                  $"session={start.Session.SessionId} room={start.Session.RoomKey} "
                                  + $"invitees={invitees.Count} rekey={start.Session.RequiresRekey}"),
            };
            return true;
        }

        /// <summary>
        /// The agents the viewer wants in the conference. startConferenceCoro sends them as an LLSD
        /// ARRAY in `params` (llimview.cpp:2484-2487, `agents.append(ids[i])`) -- unlike
        /// "start p2p voice", where params is a single UUID. Both shapes are accepted here because
        /// the cost is two lines and a viewer that sends the scalar form would otherwise open a
        /// conference with nobody in it.
        /// </summary>
        public static List<UUID> ParseInvitees(OSDMap reqmap)
        {
            List<UUID> invitees = new List<UUID>();
            if (reqmap is null || !reqmap.TryGetValue("params", out OSD raw))
                return invitees;
            switch (raw)
            {
                case OSDArray arr:
                    foreach (OSD entry in arr)
                    {
                        UUID id = entry.AsUUID();
                        if (id != UUID.Zero && !invitees.Contains(id)) invitees.Add(id);
                    }
                    break;
                default:
                    UUID single = raw.AsUUID();
                    if (single != UUID.Zero) invitees.Add(single);
                    break;
            }
            return invitees;
        }

        public static string Line(UUID agentID, UUID tempSessionId, string decision, string detail)
            => $"{InstrumentTag} agent={agentID} temp-session-id={tempSessionId} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
