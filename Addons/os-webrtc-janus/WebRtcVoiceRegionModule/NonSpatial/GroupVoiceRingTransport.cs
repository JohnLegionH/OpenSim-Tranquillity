/*
 * P1.2G-c item 1: carry a group voice ring to members on ANOTHER regionserver process.
 *
 * P1.2G-b's fan-out walks every scene in THIS process (A2AInviteDelivery). A member on a different
 * regionserver has no presence here and gets nothing -- A2AInviteDelivery.cs:11-13 says so in as
 * many words. This carries the ring across, and the remote instance rebuilds the very same
 * event-queue ChatterBoxInvitation locally, so the viewer cannot tell the difference.
 *
 * THE PATTERN IS NOT NEW. GroupsMessagingModule already crosses instances exactly this way: it
 * sends with IMessageTransferModule.SendInstantMessage (GroupsMessagingModule.cs:393-394) and
 * receives by subscribing scene.EventManager.OnIncomingInstantMessage (:136, handler :438). The
 * transported message NEVER reaches a viewer as an IM: InstantMessageModule delivers only
 * MessageFromAgent / StartTyping / StopTyping / BusyAutoResponse / MessageFromObject and drops
 * everything else in its default arm (InstantMessageModule.cs:146-155). It is a module-to-module
 * carrier, which is precisely what we want.
 *
 * THE DISCRIMINATOR, and why it needs no new dialog value. Every OnIncomingInstantMessage
 * subscriber in the tree was checked before choosing:
 *     InstantMessageModule      MessageFromAgent, StartTyping, StopTyping, BusyAutoResponse,
 *                               MessageFromObject; default drops
 *     InventoryTransferModule   InventoryOffered, InventoryDeclined
 *     LureModule / HGLureModule RequestTeleport, GodLikeRequestTeleport, RequestLure
 *     CallingCardModule         dialog 211 only
 *     GodsModule                no dialog filter on this path
 *     GroupsModule (x2)         GroupInvitation*, GroupNotice*
 *     GroupsMessagingModule     SessionSend, but ONLY when fromGroup == true (:456)
 * So SessionSend with fromGroup == FALSE is claimed by nothing. We use that, plus a magic header in
 * binaryBucket so a future claimant still cannot be confused with us. A brand new dialog value was
 * rejected deliberately: it risks colliding with viewer or upstream semantics we do not control.
 */
using System;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;   // GridInstantMessage

namespace osWebRtcVoice.NonSpatial
{
    public static class GroupVoiceRingTransport
    {
        /// <summary>InstantMessageDialog.SessionSend. Mirrored rather than referenced; see the header.</summary>
        public const byte DialogSessionSend = 17;

        /// <summary>
        /// Magic header on binaryBucket. Anything without it is not ours, whatever the dialog says.
        /// Versioned so a future change of payload shape is a different header, not a silent misparse.
        /// </summary>
        public const string BucketMagic = "LGVRING1";

        public const string InstrumentTag = "[GROUP RING XHOST]";

        public const string DecisionSent = "sent-cross-instance";
        public const string DecisionNoTransfer = "no-message-transfer-module";
        public const string DecisionNotOurs = "not-ours";
        public const string DecisionMalformed = "malformed";

        /// <summary>
        /// Build the carrier for one target. The whole invitation body rides in binaryBucket, so the
        /// remote side rebuilds byte-for-byte what a local ring would have delivered and no field can
        /// drift between the two paths.
        /// </summary>
        public static GridInstantMessage Build(UUID target, UUID fromAgent, string fromName, UUID groupID,
                                               UUID originRegion, OSDMap invitationBody)
        {
            if (invitationBody is null) throw new ArgumentNullException(nameof(invitationBody));
            byte[] payload = Encoding.UTF8.GetBytes(BucketMagic + OSDParser.SerializeJsonString(invitationBody));
            return new GridInstantMessage
            {
                toAgentID = target.Guid,
                fromAgentID = fromAgent.Guid,
                fromAgentName = fromName ?? string.Empty,
                // SessionSend + fromGroup FALSE is the free combination; see the header.
                dialog = DialogSessionSend,
                fromGroup = false,
                imSessionID = groupID.Guid,
                message = string.Empty,
                offline = 0,                       // a ring is worthless once stored and replayed later
                RegionID = originRegion.Guid,
                // DateTimeOffset, not Util.UnixTimeSinceEpoch: this builder is pure and must not
                // drag OpenSim.Framework's static initialiser into a caller that has no config.
                timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                binaryBucket = payload,
            };
        }

        /// <summary>
        /// Is this incoming grid IM one of our rings, and if so what invitation body does it carry?
        /// Returns false for everything else, having touched nothing -- every other subscriber still
        /// sees the message exactly as before.
        /// </summary>
        public static bool TryParse(GridInstantMessage msg, out UUID target, out UUID groupID, out OSDMap body)
        {
            target = UUID.Zero;
            groupID = UUID.Zero;
            body = null;
            if (msg is null || msg.dialog != DialogSessionSend || msg.fromGroup)
                return false;
            if (msg.binaryBucket is null || msg.binaryBucket.Length <= BucketMagic.Length)
                return false;

            string raw;
            try { raw = Encoding.UTF8.GetString(msg.binaryBucket); }
            catch { return false; }
            if (!raw.StartsWith(BucketMagic, StringComparison.Ordinal))
                return false;

            try
            {
                if (OSDParser.DeserializeJson(raw.Substring(BucketMagic.Length)) is not OSDMap map)
                    return false;
                body = map;
            }
            catch { return false; }

            target = new UUID(msg.toAgentID);
            groupID = new UUID(msg.imSessionID);
            return true;
        }

        public static string Line(UUID target, UUID group, string decision, string detail = null)
            => $"{InstrumentTag} target={target} group={group} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
