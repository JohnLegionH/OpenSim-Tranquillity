/*
 * Pure builders for the three ProvisionVoiceAccountRequest response maps. No Janus session,
 * no I/O, no async — extracted from WebRtcJanusService.ProvisionVoiceAccountRequestBAD so the
 * response SHAPE (the keys the region module serialises to the viewer, the connector forwards
 * over JSON-RPC, and the viewer reads by name) can be asserted in a unit test without a live
 * mixer. Build plan step S1 follow-up (per-room-visibility-emission-design-brief.md §8).
 *
 * These reproduce the former inline literals exactly — same keys, same insertion order, same
 * OSD types via the same implicit conversions — and ProvisionResponseShapeTests pins that
 * equivalence byte-for-byte on both serialisation paths. Change the shape here and nowhere else.
 */

using OpenMetaverse.StructuredData;

namespace osWebRtcVoice;

public static class ProvisionResponseBuilder
{
    /// <summary>The JSEP-offer success map: the answer, the viewer session id, and — additive
    /// since S1 — the mixer room the session actually joined. Success path only.</summary>
    public static OSDMap BuildSuccess(OSDMap jsepAnswer, string viewerSessionId, int room)
    {
        return new OSDMap
        {
            { "jsep", jsepAnswer },
            { "viewer_session", viewerSessionId },
            { "room", room }
        };
    }

    /// <summary>The failure map. <c>error_code</c> is carried only when non-zero: today only
    /// the mixer's ROOM_FULL sets it, and the CAP handler maps that to HTTP 409. Every other
    /// failure leaves it 0 and the key absent.</summary>
    public static OSDMap BuildFailure(string errorMsg, int errorCode)
    {
        OSDMap ret = new OSDMap
        {
            { "response", "failed" },
            { "error", errorMsg }
        };
        if (errorCode != 0)
            ret["error_code"] = errorCode;
        return ret;
    }

    /// <summary>The logout acknowledgement.</summary>
    public static OSDMap BuildClosed()
    {
        return new OSDMap
        {
            { "response", "closed" }
        };
    }
}
