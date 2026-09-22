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
using OpenSim.Services.Interfaces;   // P1.2G-c: IPresenceService, for the online-only ring filter
using osWebRtcVoice.NonSpatial;   // P1.2G: the non-spatial engine and the group arm

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

    // O-72: replays a provision refusal (403/404/501) to the viewer's immediate retries for RefusalCacheSeconds
    // without re-running the estate/parcel checks. Replaced from config in Initialise.
    private ProvisionRefusalCache m_refusalCache = new ProvisionRefusalCache(TimeSpan.FromSeconds(ProvisionRefusalCache.DefaultSeconds));

    // Control info
    private static bool m_Enabled = false;

    private IConfig m_Config;

    // Comma-separated STUN URIs advertised to viewers as SimulatorFeatures
    // "stun-servers"; empty => the key is not emitted.
    private string m_StunServers = string.Empty;

    // V-1 (O-78): [WebRtcVoice] VisibilityFeederEnabled / VisibilityEmitEnabled defaults. Both were
    // false before V-1, so an install that never set them pushed no permissions to the mixer.
    public const bool DefaultVisibilityFeederEnabled = true;
    public const bool DefaultVisibilityEmitEnabled = true;

    public static bool ReadVisibilityFeederEnabled(IConfig pConfig)
        => pConfig.GetBoolean("VisibilityFeederEnabled", DefaultVisibilityFeederEnabled);

    public static bool ReadVisibilityEmitEnabled(IConfig pConfig)
        => pConfig.GetBoolean("VisibilityEmitEnabled", DefaultVisibilityEmitEnabled);

    // Phase 0 slice 0.2: [WebRtcVoice] VisibilityArmingEnabled. Arming, room epochs, generations, heartbeats and
    // acting on the mixer's reply (Docs/voice/nonspatial-phase0-design.md §1-§3). DISABLED by default: with the key
    // absent or false the sim emits exactly the pre-0.2 payloads (VisibilityKnobOffGoldenTests). A deliberate
    // departure from the design's §6.1, which let 0.2 ride VisibilityEmitEnabled; that key is already true on live
    // grids, so riding it would have changed the wire on deploy.
    public const bool DefaultVisibilityArmingEnabled = false;

    public static bool ReadVisibilityArmingEnabled(IConfig pConfig)
        => pConfig.GetBoolean("VisibilityArmingEnabled", DefaultVisibilityArmingEnabled);

    // P1.2G item 6: [WebRtcVoice] GroupVoiceEnabled. OFF by default, so an install that never sets
    // it behaves exactly as it does today -- a group "call" falls through to the A2A arm and 404s,
    // and a group provision cannot exist because no room key was ever handed out. Opt-in only.
    public const bool DefaultGroupVoiceEnabled = false;

    public static bool ReadGroupVoiceEnabled(IConfig pConfig)
        => pConfig.GetBoolean("GroupVoiceEnabled", DefaultGroupVoiceEnabled);

    // [WebRtcVoice] GroupVoiceRequireVoicePower. The viewer separates GP_SESSION_JOIN (may be in the
    // session, roles_constants.h:144) from GP_SESSION_VOICE (may hear/talk, :145); we require both by
    // default so a role explicitly denied voice is not admitted. False requires JOIN only.
    public const bool DefaultGroupVoiceRequireVoicePower = true;

    public static bool ReadGroupVoiceRequireVoicePower(IConfig pConfig)
        => pConfig.GetBoolean("GroupVoiceRequireVoicePower", DefaultGroupVoiceRequireVoicePower);

    // [WebRtcVoice] GroupVoiceCap. Clamped by NonSpatialCaps.Effective to the mixer's SLV_MAX_MIX.
    public static int ReadGroupVoiceCap(IConfig pConfig)
        => pConfig.GetInt("GroupVoiceCap", NonSpatialCaps.DefaultConferenceCap);

    // P1.4a: [WebRtcVoice] AdhocVoiceEnabled. OFF by default, so this deploy changes nothing until
    // it is set: "start conference" keeps its pre-P1.4a 200-and-no-body behaviour and the viewer
    // keeps timing out exactly as it does today. Opt-in only.
    public const bool DefaultAdhocVoiceEnabled = false;

    public static bool ReadAdhocVoiceEnabled(IConfig pConfig)
        => pConfig.GetBoolean("AdhocVoiceEnabled", DefaultAdhocVoiceEnabled);

    // Phase-3a per-listener visibility feeder, one service per region. On by default (V-1, O-78);
    // false turns it off.
    private bool m_VisibilityFeederEnabled = DefaultVisibilityFeederEnabled;
    private int m_VisibilityTickMs = 250;
    // Emit the matrix to the mixer (peer_ctl_batch), separate from running the matrix. On by default
    // (V-1, O-78); false runs the feeder matrix-only for diagnostics.
    private bool m_VisibilityEmitEnabled = DefaultVisibilityEmitEnabled;
    // Slice 0.2 arming (see ReadVisibilityArmingEnabled). Off by default.
    private bool m_VisibilityArmingEnabled = DefaultVisibilityArmingEnabled;
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

    // P1.2G: the non-spatial engine (P1.1) and the group policy. The engine is constructed
    // unconditionally and costs nothing when idle; the POLICY is what gates every group arm, and it
    // is GroupVoicePolicy.Disabled unless [WebRtcVoice] GroupVoiceEnabled is set. The store is the
    // in-process one for this slice; P1.x swaps it for a service behind INonSpatialSessionStore
    // without touching anything here (O-110).
    private readonly InMemoryNonSpatialSessionStore m_nonSpatialStore = new();
    private NonSpatialVoiceSessionEngine m_nonSpatial;
    private GroupVoicePolicy m_groupVoice = GroupVoicePolicy.Disabled;

    /// <summary>P1.4a: [WebRtcVoice] AdhocVoiceEnabled. False keeps the pre-P1.4a stub behaviour.</summary>
    private bool m_adhocVoiceEnabled = DefaultAdhocVoiceEnabled;

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
                // O-72: [WebRtcVoice] RefusalCacheSeconds (default 5; 0 disables).
                m_refusalCache = new ProvisionRefusalCache(TimeSpan.FromSeconds(
                    Math.Max(0, m_Config.GetInt("RefusalCacheSeconds", ProvisionRefusalCache.DefaultSeconds))));
                m_StunServers = m_Config.GetString("StunServers", string.Empty);
                m_VisibilityFeederEnabled = ReadVisibilityFeederEnabled(m_Config);
                m_VisibilityTickMs = m_Config.GetInt("VisibilityTickMs", 250);
                m_VisibilityEmitEnabled = ReadVisibilityEmitEnabled(m_Config);
                m_VisibilityArmingEnabled = ReadVisibilityArmingEnabled(m_Config);

                // P1.2G: group voice, opt-in. The membership and power lookups are bound per-request
                // to the scene's IGroupsModule (grid state via the groups service, so the same answer
                // on every region and every host), not captured here, because scenes are added later.
                if (ReadGroupVoiceEnabled(m_Config))
                {
                    m_groupVoice = new GroupVoicePolicy
                    {
                        Enabled = true,
                        RequireVoicePower = ReadGroupVoiceRequireVoicePower(m_Config),
                        Cap = ReadGroupVoiceCap(m_Config),
                        IsMember = GroupIsMember,
                        Powers = GroupPowersOf,
                    };
                    m_log.LogInformation(
                        "{LogHeader} GROUP VOICE enabled ([WebRtcVoice] GroupVoiceEnabled): cap {Cap}, required powers 0x{Mask:X}{Note}",
                        logHeader, NonSpatialCaps.Effective(NonSpatialSessionType.Group, m_groupVoice.Cap),
                        m_groupVoice.RequiredMask,
                        m_groupVoice.RequireVoicePower ? " (GP_SESSION_JOIN + GP_SESSION_VOICE)" : " (GP_SESSION_JOIN only)");
                }

                m_adhocVoiceEnabled = ReadAdhocVoiceEnabled(m_Config);
                if (m_adhocVoiceEnabled)
                    m_log.LogInformation("{LogHeader} ADHOC VOICE enabled ([WebRtcVoice] AdhocVoiceEnabled): "
                        + "\"start conference\" answers with an event-queue ChatterBoxSessionStartReply", logHeader);

                // The SAME grid id the room numbers are hashed from (S-A2A-4, O-35), so a group room
                // key derived here and a room number derived in the bridge agree across the grid.
                m_nonSpatial = new NonSpatialVoiceSessionEngine(
                    m_nonSpatialStore,
                    new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), m_groupVoice.ToAdmissionOrNull() },
                    JanusAudioBridge.ReadGridId(config));
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
                // Slice 0.6: the same treatment for the peer_ctl sink's own counters, which had no
                // reader at all ("PLUMBING only -- read by nobody today") until the shadow soak had to
                // report them. Same supplier, same unconditional registration, same reason: with the
                // feeder off the command says so rather than the operator meeting an unknown command.
                new VoiceVisibilityCommands(SnapshotVisibilityServices).Register();

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
                scene.UnregisterModuleInterface<VoiceVisibilityService>(svc);
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
            // P1.2G-c: incoming group voice rings from ANOTHER regionserver process arrive as
            // grid instant messages; this is the receive half of GroupVoiceRingTransport.
            scene.EventManager.OnIncomingInstantMessage += OnIncomingGroupRing;
            scene.EventManager.OnClientClosed += delegate (UUID clientID, Scene s)
            {
                // O-52 (audit W-5): look the presence up in the scene the close fired for. OnClientClosed runs
                // inside Scene.RemoveClient before the presence is removed, so it is resolvable here. Same guard
                // as WebRtcVoiceServiceModule.Event_OnClientClosed.
                Scene closedIn = s ?? scene;
                ScenePresence sp = closedIn.GetScenePresence(clientID);
                if (!A2ASessionRegistry.ShouldMarkGone(sp != null, sp != null && sp.IsChildAgent))
                    return;   // child teardown (border crossing / draw distance) — the agent's voice lives in its root region
                foreach (A2ASession gone in m_a2aSessions.MarkGoneSessions(clientID, null))
                {
                    m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} region={RegionName} decision=removed-client-closed",
                        logHeader, clientID, gone.SessionId, s?.Name ?? scene.Name);
                    // S-A2A-6: tell the remaining party the departed one LEFT (if reachable here).
                    List<Scene> scenesSnap;
                    lock (m_scenes)
                        scenesSnap = new List<Scene>(m_scenes);
                    string line = A2AAgentListDelivery.SendLeave(scenesSnap, gone, clientID, null);
                    if (line != null)
                        m_log.LogDebug("{LogHeader} {Line}", logHeader, line);
                }
                // P1.2G: a ROOT presence closing releases the agent's group seats too. Same guard as
                // above (ShouldMarkGone): a child teardown is a border crossing, not a departure, and
                // must not free a seat the agent still holds from its root region. This is O-108's
                // presence backstop -- departure never waits on a POST that may never come.
                foreach (NonSpatialVoiceSession gone in
                         m_nonSpatial?.DepartAll(clientID, DepartureReason.PresenceLost)
                         ?? (IReadOnlyList<NonSpatialVoiceSession>)Array.Empty<NonSpatialVoiceSession>())
                {
                    m_log.LogDebug("{LogHeader} {Line}", logHeader,
                        GroupVoiceChatSession.Line(clientID, gone.Owner, "group-seat-released-client-closed",
                            $"room={gone.RoomKey} region={s?.Name ?? scene.Name} seats={gone.SeatsHeld}/{gone.Cap}"));
                }
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
                    TimeSpan.FromMilliseconds(m_AdminTimeoutMs), m_VisibilityArmingEnabled);
                svc.Start();
                lock (m_visibilityServices)
                    m_visibilityServices[scene] = svc;
                // S-CON-2: the connector module (same assembly, separate module instance) reaches
                // the per-region service — room record + moderation store — through the scene, the
                // same way it reaches INPCModule. Concrete type, deliberately: no other assembly
                // needs it.
                scene.RegisterModuleInterface<VoiceVisibilityService>(svc);
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

    // O-72: what one provision call decided, read back by the wrapper once the handler has answered.
    private sealed class ProvisionCallContext
    {
        public ProvisionKind? Kind;          // set once admission has decided (null: refused earlier)
        public bool AnsweredFromCache;       // the refusal cache answered this call
        public bool Provisioned;             // the voice service returned a success map (viewer_session)
    }

    // O-72: a cached region-level refusal (the exact status and body the viewer got).
    private sealed record CachedRefusal(int StatusCode, byte[] Body);

    private static bool IsCacheableRefusalStatus(int pStatusCode) =>
        pStatusCode == (int)HttpStatusCode.Forbidden || pStatusCode == (int)HttpStatusCode.NotFound
        || pStatusCode == (int)HttpStatusCode.NotImplemented;

    // O-72 (refusal throttle): the stock viewer re-provisions immediately after ANY refusal (~2 Hz live). The
    // handler below is unchanged except that, for an admitted "local" provision, a refusal recorded within
    // RefusalCacheSeconds for this (agent, region) is answered again from the cache before the estate/parcel
    // checks run. This wrapper records a fresh 403/404/501 refusal of a "local" provision, and clears the
    // entry on a successful provision or a logout. At most one INFO line per (agent, region) per window.
    public void ProvisionVoiceAccountRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        var ctx = new ProvisionCallContext();
        ProvisionVoiceAccountRequestCore(request, response, agentID, scene, ctx);

        UUID regionId = scene.RegionInfo.RegionID;
        if (ctx.Kind == ProvisionKind.Logout || ctx.Provisioned)
        {
            m_refusalCache.Clear(agentID, regionId);
        }
        else if (ctx.Kind == ProvisionKind.Local && !ctx.AnsweredFromCache && IsCacheableRefusalStatus(response.StatusCode))
        {
            int suppressed = m_refusalCache.RecordRefusal(agentID, regionId, new CachedRefusal(response.StatusCode, response.RawBuffer));
            if (suppressed > 0)
                m_log.LogInformation("{LogHeader}[ProvisionVoice]: provision from {AgentId} in \"{RegionName}\" refused again (cached, {Suppressed} retries suppressed)",
                    logHeader, agentID, scene.Name, suppressed);
        }
    }

    private void ProvisionVoiceAccountRequestCore(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene,
        ProvisionCallContext ctx)
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
                // Firestorm's Vivox module probes this cap twice per login; refused as before, logged at DEBUG.
                m_log.LogDebug($"{logHeader}[ProvisionVoice]: voice_server_type is not 'webrtc' (viewer Vivox probe, expected). Request: {map}");
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
        // P1.2G: a GROUP provision is a "multiagent" body whose `channel` is one of our room keys
        // ("nsv1:group:<uuid>"), which can never be an A2A channel because that lookup is
        // UUID.TryParse. Anything else -- every body that exists today -- goes to the untouched
        // A2AProvisionAdmission.Decide below with the same arguments it has always had.
        ProvisionAdmission admission;
        if (NonSpatialProvisionAdmission.IsGroupProvision(map))
        {
            NonSpatialProvisionResult g = NonSpatialProvisionAdmission.Decide(
                map, agentID, m_groupVoice, m_nonSpatial, scene.RegionInfo.RegionID);
            map.TryGetString("channel", out string gch);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                NonSpatialProvisionAdmission.Line(agentID, scene.Name, gch,
                    map.TryGetString("credentials", out string gcr) && !string.IsNullOrEmpty(gcr), g.Decision));
            if (!g.Admitted)
            {
                m_log.LogWarning($"{logHeader}[ProvisionVoice]: refusing group provision ({g.Decision}) from agent {agentID} in region \"{scene.Name}\"");
                response.RawBuffer = llsdUndefAnswerBytes;
                response.StatusCode = g.Status;
                return;
            }
            // Admitted. Hand the rest of the request to the SAME flow an admitted A2A multiagent
            // provision takes: it skips the parcel/estate checks (their authorization is the
            // registry / the group service) and calls the voice service with the body unchanged,
            // which selects the room by (gridId, channel, "multiagent") using the existing bridge.
            // Nothing below this line knows or needs to know that it was a group.
            admission = new ProvisionAdmission
            {
                Kind = ProvisionKind.Group,
                Decision = g.Decision,
                ChannelType = A2AProvisionAdmission.ChannelTypeMultiagent,
                Channel = gch ?? "-",
            };
        }
        else
        {
            admission = A2AProvisionAdmission.Decide(map, agentID, m_a2aSessions);
        }
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

        // O-72: record what admission decided for the wrapper, and answer a repeat of a refusal this (agent, region)
        // got within RefusalCacheSeconds straight from the cache - before the estate/parcel checks run.
        ctx.Kind = admission.Kind;
        if (admission.Kind == ProvisionKind.Local
            && m_refusalCache.TryGetRefusal(agentID, scene.RegionInfo.RegionID, out object cachedRefusal)
            && cachedRefusal is CachedRefusal cached)
        {
            ctx.AnsweredFromCache = true;
            response.RawBuffer = cached.Body;
            response.StatusCode = cached.StatusCode;
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

                // O-48 (audit W-1): for a ROOT agent the parcel is derived from the avatar's position, as
                // Vivox/FreeSwitch do (scene.GetLandData(avatar.AbsolutePosition)). It used to be looked up
                // by the viewer's parcel_local_id, so a client could name another parcel and join its room.
                // The client id is now a hint: logged on mismatch, never refused (a viewer mid-crossing can
                // be stale, O-11).
                // O-48a: a CHILD agent (the viewer's neighbour-region provision) is positioned outside this
                // region, so position finds no parcel. It keeps the pre-slice-1 path: a client id selects the
                // parcel and every check runs against it; no id (the stock viewer, estate channel) means no
                // parcel checks and the service's -999 estate room. ProvisionParcelResolver decides.
                int? clientParcelId = map.TryGetInt("parcel_local_id", out int c) ? c : null;
                ParcelResolveInput resolveInput = new ParcelResolveInput(clientParcelId, sp.IsChildAgent);

                ILandObject parcel = null;
                if (ProvisionParcelResolver.DerivesFromPosition(resolveInput))
                    parcel = scene.LandChannel.GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
                ParcelResolution res = ProvisionParcelResolver.Resolve(resolveInput, parcel?.LandData?.LocalID);
                if (res.Source == ParcelSource.ClientHint)
                    parcel = scene.LandChannel.GetLandObject(res.LocalId.Value);

                LandData land = parcel?.LandData;
                bool estateChan = land != null && (land.Flags & (uint)ParcelFlags.UseEstateVoiceChan) != 0;

                // Every path logs this line, including the refusals below and the child/no-hint estate path,
                // so no provision outcome is silent (O-48a: the NotFound arm used to return with no log).
                m_log.LogDebug("{LogHeader} [PARCEL RESOLVE] agent={AgentId} region={RegionName} child={Child} source={Source} client={ClientId} server={ServerLocalId} parcel={ParcelId} estate_chan={EstateChan} mismatch={Mismatch}",
                    logHeader, agentID, scene.Name, sp.IsChildAgent, res.Source, clientParcelId?.ToString() ?? "-",
                    res.ServerLocalId?.ToString() ?? "-", land?.LocalID.ToString() ?? "-",
                    land == null ? "-" : estateChan.ToString(), res.ClientMismatch);

                // ParcelSource.None: child agent, no client id -- no parcel checks, parcel_local_id stays
                // absent, the service defaults to the estate room. The normal neighbour-region path.
                if (res.Source != ParcelSource.None)
                {
                    if (land == null)
                    {
                        if (res.Source == ParcelSource.ServerPosition)
                            m_log.LogWarning("{LogHeader}[ProvisionVoice]: no parcel at position for root agent {AgentId} in \"{RegionName}\" — refusing",
                                logHeader, agentID, scene.Name);
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }

                    if (res.ClientMismatch)
                        m_log.LogWarning("{LogHeader}[ProvisionVoice]: parcel_local_id {ClientId} from agent {AgentId} does not match the avatar's parcel {ServerLocalId} in \"{RegionName}\" — using the server parcel",
                            logHeader, clientParcelId, agentID, res.ServerLocalId, scene.Name);

                    if (!scene.RegionInfo.EstateSettings.TaxFree && (land.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                    {
                        m_log.LogDebug($"{logHeader}[ProvisionVoice]:parcel voice not allowed");
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }

                    if (estateChan)
                    {
                        map.Remove("parcel_local_id"); // estate channel
                    }
                    else if (res.Source == ParcelSource.ServerPosition)
                    {
                        // The service hashes this into the mixer room (CalcRoomNumber). An honest viewer sends
                        // the same number, so no live room renumbers.
                        map["parcel_local_id"] = OSD.FromInteger(land.LocalID);
                    }
                    // else ParcelSource.ClientHint: the client's parcel_local_id is forwarded unchanged (pre-slice-1).

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
            ctx.Provisioned = resp.ContainsKey("viewer_session");   // O-72: a success map clears the refusal cache

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
            else if (A2AProvisionAdmission.RecordsA2ASession(admission) && resp.TryGetString("viewer_session", out string provVs) && !string.IsNullOrEmpty(provVs))
            {
                // Admitted AND joined -- only the service's success map carries viewer_session
                // (ProvisionResponseBuilder.BuildSuccess); a failure map ({response:"failed"}, with or
                // without error_code) leaves the record as it was. The callee's admitted provision is
                // the accept (Invited -> Active).
                string vs = provVs;
                bool wasActive = admission.Session.State == A2ASessionState.Active;
                A2ASession s = m_a2aSessions.MarkProvisioned(admission.Session.SessionId, agentID, vs);
                // S-A2A-4: the mixer room the service derived (grid id + channel + type) rides on the
                // success map as `room`; surfaced here so an A2A join is auditable end to end.
                m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} viewer_session={ViewerSession} room={Room} state={State} decision=provisioned",
                    logHeader, agentID, admission.Session.SessionId, vs ?? "-",
                    resp.TryGetInt("room", out int a2aRoom) ? a2aRoom.ToString() : "-",
                    s?.State.ToString() ?? "gone");
                // S-A2A-6 (O-42a): on the TRANSITION to Active (the callee's accept), both parties'
                // IM panels get ChatterBoxSessionAgentListUpdates -- each receives the other's ENTER
                // plus its own entry, can_voice_chat:true by construction (false hangs up the call,
                // llimview.cpp:4366-4382). Not re-sent on a reconnect re-provision of an already-
                // Active record. This is the participant/moderation surface only; the caller's
                // connected state is O-42b (mixer presence, M-A2A-1).
                if (!wasActive && s != null && s.State == A2ASessionState.Active)
                {
                    List<Scene> scenes;
                    lock (m_scenes)
                        scenes = new List<Scene>(m_scenes);
                    foreach (string line in A2AAgentListDelivery.SendActivePair(scenes, s, null))
                        m_log.LogDebug("{LogHeader} {Line}", logHeader, line);
                }
            }
            else if (admission.Kind == ProvisionKind.Group && resp.TryGetString("viewer_session", out string groupVs) && !string.IsNullOrEmpty(groupVs))
            {
                // P1.2G: the viewer_session exists only in the SERVICE's success map, so this is the
                // first moment the seat can be tagged with it -- and tagging it is what makes the
                // logout teardown able to find the seat later (DepartByViewerSession). Also promotes
                // Accepted -> Present: the agent is now actually in the mixer room.
                NonSpatialVoiceSession gsess = m_nonSpatial?.Store.GetByRoomKey(admission.Channel);
                if (gsess is not null)
                {
                    SessionOutcome pres = m_nonSpatial.MarkPresent(gsess.SessionId, agentID, scene.RegionInfo.RegionID, groupVs);
                    m_log.LogDebug("{LogHeader} {Line}", logHeader,
                        GroupVoiceChatSession.Line(agentID, gsess.Owner, "group-present-" + pres.Decision,
                            $"room={admission.Channel} seats={m_nonSpatial.Store.Get(gsess.SessionId)?.SeatsHeld}/{gsess.Cap}"));
                }
            }
            else if (admission.Kind == ProvisionKind.Logout && a2aVs != "-")
            {
                // Teardown by viewer session: only the record this party joined under that session is
                // affected; a spatial logout matches nothing and is a no-op here. Both parties gone
                // removes the Active record (both-logout). S-A2A-6: the remaining party (if still on
                // this instance) gets the departed party's LEAVE.
                foreach (A2ASession gone in m_a2aSessions.MarkGoneSessions(agentID, a2aVs))
                {
                    m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} viewer_session={ViewerSession} decision=removed-both-logout",
                        logHeader, agentID, gone.SessionId, a2aVs);
                    List<Scene> scenes;
                    lock (m_scenes)
                        scenes = new List<Scene>(m_scenes);
                    string line = A2AAgentListDelivery.SendLeave(scenes, gone, agentID, null);
                    if (line != null)
                        m_log.LogDebug("{LogHeader} {Line}", logHeader, line);
                }

                // P1.2G: the SAME teardown releases a group seat. Before this, the logout arm knew
                // only about A2A records, so a group member who hung up held their seat until the
                // 8 h idle TTL -- with the 50 cap load-bearing, enough hang-ups would lock a group
                // out of its own room. Keyed by viewer session, so it frees exactly the seat this
                // logout is for and never another region's.
                foreach (NonSpatialVoiceSession gone in
                         m_nonSpatial?.DepartByViewerSession(agentID, a2aVs, DepartureReason.VoiceTeardown)
                         ?? (IReadOnlyList<NonSpatialVoiceSession>)Array.Empty<NonSpatialVoiceSession>())
                {
                    m_log.LogDebug("{LogHeader} {Line}", logHeader,
                        GroupVoiceChatSession.Line(agentID, gone.Owner, "group-seat-released",
                            $"room={gone.RoomKey} viewer_session={a2aVs} seats={gone.SeatsHeld}/{gone.Cap}"));
                }
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

        // O-60 (audit W-13): an A2A ring must come from the agent's ROOT region - the same presence rule the close
        // path uses (sp == null || sp.IsChildAgent). A child agent's request is refused before any registry state.
        if (sp.IsChildAgent)
        {
            m_log.LogWarning($"{logHeader} ChatSessionRequest: refusing child agent {agentID} in region \"{scene.RegionInfo.RegionName}\" (A2A requests come from the agent's root region)");
            response.StatusCode = (int)HttpStatusCode.Forbidden;
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

        // P1.2G: a GROUP voice "call" is answered here, BEFORE the A2A arm, and only when group voice
        // is enabled and the session-id names a group this agent belongs to. TryHandleCall returns
        // false having done nothing for everything else, so the A2A path below is reached with
        // exactly the arguments and the state it would have had. A group "call" from a NON-member
        // falls through on purpose: it reaches the A2A arm and 404s there, which is the pre-slice
        // answer for an unknown session and the one the viewer already survives.
        if (GroupVoiceChatSession.TryHandleCall(reqmap, agentID, m_groupVoice, m_nonSpatial,
                                                scene.RegionInfo.RegionID, out ChatSessionOutcome groupOutcome))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, groupOutcome.Instrument);
            ApplyChatSessionOutcome(groupOutcome, response);
            if (groupOutcome.StartedRinging)
                RingGroupMembers(m_nonSpatial.Store.Get(GroupSessionIdOf(reqmap)), agentID, sp.Name, GroupNameOf(GroupSessionIdOf(reqmap)));
            return;
        }

        // P1.2G-b item 1: the popup's Accept posts "accept invitation", which the A2A arm answers
        // with 400 (it has no case for it), and a failed accept makes the viewer clear the invitation
        // and never call startCall. Handled here for a group session only; everything else still
        // falls through untouched.
        if (GroupVoiceChatSession.TryHandleAcceptInvitation(reqmap, agentID, m_groupVoice, m_nonSpatial,
                                                            out ChatSessionOutcome acceptOutcome))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, acceptOutcome.Instrument);
            ApplyChatSessionOutcome(acceptOutcome, response);
            if (acceptOutcome.StartedRinging)
                RingGroupMembers(m_nonSpatial.Store.Get(GroupSessionIdOf(reqmap)), agentID, sp.Name, GroupNameOf(GroupSessionIdOf(reqmap)));
            return;
        }

        // S-A2A-1: the decision is pure and unit-tested (ChatSessionRequestLogic); this adapter applies it.
        // "start p2p voice" records the pair in the invitation registry (params = callee; absent -> 400,
        // replacing the old UUID.Random fallback); "call" mints the per-session token and answers in the
        // HTTP body with voice_credentials { channel_uri, channel_credentials } (llvoicechannel.cpp:687).
        // Nothing here admits a multiagent provision yet -- the O-29 deny still holds until S-A2A-3.
        // P1.4a: "start conference" opens the ADHOC session and produces the two ids the viewer is
        // waiting on. Deliberately NOT an early return like the group arms: the outcome falls into
        // the shared tail below, whose Reply path already sends the event-queue
        // ChatterBoxSessionStartReply. One sender for that event, not two.
        // P1.4b: "invite" adds people to a conference already running. Early return like the group
        // arms, because the ring list is the arm's own output and there is nothing for the shared
        // tail to do with it.
        if (AdhocVoiceChatSession.TryHandleInvite(reqmap, agentID, m_nonSpatial, m_adhocVoiceEnabled,
                                                  out ChatSessionOutcome inviteOutcome, out List<UUID> addedToCall))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, inviteOutcome.Instrument);
            ApplyChatSessionOutcome(inviteOutcome, response);
            if (addedToCall.Count > 0)
                RingAdhocMembers(m_nonSpatial.Store.Get(GroupSessionIdOf(reqmap)), agentID, sp.Name, addedToCall);
            return;
        }

        // P1.4b: "decline invitation" for a conference. No ring, no seat, nothing to fan out.
        if (AdhocVoiceChatSession.TryHandleDeclineInvitation(reqmap, agentID, m_nonSpatial, m_adhocVoiceEnabled,
                                                             out ChatSessionOutcome declineOutcome))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, declineOutcome.Instrument);
            ApplyChatSessionOutcome(declineOutcome, response);
            return;
        }

        if (!AdhocVoiceChatSession.TryHandleStartConference(reqmap, agentID, m_nonSpatial,
                                                            scene.RegionInfo.RegionID, m_adhocVoiceEnabled,
                                                            out ChatSessionOutcome outcome))
        {
            outcome = ChatSessionRequestLogic.Decide(reqmap, agentID, sp.Name, m_a2aSessions);
        }

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

            // P1.4b: the conference just opened, so ring the invitees the viewer named in `params`.
            // Fired on the engine's 0 -> 1 seat transition, which for AD-HOC is the creator's own
            // seat inside Start -- there is no other accept to hang it on. Placed AFTER the re-key
            // event so a ring failure can never cost the initiator the reply it is blocked on.
            if (outcome.StartedRinging)
                RingAdhocMembers(m_nonSpatial?.Store.Get(outcome.Reply.SessionId), agentID, sp.Name, null);
        }

        ApplyChatSessionOutcome(outcome, response);
    }

    /// <summary>
    /// Write a ChatSessionRequest outcome's body and status. Extracted verbatim from the tail of
    /// <see cref="ChatSessionRequest"/> so the P1.2G group arm answers through exactly the same code
    /// the A2A arm does; it carries no event-queue or invitation work, which only the A2A arm has.
    /// </summary>
    private static void ApplyChatSessionOutcome(ChatSessionOutcome outcome, IOSHttpResponse response)
    {
        if (outcome.Body is not null)
            response.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(outcome.Body));

        response.StatusCode = (int)outcome.Status;
    }

    /// <summary>
    /// P1.2G-b items 2 and 3: ring every eligible member in THIS process when a group call begins.
    ///
    /// Fired only on the engine's 0 -> 1 seat transition, which the engine claims inside its own
    /// mutation, so two simultaneous first-joiners cannot both ring. Targets are agents present in
    /// any scene this module serves, holding both powers, excluding the initiator and anyone already
    /// seated or already rung this cycle. Delivery reuses A2AInviteDelivery, which already walks
    /// every scene in the process and prefers a root presence -- that is what makes the ring reach
    /// all three regions and not just the initiator's.
    ///
    /// Cross-HOST delivery is P1.2G-c: a member on another region server has no presence here and is
    /// simply not rung, exactly as the A2A path behaves today.
    /// </summary>
    /// <summary>
    /// P1.2G-c: ring the members this process CANNOT reach -- those on another regionserver.
    ///
    /// The roster comes from the groups service through the INITIATOR's own client, which is present
    /// here by construction (it just started the call). IGroupsModule.GroupMembersRequest
    /// dereferences its IClientAPI (GroupsModule.cs:741,743), so a null is not an option and the
    /// initiator's client is the correct requester anyway.
    ///
    /// Anyone already rung locally is excluded, so a member never gets two popups: the local walk and
    /// this list are disjoint by construction, and both are marked invited in the same cycle.
    /// </summary>
    private void RingRemoteGroupMembers(NonSpatialVoiceSession session, UUID initiator, string initiatorName,
                                        OSDMap body, List<UUID> alreadyRungLocally)
    {
        if (session is null || !m_groupVoice.IsUsable || body is null)
            return;

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);

        IMessageTransferModule transfer = null;
        Scene originScene = null;
        foreach (Scene sc in scenes)
        {
            transfer ??= sc.RequestModuleInterface<IMessageTransferModule>();
            if (sc.GetScenePresence(initiator) is not null) originScene = sc;
        }
        if (transfer is null)
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceRingTransport.Line(UUID.Zero, session.Owner, GroupVoiceRingTransport.DecisionNoTransfer));
            return;
        }

        List<GroupMembersData> roster;
        try
        {
            IClientAPI client = originScene?.GetScenePresence(initiator)?.ControllingClient;
            if (client is null) return;
            roster = GroupsModule()?.GroupMembersRequest(client, session.Owner);
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} group roster lookup failed for {GroupId}; no cross-instance ring",
                logHeader, session.Owner);
            return;
        }
        if (roster is null) return;

        HashSet<UUID> local = new HashSet<UUID>(alreadyRungLocally ?? new List<UUID>());
        HashSet<UUID> presentHere = new HashSet<UUID>(GroupVoiceInvite.PresentAgents(scenes));
        List<UUID> remote = new List<UUID>();
        foreach (GroupMembersData m in roster)
        {
            if (m.AgentID == UUID.Zero || m.AgentID == initiator) continue;
            if (local.Contains(m.AgentID) || presentHere.Contains(m.AgentID)) continue;   // the local walk has them
            if (session.Find(m.AgentID)?.HoldsSeat == true) continue;
            if (session.WasInvited(m.AgentID)) continue;
            if ((m.AgentPowers & m_groupVoice.RequiredMask) != m_groupVoice.RequiredMask) continue;
            remote.Add(m.AgentID);
        }
        if (remote.Count == 0)
            return;

        // (a) Only members the PRESENCE SERVICE reports online somewhere. An offline member cannot
        // answer a ring, and a ring delivered at their next login announces a call that ended hours
        // ago. The offline modules would not store this dialog anyway (see below), but not sending
        // is the correct behaviour rather than relying on the receiver to discard it.
        IPresenceService presence = null;
        foreach (Scene sc in scenes)
        {
            presence = sc.PresenceService;
            if (presence is not null) break;
        }
        int before = remote.Count;
        remote = GroupVoiceInvite.OnlineOnly(remote, agent =>
        {
            if (presence is null) return UUID.Zero;          // no presence service: send nothing
            OpenSim.Services.Interfaces.PresenceInfo[] found = presence.GetAgents(new[] { agent.ToString() });
            return found is { Length: > 0 } ? found[0].RegionID : UUID.Zero;
        });
        if (remote.Count != before)
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceRingTransport.Line(UUID.Zero, session.Owner, GroupVoiceInvite.DecisionOffline,
                    $"{before - remote.Count} of {before} candidate(s) are offline"));
        if (remote.Count == 0)
            return;

        UUID originRegion = originScene?.RegionInfo?.RegionID ?? UUID.Zero;
        foreach (UUID target in remote)
        {
            try
            {
                GridInstantMessage im = GroupVoiceRingTransport.Build(target, initiator, initiatorName,
                                                                      session.Owner, originRegion, body);
                transfer.SendInstantMessage(im, _ => { });
                m_log.LogDebug("{LogHeader} {Line}", logHeader,
                    GroupVoiceRingTransport.Line(target, session.Owner, GroupVoiceRingTransport.DecisionSent));
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "{LogHeader} cross-instance ring to {Target} failed", logHeader, target);
            }
        }
        m_nonSpatial?.MarkInvited(session.SessionId, remote);
    }

    /// <summary>
    /// P1.2G-c: a ring arrived from another regionserver. Adopt the session locally so the accept
    /// that follows can be admitted here, then deliver the invitation exactly as a local ring would
    /// -- same body, same A2AInviteDelivery, so the viewer cannot tell the two paths apart.
    ///
    /// This instance never fans out from an adopted session: AdoptRemoteRing latches RingSent.
    /// </summary>
    private void OnIncomingGroupRing(GridInstantMessage msg)
    {
        if (m_nonSpatial is null || (!m_groupVoice.IsUsable && !m_adhocVoiceEnabled))
            return;
        if (!GroupVoiceRingTransport.TryParse(msg, out UUID target, out UUID groupID, out OSDMap body))
            return;   // not ours: every other subscriber still sees it untouched

        OSDMap voice = body.TryGetOSDMap("voice", out OSDMap v) ? v : null;
        string roomKey = voice is not null && voice.TryGetString("channel_uri", out string ru) ? ru : null;
        string token = voice is not null && voice.TryGetString("channel_credentials", out string tk) ? tk : null;
        body.TryGetUUID("from_id", out UUID caller);

        // P1.4b: group and conference rings share the carrier, and invitation_type is what tells them
        // apart -- it is already in the body the viewer will receive, so nothing new goes on the wire.
        // Reading it from the BODY rather than adding a transport field also means the discriminator
        // and the thing the viewer acts on can never disagree.
        bool isConference = voice is not null
                            && voice.TryGetValue("invitation_type", out OSD invType)
                            && invType.AsInteger() == AdhocVoiceInvite.InvitationTypeConference;
        NonSpatialSessionType ringType = isConference ? NonSpatialSessionType.Adhoc : NonSpatialSessionType.Group;

        if (isConference ? !m_adhocVoiceEnabled : !m_groupVoice.IsUsable)
            return;   // that half is switched off on this instance

        SessionOutcome adopted = m_nonSpatial.AdoptRemoteRing(groupID, roomKey, token,
                                                              isConference ? NonSpatialCaps.DefaultConferenceCap
                                                                           : m_groupVoice.Cap,
                                                              caller, new[] { target }, ringType);
        if (!adopted.Ok)
        {
            m_log.LogWarning("{LogHeader} {Line}", logHeader,
                GroupVoiceRingTransport.Line(target, groupID, adopted.Decision,
                    "carried room key does not match this instance's derivation - check GatekeeperURI on both"));
            return;
        }

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);
        string decision = A2AInviteDelivery.Deliver(scenes, target, body, null, out string region);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            GroupVoiceRingTransport.Line(target, groupID, decision, "region=\"" + region + "\" adopted=" + adopted.Decision));
    }

    /// <summary>The session-id a ChatSessionRequest body carries, or Zero.</summary>
    private static UUID GroupSessionIdOf(OSDMap reqmap)
        => reqmap is not null && reqmap.TryGetUUID("session-id", out UUID id) ? id : UUID.Zero;

    /// <summary>
    /// The group's name for the callee's incoming-call UI. Best effort: the groups service knows it,
    /// and a blank name is survivable (GroupVoiceInvite.BuildBody substitutes a default) whereas an
    /// exception here would lose the ring.
    /// </summary>
    private string GroupNameOf(UUID groupID)
    {
        try
        {
            return GroupsModule()?.GetGroupRecord(groupID)?.GroupName ?? string.Empty;
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} group name lookup failed for {GroupId}", logHeader, groupID);
            return string.Empty;
        }
    }

    private void RingGroupMembers(NonSpatialVoiceSession session, UUID initiator, string initiatorName, string groupName)
    {
        if (session is null || m_nonSpatial is null || !m_groupVoice.IsUsable)
            return;

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);

        List<UUID> targets = GroupVoiceInvite.Targets(GroupVoiceInvite.PresentAgents(scenes), session,
                                                      initiator, m_groupVoice);
        bool noLocalTargets = targets.Count == 0;
        if (noLocalTargets)
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceInvite.Line(UUID.Zero, session.Owner, "-", "no-local-targets"));

        string token = m_nonSpatial.IssueToken(session.SessionId, initiator);
        if (string.IsNullOrEmpty(token))
        {
            m_log.LogWarning("{LogHeader} group ring for {GroupId} has no token; not ringing", logHeader, session.Owner);
            return;
        }

        OSDMap body = GroupVoiceInvite.BuildBody(session, token, initiator, initiatorName, groupName);
        foreach (UUID target in targets)
        {
            string decision = A2AInviteDelivery.Deliver(scenes, target, body, null, out string region);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceInvite.Line(target, session.Owner, region, decision));
        }
        // Marked whatever the delivery said: a member we could not reach must not be re-rung on
        // every later join. One ring per member per cycle; the cycle resets when the room empties.
        m_nonSpatial.MarkInvited(session.SessionId, targets);

        // P1.2G-c: everyone the local walk could not reach -- i.e. on another regionserver.
        RingRemoteGroupMembers(session, initiator, initiatorName, body, targets);
    }

    /// <summary>
    /// P1.4b: ring an ad-hoc conference's invitees -- local first, then anyone this process cannot
    /// reach, over P1.2G-c's carrier.
    ///
    /// SIMPLER THAN THE GROUP FAN-OUT IN ONE IMPORTANT WAY: there is no roster to consult. An ad-hoc
    /// conference's membership IS its invitation list, so the targets come from the session itself
    /// and the groups service is never touched. That is also why the target list and
    /// AdhocAdmission.CanJoin agree by construction -- both read the same records.
    ///
    /// <paramref name="only"/> narrows the ring to specific agents, which is what "invite" needs:
    /// adding one person to a running call must ring that person and nobody already in it.
    /// </summary>
    private void RingAdhocMembers(NonSpatialVoiceSession session, UUID initiator, string initiatorName,
                                  List<UUID> only)
    {
        if (session is null || m_nonSpatial is null || !m_adhocVoiceEnabled)
            return;

        List<UUID> targets = AdhocVoiceInvite.Targets(session, initiator, only);
        if (targets.Count == 0)
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocVoiceInvite.Line(UUID.Zero, session.SessionId, "no-targets"));
            return;
        }

        string token = m_nonSpatial.IssueToken(session.SessionId, initiator);
        if (string.IsNullOrEmpty(token))
        {
            m_log.LogWarning("{LogHeader} conference ring for {SessionId} has no token; not ringing",
                logHeader, session.SessionId);
            return;
        }

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);

        OSDMap body = AdhocVoiceInvite.BuildBody(session, token, initiator, initiatorName);

        // (a) anyone with a presence in THIS process, exactly as a group ring reaches them
        HashSet<UUID> presentHere = new HashSet<UUID>(GroupVoiceInvite.PresentAgents(scenes));
        List<UUID> local = new List<UUID>();
        List<UUID> remote = new List<UUID>();
        foreach (UUID t in targets)
            (presentHere.Contains(t) ? local : remote).Add(t);

        foreach (UUID target in local)
        {
            string decision = A2AInviteDelivery.Deliver(scenes, target, body, null, out string region);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocVoiceInvite.Line(target, session.SessionId, decision, "region=\"" + region + "\""));
        }

        // (b) everyone else: another regionserver, or offline. The presence service decides which,
        // and an offline invitee is simply not rung -- a conference ring is worthless once stored and
        // replayed at the next login, announcing a call that ended hours ago.
        RingRemoteAdhocMembers(session, initiator, initiatorName, body, scenes, remote);

        // Marked whatever delivery said, local and remote alike: a member we could not reach must not
        // be re-rung by every later join. One ring per member per cycle.
        m_nonSpatial.MarkInvited(session.SessionId, local);
    }

    /// <summary>
    /// P1.4b: carry a conference ring to invitees on another regionserver, over exactly the transport
    /// P1.2G-c built for groups. Nothing in GroupVoiceRingTransport is group-specific -- it is a
    /// module-to-module carrier -- so the only thing that distinguishes the two on receipt is the
    /// invitation_type already inside the body.
    /// </summary>
    private void RingRemoteAdhocMembers(NonSpatialVoiceSession session, UUID initiator, string initiatorName,
                                        OSDMap body, List<Scene> scenes, List<UUID> candidates)
    {
        if (candidates is null || candidates.Count == 0 || body is null)
            return;

        IMessageTransferModule transfer = null;
        Scene originScene = null;
        foreach (Scene sc in scenes)
        {
            transfer ??= sc.RequestModuleInterface<IMessageTransferModule>();
            if (sc.GetScenePresence(initiator) is not null) originScene = sc;
        }
        if (transfer is null)
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceRingTransport.Line(UUID.Zero, session.SessionId, GroupVoiceRingTransport.DecisionNoTransfer));
            return;
        }

        IPresenceService presence = null;
        foreach (Scene sc in scenes)
        {
            presence = sc.PresenceService;
            if (presence is not null) break;
        }
        int before = candidates.Count;
        List<UUID> online = GroupVoiceInvite.OnlineOnly(candidates, agent =>
        {
            if (presence is null) return UUID.Zero;          // no presence service: send nothing
            OpenSim.Services.Interfaces.PresenceInfo[] found = presence.GetAgents(new[] { agent.ToString() });
            return found is { Length: > 0 } ? found[0].RegionID : UUID.Zero;
        });
        if (online.Count != before)
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocVoiceInvite.Line(UUID.Zero, session.SessionId, GroupVoiceInvite.DecisionOffline,
                    $"{before - online.Count} of {before} candidate(s) are offline"));
        if (online.Count == 0)
            return;

        UUID originRegion = originScene?.RegionInfo?.RegionID ?? UUID.Zero;
        foreach (UUID target in online)
        {
            try
            {
                GridInstantMessage im = GroupVoiceRingTransport.Build(target, initiator, initiatorName,
                                                                      session.SessionId, originRegion, body);
                transfer.SendInstantMessage(im, _ => { });
                m_log.LogDebug("{LogHeader} {Line}", logHeader,
                    GroupVoiceRingTransport.Line(target, session.SessionId, GroupVoiceRingTransport.DecisionSent));
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "{LogHeader} cross-instance conference ring to {Target} failed", logHeader, target);
            }
        }
        m_nonSpatial?.MarkInvited(session.SessionId, online);
    }

    // ---- P1.2G: group membership and powers, resolved as GRID state ------------------------------
    //
    // IGroupsModule answers through the groups service (local or remote connector), so these are the
    // same on every region of the grid and on every host -- which is what lets the session be
    // grid-wide while admission stays correct wherever the agent happens to be standing. Any scene in
    // this process can answer; the first one with the module wins. No scene means no groups, which is
    // a refusal, never an admission.

    private IGroupsModule GroupsModule()
    {
        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);
        foreach (Scene s in scenes)
        {
            IGroupsModule g = s.RequestModuleInterface<IGroupsModule>();
            if (g is not null)
                return g;
        }
        return null;
    }

    private bool GroupIsMember(UUID agentID, UUID groupID)
    {
        try
        {
            return GroupsModule()?.GetMembershipData(groupID, agentID) is not null;
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} group membership lookup failed for agent {AgentId} group {GroupId}; refusing",
                logHeader, agentID, groupID);
            return false;
        }
    }

    private ulong GroupPowersOf(UUID agentID, UUID groupID)
    {
        try
        {
            return GroupsModule()?.GetFullGroupPowers(agentID, groupID) ?? 0UL;
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} group power lookup failed for agent {AgentId} group {GroupId}; refusing",
                logHeader, agentID, groupID);
            return 0UL;
        }
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
