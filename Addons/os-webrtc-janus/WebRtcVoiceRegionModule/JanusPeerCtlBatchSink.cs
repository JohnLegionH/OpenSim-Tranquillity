/*
 * Janus-side implementation of IPeerCtlBatchSink (Phase 3a option C). Stamps the estate room number
 * (JanusAudioBridge.CalcRoomNumber — the SAME hash the mixer uses) and sends the peer_ctl_batch via
 * JanusAdminClient (Admin API message_plugin, admin_secret in the body). One instance per region;
 * owns its JanusAdminClient (and its HttpClient) for its lifetime.
 *
 * This is the ONLY place the room number is computed (correction: room lives sink-side; the feeder
 * and orchestrator stay room-agnostic). The computed number is logged once at Info so it is
 * eyeball-comparable against the mixer's handle_info — a wrong room is invisible on the wire.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public sealed class JanusPeerCtlBatchSink : IPeerCtlBatchSink, IDisposable
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[JANUS PEERCTL SINK]";
        private const string SlvoicePlugin = "janus.plugin.slvoice";

        private readonly JanusAdminClient _admin;
        private readonly int _room;

        public JanusPeerCtlBatchSink(string adminUri, string adminToken, TimeSpan timeout,
                                     UUID regionId, string regionName)
        {
            _admin = new JanusAdminClient(adminUri, adminToken, timeout);
            // Estate/shared channel room: the "local" channel at REGION_ROOM_ID (-999), hashed by
            // the identical CalcRoomNumber the mixer computes on the Janus side.
            _room = JanusAudioBridge.CalcRoomNumber(
                regionId.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            m_log.LogInformation("{LogHeader} region {RegionName} ({RegionId}) -> estate peer_ctl_batch room {RoomNumber} (compare vs handle_info)",
                LogHeader, regionName, regionId, _room);
        }

        public void Dispose() => _admin?.Dispose();

        public async Task<PeerCtlSendResult> SendAsync(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl)
        {
            OSDMap request = PeerCtlBatchSerializer.BuildRequest(op, excl);
            request["room"] = new OSDInteger(_room);   // the sink stamps the room
            AdminSendResult r = await _admin.SendPluginMessageAsync(SlvoicePlugin, request).ConfigureAwait(false);
            switch (r)
            {
                case AdminSendResult.Ok:
                    return PeerCtlSendResult.Ok;
                case AdminSendResult.ProtocolError:
                    return PeerCtlSendResult.ProtocolError;
                case AdminSendResult.TransportError:
                default:
                    return PeerCtlSendResult.TransportError;
            }
        }
    }
}
