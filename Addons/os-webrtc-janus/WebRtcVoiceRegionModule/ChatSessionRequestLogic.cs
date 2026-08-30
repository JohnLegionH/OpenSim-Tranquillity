/*
 * Pure decision logic for the ChatSessionRequest capability (Docs/voice/a2a-build-plan.md S-A2A-1).
 *
 * The HTTP handler in WebRtcVoiceRegionModule.ChatSessionRequest is a thin adapter: it parses the body,
 * calls Decide(), then applies the outcome (status code, LLSD response body, ChatterBoxSessionStartReply
 * event) and logs the instrument line. Everything that can be unit-tested without a Scene lives here.
 *
 * Wire contract (docs/voice-a2a-wire-trace-20260830.md in the Firestorm tree, LL-upstream paths):
 *  - "start p2p voice" (llimview.cpp:627-664): session-id = viewer XOR of the two agent ids, params = the
 *    other participant's UUID. The viewer needs the ChatterBoxSessionStartReply echo (temp_session_id ==
 *    its session-id). A missing/unparseable params is a malformed request -> 400 (plan §1.3), replacing the
 *    old UUID.Random() fallback which minted a session no one else could ever join.
 *  - "call" (llvoicechannel.cpp:623-688): expects voice_credentials { channel_uri, channel_credentials } in
 *    the HTTP RESPONSE BODY; 403 -> VoiceNotAllowed, other failures -> VoiceCallGenericError.
 *  - "decline p2p voice" / "decline invitation" / "start conference" / "fetch history": 200, no body (the
 *    decline arms gain behaviour in S-A2A-3).
 */
using System;
using System.Collections.Generic;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    /// <summary>What the HTTP adapter must do for one ChatSessionRequest.</summary>
    public sealed class ChatSessionOutcome
    {
        public HttpStatusCode Status { get; init; }
        /// <summary>LLSD map to serialise as the response body, or null for an empty body.</summary>
        public OSDMap Body { get; init; }
        /// <summary>When non-null, enqueue a ChatterBoxSessionStartReply with these values to the requester.</summary>
        public StartReply Reply { get; init; }
        /// <summary>Single-line, greppable instrument text (plan §1.8). Always set.</summary>
        public string Instrument { get; init; }

        public sealed class StartReply
        {
            public UUID SessionId { get; init; }
            public UUID TempSessionId { get; init; }
        }
    }

    public static class ChatSessionRequestLogic
    {
        public const string MethodStartP2PVoice = "start p2p voice";
        public const string MethodCall = "call";
        public const string MethodDeclineP2PVoice = "decline p2p voice";
        public const string MethodDeclineInvitation = "decline invitation";
        public const string MethodStartConference = "start conference";
        public const string MethodFetchHistory = "fetch history";

        /// <summary>The instrument prefix; grep for it.</summary>
        public const string InstrumentTag = "[A2A CHATSESSION]";

        /// <summary>
        /// Decide the outcome for a parsed ChatSessionRequest body. Pure apart from the registry.
        /// </summary>
        /// <param name="reqmap">The parsed LLSD body (already known to be a map).</param>
        /// <param name="agentID">The cap-bound requesting agent.</param>
        /// <param name="registry">The instance's invitation registry.</param>
        public static ChatSessionOutcome Decide(OSDMap reqmap, UUID agentID, A2ASessionRegistry registry)
        {
            if (reqmap == null)
                return Fail(HttpStatusCode.NoContent, agentID, "-", UUID.Zero, "no body");

            if (!reqmap.TryGetString("method", out string method) || string.IsNullOrEmpty(method))
                return Fail(HttpStatusCode.NotFound, agentID, "-", UUID.Zero, "missing method");

            method = method.ToLowerInvariant();

            if (!reqmap.TryGetUUID("session-id", out UUID sessionID))
                return Fail(HttpStatusCode.NotFound, agentID, method, UUID.Zero, "missing session-id");

            switch (method)
            {
                case MethodStartP2PVoice:
                    return StartP2PVoice(reqmap, agentID, sessionID, registry);

                case MethodCall:
                    return Call(reqmap, agentID, sessionID, registry);

                // Stubs carried over unchanged: 200, no body. The decline arms gain behaviour in S-A2A-3.
                case MethodDeclineP2PVoice:
                case MethodDeclineInvitation:
                case MethodStartConference:
                case MethodFetchHistory:
                    return new ChatSessionOutcome
                    {
                        Status = HttpStatusCode.OK,
                        Instrument = Line(agentID, method, sessionID, "stub-ok", null),
                    };

                default:
                    return Fail(HttpStatusCode.BadRequest, agentID, method, sessionID, "unknown method");
            }
        }

        private static ChatSessionOutcome StartP2PVoice(OSDMap reqmap, UUID agentID, UUID sessionID, A2ASessionRegistry registry)
        {
            // params = the other participant. Absent or not a UUID -> malformed -> 400 (plan §1.3). The
            // viewer maps 400 on this POST to session_does_not_exist_error (llimview.cpp:658-662).
            if (!reqmap.TryGetUUID("params", out UUID otherID) || otherID == UUID.Zero)
                return Fail(HttpStatusCode.BadRequest, agentID, MethodStartP2PVoice, sessionID, "params missing or not a UUID");
            if (otherID == agentID)
                return Fail(HttpStatusCode.BadRequest, agentID, MethodStartP2PVoice, sessionID, "params is the caller");

            UUID derived = A2ASessionRegistry.ComputeSessionId(agentID, otherID);
            A2ASession s = registry.Record(agentID, otherID, out bool created);

            string altVst = reqmap.TryGetOSDMap("alt_params", out OSDMap alt) && alt.TryGetString("voice_server_type", out string v) ? v : "-";
            string detail = $"callee={otherID} derived={derived} viewer-session-id={sessionID} match={(derived == sessionID).ToString().ToLowerInvariant()} " +
                            $"record={(created ? "created" : "refreshed")} token={(s.Token == null ? "none" : "present")} alt.voice_server_type={altVst}";

            return new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                // The viewer keys the reply by temp_session_id == the session-id it sent (llimview.cpp:4881);
                // session_id is the server's id for the session -- the same XOR, so the re-key is a no-op.
                Reply = new ChatSessionOutcome.StartReply { SessionId = derived, TempSessionId = sessionID },
                Instrument = Line(agentID, MethodStartP2PVoice, sessionID, "recorded", detail),
            };
        }

        private static ChatSessionOutcome Call(OSDMap reqmap, UUID agentID, UUID sessionID, A2ASessionRegistry registry)
        {
            string vst = reqmap.TryGetOSDMap("alt_params", out OSDMap alt) && alt.TryGetString("preferred_voice_server_type", out string v) ? v : "-";
            A2ASession s = registry.IssueToken(sessionID, agentID);
            if (s == null)
            {
                // Unknown/expired session, or not a party. 404 -> the viewer shows VoiceCallGenericError and
                // deactivates the channel (llvoicechannel.cpp:668-673); 403 would say "VoiceNotAllowed", which
                // is a policy message we have not earned here.
                return Fail(HttpStatusCode.NotFound, agentID, MethodCall, sessionID, $"no live invitation for this agent alt.preferred_voice_server_type={vst}");
            }

            OSDMap creds = new OSDMap
            {
                ["channel_uri"] = OSD.FromString(s.ChannelUri),
                ["channel_credentials"] = OSD.FromString(s.Token),
            };
            OSDMap body = new OSDMap { ["voice_credentials"] = creds };

            return new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                Body = body,
                Instrument = Line(agentID, MethodCall, sessionID, "credentials-issued",
                    $"channel_uri={s.ChannelUri} other={s.OtherParty(agentID)} alt.preferred_voice_server_type={vst}"),
            };
        }

        private static ChatSessionOutcome Fail(HttpStatusCode status, UUID agentID, string method, UUID sessionID, string reason)
            => new ChatSessionOutcome
            {
                Status = status,
                Instrument = Line(agentID, method, sessionID, $"refused-{(int)status}", reason),
            };

        /// <summary>One greppable line: tag agent method session-id decision [detail]. Never contains the token.</summary>
        public static string Line(UUID agentID, string method, UUID sessionID, string decision, string detail)
            => $"{InstrumentTag} agent={agentID} method=\"{method}\" session-id={sessionID} decision={decision}" +
               (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
