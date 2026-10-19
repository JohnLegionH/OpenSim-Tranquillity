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
// Adapted 2026 by Legion Builds

using System.Reflection;

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.Base;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using Nini.Config;

using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

// NPC voice: an NPC presence gets voice only as a registered connector identity or when the operator opens it.
public partial class WebRtcVoiceServiceModule
{
    // [WebRtcVoice] AllowNpcVoice. The default is false because the stock server gives an NPC no way to reach
    // voice provisioning: the provision handler is a per-agent capability, capabilities are created only for
    // real client circuits (Scene.NewUserConnection calls ICapabilitiesModule.CreateCaps), and NPCModule adds its
    // avatar with Scene.AddNewAgent without creating any. False therefore keeps the stock behaviour (an NPC gets
    // no voice); true is an explicit opt-in, and a registered voice connector identity is admitted either way.
    public const bool DefaultAllowNpcVoice = false;

    private static bool m_allowNpcVoice = DefaultAllowNpcVoice;

    // [WebRtcVoice] AllowNpcVoice from a config section; a missing key reads as DefaultAllowNpcVoice.
    public static bool ReadAllowNpcVoice(IConfig pConfig)
    {
        return pConfig.GetBoolean("AllowNpcVoice", DefaultAllowNpcVoice);
    }

    // The AllowNpcVoice decision, pure and side-effect-free so it is unit-testable without a Scene (NpcProvisionGuardTests — the
    // IsProvisionableChannelType / HasRealViewerSession extraction pattern). Refuse iff the
    // presence is an NPC AND the operator has not opened the surface AND the id is not a
    // registered voice-connector identity. A non-NPC presence is never refused here.
    public static bool IsNpcProvisionRefused(bool pIsNpcPresence, bool pAllowNpcVoice, bool pIsConnectorIdentity)
    {
        return pIsNpcPresence && !pAllowNpcVoice && !pIsConnectorIdentity;
    }

    // The AllowNpcVoice guard runs
    // BEFORE any session is created. An NPC presence gets voice only when the operator
    // opened the surface ([WebRtcVoice] AllowNpcVoice=true) or the id is a registered
    // voice-connector identity (IVoiceConnectorRegistry on the scene; null registry ==
    // no connector identities). Real-avatar presences never enter the predicate's refuse
    // arm. The decision is the pure IsNpcProvisionRefused so it is unit-testable
    // (NpcProvisionGuardTests) — the same extraction pattern as IsProvisionableChannelType.
    private bool RefuseNpcProvision(UUID pUserID, UUID pSceneID)
    {
        Scene npcScene;
        lock (m_scenes)
            m_scenes.TryGetValue(pSceneID, out npcScene);
        ScenePresence npcSp = npcScene?.GetScenePresence(pUserID);
        if (npcSp is not null && IsNpcProvisionRefused(
                npcSp.PresenceType == PresenceType.Npc,
                m_allowNpcVoice,
                npcScene.RequestModuleInterface<IVoiceConnectorRegistry>()?.IsConnectorIdentity(pUserID) ?? false))
        {
            m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} region={RegionName} decision=refused-npc",
                LogHeader, pUserID, npcScene.Name);
            m_log.LogWarning("{LogHeader} refusing NPC voice provision from agent {AgentId} in region \"{RegionName}\" (AllowNpcVoice=false, not a connector identity)",
                LogHeader, pUserID, npcScene.Name);
            return true;
        }
        return false;
    }
}
