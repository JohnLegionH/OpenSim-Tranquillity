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

using System.Reflection;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice;

/// <summary>
/// S-CON-1 (Docs/voice/connector-build-plan.md): the voice-connector region module. In this slice
/// it ONLY loads the [VoiceConnector.&lt;name&gt;] policy records (brief Amendment 2 D1), logs the
/// outcome, and exposes the registry per scene via IVoiceConnectorRegistry — no NPC is created and
/// no voice session registered (that is S-CON-2). Non-shared: one instance (and one load, so the
/// per-record INFO repeats per region — small grids, accepted) per region.
/// </summary>
public class VoiceConnectorModule : INonSharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[CONNECTOR]";

    public const string DefaultNpcNameToken = "NPC";

    private VoiceConnectorRegistry m_registry;
    private string m_npcNameToken = DefaultNpcNameToken;
    private bool m_allowNpcVoice = false;   // read for visibility; ENFORCED in WebRtcVoiceServiceModule

    public string Name => "VoiceConnectorModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource pConfig)
    {
        IConfig moduleConfig = pConfig?.Configs["WebRtcVoice"];
        if (moduleConfig is null)
            return;   // no voice config at all -> stay inert (no registry registered)

        m_npcNameToken = moduleConfig.GetString("NpcNameToken", DefaultNpcNameToken);
        m_allowNpcVoice = moduleConfig.GetBoolean("AllowNpcVoice", false);

        VoiceConnectorLoadResult result = VoiceConnectorRegistry.LoadFrom(pConfig, m_npcNameToken);
        m_registry = result.Registry;

        foreach ((string sectionName, string reason) in result.Refusals)
            m_log.LogWarning("{LogHeader} record [{Section}] REFUSED: {Reason}", LogHeader, sectionName, reason);
        foreach (string sectionName in result.SkippedDisabled)
            m_log.LogDebug("{LogHeader} record [{Section}] disabled; skipped", LogHeader, sectionName);
        foreach (VoiceConnectorRecord r in m_registry.Snapshot())
            m_log.LogInformation("{LogHeader} loaded {Name} inject={MayInject} scope=estate",
                LogHeader, r.Name, r.MayInject);
    }

    public void AddRegion(Scene scene)
    {
        // The registry is registered even when empty: the AllowNpcVoice guard resolves it per
        // scene and an empty registry answers IsConnectorIdentity=false, exactly like a null one.
        if (m_registry is not null)
            scene.RegisterModuleInterface<IVoiceConnectorRegistry>(m_registry);
    }

    public void RemoveRegion(Scene scene)
    {
        if (m_registry is not null)
            scene.UnregisterModuleInterface<IVoiceConnectorRegistry>(m_registry);
    }

    public void RegionLoaded(Scene scene)
    {
        // S-CON-2: NPC creation and voice registration happen here, at region-ready. Not in
        // this slice.
    }

    public void Close()
    {
    }
}
