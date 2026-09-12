/*
 * O-48 (audit W-1): which parcel a "local" voice provision is authorized and roomed against.
 *
 * The parcel is derived server-side from the avatar's position; the viewer's parcel_local_id is a
 * hint only. It never selects the parcel — it only decides what gets logged (a mismatch, or an
 * absent id). Before O-48 the region module looked the parcel up by the client's id, so a viewer
 * could name another parcel and join its mixer room, or omit the id and land in the estate room.
 * A mismatch is never refused: a viewer mid-crossing can be honestly stale (O-11).
 *
 * Pure and Scene-free so the decision unit-tests without a Scene.
 */

namespace osWebRtcVoice
{
    public readonly record struct ParcelResolution(int ServerLocalId, bool ClientMismatch, bool ClientAbsent);

    public static class ProvisionParcelResolver
    {
        public static ParcelResolution Resolve(int? clientLocalId, int serverLocalId)
        {
            return new ParcelResolution(
                ServerLocalId: serverLocalId,
                ClientMismatch: clientLocalId is not null && clientLocalId != serverLocalId,
                ClientAbsent: clientLocalId is null);
        }
    }
}
