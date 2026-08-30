/*
 * Provision admission for voice (Docs/voice/a2a-build-plan.md S-A2A-3): the O-29 predicate REPLACED for
 * "multiagent" only, plus recognition of the viewer's teardown body.
 *
 * Kinds, in the order the region module needs them:
 *  - Logout: {logout:true, viewer_session, voice_server_type} and NO channel_type (wire trace §4,
 *    llvoicewebrtc.cpp:2809-2811). Before this slice the O-29 guard refused it with 403 before the
 *    service could see `logout` (live logs: 'refusing provision with channel_type ""' at every teardown).
 *    It is routed to the service by viewer_session; it needs no channel authorization because it can
 *    only end the caller's own session (S-A2A-5 binds viewer_session to the agent).
 *  - Local: channel_type == "local" -> IsProvisionableChannelType true -> the parcel/estate checks, unchanged.
 *  - Multiagent: channel_type == "multiagent" -> admitted iff the body's `channel` (NOT channel_id, U-13)
 *    names a live registry session AND the requesting agent is one of its two parties AND `credentials`
 *    equals the session token. Any other outcome is refused 403.
 *  - Refused: anything else (absent / other channel_type), exactly as O-29 left it.
 *
 * Pure and Scene-free so it is unit-testable; the region module applies the result.
 */
using System;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    public enum ProvisionKind
    {
        Refused = 0,
        Local = 1,
        Multiagent = 2,
        Logout = 3,
    }

    public sealed class ProvisionAdmission
    {
        public ProvisionKind Kind { get; init; }
        /// <summary>Instrument decision word: local-admitted | multiagent-admitted | refused-no-session | refused-not-party | refused-bad-token | refused-o29 | logout.</summary>
        public string Decision { get; init; }
        /// <summary>The A2A session for an admitted multiagent provision; null otherwise.</summary>
        public A2ASession Session { get; init; }
        /// <summary>The channel_type seen (empty when absent) for the refusal log.</summary>
        public string ChannelType { get; init; }
        /// <summary>The body's `channel` value ("-" when absent).</summary>
        public string Channel { get; init; }
        public bool Admitted => Kind != ProvisionKind.Refused;
    }

    public static class A2AProvisionAdmission
    {
        public const string ChannelTypeLocal = "local";
        public const string ChannelTypeMultiagent = "multiagent";

        public const string DecisionLocal = "local-admitted";
        public const string DecisionMultiagent = "multiagent-admitted";
        public const string DecisionLogout = "logout";
        public const string DecisionNoSession = "refused-no-session";
        public const string DecisionNotParty = "refused-not-party";
        public const string DecisionBadToken = "refused-bad-token";
        public const string DecisionO29 = "refused-o29";

        public static bool IsLogout(OSDMap map)
            => map != null && map.TryGetBool("logout", out bool lg) && lg;

        /// <summary>
        /// Plan §1.4 (a): only a spatial ("local") provision records the agent's mixer room in the
        /// visibility service's AgentRoomTable. An A2A room is not the agent's spatial room and must
        /// never be recorded (the exclusion batches would be sent to the wrong room); a logout carries
        /// no room; a refusal never reaches the service.
        /// </summary>
        public static bool RecordsListenerRoom(ProvisionKind kind) => kind == ProvisionKind.Local;

        public static ProvisionAdmission Decide(OSDMap map, UUID agentID, A2ASessionRegistry registry)
        {
            string channelType = map != null && map.TryGetString("channel_type", out string ct) ? ct : string.Empty;
            string channel = map != null && map.TryGetString("channel", out string ch) && !string.IsNullOrEmpty(ch) ? ch : "-";

            // Teardown: recognised by `logout`, never by channel_type (it carries none).
            if (IsLogout(map))
                return new ProvisionAdmission { Kind = ProvisionKind.Logout, Decision = DecisionLogout, ChannelType = channelType, Channel = channel };

            if (channelType == ChannelTypeLocal)
                return new ProvisionAdmission { Kind = ProvisionKind.Local, Decision = DecisionLocal, ChannelType = channelType, Channel = channel };

            if (channelType == ChannelTypeMultiagent)
            {
                A2ASession s = registry?.TryGetByChannel(channel);
                if (s == null)
                    return Refuse(DecisionNoSession, channelType, channel);
                if (!s.IsParty(agentID))
                    return Refuse(DecisionNotParty, channelType, channel);
                string creds = map.TryGetString("credentials", out string c) ? c : string.Empty;
                if (string.IsNullOrEmpty(s.Token) || !TokenEquals(creds, s.Token))
                    return Refuse(DecisionBadToken, channelType, channel);
                return new ProvisionAdmission { Kind = ProvisionKind.Multiagent, Decision = DecisionMultiagent, Session = s, ChannelType = channelType, Channel = channel };
            }

            return Refuse(DecisionO29, channelType, channel);
        }

        private static ProvisionAdmission Refuse(string decision, string channelType, string channel)
            => new ProvisionAdmission { Kind = ProvisionKind.Refused, Decision = decision, ChannelType = channelType, Channel = channel };

        /// <summary>Constant-time comparison; the token is a shared secret.</summary>
        private static bool TokenEquals(string presented, string expected)
        {
            if (presented == null || expected == null || presented.Length != expected.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++)
                diff |= presented[i] ^ expected[i];
            return diff == 0;
        }
    }
}
