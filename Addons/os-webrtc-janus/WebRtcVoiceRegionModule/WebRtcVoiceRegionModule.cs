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

using System.Collections.Generic;
using System.Net;
using System.Reflection;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Caps = OpenSim.Framework.Capabilities.Caps;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using Nini.Config;
using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

/// <summary>
/// This module provides the WebRTC voice interface for viewer clients..
/// 
/// In particular, it provides the following capabilities:
///      ProvisionVoiceAccountRequest, VoiceSignalingRequest and limited ChatSessionRequest
/// which are the user interface to the voice service.
/// 
/// Initially, when the user connects to the region, the region feature "VoiceServiceType" is
/// set to "webrtc" and the capabilities that support voice are enabled.
/// The capabilities then pass the user request information to the IWebRtcVoiceService interface
/// that has been registered for the reqion.
/// </summary>
public class WebRtcVoiceRegionModule : ISharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string logHeader = "[REGION WEBRTC VOICE]";

    private static byte[] llsdUndefAnswerBytes = Util.UTF8.GetBytes("<llsd><undef /></llsd>"); 
    private bool _MessageDetails = false;

    // Control info
    private static bool m_Enabled = false;

    private IConfig m_Config;

    // Comma-separated STUN URIs advertised to viewers as SimulatorFeatures
    // "stun-servers"; empty => the key is not emitted.
    private string m_StunServers = string.Empty;

    // Phase-3a per-listener visibility feeder. Off by default (no Janus sender consumes it yet);
    // enable for the in-world DEBUG smoke check. One service per region.
    private bool m_VisibilityFeederEnabled = false;
    private int m_VisibilityTickMs = 250;
    // Emit the matrix to the mixer (peer_ctl_batch) — separate from running the matrix. Default
    // FALSE: the feeder can run matrix-only for diagnostics without emitting.
    private bool m_VisibilityEmitEnabled = false;
    // [JanusWebRtcVoice] admin endpoint/secret for the peer_ctl_batch sink this module now OWNS
    // (option c-new): the sink is constructed here and handed directly to the feeder's sender, so
    // sink and sender share one ALC and IPeerCtlBatchSink identity matches.
    private string m_JanusAdminUri = string.Empty;
    private string m_JanusAdminToken = string.Empty;
    private int m_AdminTimeoutMs = 5000;
    private int m_VisibilityRoomSendConcurrency = JanusPeerCtlBatchSink.DefaultRoomSendConcurrency;
    private readonly Dictionary<Scene, VoiceVisibilityService> m_visibilityServices = new();

    // Avatar-to-avatar invitation registry (Docs/voice/a2a-build-plan.md §1.3, S-A2A-1). One per module
    // instance = per region-server process, shared by every scene this shared module serves; thread-safe
    // because ChatSessionRequest arrives on cap HTTP threads. Cross-instance A2A is out of scope (§1.7).
    private readonly A2ASessionRegistry m_a2aSessions = new();

    // ISharedRegionModule.Initialize
    public void Initialise(IConfigSource config)
    {
        m_Config = config.Configs["WebRtcVoice"];
        if (m_Config is not null)
        {
            m_Enabled = m_Config.GetBoolean("Enabled", false);
            if (m_Enabled)
            {
                _MessageDetails = m_Config.GetBoolean("MessageDetails", false);
                m_StunServers = m_Config.GetString("StunServers", string.Empty);
                m_VisibilityFeederEnabled = m_Config.GetBoolean("VisibilityFeederEnabled", false);
                m_VisibilityTickMs = m_Config.GetInt("VisibilityTickMs", 250);
                m_VisibilityEmitEnabled = m_Config.GetBoolean("VisibilityEmitEnabled", false);
                // S3b: rooms addressed concurrently within one send. A latency budget, not a
                // throughput knob � see JanusPeerCtlBatchSink.DefaultRoomSendConcurrency.
                m_VisibilityRoomSendConcurrency = m_Config.GetInt("VisibilityRoomSendConcurrency",
                    JanusPeerCtlBatchSink.DefaultRoomSendConcurrency);

                // Sink endpoint from [JanusWebRtcVoice] (the same section the Janus service reads).
                IConfig janusCfg = config.Configs["JanusWebRtcVoice"];
                if (janusCfg is not null)
                {
                    m_JanusAdminUri = janusCfg.GetString("JanusGatewayAdminURI", string.Empty);
                    m_JanusAdminToken = janusCfg.GetString("AdminAPIToken", string.Empty);
                    m_AdminTimeoutMs = janusCfg.GetInt("AdminTimeoutMs", 5000);
                }

                // Console surface for the moderation store. Registered HERE, once, exactly where
                // WebRtcVoiceServiceModule registers "show voice closing" - this module registered
                // no console commands at all before now. Registration is unconditional on the
                // feeder flag on purpose: with the feeder off there IS no moderation state, and the
                // commands say so, which is a better answer to an operator than an unknown command.
                new VoiceModerationCommands(SnapshotVisibilityServices).Register();

                m_log.LogInformation($"{logHeader}: enabled");
            }
        }
    }

    // A copy of the per-region service map for the console commands. A copy, not the live
    // dictionary: a console handler resolves names, enumerates parcels and writes to a terminal,
    // and none of that may happen while holding the lock that the CAP handler and RegionLoaded
    // contend for.
    private List<KeyValuePair<Scene, VoiceVisibilityService>> SnapshotVisibilityServices()
    {
        lock (m_visibilityServices)
            return new List<KeyValuePair<Scene, VoiceVisibilityService>>(m_visibilityServices);
    }

    // ISharedRegionModule.PostInitialize
    public void PostInitialise()
    {
    }

    // Scenes this shared module serves, for callee resolution on THIS instance (S-A2A-2; the group
    // module's m_sceneList / GetActiveClient pattern). Cross-instance A2A is out of scope (plan §1.7).
    private readonly List<Scene> m_scenes = new();

    // ISharedRegionModule.AddRegion
    public void AddRegion(Scene scene)
    {
        lock (m_scenes)
            if (!m_scenes.Contains(scene))
                m_scenes.Add(scene);
    }

    // ISharedRegionModule.RemoveRegion
    public void RemoveRegion(Scene scene)
    {
        lock (m_scenes)
            m_scenes.Remove(scene);

        lock (m_visibilityServices)
        {
            if (m_visibilityServices.TryGetValue(scene, out VoiceVisibilityService svc))
            {
                svc.Stop();
                m_visibilityServices.Remove(scene);
            }
        }
    }

    // ISharedRegionModule.RegionLoaded
    public void RegionLoaded(Scene scene)
    {
        if (m_Enabled)
        {
            scene.EventManager.OnRegisterCaps += delegate (UUID agentID, Caps caps)
            {
                OnRegisterCaps(scene, agentID, caps);
            };
            // S-A2A-3 (reported deviation): a client that drops without a logout provision (crash, kill)
            // would otherwise leave its Active A2A record until the idle backstop, suppressing a re-ring
            // between the same pair. Treat the close as that party gone from every record it is in;
            // the record is removed only when the other party is gone too (both-logout semantics), and a
            // later admitted provision re-marks the party present, so this is reversible.
            scene.EventManager.OnClientClosed += delegate (UUID clientID, Scene s)
            {
                foreach (UUID gone in m_a2aSessions.MarkGone(clientID, null))
                    m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} region={RegionName} decision=removed-client-closed",
                        logHeader, clientID, gone, s?.Name ?? scene.Name);
            };

            ISimulatorFeaturesModule simFeatures = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            simFeatures?.AddFeature("VoiceServerType", OSD.FromString("webrtc"));

            // Advertise STUN servers to viewers so their WebRTC ICE config is non-empty.
            // The viewer's OpenSim path reads SimulatorFeatures["stun-servers"] as a
            // comma-separated string of full ICE URIs (llviewerregion.cpp). Absent config
            // => omit the key. Stock viewers REQUIRE a non-empty valid entry or
            // CreatePeerConnection fails "ICE server parsing failed: Empty uri".
            if (!string.IsNullOrWhiteSpace(m_StunServers))
            {
                simFeatures?.AddFeature("stun-servers", OSD.FromString(m_StunServers));
            }

            // Phase-3a: start the per-listener visibility feeder for this region (opt-in).
            if (m_VisibilityFeederEnabled)
            {
                // Build the sink HERE and hand it directly to the service — same ALC, no scene
                // registry (option c-new). Null when emission is off or admin config is missing;
                // the service/sender then runs matrix-only and logs once.
                IPeerCtlBatchSink sink = BuildPeerCtlSinkOrNull(scene);
                VoiceVisibilityService svc = new VoiceVisibilityService(scene, m_VisibilityTickMs, m_VisibilityEmitEnabled, sink,
                    TimeSpan.FromMilliseconds(m_AdminTimeoutMs));
                svc.Start();
                lock (m_visibilityServices)
                    m_visibilityServices[scene] = svc;
            }
        }
    }

    // Construct the Janus peer_ctl_batch sink for this scene, or null to run matrix-only.
    // Null when emission is disabled (no log — intentional) or when [JanusWebRtcVoice] admin
    // endpoint/secret is absent (one loud WARN — the config saved us before, keep it loud).
    private IPeerCtlBatchSink BuildPeerCtlSinkOrNull(Scene scene)
    {
        if (!m_VisibilityEmitEnabled)
            return null;

        if (string.IsNullOrEmpty(m_JanusAdminUri) || string.IsNullOrEmpty(m_JanusAdminToken))
        {
            m_log.LogWarning($"{logHeader}[Visibility]: VisibilityEmitEnabled but [JanusWebRtcVoice] " +
                $"JanusGatewayAdminURI/AdminAPIToken missing; region \"{scene.RegionInfo.RegionName}\" runs matrix-only (no emission)");
            return null;
        }

        // The sink logs its fallback room number once at Info, and the service hands it the room
        // resolver in the service constructor immediately below this call (S3b).
        return new JanusPeerCtlBatchSink(m_JanusAdminUri, m_JanusAdminToken,
            TimeSpan.FromMilliseconds(m_AdminTimeoutMs), scene.RegionInfo.RegionID, scene.RegionInfo.RegionName,
            m_VisibilityRoomSendConcurrency);
    }

    // ISharedRegionModule.Close
    public void Close()
    {
        lock (m_visibilityServices)
        {
            foreach (VoiceVisibilityService svc in m_visibilityServices.Values)
                svc.Stop();
            m_visibilityServices.Clear();
        }
    }

    // ISharedRegionModule.Name
    public string Name
    {
        get { return "RegionVoiceModule"; }
    }

    // ISharedRegionModule.ReplaceableInterface
    public Type ReplaceableInterface
    {
        get { return null; }
    }

    // <summary>
    // OnRegisterCaps is invoked via the scene.EventManager
    // everytime OpenSim hands out capabilities to a client
    // (login, region crossing). We contribute three capabilities to
    // the set of capabilities handed back to the client:
    // ProvisionVoiceAccountRequest, VoiceSignalingRequest and limited ChatSessionRequest
    //
    // ProvisionVoiceAccountRequest allows the client to obtain
    // voice communication information the the avater.
    //
    // VoiceSignalingRequest: Used for trickling ICE candidates.
    //
    // ChatSessionRequest
    //
    // Note that OnRegisterCaps is called here via a closure
    // delegate containing the scene of the respective region (see
    // Initialise()).
    // </summary>
    public void OnRegisterCaps(Scene scene, UUID agentID, Caps caps)
    {
        m_log.LogDebug(
            $"{logHeader}: OnRegisterCaps called with agentID {agentID} caps {caps} in scene {scene.Name}");

        caps.RegisterSimpleHandler("ProvisionVoiceAccountRequest",
                new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                {
                    ProvisionVoiceAccountRequest(httpRequest, httpResponse, agentID, scene);
                }));

        caps.RegisterSimpleHandler("VoiceSignalingRequest",
                new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                {
                    VoiceSignalingRequest(httpRequest, httpResponse, agentID, scene);
                }));

        caps.RegisterSimpleHandler("ChatSessionRequest",
                new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                {
                    ChatSessionRequest(httpRequest, httpResponse, agentID, scene);
                }));

        // Parcel voice moderation (parity with SL viewer 26.1). RegisterSimpleHandler both
        // ADVERTISES the capability in the seed set (the viewer's getCapability resolves it) and
        // routes the POST — identical to the three above, so the viewer will actually send here.
        caps.RegisterSimpleHandler("SpatialVoiceModerationRequest",
                new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                {
                    SpatialVoiceModerationRequest(httpRequest, httpResponse, agentID, scene);
                }));
    }

    /// <summary>
    /// Handles the viewer's SpatialVoiceModerationRequest CAP (parity with SL viewer 26.1 parcel
    /// voice moderation). Slice 1, first half: authorise and record sticky per-parcel moderation
    /// state in memory. NOTHING consumes the store yet — the matrix enforcement rule is a separate
    /// commit. The body shape is fixed by the viewer (llnearbyvoicemoderation.cpp):
    ///   individual: { "operand": "mute" | "unmute", "agent_id": &lt;uuid&gt; }
    ///   everyone:   { "operand": "mute_all" | "unmute_all" }
    /// The body carries NO parcel id, so scope is resolved from the requester's position — this is
    /// what makes moderation parcel-bound rather than viewer-declared.
    /// </summary>
    public void SpatialVoiceModerationRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        if (request.HttpMethod != "POST")
        {
            m_log.LogDebug($"{logHeader}[Moderation]: not a POST request. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        OSDMap map = BodyToMap(request, "SpatialVoiceModerationRequest");
        if (map is null)
        {
            m_log.LogError($"{logHeader}[Moderation]: no request data. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        // (2) Operand — conform to the viewer's shape exactly; reject anything else. mute/unmute
        // carry an agent_id; mute_all/unmute_all do not. No other fields are read.
        if (!map.TryGetString("operand", out string operand))
        {
            m_log.LogWarning($"{logHeader}[Moderation]: missing 'operand'. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }
        bool everyoneOp   = operand == "mute_all" || operand == "unmute_all";
        bool individualOp = operand == "mute" || operand == "unmute";
        if (!everyoneOp && !individualOp)
        {
            m_log.LogWarning($"{logHeader}[Moderation]: unknown operand \"{operand}\". Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }
        UUID targetAgent = UUID.Zero;
        if (individualOp)
        {
            if (!map.ContainsKey("agent_id") || (targetAgent = map["agent_id"].AsUUID()).IsZero())
            {
                m_log.LogWarning($"{logHeader}[Moderation]: operand \"{operand}\" without a valid agent_id. Agent={agentID}");
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }
        }

        // (3) Resolve the target parcel from the REQUESTER's position. The body names no parcel,
        // so this is the only trustworthy scope and it pins mute_all to the moderator's own parcel
        // rather than a viewer-declared region.
        if (scene.LandChannel is null || !scene.TryGetScenePresence(agentID, out ScenePresence sp))
        {
            m_log.LogWarning($"{logHeader}[Moderation]: cannot resolve requester presence/land in region \"{scene.Name}\". Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }
        ILandObject parcel = scene.LandChannel.GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
        LandData land = parcel?.LandData;
        if (land is null)
        {
            m_log.LogWarning($"{logHeader}[Moderation]: could not resolve a parcel at the requester's position. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        // (4) Authorise server-side; never trust the viewer's own isNearbyChatModerator() gate.
        // Compose owner / estate-manager / group-ModerateChat, the pieces the ban path uses.
        if (!MayModerateVoice(scene, land, agentID))
        {
            m_log.LogWarning($"{logHeader}[Moderation]: DENIED {operand} on parcel {land.GlobalID} (\"{land.Name}\") for {agentID}: not owner, estate manager, or group moderator");
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // (5) The store lives on the per-region visibility service. The matrix is the single
        // enforcement point, so if the feeder is disabled there is no way to enforce a mute —
        // refuse loudly rather than silently accept an unenforceable one.
        VoiceVisibilityService svc;
        lock (m_visibilityServices)
            m_visibilityServices.TryGetValue(scene, out svc);
        if (svc is null)
        {
            m_log.LogWarning($"{logHeader}[Moderation]: {operand} authorised on parcel {land.GlobalID} but the visibility feeder is disabled in region \"{scene.Name}\"; cannot enforce, not recorded.");
            response.StatusCode = (int)HttpStatusCode.NotImplemented;
            return;
        }

        switch (operand)
        {
            case "mute_all":   svc.Moderation.SetMuteEveryone(land.GlobalID, true);  break;
            case "unmute_all": svc.Moderation.SetMuteEveryone(land.GlobalID, false); break;
            case "mute":       svc.Moderation.MuteAgent(land.GlobalID, targetAgent);   break;
            case "unmute":     svc.Moderation.UnmuteAgent(land.GlobalID, targetAgent); break;
        }

        // (6) Diagnosable from day one — accepted op with parcel GlobalID, operand, requester.
        if (individualOp)
            m_log.LogInformation($"{logHeader}[Moderation]: {operand} agent {targetAgent} on parcel {land.GlobalID} (\"{land.Name}\") by {agentID}");
        else
            m_log.LogInformation($"{logHeader}[Moderation]: {operand} on parcel {land.GlobalID} (\"{land.Name}\") by {agentID}");

        response.RawBuffer = llsdUndefAnswerBytes;
        response.StatusCode = (int)HttpStatusCode.OK;
    }

    // SL's three authorisation cases: land owner, estate manager/owner, or a member with
    // GroupPowers.ModerateChat on a group-owned parcel. The composition now lives in the shared
    // VoiceModerationAuth so the matrix's moderator-exemption uses exactly the same rule; behaviour
    // is unchanged. Server-side only — the viewer's own gate is UI and spoofable.
    private bool MayModerateVoice(Scene scene, LandData land, UUID agentID)
        => VoiceModerationAuth.MayModerate(scene, land, agentID);

    /// <summary>
    /// Callback for a client request for Voice Account Details
    /// </summary>
    /// <param name="scene">current scene object of the client</param>
    /// <param name="request"></param>
    /// <param name="path"></param>
    /// <param name="param"></param>
    /// <param name="agentID"></param>
    /// <param name="caps"></param>
    /// <returns></returns>
    /// <summary>Fail-closed channel-type admission for voice provisioning (ledger O-29). Voice
    /// authorization -- parcel/estate ban &amp; restrict -- is only implemented for the "local"
    /// channel; a request with any other channel_type, or none, must be REFUSED before it reaches
    /// the voice service, or it would provision past those checks. "multiagent" is RESERVED for the
    /// future avatar-to-avatar feature, which must bring its OWN authorization; this is a deliberate
    /// deny, not a stub to remove. Returns true iff channel_type is present and exactly "local";
    /// <paramref name="channelType"/> is the value seen (empty string when absent) for the caller's
    /// refusal log. Pure and side-effect-free so it is unit-testable (ProvisionChannelTypeGuardTests).</summary>
    public static bool IsProvisionableChannelType(OSDMap map, out string channelType)
    {
        channelType = map.TryGetString("channel_type", out string ct) ? ct : string.Empty;
        return channelType == "local";
    }

    public void ProvisionVoiceAccountRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        // Get the voice service. If it doesn't exist, return an error.
        IWebRtcVoiceService voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
        if (voiceService is null)
        {
            m_log.LogError($"{logHeader}[ProvisionVoice]: voice service not loaded");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if(request.HttpMethod != "POST")
        {
            m_log.LogDebug($"[{logHeader}][ProvisionVoice]: Not a POST request. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        // Deserialize the request. Convert the LLSDXml to OSD for our use
        OSDMap map = BodyToMap(request, "ProvisionVoiceAccountRequest");
        if (map is null)
        {
            m_log.LogError($"{logHeader}[ProvisionVoice]: No request data found. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        // Make sure the request is for WebRtc voice
        if (map.TryGetValue("voice_server_type", out OSD vstosd))
        {
            if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
            {
                m_log.LogWarning($"{logHeader}[ProvisionVoice]: voice_server_type is not 'webrtc'. Request: {map}");
                response.RawBuffer = llsdUndefAnswerBytes;
                response.StatusCode = (int)HttpStatusCode.OK;
                return;
            }
        }

        if (_MessageDetails) m_log.LogDebug($"{logHeader}[ProvisionVoice]: request: {map}");

        // FAIL CLOSED (ledger O-29): voice authorization -- the parcel/estate ban & restrict
        // checks below -- is only implemented for the "local" channel, and those checks are nested
        // under `channel_type == "local"`. A request whose channel_type is ANYTHING ELSE, or is
        // missing, would skip every one of them and provision voice past a parcel or estate ban.
        // Refuse it here, BEFORE room selection and BEFORE any Janus session creation, with the
        // SAME response an unauthorized local request gets (llsd <undef/> + 403 Forbidden; see the
        // ban/restrict branch below). "multiagent" is RESERVED for the future avatar-to-avatar
        // feature, which must bring its OWN authorization when it is built -- this deny is
        // DELIBERATE, not a stub to remove.
        // S-A2A-3: admission. "local" -> IsProvisionableChannelType (unchanged O-29 predicate) -> the
        // parcel/estate checks below. "multiagent" -> the invitation registry: the body's `channel`
        // (NOT channel_id, U-13) names a live session, the agent is a named party, `credentials` equals the
        // session token; else 403. A teardown body ({logout, viewer_session}, no channel_type) is routed to
        // the voice service by viewer_session -- the O-29 guard had been refusing every logout provision
        // since it shipped (live logs: 'refusing provision with channel_type ""' at each teardown), leaving
        // mixer teardown to the close-capture path. Everything else stays refused exactly as O-29 left it.
        ProvisionAdmission admission = A2AProvisionAdmission.Decide(map, agentID, m_a2aSessions);
        string channelType = admission.ChannelType;
        string a2aVs = map.TryGetString("viewer_session", out string vsRaw) && !string.IsNullOrEmpty(vsRaw) ? vsRaw : "-";

        // Permanent instrument (Docs/voice/a2a-build-plan.md §1.8): one greppable DEBUG line per provision
        // naming the fields the A2A authorization decides on; the token itself is never logged.
        m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} region={RegionName} channel_type=\"{ChannelType}\" channel={Channel} credentials={Credentials} viewer_session={ViewerSession} logout={Logout} decision={Decision}",
            logHeader, agentID, scene.Name, channelType, admission.Channel,
            map.TryGetString("credentials", out string a2aCreds) && !string.IsNullOrEmpty(a2aCreds) ? "present" : "absent",
            a2aVs, admission.Kind == ProvisionKind.Logout, admission.Decision);

        if (!admission.Admitted)
        {
            m_log.LogWarning($"{logHeader}[ProvisionVoice]: refusing provision with channel_type \"{channelType}\" ({admission.Decision}) from agent {agentID} in region \"{scene.Name}\"");
            response.RawBuffer = llsdUndefAnswerBytes;
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // channel_type is "local": the parcel/estate authorization below is UNCHANGED. A multiagent or
        // logout request skips it (its authorization is the registry / the viewer session).
        if (admission.Kind == ProvisionKind.Local)
        {
            //do fully not trust viewers voice parcel requests
            if (channelType == "local")
            {
                if (!scene.RegionInfo.EstateSettings.AllowVoice)
                {
                    m_log.LogDebug($"{logHeader}[ProvisionVoice]:region \"{scene.Name}\": voice not enabled in estate settings");
                    response.RawBuffer = llsdUndefAnswerBytes;
                    response.StatusCode = (int)HttpStatusCode.NotImplemented;
                    return;
                }
                if (scene.LandChannel == null)
                {
                    m_log.LogError($"{logHeader}[ProvisionVoice] region \"{scene.Name}\" land data not yet available");
                    response.RawBuffer = llsdUndefAnswerBytes;
                    response.StatusCode = (int)HttpStatusCode.NotImplemented;
                    return;
                }

                if(!scene.TryGetScenePresence(agentID, out ScenePresence sp))
                {
                    m_log.LogDebug($"{logHeader}[ProvisionVoice]:avatar not found");
                    response.RawBuffer = llsdUndefAnswerBytes;
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                if(map.TryGetInt("parcel_local_id", out int parcelID))
                {
                    ILandObject parcel = scene.LandChannel.GetLandObject(parcelID);
                    if (parcel == null)
                    {
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }
                    
                    LandData land = parcel.LandData;
                    if (land == null)
                    {
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }

                    if (!scene.RegionInfo.EstateSettings.TaxFree && (land.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                    {
                        m_log.LogDebug($"{logHeader}[ProvisionVoice]:parcel voice not allowed");
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }

                    if ((land.Flags & (uint)ParcelFlags.UseEstateVoiceChan) != 0)
                    {
                        map.Remove("parcel_local_id"); // estate channel
                    }

                    // Defect #13 (Docs/voice/parcel-voice-semantics.md, OPEN items): this
                    // check used to be chained as the "else" of the UseEstateVoiceChan branch
                    // above, so setting the estate-channel flag skipped ban/restrict enforcement
                    // entirely. Room selection (which Janus room to route to) and access control
                    // (may this agent have voice here at all) are independent decisions, so the
                    // check now runs on both the estate-channel and per-parcel paths.
                    if(parcel.IsRestrictedFromLand(agentID) || parcel.IsBannedFromLand(agentID))
                    {
                        // check Z distance?
                        m_log.LogDebug($"{logHeader}[ProvisionVoice]:agent not allowed on parcel");
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }
                }
            }
        }

        // The checks passed. Send the request to the voice service.
        OSDMap resp = voiceService.ProvisionVoiceAccountRequest(map, agentID, scene.RegionInfo.RegionID);

        if(resp is not null)
        {
            if (_MessageDetails) m_log.LogDebug($"{logHeader}[ProvisionVoice]: response: {resp}");

            // Convert the OSD to LLSDXml for the response
            string xmlResp = OSDParser.SerializeLLSDXmlString(resp);
            response.RawBuffer = Util.UTF8.GetBytes(xmlResp);
            // A capacity rejection carries the mixer's ROOM_FULL code; return HTTP 409 Conflict,
            // which the viewer maps to ERROR_CHANNEL_FULL (llvoicewebrtc.cpp:2901). Closes the
            // pre-existing "check for errors" TODO for the capacity case ONLY — every other
            // failure map carries no error_code and keeps its OK status. Referencing the service
            // constant keeps the 495 in one place (WebRtcJanusService.JANUS_ROOM_FULL_ERROR_CODE).
            if (resp.TryGetInt("error_code", out int provErrorCode) && provErrorCode == WebRtcJanusService.JANUS_ROOM_FULL_ERROR_CODE)
                response.StatusCode = (int)HttpStatusCode.Conflict;
            else
                response.StatusCode = (int)HttpStatusCode.OK;

            // Phase-3a (correction 1): a successful provision means this agent will join the mixer
            // room — hand it to the visibility sender's pending-join path so its full exclusion
            // column is (re)sent once it is present (the mixer silently drops a batch for a listener
            // not yet in the room). Estate-channel scoped; harmless for a per-parcel-channel agent
            // (its replace targets the estate room and the bounded re-send simply gives up loudly).
            // Step S2: the success map also carries the mixer room the service actually joined (S1).
            // Record it per agent so S3b can address batches per room. A failure or logout map has
            // no "room" -> null -> the service leaves any earlier record untouched.
            // S-A2A-3 / plan §1.4 (a): ONLY a spatial ("local") provision is recorded. An A2A room is
            // not the agent's spatial room -- recording it would point the visibility batches at the
            // A2A room and the spatial exclusions would silently miss. The pending-join re-send is
            // therefore never armed for an A2A join either. A logout map has no room and is excluded
            // by the same gate (the service keeps its earlier record; the close/leave path clears it).
            if (A2AProvisionAdmission.RecordsListenerRoom(admission.Kind))
            {
                int? provisionedRoom = resp.TryGetInt("room", out int provRoom) ? provRoom : (int?)null;
                VoiceVisibilityService svc;
                lock (m_visibilityServices)
                    m_visibilityServices.TryGetValue(scene, out svc);
                svc?.OnListenerProvisioned(agentID, provisionedRoom);
            }
            else if (admission.Kind == ProvisionKind.Multiagent && resp.TryGetString("viewer_session", out string provVs) && !string.IsNullOrEmpty(provVs))
            {
                // Admitted AND joined -- only the service's success map carries viewer_session
                // (ProvisionResponseBuilder.BuildSuccess); a failure map ({response:"failed"}, with or
                // without error_code) leaves the record as it was. The callee's admitted provision is
                // the accept (Invited -> Active).
                string vs = provVs;
                A2ASession s = m_a2aSessions.MarkProvisioned(admission.Session.SessionId, agentID, vs);
                // S-A2A-4: the mixer room the service derived (grid id + channel + type) rides on the
                // success map as `room`; surfaced here so an A2A join is auditable end to end.
                m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} viewer_session={ViewerSession} room={Room} state={State} decision=provisioned",
                    logHeader, agentID, admission.Session.SessionId, vs ?? "-",
                    resp.TryGetInt("room", out int a2aRoom) ? a2aRoom.ToString() : "-",
                    s?.State.ToString() ?? "gone");
            }
            else if (admission.Kind == ProvisionKind.Logout && a2aVs != "-")
            {
                // Teardown by viewer session: only the record this party joined under that session is
                // affected; a spatial logout matches nothing and is a no-op here. Both parties gone
                // removes the Active record (both-logout).
                foreach (UUID gone in m_a2aSessions.MarkGone(agentID, a2aVs))
                    m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} viewer_session={ViewerSession} decision=removed-both-logout",
                        logHeader, agentID, gone, a2aVs);
            }
        }
        else
        {
            m_log.LogDebug($"{logHeader}[ProvisionVoice]: got null response");
            response.StatusCode = (int)HttpStatusCode.OK;
        }
        return;
    }

    public void VoiceSignalingRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        IWebRtcVoiceService voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
        if (voiceService is null)
        {
            m_log.LogError($"{logHeader}[VoiceSignalingRequest]: avatar \"{agentID}\": no voice service");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if(request.HttpMethod != "POST")
        {
            m_log.LogError($"[{logHeader}][VoiceSignaling]: Not a POST request. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        // Deserialize the request. Convert the LLSDXml to OSD for our use
        OSDMap map = BodyToMap(request, "VoiceSignalingRequest");
        if (map is null)
        {
            m_log.LogError($"{logHeader}[VoiceSignalingRequest]: No request data found. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        // Make sure the request is for WebRTC voice
        if (map.TryGetValue("voice_server_type", out OSD vstosd))
        {
            if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
            {
                response.RawBuffer = llsdUndefAnswerBytes;
                response.StatusCode = (int)HttpStatusCode.OK;
                return;
            }
        }

        OSDMap resp = voiceService.VoiceSignalingRequest(map, agentID, scene.RegionInfo.RegionID);

        if (_MessageDetails) m_log.LogDebug($"{logHeader}[VoiceSignalingRequest]: Response: {resp}");

        // TODO: check for errors and package the response

        response.RawBuffer = llsdUndefAnswerBytes;
        response.StatusCode = (int)HttpStatusCode.OK;
        return;
    }

    /// <summary>
    /// Callback for a client request for ChatSessionRequest.
    /// The viewer sends this request when the user tries to start a P2P text or voice session
    /// with another user. We need to generate a new session ID and return it to the client.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="response"></param>
    /// <param name="agentID"></param>
    /// <param name="scene"></param>
    public void ChatSessionRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        m_log.LogDebug("{0}: ChatSessionRequest received for agent {1} in scene {2}", logHeader, agentID, scene.RegionInfo.RegionName);
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if (!scene.TryGetScenePresence(agentID, out ScenePresence sp) || sp.IsDeleted)
        {
            m_log.LogWarning($"{logHeader} ChatSessionRequest: scene presence not found or deleted for agent {agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        OSDMap reqmap = BodyToMap(request, "[ChatSessionRequest]");
        if (reqmap is null)
        {
            m_log.LogWarning($"{logHeader} ChatSessionRequest: message body not parsable in request for agent {agentID}");
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        // Permanent instrument (Docs/voice/a2a-build-plan.md §1.8): the body the viewer actually sent,
        // single-line and greppable, BEFORE any decision. Never carries a token (the request has none).
        m_log.LogDebug("{LogHeader} {Tag} agent={AgentId} region={RegionName} body={Body}",
            logHeader, ChatSessionRequestLogic.InstrumentTag, agentID, scene.RegionInfo.RegionName,
            OSDParser.SerializeJsonString(reqmap));

        // S-A2A-1: the decision is pure and unit-tested (ChatSessionRequestLogic); this adapter applies it.
        // "start p2p voice" records the pair in the invitation registry (params = callee; absent -> 400,
        // replacing the old UUID.Random fallback); "call" mints the per-session token and answers in the
        // HTTP body with voice_credentials { channel_uri, channel_credentials } (llvoicechannel.cpp:687).
        // Nothing here admits a multiagent provision yet -- the O-29 deny still holds until S-A2A-3.
        ChatSessionOutcome outcome = ChatSessionRequestLogic.Decide(reqmap, agentID, sp.Name, m_a2aSessions);

        m_log.LogDebug("{LogHeader} {Line}", logHeader, outcome.Instrument);

        // S-A2A-2: "call" produced an invitation for the other party. Deliver it as a generic
        // ChatterBoxInvitation event (BuildEvent + Enqueue) to the callee on THIS instance; an
        // unreachable callee (offline / another region server) gets nothing and the caller rings out.
        // Never affects this request's outcome: the caller's credentials are returned regardless.
        if (outcome.Invite is not null)
        {
            List<Scene> scenes;
            lock (m_scenes)
                scenes = new List<Scene>(m_scenes);
            string decision = A2AInviteDelivery.Deliver(scenes, outcome.Invite.Callee, outcome.Invite.Body, null, out string calleeRegion);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                A2AInviteDelivery.Line(outcome.Invite.Callee, outcome.Invite.Caller, outcome.Invite.SessionId, calleeRegion, decision));
            // S-A2A-2.1: one ring per Invited record. Marked only on a confirmed enqueue, so a
            // callee-unreachable delivery leaves the flag clear and a caller retry can ring later.
            if (decision == A2AInviteDelivery.DecisionSent)
                m_a2aSessions.MarkInviteSent(outcome.Invite.SessionId);
        }

        if (outcome.Reply is not null)
        {
            IEventQueue queue = scene.RequestModuleInterface<IEventQueue>();
            if (queue is null)
            {
                m_log.LogError("{0}: no event queue for scene {1}", logHeader, scene.RegionInfo.RegionName);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return;
            }

            // The viewer reads only success / temp_session_id / session_id from this event; it never reads
            // voice_enabled, session_name or type (wire trace §2). Values kept as before for stock viewers.
            queue.ChatterBoxSessionStartReply(
                    outcome.Reply.SessionId,
                    sp.Name,
                    2,
                    false,
                    true,
                    outcome.Reply.TempSessionId,
                    true,
                    string.Empty,
                    agentID);
        }

        if (outcome.Body is not null)
            response.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(outcome.Body));

        response.StatusCode = (int)outcome.Status;
    }

    /// <summary>
    /// Convert the LLSDXml body of the request to an OSDMap for easier handling.
    /// Also logs the request if message details is enabled.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="pCaller"></param>
    /// <returns>'null' if the request body is empty or cannot be deserialized</returns>
    private OSDMap BodyToMap(IOSHttpRequest request, string pCaller)
    {
        try
        {
            using Stream inputStream = request.InputStream;
            if (inputStream.Length > 0)
            {
                OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                if (_MessageDetails)
                    m_log.LogDebug($"{pCaller} BodyToMap: Request: {tmp}");
                if(tmp is OSDMap map)
                    return map;
            }
        }
        catch
        {
            m_log.LogDebug($"{pCaller} BodyToMap: Fail to decode LLSDXml request");
        }
        return null;
    }
}
