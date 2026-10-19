/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice;

// Per-participant mute in a call's room through the AudioBridge's own moderator mute.
public partial class WebRtcJanusService : INonSpatialParticipantMute
{
    // INonSpatialParticipantMute
    public ParticipantMuteResult SetCallMute(UUID pAgentID, int pRoomNumber, bool pMuted)
    {
        try
        {
            return SetCallMuteAsync(pAgentID, pRoomNumber, pMuted).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, $"{LogHeader} SetCallMute failed for agent {pAgentID}");
            return ParticipantMuteResult.Failed;
        }
    }

    private async Task<ParticipantMuteResult> SetCallMuteAsync(UUID pAgentID, int pRoomNumber, bool pMuted)
    {
        // The agent's joined session in that room, if this process holds it. The registry is process-wide.
        long participant = 0;
        lock (VoiceViewerSession.ViewerSessions)
        {
            foreach (IVoiceViewerSession s in VoiceViewerSession.ViewerSessions.Values)
            {
                if (s is JanusViewerSession js && js.AgentId == pAgentID && !js.Spatial
                    && js.Room is not null && js.Room.RoomId == pRoomNumber && js.ParticipantId != 0)
                {
                    participant = js.ParticipantId;
                    break;
                }
            }
        }

        // The service's own handle sends the request: the mute is a room-level request, and a participant's handle
        // may be closing.
        JanusViewerSession svc = await EnsureServiceSessionAsync().ConfigureAwait(false);
        if (svc?.AudioBridge is null)
            return ParticipantMuteResult.Failed;

        if (participant == 0)
        {
            // Not joined through this process. A participant joins under its agent id as its display name, so the room
            // itself says whether the agent is there, whichever region server it joined through.
            AudioBridgeResp listed = await svc.AudioBridge.SendAudioBridgeMsg(new AudioBridgeListParticipantsReq(pRoomNumber)).ConfigureAwait(false);
            if (listed is null)
                return ParticipantMuteResult.Failed;
            if (listed.AudioBridgeReturnCode != "participants")
                return listed.AudioBridgeErrorCode == WebRtcJanusService.JANUS_NO_SUCH_ROOM_ERROR_CODE
                    ? ParticipantMuteResult.NotInRoom : ParticipantMuteResult.Failed;
            string display = pAgentID.ToString();
            if (listed.PluginRespData.TryGetValue("participants", out OSD found) && found is OSDArray all)
                foreach (OSD one in all)
                    if (one is OSDMap entry && string.Equals(entry["display"].AsString(), display, StringComparison.OrdinalIgnoreCase))
                    {
                        participant = entry["id"].AsLong();
                        break;
                    }
            if (participant == 0)
                return ParticipantMuteResult.NotInRoom;
        }
        return await svc.AudioBridge.SetParticipantMute(pRoomNumber, participant, pMuted).ConfigureAwait(false);
    }
}
