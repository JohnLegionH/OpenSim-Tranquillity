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

        /// <summary>
        /// P1.4b: add people to a conference that is ALREADY RUNNING. `params` is an LLSD array of
        /// agent ids and `session-id` is the authoritative conference id -- the viewer has been
        /// re-keyed by now, so it sends ours and not its temp one.
        /// llfloaterimsession.cpp:1256-1266, and fsfloaterim.cpp:2117-2126 sends the identical shape
        /// from Firestorm's own IM floater. Both were checked because either can be the one in John's
        /// hands.
        /// </summary>
        public const string MethodInvite = "invite";

        /// <summary>
        /// P1.4b: llimview.cpp:3436-3438. No `params` -- just the method and the session id. The
        /// viewer clears its own pending invitation immediately afterwards (:3444-3445), so the
        /// server-side unwind here is the other half of an action the viewer already considers done.
        /// </summary>
        public const string MethodDeclineInvitation = "decline invitation";

        public const string DecisionStarted = "adhoc-conference-started";
        public const string DecisionIdempotent = "adhoc-conference-idempotent";
        public const string DecisionNoParams = "adhoc-refused-no-invitees";
        public const string DecisionInvited = "adhoc-invited";
        public const string DecisionInviteNothingNew = "adhoc-invite-nothing-new";
        public const string DecisionDeclined = "adhoc-declined";
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
                // P1.4b: the creator takes the first seat inside Start, so the 0 -> 1 transition --
                // the moment the conference begins -- happens HERE, not at anybody's accept. This is
                // what makes the invitees from `params` get rung. Latched in the engine, so a retried
                // start does not ring the room twice.
                StartedRinging = start.StartedRinging,
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

        /// <summary>
        /// Answer "invite": add the named agents to a conference already running, and hand back the
        /// ones that are NEWLY invited so the caller rings those and nobody else. Someone already
        /// seated, or whose popup is already up, is not returned -- re-inviting must never produce a
        /// second ring for a person who is sitting in the call.
        ///
        /// Ours only when the session-id names an AD-HOC session this store holds. A group session,
        /// an A2A session, or an id we have never seen returns false and falls through untouched, so
        /// this arm cannot capture a method that today reaches the A2A path.
        /// </summary>
        public static bool TryHandleInvite(OSDMap reqmap, UUID agentID, NonSpatialVoiceSessionEngine engine,
                                           bool enabled, out ChatSessionOutcome outcome, out List<UUID> newlyInvited)
        {
            outcome = null;
            newlyInvited = new List<UUID>();
            if (!Ours(reqmap, engine, enabled, MethodInvite, out UUID sessionId, out NonSpatialVoiceSession session))
                return false;

            List<UUID> asked = ParseInvitees(reqmap);
            List<string> refusals = new List<string>();
            foreach (UUID invitee in asked)
            {
                if (invitee == agentID) continue;                      // never invite yourself
                NonSpatialMember before = session.Find(invitee);
                bool alreadyInTheCall = before != null && before.HoldsSeat;
                bool alreadyRung = session.WasInvited(invitee);

                SessionOutcome r = engine.Invite(sessionId, agentID, invitee);
                if (!r.Ok)
                {
                    refusals.Add(invitee + ":" + r.Decision);
                    continue;
                }
                if (!alreadyInTheCall && !alreadyRung)
                    newlyInvited.Add(invitee);
            }

            outcome = new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                Instrument = Line(agentID, sessionId,
                                  newlyInvited.Count > 0 ? DecisionInvited : DecisionInviteNothingNew,
                                  $"session={sessionId} asked={asked.Count} new={newlyInvited.Count}"
                                  + (refusals.Count > 0 ? " refused=[" + string.Join(",", refusals) + "]" : string.Empty)),
            };
            return true;
        }

        /// <summary>
        /// Answer "decline invitation" for an ad-hoc conference: drop the pending invitation without
        /// touching a seat, because an invitation never held one. The engine also forgets that this
        /// agent was rung, so a later invite can ring them again -- a mis-clicked decline must not
        /// make someone unreachable for the rest of the call.
        ///
        /// Scoped to AD-HOC sessions only. The viewer sends this same method for a declined GROUP
        /// invitation (llimview.cpp:3429-3438), and that path is not in this slice; letting it fall
        /// through keeps today's behaviour for everything that is not a conference.
        /// </summary>
        public static bool TryHandleDeclineInvitation(OSDMap reqmap, UUID agentID, NonSpatialVoiceSessionEngine engine,
                                                      bool enabled, out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (!Ours(reqmap, engine, enabled, MethodDeclineInvitation, out UUID sessionId, out NonSpatialVoiceSession session))
                return false;
            if (session.Find(agentID) is null)
                return false;              // not a party to this conference: not ours to answer

            SessionOutcome r = engine.Decline(sessionId, agentID);
            outcome = new ChatSessionOutcome
            {
                // A decline the engine refused is still a 200: the viewer has already torn its own
                // pending invitation down and has nothing useful to do with an error.
                Status = HttpStatusCode.OK,
                Instrument = Line(agentID, sessionId, DecisionDeclined,
                                  $"session={sessionId} engine={r.Decision} "
                                  + $"seats={engine.Store.Get(sessionId)?.SeatsHeld}"),
            };
            return true;
        }

        /// <summary>
        /// The shared "is this ours?" gate for the post-start ad-hoc methods: enabled, the right
        /// method, a session id, and a session this store holds that is genuinely AD-HOC.
        /// </summary>
        private static bool Ours(OSDMap reqmap, NonSpatialVoiceSessionEngine engine, bool enabled, string wanted,
                                 out UUID sessionId, out NonSpatialVoiceSession session)
        {
            sessionId = UUID.Zero;
            session = null;
            if (!enabled || engine is null || reqmap is null)
                return false;
            if (!reqmap.TryGetString("method", out string method)
                || !string.Equals(method, wanted, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!reqmap.TryGetUUID("session-id", out sessionId) || sessionId == UUID.Zero)
                return false;
            session = engine.Store.Get(sessionId);
            return session is not null && session.Type == NonSpatialSessionType.Adhoc;
        }

        public static string Line(UUID agentID, UUID tempSessionId, string decision, string detail)
            => $"{InstrumentTag} agent={agentID} temp-session-id={tempSessionId} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
