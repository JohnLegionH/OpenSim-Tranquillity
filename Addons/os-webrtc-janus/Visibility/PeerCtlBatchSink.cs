/*
 * Transport seam for the peer_ctl_batch feed (mixer-feed-protocol.md §3.3). Backend-neutral: the
 * orchestrator passes a typed op + per-listener exclusion sets; the implementation serializes,
 * STAMPS THE ROOM, wraps the Janus admin envelope, and sends. The orchestrator stays room-agnostic
 * and Janus-agnostic — this interface is the only thing it knows about the transport.
 *
 * Lives in Visibility so both the orchestrator (WebRtcVoiceRegionModule) and the prod
 * implementation (Janus-side, WebRtcVoiceServiceModule) can reference it without either referencing
 * the other, and without touching WebRtcVoice.dll.
 */

using System.Collections.Generic;
using System.Threading.Tasks;
using OpenMetaverse;

namespace osWebRtcVoice
{
    /// <summary>How a peer_ctl_batch send resolved. Mirrors the transport's Ok/TransportError/
    /// ProtocolError, mapped by the sink. Note: Ok means the mixer accepted+dispatched the batch,
    /// NOT that it applied — an unknown room returns success (see JanusAdminClient / §3.3.1).</summary>
    public enum PeerCtlSendResult
    {
        Ok,
        TransportError,
        ProtocolError,
    }

    /// <summary>The orchestrator→transport seam. One op (add/remove/replace) with per-listener
    /// excluded-source sets. The implementation stamps the room and sends.</summary>
    public interface IPeerCtlBatchSink
    {
        Task<PeerCtlSendResult> SendAsync(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl);
    }
}
