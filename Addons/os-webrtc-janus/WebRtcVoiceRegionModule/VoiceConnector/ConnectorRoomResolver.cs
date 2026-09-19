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

    /// <summary>Slice 0.8h (O-62): the connector's position in EXACTLY the frame a viewer sends in SLData - GLOBAL
    /// centimetres as integers, (region global origin + the record's region-local Position) x 100. The viewer builds its "sp" as
    /// (int)(getPositionGlobal() * 100) (Firestorm llvoicewebrtc.cpp:1108, :1241-1244) and the mixer only ever uses
    /// differences, so a connector in any other frame - region-local metres above all - is kilometres from every
    /// avatar and always culled. The origin is RegionInfo.WorldLocX/Y in metres (RegionLocX x 256), which holds for a
    /// var region too: its origin is its south-west corner, and Position runs to RegionSizeX (1024 for Elm).
    /// Truncation toward zero, as the viewer's (int) cast. Z has no origin.</summary>
    public static (int X, int Y, int Z) GlobalCentimetres(uint pWorldLocX, uint pWorldLocY, Vector3 pPosition)
        => ((int)((pWorldLocX + (double)pPosition.X) * 100.0),
            (int)((pWorldLocY + (double)pPosition.Y) * 100.0),
            (int)((double)pPosition.Z * 100.0));

    /// <summary>Slice 0.8f (O-98, ruling R1): run the ensure and, only if it answered with a room, tell the visibility
    /// authority that room EXISTS. A null answer proves nothing, so it resets nothing. Returns the ensure's answer.</summary>
    public static int? EnsureAndProve(System.Func<int?> pEnsure, System.Action<int> pRoomExists)
    {
        int? ensured = pEnsure();
        if (ensured.HasValue)
            pRoomExists?.Invoke(ensured.Value);
        return ensured;
    }
}
