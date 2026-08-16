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

using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.Base;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using Nini.Config;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice;

/// <summary>
/// Interface for the WebRtcVoiceService.
/// An instance of this is registered as the IWebRtcVoiceService for this region.
/// The function here is to direct the capability requests to the appropriate voice service.
/// For the moment, there are separate voice services for spatial and non-spatial voice
/// with the idea that a region could have a pre-region spatial voice service while
/// the grid could have a non-spatial voice service for group chat, etc.
/// Fancier configurations are possible.
/// </summary>
public class WebRtcVoiceServiceModule : ISharedRegionModule, IWebRtcVoiceService
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static string LogHeader = "[WEBRTC VOICE SERVICE MODULE]";

    private static bool m_Enabled = false;
    private IConfigSource m_Config;

    private IWebRtcVoiceService m_spatialVoiceService;
    private IWebRtcVoiceService m_nonSpatialVoiceService;

    // Phase-3a option (C): the Janus-side peer_ctl_batch sink, registered per scene as
    // IPeerCtlBatchSink so the region-module orchestrator can resolve it without referencing Janus.
    private bool m_visibilityEmitEnabled = false;
    private string m_janusAdminUri = string.Empty;
    private string m_janusAdminToken = string.Empty;
    private int m_adminTimeoutMs = 5000;
    private readonly Dictionary<Scene, JanusPeerCtlBatchSink> m_peerCtlSinks = new();

    // =====================================================================

    // ISharedRegionModule.Initialize
    // Get configuration and load the modules that will handle spatial and non-spatial voice.
    public void Initialise(IConfigSource pConfig)
    {
        m_Config = pConfig;
        IConfig moduleConfig = m_Config.Configs["WebRtcVoice"];

        if (moduleConfig is not null)
        {
            m_Enabled = moduleConfig.GetBoolean("Enabled", false);
            if (m_Enabled)
            {
                // Get the DLLs for the two voice services
                string spatialDllName = moduleConfig.GetString("SpatialVoiceService", string.Empty);
                string nonSpatialDllName = moduleConfig.GetString("NonSpatialVoiceService", string.Empty);
                if (string.IsNullOrEmpty(spatialDllName) && string.IsNullOrEmpty(nonSpatialDllName))
                {
                    m_log.LogError($"{LogHeader} No VoiceService specified in configuration");
                    m_Enabled = false;
                    return;
                }

                // Default non-spatial to spatial if not specified
                if (string.IsNullOrEmpty(nonSpatialDllName))
                {
                    m_log.LogDebug($"{LogHeader} nonSpatialDllName not specified. Defaulting to spatialDllName");
                    nonSpatialDllName = spatialDllName;
                }

                // Load the two voice services
                m_log.LogDebug($"{LogHeader} Loading SpatialVoiceService from {spatialDllName}");
                m_spatialVoiceService = ServerUtils.LoadPlugin<IWebRtcVoiceService>(spatialDllName, [m_Config]);
                if (m_spatialVoiceService is null)
                {
                    m_log.LogError($"{LogHeader} Could not load SpatialVoiceService from {spatialDllName}, module disabled");
                    m_Enabled = false;
                    return;
                }

                m_log.LogDebug($"{LogHeader} Loading NonSpatialVoiceService from {nonSpatialDllName}");
                if (spatialDllName != nonSpatialDllName)
                {
                    m_nonSpatialVoiceService = ServerUtils.LoadPlugin<IWebRtcVoiceService>(nonSpatialDllName, [ m_Config ]);
                    if (m_nonSpatialVoiceService is null)
                    {
                        m_log.LogError("{LogHeader} Could not load NonSpatialVoiceService from {nonSpatialDllName}");
                        m_Enabled = false;
                    }
                }

                if (m_Enabled)
                {
                    m_log.LogInformation($"{LogHeader} WebRtcVoiceService enabled");

                    // Phase-3a: peer_ctl_batch emission config. VisibilityEmitEnabled gates whether
                    // a sink is registered at all (default off); the Janus admin endpoint + secret
                    // come from [JanusWebRtcVoice], the same section the Janus service reads.
                    m_visibilityEmitEnabled = moduleConfig.GetBoolean("VisibilityEmitEnabled", false);
                    IConfig janusCfg = m_Config.Configs["JanusWebRtcVoice"];
                    if (janusCfg is not null)
                    {
                        m_janusAdminUri = janusCfg.GetString("JanusGatewayAdminURI", string.Empty);
                        m_janusAdminToken = janusCfg.GetString("AdminAPIToken", string.Empty);
                        m_adminTimeoutMs = janusCfg.GetInt("AdminTimeoutMs", 5000);
                    }
                }
            }
        }
    }

    // ISharedRegionModule.PostInitialize
    public void PostInitialise()
    {
    }

    // ISharedRegionModule.Close
    public void Close()
    {
        lock (m_peerCtlSinks)
        {
            foreach (JanusPeerCtlBatchSink sink in m_peerCtlSinks.Values)
                sink.Dispose();
            m_peerCtlSinks.Clear();
        }
    }

    // ISharedRegionModule.ReplaceableInterface
    public Type ReplaceableInterface
    {
        get { return null; }
    }

    // ISharedRegionModule.Name
    public string Name
    {
        get { return "WebRtcVoiceServiceModule"; }
    }

    // ISharedRegionModule.AddRegion
    public void AddRegion(Scene scene)
    {
        if (m_Enabled)
        {
            m_log.LogDebug($"{LogHeader} Adding WebRtcVoiceService to region {scene.Name}");
            scene.RegisterModuleInterface<IWebRtcVoiceService>(this);

            // Phase-3a: register the peer_ctl_batch sink for this scene (before RegionLoaded, where
            // the orchestrator resolves it). Only when emission is on AND the admin endpoint/secret
            // are configured — otherwise the region-module sender logs "no sink" and runs matrix-only.
            if (m_visibilityEmitEnabled)
            {
                if (!string.IsNullOrEmpty(m_janusAdminUri) && !string.IsNullOrEmpty(m_janusAdminToken))
                {
                    var sink = new JanusPeerCtlBatchSink(m_janusAdminUri, m_janusAdminToken,
                        TimeSpan.FromMilliseconds(m_adminTimeoutMs),
                        scene.RegionInfo.RegionID, scene.RegionInfo.RegionName);
                    scene.RegisterModuleInterface<IPeerCtlBatchSink>(sink);
                    lock (m_peerCtlSinks)
                        m_peerCtlSinks[scene] = sink;
                    m_log.Info($"{LogHeader} registered peer_ctl_batch sink for {scene.RegionInfo.RegionName}");
                }
                else
                {
                    m_log.Warn($"{LogHeader} VisibilityEmitEnabled but [JanusWebRtcVoice] JanusGatewayAdminURI/AdminAPIToken missing; no peer_ctl_batch sink registered");
                }
            }

            // TODO: figure out what events we care about
            // When new client (child or root) is added to scene, before OnClientLogin
            // scene.EventManager.OnNewClient         += Event_OnNewClient;
            // When client is added on login.
            // scene.EventManager.OnClientLogin       += Event_OnClientLogin;
            // New presence is added to scene. Child, root, and NPC. See Scene.AddNewAgent()
            // scene.EventManager.OnNewPresence       += Event_OnNewPresence;
            // scene.EventManager.OnRemovePresence    += Event_OnRemovePresence;
            // update to client position (either this or 'significant')
            // scene.EventManager.OnClientMovement    += Event_OnClientMovement;
            // "significant" update to client position
            // scene.EventManager.OnSignificantClientMovement += Event_OnSignificantClientMovement;
        }

    }

    // ISharedRegionModule.RemoveRegion
    public void RemoveRegion(Scene scene)
    {
        if (m_Enabled)
        {
            scene.UnregisterModuleInterface<IWebRtcVoiceService>(this);

            lock (m_peerCtlSinks)
            {
                if (m_peerCtlSinks.TryGetValue(scene, out JanusPeerCtlBatchSink sink))
                {
                    scene.UnregisterModuleInterface<IPeerCtlBatchSink>(sink);
                    sink.Dispose();
                    m_peerCtlSinks.Remove(scene);
                }
            }
        }
    }

    // ISharedRegionModule.RegionLoaded
    public void RegionLoaded(Scene scene)
    {
    }

    // =====================================================================
    // Thought about doing this but currently relying on the voice service
    //     event ("hangup") to remove the viewer session.
    private void Event_OnRemovePresence(UUID pAgentID)
    {
        // When a presence is removed, remove the viewer sessions for that agent
        IEnumerable<KeyValuePair<string, IVoiceViewerSession>> vSessions;
        if (VoiceViewerSession.TryGetViewerSessionByAgentId(pAgentID, out vSessions))
        {
            foreach(KeyValuePair<string, IVoiceViewerSession> v in vSessions)
            {
                m_log.LogDebug("{0} Event_OnRemovePresence: removing viewer session {1}", LogHeader, v.Key);
                VoiceViewerSession.RemoveViewerSession(v.Key);
                v.Value.Shutdown();
            }
        }
    }
    // =====================================================================
    // IWebRtcVoiceService

    // A viewer_session that is absent, empty, or the zero UUID indicates an INITIAL
    // provision request (no session created yet), NOT a lookup of an existing session.
    // NOTE: OSDMap.TryGetString returns true for a *present* OSDUUID(UUID.Zero), yielding
    // "00000000-0000-0000-0000-000000000000" -- so a present-but-zero value must be
    // treated as "no session yet", otherwise the first provision is routed to the lookup
    // branch and fails with "viewer session 00000000-... not found". Registered session
    // ids are UUID strings (see VoiceViewerSession ctor), so parse and reject UUID.Zero.
    public static bool HasRealViewerSession(OSDMap pRequest, out string viewerSessionId)
    {
        viewerSessionId = null;
        if (!pRequest.TryGetString("viewer_session", out string vs))
            return false;                                      // absent
        if (string.IsNullOrEmpty(vs))
            return false;                                      // empty
        if (UUID.TryParse(vs, out UUID vsid) && vsid == UUID.Zero)
            return false;                                      // zero UUID
        viewerSessionId = vs;
        return true;
    }

    // IWebRtcVoiceService.ProvisionVoiceAccountRequest
        public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        OSDMap response = null;
        IVoiceViewerSession vSession = null;
        if (HasRealViewerSession(pRequest, out string viewerSessionId))
        {
            // request has a real viewer session. Use that to find the voice service
            if (!VoiceViewerSession.TryGetViewerSession(viewerSessionId, out vSession))
            {
                m_log.LogError($"{LogHeader} ProvisionVoiceAccountRequest: viewer session {viewerSessionId} not found");
            }
        }
        else
        {
            // no (usable) viewer session -> this is an initial request
            if (pRequest.TryGetString("channel_type", out string channelType))
            {
                if (channelType == "local")
                {
                    // TODO: check if this userId is making a new session (case that user is reconnecting)
                    vSession = m_spatialVoiceService.CreateViewerSession(pRequest, pUserID, pSceneID);
                    VoiceViewerSession.AddViewerSession(vSession);
                }
                else
                {
                    // TODO: check if this userId is making a new session (case that user is reconnecting)
                    vSession = m_nonSpatialVoiceService.CreateViewerSession(pRequest, pUserID, pSceneID);
                    VoiceViewerSession.AddViewerSession(vSession);
                }
            }
            else
            {
                m_log.LogError($"{LogHeader} ProvisionVoiceAccountRequest: no channel_type in request");
            }
        }
        if (vSession is not null)
        {
                response = vSession.VoiceService.ProvisionVoiceAccountRequest(vSession, pRequest, pUserID, pSceneID);
        }
        return response;
    }

    // IWebRtcVoiceService.VoiceSignalingRequest
        public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        OSDMap response = null;
        IVoiceViewerSession vSession = null;
        if (pRequest.TryGetString("viewer_session", out string viewerSessionId))
        {
            // request has a viewer session. Use that to find the voice service
            if (VoiceViewerSession.TryGetViewerSession(viewerSessionId, out vSession))
            {
                    response = vSession.VoiceService.VoiceSignalingRequest(vSession, pRequest, pUserID, pSceneID);
            }
            else
            {
                m_log.LogError("{0} VoiceSignalingRequest: viewer session {1} not found", LogHeader, viewerSessionId);
            }
        }   
        else
        {
            m_log.LogError("{0} VoiceSignalingRequest: no viewer_session in request", LogHeader);
        }
        return response;
    }

    // This module should never be called with this signature
        public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        throw new NotImplementedException();
    }

    // This module should never be called with this signature
        public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        throw new NotImplementedException();
    }

    public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        throw new NotImplementedException();
    }
}
