/*
 * Slice 0.8c (ledger O-93, and the connector half of O-92): the room a connector belongs in.
 *
 * Before 0.8c a connector always recorded the ESTATE room (VoiceConnectorModule.StartRecord), so on a parcel that
 * runs its own voice channel the sim armed the NPC, minted its capability and addressed its batches at a room no
 * avatar standing beside it would ever be in — and, where nobody had provisioned the estate channel, at a room that
 * did not exist at all.
 *
 * The rule here is the one the provisioning path applies to an avatar, extracted so it can be asked without a viewer
 * request: the parcel's own channel unless the parcel says UseEstateVoiceChan, then the estate/local channel
 * (WebRtcVoiceRegionModule.cs:708 for the flag, :743-752 for what reaches parcel_local_id), hashed by the same
 * CalcRoomNumber the mixer side computes (JanusAudioBridge.cs:266, via WebRtcJanusService.cs:424).
 *
 * Pure and Scene-free: the caller resolves the parcel, this decides the channel and the number.
 */

using OpenMetaverse;
using OpenSim.Framework;

namespace osWebRtcVoice;

public static class ConnectorRoomResolver
{
    /// <summary>The parcel_local_id an avatar on this parcel would be provisioned with: the parcel's own id, or
    /// REGION_ROOM_ID when the parcel runs the estate channel (or when there is no parcel at all, which the
    /// provisioning path also treats as the estate channel).</summary>
    public static int ParcelLocalIdFor(LandData pLand)
        => pLand is null || (pLand.Flags & (uint)ParcelFlags.UseEstateVoiceChan) != 0
            ? JanusAudioBridge.REGION_ROOM_ID
            : pLand.LocalID;

    /// <summary>The mixer room an avatar at this parcel in this region would be provisioned into. The grid id is
    /// deliberately empty: the "local" arm of CalcRoomNumber ignores it (JanusAudioBridge.cs:266-276), exactly as
    /// the provisioning path leaves it out.</summary>
    public static int RoomFor(UUID pRegionId, LandData pLand)
        => JanusAudioBridge.CalcRoomNumber(string.Empty, pRegionId.ToString(), "local",
            ParcelLocalIdFor(pLand), string.Empty);

    /// <summary>True when the parcel runs the estate channel, so the connector's room is the estate room — the
    /// pre-0.8c behaviour, unchanged for that case.</summary>
    public static bool IsEstateChannel(LandData pLand) => ParcelLocalIdFor(pLand) == JanusAudioBridge.REGION_ROOM_ID;
}
