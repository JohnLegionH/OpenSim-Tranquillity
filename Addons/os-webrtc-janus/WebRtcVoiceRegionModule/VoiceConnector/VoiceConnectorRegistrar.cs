/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>
/// S-CON-2 (Docs/voice/connector-build-plan.md): the registration/teardown orchestration for one
/// connector record, extracted from VoiceConnectorModule so it is unit-testable with fakes at the
/// seams (the IsProvisionableChannelType extraction pattern). The delegates are the module's real
/// integrations: INPCModule.CreateNPC/DeleteNPC, IWebRtcVoiceService.CreateViewerSession, the
/// VoiceVisibilityService room record and moderation store. The registrar owns the ORDER and the
/// record's mutable slots; it holds no locks of its own (the module serialises calls per record).
/// </summary>
public static class VoiceConnectorRegistrar
{
    /// <summary>Create the NPC; UUID.Zero on failure (the INPCModule.CreateNPC contract).</summary>
    public delegate UUID CreateNpcDelegate(VoiceConnectorRecord pRecord);
    /// <summary>Delete the NPC (INPCModule.DeleteNPC).</summary>
    public delegate void DeleteNpcDelegate(UUID pNpcId);
    /// <summary>Create (NOT provision) a viewer session for the agent id — no JSEP exists for an
    /// NPC; the session only carries identity and membership (assessment §3).</summary>
    public delegate IVoiceViewerSession CreateSessionDelegate(UUID pNpcId);
    /// <summary>Record the room the connector listens in (the OnListenerProvisioned equivalent).</summary>
    public delegate void RecordRoomDelegate(UUID pNpcId, int pRoom);
    /// <summary>Push the moderation mute for the NPC identity (brief Amendment 2 D2).</summary>
    public delegate void MuteDelegate(UUID pNpcId);

    /// <summary>
    /// Register one enabled record: NPC -> session (non-zero ClientSessionId, the assessment §7(d)
    /// trap) -> AddViewerSession (membership; IsAgentInRegion flips true) -> room record ->
    /// moderation mute iff MayInject=false. Returns false and leaves the record INACTIVE (slots
    /// empty, no session, no membership) when the NPC could not be created. Idempotent: an
    /// already-registered record (NpcId set) is a no-op true.
    /// </summary>
    public static bool Register(VoiceConnectorRecord pRecord, int pEstateRoom,
        CreateNpcDelegate pCreateNpc, CreateSessionDelegate pCreateSession,
        RecordRoomDelegate pRecordRoom, MuteDelegate pMute, ILogger pLog,
        VoiceConnectorDisclosure pDisclosure = null)
    {
        if (pRecord.NpcId != UUID.Zero)
            return true;   // already registered

        UUID npcId = pCreateNpc(pRecord);
        if (npcId == UUID.Zero)
        {
            pLog?.LogWarning("[CONNECTOR] {Name}: CreateNPC failed; record left inactive (no session, no membership)",
                pRecord.Name);
            return false;
        }
        pLog?.LogDebug("[CONNECTOR] {Name}: NPC created npc={NpcId}", pRecord.Name, npcId);

        IVoiceViewerSession session = pCreateSession(npcId);
        // The generation token (assessment §3/§7(d)): NPCAvatar.SessionId is UUID.Zero, so the
        // normal CaptureGenerationToken path would register this session sweepable by ANY close
        // for the agent. A fresh UUID per NPC incarnation gives it the same close-selection
        // semantics a real login has.
        session.ClientSessionId = UUID.Random();
        VoiceViewerSession.AddViewerSession(session);
        pRecord.NpcId = npcId;
        pRecord.ViewerSessionId = session.ViewerSessionID;
        pLog?.LogDebug("[CONNECTOR] {Name}: voice session registered session={ViewerSessionId} (membership on)",
            pRecord.Name, session.ViewerSessionID);

        pRecordRoom(npcId, pEstateRoom);

        if (!pRecord.MayInject)
        {
            // Brief Amendment 2 D2: recording-only connectors are silenced at the source for
            // every listener via the existing moderation mute channel — the same call the
            // SpatialVoiceModerationRequest "mute" case makes (WebRtcVoiceRegionModule.cs,
            // svc.Moderation.MuteAgent). The rule reads the SOURCE's own parcel, and the NPC
            // never moves off its configured position, so the mute holds for its lifetime.
            pMute(npcId);
            pLog?.LogDebug("[CONNECTOR] {Name}: moderation mute pushed (MayInject=false)", pRecord.Name);
        }

        // S-CON-3 door notice (brief D3(ii)): the attach alert fires LAST — only a fully
        // registered connector (session, room, mute all in place) is announced as attached.
        pDisclosure?.OnAttach(pRecord);
        return true;
    }

    /// <summary>
    /// Tear one record down: session first (membership off — the matrix stops emitting for the
    /// identity), then the NPC. Plain RemoveViewerSession is CORRECT here, unlike the O-41 logout
    /// case: this session was created but never provisioned, so no Janus session, handle, or room
    /// membership exists to shut down — there is nothing to orphan. No moderation UNMUTE is
    /// pushed either: the mute entry keys on the (parcel, NPC id) pair, and the identity it
    /// silences dies with the NPC — a future incarnation gets a fresh UUID and its own mute.
    /// Idempotent: an inactive record is a no-op.
    /// </summary>
    public static void Unregister(VoiceConnectorRecord pRecord, DeleteNpcDelegate pDeleteNpc, ILogger pLog,
        VoiceConnectorDisclosure pDisclosure = null)
    {
        bool wasActive = pRecord.NpcId != UUID.Zero || pRecord.ViewerSessionId != null;
        if (pRecord.ViewerSessionId != null)
        {
            VoiceViewerSession.RemoveViewerSession(pRecord.ViewerSessionId);
            pLog?.LogDebug("[CONNECTOR] {Name}: voice session removed session={ViewerSessionId} (membership off)",
                pRecord.Name, pRecord.ViewerSessionId);
            pRecord.ViewerSessionId = null;
        }
        if (pRecord.NpcId != UUID.Zero)
        {
            pDeleteNpc(pRecord.NpcId);
            pLog?.LogDebug("[CONNECTOR] {Name}: NPC removed npc={NpcId}", pRecord.Name, pRecord.NpcId);
            pRecord.NpcId = UUID.Zero;
        }
        // S-CON-3 door notice (brief D3(ii)): announced only if something was actually torn down
        // — an idempotent re-teardown of an inactive record stays silent.
        if (wasActive)
            pDisclosure?.OnDetach(pRecord);
    }
}
