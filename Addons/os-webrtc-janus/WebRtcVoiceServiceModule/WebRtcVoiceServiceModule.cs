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

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.Base;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using Nini.Config;

using Microsoft.Extensions.Logging;

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

    // Scenes this shared module serves, for resolving a Scene from the pSceneID the provision
    // path carries — needed to read the provisioning client's SessionId (the generation token).
    private readonly Dictionary<UUID, Scene> m_scenes = new Dictionary<UUID, Scene>();

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
                    RegisterConsoleCommands();
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

            lock (m_scenes)
                m_scenes[scene.RegionInfo.RegionID] = scene;

            // Close-time voice teardown. OnClientClosed (NOT OnRemovePresence) because it fires
            // while the presence is still resolvable, so the handler can classify child-vs-root
            // and read the dying login's SessionId itself — no core EventManager change needed
            // (KnownDefects OnRemovePresence entry, external review 2026-08-22).
            scene.EventManager.OnClientClosed += Event_OnClientClosed;

            // Other candidate events, considered and not needed:
            // scene.EventManager.OnNewClient / OnClientLogin / OnNewPresence — provision-driven
            // scene.EventManager.OnClientMovement / OnSignificantClientMovement — feeder-driven
        }

    }

    // ISharedRegionModule.RemoveRegion
    public void RemoveRegion(Scene scene)
    {
        if (m_Enabled)
        {
            scene.EventManager.OnClientClosed -= Event_OnClientClosed;
            lock (m_scenes)
                m_scenes.Remove(scene.RegionInfo.RegionID);
            scene.UnregisterModuleInterface<IWebRtcVoiceService>(this);
        }
    }

    // ISharedRegionModule.RegionLoaded
    public void RegionLoaded(Scene scene)
    {
    }

    // =====================================================================
    // Close-time voice teardown. Replaces the never-wired Event_OnRemovePresence, which was
    // broken three ways as written: it enumerated a deferred registry query outside the lock,
    // mutated the registry inside that enumeration, and fire-and-forgot Shutdown unobserved.
    //
    // This handler runs INSIDE Scene.RemoveClient under the (global) removal lock, after
    // TriggerClientClosed fires and BEFORE the presence is removed — so the presence is
    // resolvable here and only here. Everything up to the capture must complete synchronously
    // and cheaply; the Janus cleanup must never run on this thread.
    private void Event_OnClientClosed(UUID pClientID, Scene pScene)
    {
        ScenePresence sp = pScene.GetScenePresence(pClientID);
        if (sp == null || sp.IsChildAgent)
            return;   // child teardown (border crossing / draw distance) — the agent's voice lives in its root region

        // The dying login's SessionId — the generation token captured at provision. Selection is
        // token-strict so a successor login's freshly-provisioned session can never be captured,
        // even if that provision races this close.
        UUID generation = sp.ControllingClient is not null ? sp.ControllingClient.SessionId : UUID.Zero;

        // Retry hook FIRST, capture SECOND — deliberate and load-bearing. CaptureSessionsForClose
        // parks its captures in ClosingSessions in the same locked statement, so reading the
        // closing-set AFTER capturing observes this handler's own just-parked work and logs a
        // false "prior teardown pending/failed (age 0s)" WARN on every clean logout (observed
        // live 2026-08-22). Snapshot-before-capture is stateless and race-free: genuinely stale
        // entries (parked by an earlier failed close) still surface with honest ages, the
        // just-captured set cannot appear, and no per-agent suppression window exists to
        // wrongly swallow a genuine second close of a different session generation.
        List<IVoiceViewerSession> toShutdown = new List<IVoiceViewerSession>();
        foreach ((IVoiceViewerSession s, long ageMs) in VoiceViewerSession.GetClosingSessions(pClientID))
        {
            m_log.LogWarning("{LogHeader} Event_OnClientClosed: prior teardown for {ClientId} still pending/failed (session {ViewerSessionId}, age {AgeSeconds:F0}s) - retrying",
                LogHeader, pClientID, s.ViewerSessionID, ageMs / 1000.0);
            toShutdown.Add(s);
        }

        foreach (IVoiceViewerSession s in VoiceViewerSession.CaptureSessionsForClose(
            pScene.RegionInfo.RegionID, pClientID, generation))
        {
            if (!toShutdown.Contains(s))
                toShutdown.Add(s);
        }

        if (toShutdown.Count == 0)
            return;

        m_log.LogDebug("{LogHeader} Event_OnClientClosed: captured {CapturedSessionCount} voice session(s) for {ClientId} in {SceneName}",
            LogHeader, toShutdown.Count, pClientID, pScene.Name);

        // Asynchronous, on the captured references only — never a re-query by avatar, so a
        // session provisioned after the capture is untouchable by this teardown.
        _ = Task.Run(() => ShutdownCapturedSessions(toShutdown, $"client close {pClientID}"));
    }

    // Observed asynchronous teardown of captured sessions. Per-session failure isolation: one
    // failure is logged and that session stays parked in ClosingSessions — discoverable and
    // retried by the provision/close hooks — while the rest proceed. Never remove-and-forget.
    private async Task ShutdownCapturedSessions(List<IVoiceViewerSession> pSessions, string pReason)
    {
        foreach (IVoiceViewerSession s in pSessions)
        {
            try
            {
                await s.Shutdown();
                VoiceViewerSession.CloseCompleted(s);
                m_log.LogDebug("{LogHeader} voice-session teardown complete ({TeardownReason}): session {ViewerSessionId} agent {AgentId}",
                    LogHeader, pReason, s.ViewerSessionID, s.AgentId);
            }
            catch (Exception e)
            {
                // Record the cause on the parked entry (console: "show voice closing") and log the
                // FULL exception - type, message, stack, inners - via the (Exception, message)
                // overload. A parked session with no recorded cause was the gap this closes.
                VoiceViewerSession.RecordCloseFailure(s, $"{e.GetType().Name}: {e.Message}");
                m_log.LogWarning(e, $"{LogHeader} voice-session teardown FAILED ({pReason}): session {s.ViewerSessionID} agent {s.AgentId} region {s.RegionId} - retained for retry");
            }
        }
    }

    // Read-only console observability for the closing-set: "show voice closing" lists every
    // parked session with agent, session id, age, and the last recorded failure. Closes the gap
    // that ClosingSessions was otherwise inspectable only by log-accounting inference.
    private void RegisterConsoleCommands()
    {
        if (MainConsole.Instance == null)
            return;   // unit tests / embedded hosts have no console
        MainConsole.Instance.Commands.AddCommand("Voice", false, "show voice closing",
            "show voice closing",
            "Show voice sessions parked in the closing-set (teardown in flight or failed), with age and last failure reason",
            HandleShowVoiceClosing);
    }

    private void HandleShowVoiceClosing(string module, string[] args)
    {
        List<(UUID AgentId, string SessionId, long AgeMs, string LastFailure)> snap =
            VoiceViewerSession.GetClosingSnapshot();
        if (snap.Count == 0)
        {
            MainConsole.Instance.Output("No voice sessions in closing state.");
            return;
        }
        MainConsole.Instance.Output("{0} voice session(s) in closing state:", snap.Count);
        foreach ((UUID agentId, string sessionId, long ageMs, string lastFailure) in snap)
            MainConsole.Instance.Output("  agent {0} session {1} age {2:F0}s failure: {3}",
                agentId, sessionId, ageMs / 1000.0, lastFailure ?? "(none - first attempt in flight)");
    }

    // Capture the generation token onto a freshly-created session: the provisioning client's
    // login SessionId, read from the live presence. Runs before AddViewerSession so the session
    // enters the registry fully formed. UUID.Zero (capture failed) makes the session sweepable
    // by ANY close for its agent — logged loudly because it should not happen in practice.
    private void CaptureGenerationToken(IVoiceViewerSession pSession, UUID pUserID, UUID pSceneID)
    {
        Scene scene;
        lock (m_scenes)
            m_scenes.TryGetValue(pSceneID, out scene);
        ScenePresence sp = scene?.GetScenePresence(pUserID);
        pSession.ClientSessionId = sp?.ControllingClient?.SessionId ?? UUID.Zero;
        if (pSession.ClientSessionId == UUID.Zero)
            m_log.LogWarning("{LogHeader} provision for {UserId}: could not capture client SessionId (scene {SceneState}, presence {PresenceState}) - session sweepable by any close for this agent",
                LogHeader, pUserID, scene is null ? "unresolved" : "resolved", sp is null ? "absent" : "present");
    }
    // =====================================================================
    // IWebRtcVoiceService

    // S-A2A-5 (a2a-assessment §4): the viewer_session binding. VoiceViewerSession.TryGetViewerSession
    // binds by id string only, so any agent presenting another agent's viewer_session id could
    // drive that session (re-provision it, feed it ICE, log it out). Every registered session
    // carries the agent it was created for (WebRtcJanusService.CreateViewerSession sets AgentId
    // from the cap-bound requester), so both cap sites resolve through here: found AND owned by
    // the requester, else exactly the caller's existing not-found path -- no new error shape, and
    // the session itself is untouched. A mismatch is a spoof attempt or a viewer bug, so it is
    // logged at WARN naming both agents; a plain miss is not (the call sites keep their ERROR).
    // UUID.Zero is never a wildcard. Static and logger-injected so it is unit-testable.
    public static bool TryGetViewerSessionFor(string pViewerSessionId, UUID pRequester, string pSite, ILogger pLog, out IVoiceViewerSession pSession)
    {
        pSession = null;
        if (!VoiceViewerSession.TryGetViewerSession(pViewerSessionId, out IVoiceViewerSession found) || found is null)
            return false;
        if (pRequester == UUID.Zero || found.AgentId != pRequester)
        {
            pLog?.LogWarning("{LogHeader} {Site}: viewer session {ViewerSessionId} is bound to agent {BoundAgentId} but was presented by agent {RequesterId} - treated as not found",
                LogHeader, pSite, pViewerSessionId, found.AgentId, pRequester);
            return false;
        }
        pSession = found;
        return true;
    }

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
        // Retry hook (remove-and-forget guard): if an earlier close-time teardown for this agent
        // FAILED, its sessions are parked in ClosingSessions. A new provision is exactly the
        // moment that residue would become a duplicate handle in the mixer, so re-drive the
        // teardown first — observed, asynchronous, on the parked references only.
        List<(IVoiceViewerSession Session, long AgeMs)> stale = VoiceViewerSession.GetClosingSessions(pUserID);
        if (stale.Count > 0)
        {
            foreach ((IVoiceViewerSession s, long ageMs) in stale)
                m_log.LogWarning("{LogHeader} provision for {UserId}: prior voice-session teardown still pending/failed (session {ViewerSessionId}, age {AgeSeconds:F0}s) - retrying",
                    LogHeader, pUserID, s.ViewerSessionID, ageMs / 1000.0);
            List<IVoiceViewerSession> retry = stale.Select(t => t.Session).ToList();
            _ = Task.Run(() => ShutdownCapturedSessions(retry, $"provision retry {pUserID}"));
        }

        OSDMap response = null;
        IVoiceViewerSession vSession = null;
        if (HasRealViewerSession(pRequest, out string viewerSessionId))
        {
            // request has a real viewer session. Use that to find the voice service -- it must be
            // this agent's own session (S-A2A-5); another agent's id resolves to nothing.
            if (!TryGetViewerSessionFor(viewerSessionId, pUserID, "ProvisionVoiceAccountRequest", m_log, out vSession))
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
                    CaptureGenerationToken(vSession, pUserID, pSceneID);
                    VoiceViewerSession.AddViewerSession(vSession);
                }
                else
                {
                    // TODO: check if this userId is making a new session (case that user is reconnecting)
                    vSession = m_nonSpatialVoiceService.CreateViewerSession(pRequest, pUserID, pSceneID);
                    CaptureGenerationToken(vSession, pUserID, pSceneID);
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
            // request has a viewer session. Use that to find the voice service -- it must be this
            // agent's own session (S-A2A-5); another agent's id resolves to nothing.
            if (TryGetViewerSessionFor(viewerSessionId, pUserID, "VoiceSignalingRequest", m_log, out vSession))
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
