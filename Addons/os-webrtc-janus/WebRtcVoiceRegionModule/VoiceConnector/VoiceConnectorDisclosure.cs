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

using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>
/// S-CON-3 (Docs/voice/connector-build-plan.md; brief Amendment 2 D3): the three mandatory
/// disclosure layers beyond the name marker — door notices (attach/detach region alert + entry
/// notice on becoming root) and the proximity notice from a voiced NPC. UNCONDITIONAL by design:
/// no config key disables any of them (D3 — no undisclosed mode exists). Delivery is behind
/// delegate seams so the logic and dedupe are unit-testable without a Scene
/// (VoiceConnectorDisclosureTests); VoiceConnectorModule wires the real scene surfaces.
/// Dedupe keys on the LOGIN SESSION id (ControllingClient.SessionId): a relog re-arms every
/// notice — disclosure repeats rather than lapses. Thread-safe: heartbeat, MakeRoot and console
/// paths all take the one lock.
/// </summary>
public sealed class VoiceConnectorDisclosure
{
    /// <summary>Region-wide notice to everyone (the estate "message region" surface).</summary>
    public delegate void RegionAlertDelegate(string pMessage);
    /// <summary>One notice line to one agent (the entry notice).</summary>
    public delegate void AgentNoticeDelegate(UUID pAgentId, string pMessage);
    /// <summary>One local chat line to one agent, spoken AS the record's NPC (the proximity notice).</summary>
    public delegate void NpcChatDelegate(VoiceConnectorRecord pRecord, UUID pAgentId, string pMessage);

    private readonly RegionAlertDelegate m_regionAlert;
    private readonly AgentNoticeDelegate m_agentNotice;
    private readonly NpcChatDelegate m_npcChat;
    private readonly float m_voiceRangeSq;

    private readonly object m_lock = new object();
    // Entry-notice dedupe: login sessions already told (SessionId is unique per login per agent,
    // and this object is per region instance — so the key IS agent+region+login).
    private readonly HashSet<UUID> m_entryNoticed = new HashSet<UUID>();
    // Proximity dedupe: (login session, NPC id) pairs already told. An NPC re-incarnation gets a
    // fresh UUID, so a stop/start naturally re-arms — disclosure repeats rather than lapses.
    private readonly HashSet<(UUID Session, UUID NpcId)> m_proximityNoticed = new HashSet<(UUID, UUID)>();

    public VoiceConnectorDisclosure(RegionAlertDelegate pRegionAlert, AgentNoticeDelegate pAgentNotice,
        NpcChatDelegate pNpcChat, float pVoiceRangeMetres)
    {
        m_regionAlert = pRegionAlert;
        m_agentNotice = pAgentNotice;
        m_npcChat = pNpcChat;
        m_voiceRangeSq = pVoiceRangeMetres * pVoiceRangeMetres;
    }

    // ---- Layer (ii), door notice 1: attach/detach region alert. Called by the registrar.

    public void OnAttach(VoiceConnectorRecord pRecord)
    {
        m_regionAlert(
            $"Voice connector {pRecord.NpcFullName} attached — an NPC (recording / automated voice) is present in this region's voice.");
    }

    public void OnDetach(VoiceConnectorRecord pRecord)
    {
        m_regionAlert(
            $"Voice connector {pRecord.NpcFullName} detached — its NPC has left this region's voice.");
    }

    // ---- Layer (ii), door notice 2: entry notice on becoming root while attached.

    /// <summary>One line to an agent becoming root, naming every attached connector and whether it
    /// is voiced. Once per login session; nothing when no connector is attached (and the session
    /// is NOT marked in that case, so a connector attaching later still gets announced via the
    /// attach alert — the two door notices cover each other's window).</summary>
    public void OnMakeRoot(UUID pAgentId, UUID pLoginSessionId, IReadOnlyList<VoiceConnectorRecord> pAttached)
    {
        if (pAttached is null || pAttached.Count == 0)
            return;
        lock (m_lock)
        {
            if (!m_entryNoticed.Add(pLoginSessionId))
                return;
        }
        List<string> parts = new List<string>(pAttached.Count);
        foreach (VoiceConnectorRecord r in pAttached)
            parts.Add($"{r.NpcFullName} ({(r.MayInject ? "voiced" : "recording")})");
        m_agentNotice(pAgentId,
            $"This region's voice has NPC connector(s) attached: {string.Join(", ", parts)}.");
    }

    // ---- Layer (iii): proximity notice from a voiced NPC.

    /// <summary>
    /// Called from the region heartbeat. For every attached MayInject connector, the first time a
    /// root agent's position comes within voice range of the NPC, one chat line spoken as the NPC.
    /// Cost: O(rootAgents × attached voiced connectors) squared-distance compares per call —
    /// no allocation on the quiet path beyond the caller's snapshots.
    /// </summary>
    public void ProximityTick(IReadOnlyList<(UUID AgentId, UUID LoginSessionId, Vector3 Position)> pRootAgents,
        IReadOnlyList<VoiceConnectorRecord> pAttachedVoiced)
    {
        if (pRootAgents is null || pAttachedVoiced is null || pRootAgents.Count == 0 || pAttachedVoiced.Count == 0)
            return;
        foreach (VoiceConnectorRecord record in pAttachedVoiced)
        {
            if (!record.MayInject || record.NpcId == UUID.Zero)
                continue;   // recording-only or not attached: no voice, no proximity notice
            foreach ((UUID agentId, UUID sessionId, Vector3 pos) in pRootAgents)
            {
                if (agentId == record.NpcId)
                    continue;   // the NPC's own presence never notices itself
                if (Vector3.DistanceSquared(pos, record.Position) > m_voiceRangeSq)
                    continue;
                lock (m_lock)
                {
                    if (!m_proximityNoticed.Add((sessionId, record.NpcId)))
                        continue;
                }
                m_npcChat(record, agentId,
                    $"{record.NpcFullName} is an NPC — its voice is automated or remotely operated.");
            }
        }
    }
}
