# Spec coverage — claim inventory and verdicts

**Source:** `Docs/voice/webrtc-voice-spec.md`, "Spatial WebRTC Voice Service — Feature Specification",
written 2026-08-15.
- **Pass 0:** enumerated the spec's normative claims.
- **Pass 0b (2026-09-14):** applied John's flag rulings.
- **Pass 1a (2026-09-14):** added a Verdict and an Evidence column for the rows in §1, §3 and §4
  (SC-3 – SC-55). The other rows' verdict cells stay blank until later passes.

## Header

- **Length:** `wc -l Docs/voice/webrtc-voice-spec.md` → `198`.
- **Tracking:** tracked in git. `git log --follow` shows one commit, `2a592f86e3` (2026-08-31 06:03 -0500,
  `docs(voice): O-28 — resync Docs/voice with the mixer (sim side)`). `git blame` attributes all 198
  lines to `2a592f86e3`. The 2026-08-15 authoring date therefore does not appear in this repository's
  history, which starts at the O-28 mirror resync. The mixer copy
  (`legion-voice-mixer/docs/voice/webrtc-voice-spec.md`) is byte-identical (SHA-256 `4F1F9904…4433A`).
- **Line numbers:** taken from the file at tranq-ais `feature/ais-v3` `a8b85fbcdd`.

### Table of contents of the spec

| Line | Heading |
|---|---|
| 1 | # Spatial WebRTC Voice Service — Feature Specification |
| 8 | ## 1. Goals and non-goals |
| 27 | ## 2. Requirements provenance |
| 37 | ## 3. Tier 0 — Trust and privacy invariants |
| 41 | ### 3.1 All media is server-relayed, always **[OPS]** |
| 45 | ### 3.2 Voice-server trust domains **[OPS]** |
| 53 | ### 3.3 Sim-authoritative permission enforcement **[ENG]** |
| 57 | ### 3.4 Mixer output is the permission boundary **[ENG]** |
| 61 | ### 3.5 Capture requires consent **[OPS]** |
| 67 | ## 4. Tier 1 — Reliability and diagnosability |
| 71 | ### 4.1 Per-connection state vector |
| 79 | ### 4.2 Built-in echo test |
| 83 | ### 4.3 Fail loud |
| 87 | ### 4.4 Session event log |
| 91 | ### 4.5 Fleet observability (activates at scale) |
| 97 | ## 5. Tier 2 — Media plane quality |
| 110 | ## 6. Tier 3 — Spatial engine |
| 123 | ## 7. Tier 4 — Features |
| 125 | ### 7.1 Camera-position listening **[REC]** |
| 132 | ### 7.2 Performer mode **[REC]** |
| 136 | ### 7.3 Parcel and estate voice zones **[OPS]** |
| 140 | ### 7.4 Voice morphing |
| 150 | ### 7.5 Connector layer **[OPS]** |
| 158 | ### 7.6 Moderation surface **[OPS]** |
| 164 | ## 8. Deployment and scaling model |
| 168 | ### 8.1 Small (1 region – ~20 regions) |
| 172 | ### 8.2 Medium (grid, tens–hundreds of regions) |
| 176 | ### 8.3 Large (InWorldz/SL scale) |
| 186 | ## 9. Protocol compatibility summary |
| 193 | ## 10. Open questions |

### Rules applied

- **One row per claim:** every "will / must / provides / supports" statement, named feature, or committed
  behaviour gets its own row. The header scope line (line 4) and §1 goals and non-goals count.
- **Not inventoried:**
  - §2 (provenance tags, lines 27–33) commits to nothing.
  - §10 lists open questions (lines 195–198), not claims; they are carried verbatim in the appendix.
  - Prose asides that justify a claim without adding one: the §4 and §5 introductions, the CPU and quality
    estimates in §7.4, and the cost remarks in §8.2–§8.3.
- **Scope:** MIXER (the C plugin), SIM (the .cs addon), BOTH, OPS (deployment/config/docs), VIEWER (would
  need viewer changes).
- **Flag** (John's rulings, applied in pass 0b):
  - blank: an ordinary claim for pass 1.
  - `AMENDED`: a later decision changed the mechanism; the invariant still binds, and pass 1 tests the
    amended form.
  - `BLOCKED-BY-CONSTRAINT`: satisfying the claim would break a standing project constraint.
  - `DERIVED`: an umbrella goal with no separate finding of its own; it follows from the claims named in
    its ruling.
  - `NOT-A-COMMITMENT`: the spec's own wording does not commit to it.

  The flag rulings table below gives each ruling's reason and, where one was set, the criterion pass 1
  must apply.
- **Wording:** claims follow the spec's own words. Three claims (SC-19, SC-92, SC-115) were lightly
  reworded in pass 0; see the wording notes below the rulings table.

### Pass 1a verdicts (§1, §3, §4)

- **Verdict:**
  - `MET`: the claim is satisfied.
  - `PARTIAL`: some of it is satisfied; the evidence names the part that is not.
  - `UNMET`: the claim is not satisfied.
  - `BLOCKED`: it cannot be satisfied under a standing constraint, which the evidence names.
  - `DERIVED`: no independent verdict; the evidence names the rows it derives from.
  - `UNKNOWN`: not determined within budget (no row in pass 1a).
- **Source trees checked:** tranq-ais `feature/ais-v3` at `3bf4f92b7c` and legion-voice-mixer `main` at
  `4198951`, read only.
- **Citations:**
  - `SIM <path>:<line>` is relative to `D:\tranq-ais`; `MIXER <path>:<line>` is relative to
    `D:\legion-voice-mixer`. A bare `:<line>` repeats the file cited just before it.
  - Every cited line was opened and matched against the evidence text before this pass was committed.
  - Only source is cited as evidence: code, config, scripts. Some citations point at a source comment
    stating a design limit; those are labelled "(comment)".
- **Searches:** each `UNMET` row gives the literal `git grep` it rests on and its result. Inside table
  cells the regex alternation bar is escaped as `\|`; the command itself uses a plain `|`.
- **Criteria:** where the pass-0b rulings set a pass-1 criterion, it was applied literally. SC-32 reports
  its two paths separately.

## Inventory

| ID | Spec § | Line | Claim | Scope | Flag | Verdict | Evidence |
|---|---|---|---|---|---|---|---|
| SC-1 | Header | 4 | A server-side WebRTC voice service (mixer plugin + region/grid integration) for the published Second Life WebRTC voice protocol, for OpenSimulator-derived grids | BOTH | | | |
| SC-2 | Header | 4 | Designed to run unchanged from a single-region standalone to a large multi-mixer grid | OPS | | | |
| SC-3 | 1 | 12 | Full compatibility with the SL WebRTC viewer protocol (ProvisionVoiceAccountRequest / VoiceSignalingRequest caps, SLData data channel) | BOTH | | PARTIAL | **Met:** both caps are registered: `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:328` "RegisterSimpleHandler("ProvisionVoiceAccountRequest"" and `:334` "RegisterSimpleHandler("VoiceSignalingRequest"". Trickle completion is handled: `SIM Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs:507` "TryGetBool("completed", out bool iscompleted)". SLData is parsed, `MIXER src/sldata.c:142` "json_object_get(root, "lp")", and answered with a join presence, `MIXER src/janus_slvoice.c:852` "json_object_set_new(jd, "p", json_true());", and a power batch, `:2585` "json_object_set_new(pv, "p", json_integer(ppow));". **Not met:** group/ad-hoc conference voice. `start conference` is a stub, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/ChatSessionRequestLogic.cs:169` "case MethodStartConference:", under `:167` "Stubs carried over unchanged: 200, no body". A multiagent provision is admitted only for a registered avatar-to-avatar session: `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/A2AProvisionAdmission.cs:85` "registry?.TryGetByChannel(channel)". |
| SC-4 | 1 | 12 | Stock WebRTC-capable viewers work without modification | BOTH | | MET | The answer accepts the MID extension a bundled stock viewer requires: `MIXER src/janus_slvoice.c:2022` "JANUS_SDP_OA_ACCEPT_EXTMAP, JANUS_RTP_EXTMAP_MID,". The join presence uses the object shape the viewer parses: `:852` "json_object_set_new(jd, "p", json_true());". The sim advertises STUN to the viewer: `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:236` "AddFeature("stun-servers", OSD.FromString(m_StunServers))", set by default in `SIM Addons/os-webrtc-janus/os-webrtc-janus.ini.example:26` "StunServers = stun:stun.l.google.com:19302". Source review only; no live viewer was run in this pass. |
| SC-5 | 1 | 13 | True per-listener spatial audio (distance, azimuth, listener orientation) | MIXER | | MET | **Distance** per listener: `MIXER src/janus_slvoice.c:3143` "slv_vec3_mag(slv_vec3_sub(s->snap_lp, sess[j]->snap_sp))", with falloff at `:3154` "gains[j] *= (float)pow(t, slv_spatial.falloff_exp);". **Azimuth** in the listener's orientation: `:3162` "slv_azimuth(s->snap_lp, s->snap_lh, sess[j]->snap_sp)". The heading quaternion sets the forward vector: `MIXER src/mixer/azimuth.h:57` "double fy = 2.0 * (q.x * q.y + q.w * q.z);". **Pan:** constant-power, `MIXER src/mixer/pan.h:53` "double p = (sin(azimuth) + 1.0) / 2.0;". |
| SC-6 | 1 | 14 | Privacy and permission enforcement performed in the mix, on the server, not delegated to clients | BOTH | | MET | **Sim decides:** estate ban, `SIM Addons/os-webrtc-janus/Visibility/VisibilityRules.cs:27` "estate.IsEstateBanned(source.Id)"; parcel ban, `:41` "sourceParcel.ExcludesByBan(listener.Id)". It pushes the result: `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/JanusPeerCtlBatchSink.cs:219` "PeerCtlBatchSerializer.BuildRequest(op, exclSlice, muteSlice);". **Mixer enforces** it in the mix, `MIXER src/janus_slvoice.c:3125` "if(slv_roster_excludes(s->excluded, disp))" and `:3110` "g_hash_table_contains(s->mod_muted, disp)", and in the roster, `:929` "if(slv_roster_excludes(listener->excluded, p->display))". |
| SC-7 | 1 | 15 | First-class diagnosability: voice failures must be triageable by the user or operator in minutes, without log-emailing rituals | BOTH | DERIVED | DERIVED | Derives from SC-41, SC-52 and SC-53 (pass-0b ruling). |
| SC-8 | 1 | 16 | Restoration of camera-position listening, lost in the Vivox→WebRTC transition | BOTH | | MET | The listener position is the viewer's camera `lp`: `MIXER src/janus_slvoice.c:2972` "s->snap_lp = s->last_data.lp;". The server applies a leash around the avatar: `:2980` "s->snap_lp = slv_vec3_madd(s->snap_sp, slv_spatial.leash_dist / d, off);". Distance and pan are taken from `lp`: `:3143` "slv_vec3_sub(s->snap_lp, sess[j]->snap_sp)" and `:3162` "slv_azimuth(s->snap_lp, s->snap_lh, sess[j]->snap_sp)". |
| SC-9 | 1 | 16 | Restoration of voice morphing, lost in the Vivox→WebRTC transition | MIXER | | UNMET | `git -C D:/legion-voice-mixer grep -n -i -E 'morph\|pitch.?shift\|formant\|psola\|vocoder\|voice.?font' -- src connectors etc docker-entrypoint.sh` → 0 matches. `git -C D:/tranq-ais grep -n -i -E 'morph\|pitch.?shift\|formant\|psola\|vocoder\|voice.?font' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. |
| SC-10 | 1 | 16 | A capability Vivox never had: mixer-enforced moderation | BOTH | | MET | **Operator mute:** `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VoiceModerationCommands.cs:89` "AddCommand("Voice", false, "voice moderation mute",". The matrix emits a mute channel, `SIM Addons/os-webrtc-janus/Visibility/VisibilityMatrix.cs:84` "VisibilityRules.IsModerationMuted(agents[si], parcels[si])", sent in the batch at `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/JanusPeerCtlBatchSink.cs:219` "BuildRequest(op, exclSlice, muteSlice);". **Mixer:** applies it, `MIXER src/janus_slvoice.c:1838` "janus_slvoice_set_mod_muted_locked(L, e->excl[m], TRUE)", and silences the source in the mix, `:3110` "g_hash_table_contains(s->mod_muted, disp)" → `:3111` "mutes[j] = 1;". |
| SC-11 | 1 | 16 | A capability Vivox never had: effects/connector hooks | MIXER | | PARTIAL | **Met — connector hooks:** send hook `MIXER connectors/common/peer.py:57` "def local_track(self):"; injector `MIXER connectors/injector/injector.py:208` "def local_track(self):"; recorder `MIXER connectors/recorder/recorder.py:49` "def on_audio_track(self, track) -> None:"; sim policy record `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VoiceConnector/VoiceConnectorRecord.cs:59` "public bool MayInject { get; }". **Not met — effects:** `git -C D:/legion-voice-mixer grep -n -i -E 'reverb\|biquad\|equali[sz]\|distortion\|chorus\|flanger\|dsp.?chain\|audio.?filter\|voice.?effect' -- src connectors etc` → 0 matches; the same pattern over `git -C D:/tranq-ais … -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. |
| SC-12 | 1 | 17 | Scale-invariant deployment: identical code path and configuration model from a one-region hobby grid to a large commercial grid | OPS | | PARTIAL | **Met — one configuration model:** the same service class loads in the region or behind the grid service, `SIM Addons/os-webrtc-janus/WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.cs:112` "ServerUtils.LoadPlugin<IWebRtcVoiceService>(spatialDllName, [m_Config]);"; one env file, `MIXER docker-compose.yml:61` "env_file:". **Not met — large-grid scale-out:** each service has a single mixer endpoint, `SIM Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs:105` "janusConfig.GetString("JanusGatewayURI", string.Empty);"; rooms are capped per mixer, `MIXER src/janus_slvoice.c:164` "#define SLV_MAX_MIX         110". No sharding: `git -C D:/tranq-ais grep -n -i -E 'shard\|load.?balanc\|GatewayURIs\|MixerPool\|mixer.?pool' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches; `git -C D:/legion-voice-mixer grep -n -i -E 'shard\|load.?balanc\|mixer.?pool\|replica' -- src docker-compose.yml env.sample docker-entrypoint.sh etc` → 0 matches. |
| SC-13 | 1 | 17 | Small deployments must not pay complexity for scale they don't need | OPS | | MET | One prebuilt image, `MIXER docker-compose.yml:14` "image: ghcr.io/johnlegionh/legion-voice-mixer:latest", configured from one env file, `:61` "env_file:" / `:62` "- .env". The operator values are the secrets and public address, `MIXER env.sample:28` "JS_API_SECRET=", `:30` "JS_ADMIN_SECRET=", `:6` "JS_PUBLIC_IP="; everything else has defaults, e.g. `MIXER docker-entrypoint.sh:28` ": "${JS_HTTP_PORT:=14223}"". The Janus config is generated from them: `:242` "set_kv "$HTTP_JCFG" port            "${JS_HTTP_PORT}"". |
| SC-14 | 1 | 21 | Non-goal: client-side spatialization via selective forwarding (SFU); ruled out by the privacy model (§3) | MIXER | | MET | No SFU: inbound RTP only enters a jitter buffer, `MIXER src/janus_slvoice.c:3352` "janus_slvoice_jb_insert(session, seq, payload, plen);". Each listener gets a server-built N-minus-one mix, `:3175` "summed = slv_mix_nminus1_stereo(frame, SLV_FRAME_TOTAL, srcbuf, audible,", and only that mix is relayed, `:2949` "gateway->relay_rtp(s->handle, &outp);". |
| SC-15 | 1 | 22 | Non-goal: true peer-to-peer media between clients; ruled out permanently (§3.1) | BOTH | | MET | Every viewer session, including avatar-to-avatar calls, is placed in a mixer room: `SIM Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs:408` "viewerSession.Room = await viewerSession.AudioBridge.SelectRoom(pSceneID.ToString(),". The mixer is the media endpoint: inbound RTP is jitter-buffered at `MIXER src/janus_slvoice.c:3352` "janus_slvoice_jb_insert(session, seq, payload, plen);", and only the mix goes out, `:2949` "gateway->relay_rtp(s->handle, &outp);". |
| SC-16 | 1 | 23 | Non-goal: video | MIXER | | MET | The answer is generated with every m-line rejected by default, `MIXER src/janus_slvoice.c:2008` "janus_sdp *answer = janus_sdp_generate_answer(offer);", with only audio and application accepted afterwards. Video RTP is dropped: `:3338` "if(packet == NULL \|\| packet->buffer == NULL \|\| packet->video)". |
| SC-17 | 3.1 | 43 | No client ever receives another client's ICE candidates | BOTH | | MET | A viewer's candidates go only to its own Janus session: `SIM Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs:545` "viewerSession.Session.TrickleCandidates(viewerSession, candidatesArray)". Janus-to-sim trickle is raised as an event, `SIM Addons/os-webrtc-janus/Janus/JanusSession.cs:634` "OnTrickle?.Invoke(eventResp);", that nothing subscribes to: `git -C D:/tranq-ais grep -n -i -E 'OnTrickle' -- Addons/os-webrtc-janus ':!*.md'` → 3 matches, the declaration (:566), the reset (:580) and the invoke (:634). The mixer never handles candidates: `git -C D:/legion-voice-mixer grep -n -i -E 'candidate\|trickle' -- src` → 0 matches. |
| SC-18 | 3.1 | 43 | Every media connection is client ↔ voice server | BOTH | | MET | The sim joins the viewer to a mixer room, `SIM Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs:419` "var joinResult = await viewerSession.Room.JoinRoom(viewerSession)", and returns the mixer's answer, `SIM Addons/os-webrtc-janus/Janus/ProvisionResponseBuilder.cs:25` "{ "jsep", jsepAnswer },". The mixer writes that answer, `MIXER src/janus_slvoice.c:2369` "jsep_answer = json_pack("{ssss}", "type", "answer", "sdp", answer_sdp);", and relays media only on the listener's own handle, `:2949` "gateway->relay_rtp(s->handle, &outp);". |
| SC-19 | 3.1 | 43 | "Peer-to-peer" calls are two-party sessions on the non-spatial pool, exactly as Second Life does | BOTH | | PARTIAL | **Met — sim side:** admission requires the caller to be a party, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/A2AProvisionAdmission.cs:88` "if (!s.IsParty(agentID))", and a session has exactly two, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/A2ASessionRegistry.cs:80` "agent == Caller \|\| agent == Callee". Non-local provisions go to the non-spatial service, `SIM Addons/os-webrtc-janus/WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.cs:565` "m_nonSpatialVoiceService.CreateViewerSession(pRequest, pUserID, pSceneID);", and the room is created non-spatial, `SIM Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs:394` "bool isSpatial = channel_type == "local";". **Not met:** (1) the mixer stores the flag, `MIXER src/janus_slvoice.c:590` "room->spatial_audio = spatial;", but the tick never reads it (`git -C D:/legion-voice-mixer grep -n -E 'spatial_audio' -- src` → 5 matches: :324 field, :590 set, :1013 config, :2149 create, :2433 list output), so the distance cull still runs in these rooms, `:3144` "if(janus_slvoice_distance_cull_locked(s, disp, dcull, room->tick_seq))". (2) The mixer has no two-party limit, only the general cap at `:2262` "if(room_pop >= SLV_MAX_MIX)". (3) Without a configured non-spatial service the sim falls back to the spatial one, `WebRtcVoiceServiceModule.cs:138` "m_nonSpatialVoiceService = m_spatialVoiceService;". "Exactly as Second Life does" cannot be checked from source. |
| SC-20 | 3.1 | 43 | Client IP addresses are never exposed to other users | BOTH | | MET | The participant list carries only the agent UUID: `MIXER src/janus_slvoice.c:2092` "json_object_set_new(pl, "display", json_string(p->display));". No address handling in either tree: `git -C D:/legion-voice-mixer grep -n -i -E 'inet_ntop\|getpeername\|remote_ip\|ip_address\|"ip"' -- src` → 0 matches; `git -C D:/tranq-ais grep -n -i -E 'RemoteEndPoint\|ClientIP\|IPAddress' -- Addons/os-webrtc-janus ':!*Test*' ':!*.md'` → 0 matches. Candidates are not relayed to other clients (see SC-17's source lines). |
| SC-21 | 3.1 | 43 | Structurally impossible to misconfigure: there is no configuration option that enables direct client-to-client ICE | BOTH | | MET | The only ICE settings are server-side NAT settings, `MIXER docker-entrypoint.sh:223` "ensure_kv_in_section "$JANUS_JCFG" nat nat_1_1_mapping" and `:224` "nat keep_private_host", plus the sim's STUN list, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:236` "AddFeature("stun-servers", OSD.FromString(m_StunServers))". No peer-to-peer option: `git -C D:/tranq-ais grep -n -i -E 'p2p_?ice\|direct.?ice\|peer.?to.?peer\|client.?to.?client' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches; `git -C D:/legion-voice-mixer grep -n -i -E 'p2p_?ice\|direct.?ice\|peer.?to.?peer\|client.?to.?client' -- src connectors docker-entrypoint.sh env.sample etc` → 0 matches. |
| SC-22 | 3.2 | 47 | Every voice server instance is classified as grid-operated or region-operated | OPS | | UNMET | `git -C D:/tranq-ais grep -n -i -E 'trust.?domain\|grid.?operated\|region.?operated\|operator.?class\|TrustClass' -- Addons/os-webrtc-janus Source ':!*.md'` → 0 matches. `git -C D:/legion-voice-mixer grep -n -i -E 'trust.?domain\|grid.?operated\|region.?operated\|operator.?class' -- src connectors docker-compose.yml docker-entrypoint.sh env.sample etc ':!*.md'` → 0 matches. |
| SC-23 | 3.2 | 47 | The trust-domain classification is communicated to the client at provisioning time | SIM | | UNMET | Criterion: the provisioning response carries no trust-domain field. `git -C D:/tranq-ais grep -n -i -E 'trust.?domain\|grid.?operated\|region.?operated\|operator.?class\|TrustClass' -- Addons/os-webrtc-janus Source ':!*.md'` → 0 matches. The success response carries only three keys: `SIM Addons/os-webrtc-janus/Janus/ProvisionResponseBuilder.cs:25` "{ "jsep", jsepAnswer },", `:26` "{ "viewer_session", viewerSessionId },", `:27` "{ "room", room }". |
| SC-24 | 3.2 | 49 | Hypergrid visitors are provisioned only onto grid-operated servers | SIM | | UNMET | No hypergrid check on the provisioning path: `git -C D:/tranq-ais grep -n -i -E 'IsLocalUser\|IsForeign\|foreign\|HGVisitor\|ViaHG\|HomeURI\|IsLocalGridUser\|ServiceURLs' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. No server classification to route to either (SC-22's searches → 0 matches). |
| SC-25 | 3.2 | 49 | Region-local voice is not offered to hypergrid visitors | SIM | | UNMET | `git -C D:/tranq-ais grep -n -i -E 'IsLocalUser\|IsForeign\|foreign\|HGVisitor\|ViaHG\|HomeURI\|IsLocalGridUser\|ServiceURLs' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. Local provisioning checks only estate voice, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:636` "if (!scene.RegionInfo.EstateSettings.AllowVoice)", and parcel ban/restrict, `:732` "if(parcel.IsRestrictedFromLand(agentID) \|\| parcel.IsBannedFromLand(agentID))". |
| SC-26 | 3.2 | 50 | Local users connecting to region-operated servers receive a disclosure | BOTH | | UNMET | Criterion: nothing on the provisioning path emits disclosure text or a flag. `git -C D:/tranq-ais grep -n -i -E 'disclos' -- Addons/os-webrtc-janus/Janus Addons/os-webrtc-janus/WebRtcVoice Addons/os-webrtc-janus/WebRtcVoiceServiceModule Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs` → 0 matches. `git -C D:/legion-voice-mixer grep -n -i -E 'disclos' -- src etc docker-entrypoint.sh` → 0 matches. The only disclosure code is the connector notice (SC-36), which has nothing to do with server type. |
| SC-27 | 3.2 | 51 | Default topology is grid-operated | OPS | | UNMET | No grid-operated classification exists: `git -C D:/tranq-ais grep -n -i -E 'trust.?domain\|grid.?operated\|region.?operated\|operator.?class\|TrustClass' -- Addons/os-webrtc-janus Source ':!*.md'` → 0 matches. The shipped default points the region at its own Janus: `SIM Addons/os-webrtc-janus/os-webrtc-janus.ini:5` "SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService", described at `:4` "a Janus service just for spatial voice for this region". |
| SC-28 | 3.2 | 51 | Region-local is an opt-in for closed estates that want the latency benefit | OPS | | UNMET | `git -C D:/tranq-ais grep -n -i -E 'closed.?estate\|RegionOperated\|GridOperated' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. `git -C D:/legion-voice-mixer grep -n -i -E 'closed.?estate\|region.?operated\|grid.?operated' -- src connectors etc docker-compose.yml docker-entrypoint.sh env.sample` → 0 matches. |
| SC-29 | 3.3 | 55 | Parcel and estate audibility is always computed from the avatar's authoritative sim-side position and the sim's access lists | SIM | | PARTIAL | **Met — visibility feeder and root-agent provisioning.** The feeder reads the sim's position, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/FeederWorldFromScene.cs:66` "sp.UUID, sp.IsChildAgent, sp.AbsolutePosition, sp.currentParcelUUID", looks up the parcel from it, `SIM Addons/os-webrtc-janus/Visibility/VisibilityMatrix.cs:101` "return world.GetParcelAt(a.Position);", and reads the live ban list, `FeederWorldFromScene.cs:122` "banned = parcel.IsBannedFromLand;". Root-agent provisioning uses the sim position: `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:673` "GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y)". **Not met — child agents** (neighbour-region provisions): the viewer's `parcel_local_id` selects the parcel the checks run against, `:675` "if (res.Source == ParcelSource.ClientHint)" → `:676` "GetLandObject(res.LocalId.Value)", set from the client at `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/ProvisionParcelResolver.cs:53` "new ParcelResolution(ParcelSource.ClientHint, LocalId: client". |
| SC-30 | 3.3 | 55 | Access/visibility state is pushed from the simulator to the mixer with version epochs | BOTH | | UNMET | Criterion: the sim-to-mixer batch carries no version epoch. The mixer parses only `op`, `room`, `excl` and `mute`: `MIXER src/visbatch.c:149` "json_object_get(b, "op")", `:167` "json_object_get(b, "room")", `:175` "json_object_get(b, "excl")", `:182` "json_object_get(b, "mute")". `git -C D:/legion-voice-mixer grep -n -i -E 'epoch\|version\|\bseq' -- src/visbatch.c src/visbatch.h` → 0 matches. `git -C D:/tranq-ais grep -n -i -E 'epoch' -- Addons/os-webrtc-janus/Visibility/PeerCtlBatchSerializer.cs Addons/os-webrtc-janus/WebRtcVoiceRegionModule/JanusPeerCtlBatchSink.cs` → 0 matches. Two epoch-named counters exist but neither goes on the wire: the mixer's local count of applied batches, `MIXER src/janus_slvoice.c:1735` "room->vis_epoch++;", and the sim's send-lock token, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VisibilityBatchSender.cs:66` "private long _sendEpochSeq;". |
| SC-31 | 3.3 | 55 | Access/visibility state fails closed on staleness | BOTH | | UNMET | Criterion: with stale or absent state the mixer **passes** audio. Code path: a session's exclusion set starts empty, `MIXER src/janus_slvoice.c:1315` "session->excluded = g_hash_table_new_full(g_str_hash, g_str_equal, g_free, NULL);". The check excludes only what is in the set, `MIXER src/roster.h:27` "g_hash_table_contains(excluded, uuid)". The tick silences a source only on that check, `MIXER src/janus_slvoice.c:3125` "if(slv_roster_excludes(s->excluded, disp))" → `:3126` "mutes[j] = 1;"; with no batch every source is mixed. The last-batch time is only reported, never acted on: `:1475` "gint64 age_ms = (janus_get_monotonic_time() - qroom->vis_last_ts) / 1000;". `git -C D:/legion-voice-mixer grep -n -i -E 'stale\|fail.?closed\|timeout\|expire' -- src/roster.h src/visbatch.c src/visbatch.h src/deferred.c src/deferred.h` → 0 matches. Sim side: the feeder is off by default, so no batches are sent at all, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:116` "GetBoolean("VisibilityFeederEnabled", false)"; its staleness guard only frees a stuck send, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VisibilityBatchSender.cs:173` "if (elapsed <= _staleThresholdMs)". |
| SC-32 | 3.3 | 55 | Client-reported listener position is accepted only as a rendering hint (§6.1), never as a permission input | BOTH | | PARTIAL | §6.1 read as §7.1, per the pass-0b ruling. Two paths checked separately. **Not met** in both is the child-agent path, which uses the viewer-supplied `parcel_local_id`. **(a) Authorization — PARTIAL.** Root agents use the sim position, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:673` "GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y)", and a mismatching client id is only logged (`:703` "does not match the avatar's parcel"). For child agents the client's `parcel_local_id` selects the parcel, `:676` "GetLandObject(res.LocalId.Value)", that the voice-allowed check, `:706` "(land.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0", and the ban/restrict check, `:732` "parcel.IsRestrictedFromLand(agentID)", run against. **(b) Room choice — PARTIAL.** Root agents get the server's parcel id, `:722` "map["parcel_local_id"] = OSD.FromInteger(land.LocalID);". A child agent's client id is forwarded unchanged (`:724` "(comment) the client's parcel_local_id is forwarded unchanged"), read at `SIM Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs:385` "pRequest.TryGetInt("parcel_local_id", out int pli)", and hashed into the room at `SIM Addons/os-webrtc-janus/Janus/JanusAudioBridge.cs:251` "hasher.Add(pParcelLocalID);". **Mixer note:** the client's SLData `lp` drives the distance cull, `MIXER src/janus_slvoice.c:2972` "s->snap_lp = s->last_data.lp;" → `:3144` "janus_slvoice_distance_cull_locked(...)", which can only remove audio. It runs after the sim's exclusion and moderation mute, `:3132` "if(mutes[j])", so it cannot restore a source the sim excluded. |
| SC-33 | 3.4 | 59 | A listener's downstream mix contains only audio that listener is entitled to hear | MIXER | | PARTIAL | **Met while sim state is present:** moderation mute, `MIXER src/janus_slvoice.c:3110` "g_hash_table_contains(s->mod_muted, disp)"; exclusion, `:3125` "if(slv_roster_excludes(s->excluded, disp))"; both honoured by the N-minus-one mix, `:3175` "slv_mix_nminus1_stereo(frame, SLV_FRAME_TOTAL, srcbuf, audible,". Batches that arrived before the join are replayed at join, `:2318` "janus_slvoice_room_replay_deferred_locked(room, session);". **Not met:** before the first batch reaches a listener, the set is empty, `:1315` "session->excluded = g_hash_table_new_full(", and the listener hears every source. With the feeder off, the default at `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs:116` "GetBoolean("VisibilityFeederEnabled", false)", no batch ever arrives. |
| SC-34 | 3.4 | 59 | No "muted client-side" audio in flight; a modified viewer cannot un-hide a hidden avatar or un-mute a moderator mute | MIXER | | MET | Hiding and moderation mute are applied server-side inside the per-listener mix: `MIXER src/janus_slvoice.c:3111` "mutes[j] = 1;" (moderation) and `:3126` "mutes[j] = 1;" (exclusion). The viewer's own peer control can only add a mute: `:3114` "if(s->peer_ctl[k].muted)". Excluded sources are also left out of the roster, `:873` "if(slv_roster_excludes(p->excluded, who))", and only the finished per-listener encode is relayed, `:2949` "gateway->relay_rtp(s->handle, &outp);". |
| SC-35 | 3.4 | 59 | The service is an MCU (server mixes) rather than an SFU (server forwards) | MIXER | | MET | Inbound RTP enters a per-session jitter buffer and is not forwarded, `MIXER src/janus_slvoice.c:3352` "janus_slvoice_jb_insert(session, seq, payload, plen);". The server mixes per listener, `:3175` "slv_mix_nminus1_stereo(", re-encodes, `:2926` "int enc_len = opus_encode_float(s->enc, frame, samples, s->outbuf + 12,", and relays the result, `:2949` "gateway->relay_rtp(s->handle, &outp);". |
| SC-36 | 3.5 | 63 | Any tap on a spatial or group mix requires an in-channel disclosure to affected participants | BOTH | | PARTIAL | **Met for connectors registered in the sim:** registration fires the attach notice, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VoiceConnector/VoiceConnectorRegistrar.cs:106` "pDisclosure?.OnAttach(pRecord);"; the region notice text, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VoiceConnector/VoiceConnectorDisclosure.cs:79` "is present in this region's voice."; wired unconditionally, `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VoiceConnector/VoiceConnectorModule.cs:157` "m_disclosure = new VoiceConnectorDisclosure(". **Not met:** (1) the mixer admits any `display` and room with no registration check, `MIXER src/janus_slvoice.c:2223` "json_object_get(root, "display")" → `:2280` "session->display = display ? g_strdup(display) : NULL;". The peers take the room straight from env, `MIXER connectors/common/config.py:15` ""room": os.environ.get("ROOM"),", so a tap joined with the API secret gets no disclosure. (2) There is no disclosure path for group or avatar-to-avatar mixes: registration computes only the estate room, `VoiceConnectorModule.cs:281` "int estateRoom = JanusAudioBridge.CalcRoomNumber(". |
| SC-37 | 3.5 | 63 | No plaintext media at rest without explicit configuration and disclosure | OPS | | PARTIAL | **Met — explicit configuration:** the recorder runs only under a compose profile, `MIXER docker-compose.yml:101` "profiles: [recorder]"; the peers refuse to start without their env, `MIXER connectors/common/config.py:21` "sys.exit(f"{prog}: missing required env"; the injector records only with `RECORD=1`, `MIXER connectors/injector/injector.py:52` ""record": os.environ.get("RECORD", "0") == "1",". **Not met:** recordings are plain WAV, `MIXER connectors/common/segments.py:119` "w = wave.open(path, "wb")", and nothing in the recorder or the mixer makes disclosure a precondition of writing (see SC-36's unregistered-tap gap). |
| SC-38 | 3.5 | 63 | The moderation and legal posture ships with the hook, not after the first incident | OPS | | UNMET | Criterion: capture is not consent-gated at the hook. `git -C D:/tranq-ais grep -n -i -E 'consent' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. `git -C D:/legion-voice-mixer grep -n -i -E 'consent' -- src connectors docker-compose.yml docker-entrypoint.sh env.sample etc ':!*.md'` → 0 matches. The only per-connector gate is injection, not capture: `SIM Addons/os-webrtc-janus/WebRtcVoiceRegionModule/VoiceConnector/VoiceConnectorRegistrar.cs:93` "if (!pRecord.MayInject)". |
| SC-39 | 4.1 | 73 | The mixer maintains, per connection: ICE state, DTLS state, inbound RTP rate, decode success, active mix memberships, outbound RTP rate, and last client acknowledgment | MIXER | | PARTIAL | **Met — 6 of 7 items**, all in the per-session query: ICE `MIXER src/janus_slvoice.c:1368` "json_object_set_new(info, "ice_state",", DTLS `:1369` "json_object_set_new(info, "dtls_state",", inbound RTP rate `:1386` "json_real(session->rtp_in_rate)", outbound RTP rate `:1387` "json_real(session->rtp_out_rate)", decode success `:1388` "json_object_set_new(info, "decode_ok",", mix memberships `:1446` "json_object_set_new(info, "mix_memberships",". **Not met — last client acknowledgment:** `git -C D:/legion-voice-mixer grep -n -i -E '\back\b\|acknowledg\|last_ack\|client_ack\|last_data_ts\|last_msg_ts\|last_client' -- src` → 0 matches. **Also:** ICE and DTLS are not separate states; both are reported from one up/down flag, `:1367` "(comment) so ICE/DTLS are reported coarsely", and `:1368`–`:1369` both use "up ? "connected" : "disconnected"". |
| SC-40 | 4.1 | 75 | Per-connection state exposed on the console: `voice status <avatar>` on the region/grid console | BOTH | | UNMET | `git -C D:/tranq-ais grep -n -i -E 'voice status' -- Addons Source` → 0 matches. `git -C D:/legion-voice-mixer grep -n -i -E 'voice status' -- src connectors docker-entrypoint.sh` → 0 matches. What exists instead: `SIM Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs:598` "AddCommand("Webrtc", false, "janus info"," and `:602` "janus list rooms", which prints per participant only `:667` "{0}/{1},muted={2},talking={3},pos={4}". |
| SC-41 | 4.1 | 76 | Per-connection state exposed on an admin surface: per-region and per-mixer web view | OPS | | UNMET | The criterion is literal: a web view. None exists in either tree. `git -C D:/legion-voice-mixer grep -n -i -E 'text/html\|<html\|status page\|dashboard' -- src etc connectors docker-entrypoint.sh docker-compose.yml Dockerfile` → 0 matches. `git -C D:/tranq-ais grep -n -i -E 'text/html\|<html\|status page\|dashboard' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. What exists, which is not a web view: the Janus Admin API's per-session JSON, `MIXER src/janus_slvoice.c:1354` "json_t *janus_slvoice_query_session(janus_plugin_session *handle) {", served because `MIXER docker-entrypoint.sh:244` "set_kv "$HTTP_JCFG" admin_http      true"; and the console commands listed under SC-40. |
| SC-42 | 4.1 | 77 | Per-connection state exposed to the client as a `diag` member pushed on the SLData channel | MIXER | | UNMET | `git -C D:/legion-voice-mixer grep -n -i -E '"diag"' -- src connectors` → 0 matches. The only data-channel sends are the JSON relay helper, `MIXER src/janus_slvoice.c:829` "gateway->relay_data(s->handle, &data);" (presence), and the power/VAD batch, `:2626` and `:2644` "gateway->relay_data(p->handle, &d);". |
| SC-43 | 4.1 | 77 | A viewer can render "connected; receiving 0 pkt/s from server" vs "receiving 340 pkt/s; output device delivered 0 frames", splitting server-fault from device-fault | VIEWER | | BLOCKED | Needs a viewer UI change. Constraint: stock SL/Firestorm viewer compatibility; viewer changes only when unavoidable, and then fork-only. |
| SC-44 | 4.2 | 81 | Every region offers a self-serve echo test | MIXER | | PARTIAL | **Met:** any participant in any mixer room can switch echo on: `MIXER src/janus_slvoice.c:3439` "if(d.fields_seen & SLV_FIELD_ECHO) {" → `:3442` "janus_slvoice_echo_start_locked(session);". **Not met — "self-serve":** the toggle is an SLData extension a stock viewer does not send (`:300` "(comment) which cannot send the {"echo":true} SLData toggle"). The only viewer-independent route is the process-wide autostart, which replaces every participant's mix: `:3322` "if(echo_autostart) {". |
| SC-45 | 4.2 | 81 | The echo test runs on request by SLData message | MIXER | | MET | The field is parsed at `MIXER src/sldata.c:213` "slv_get_bool(json_object_get(root, "echo"), &b)" and acted on at `MIXER src/janus_slvoice.c:3439` "if(d.fields_seen & SLV_FIELD_ECHO) {": start at `:3442` "janus_slvoice_echo_start_locked(session);", stop at `:3447` "janus_slvoice_echo_stop_locked(session);". |
| SC-46 | 4.2 | 81 | The echo test runs on request from a viewer menu | VIEWER | | BLOCKED | A viewer menu item is a viewer change. Constraint: stock SL/Firestorm viewer compatibility; viewer changes only when unavoidable, and then fork-only. |
| SC-47 | 4.2 | 81 | The mixer loops the user's own audio back with ~500 ms delay | MIXER | | MET | `MIXER src/janus_slvoice.c:135` "#define SLV_ECHO_DELAY_MS   500". The ring reads the delayed position, `:2899` "int rp = (wp + SLV_RING_SAMPLES - SLV_DELAY_SAMPLES) % SLV_RING_SAMPLES;", and that frame is sent back to the same participant, `:3173` "janus_slvoice_build_echo(s, frame, SLV_FRAME_SAMPLES);". |
| SC-48 | 4.2 | 81 | The echo test replaces the single grid-wide test region model with a capability available everywhere at negligible cost | OPS | | PARTIAL | **Met — available in every mixer room at negligible cost:** the delay ring is allocated only when echo is first enabled, `MIXER src/janus_slvoice.c:2762` "if(s->ring == NULL)" → `:2763` "s->ring = g_malloc0(". **Not met — as a replacement for a test region:** a user on a stock viewer cannot request it, `:300` "(comment) which cannot send the {"echo":true} SLData toggle". The only other route is the process-wide autostart, `:3322` "if(echo_autostart) {", which replaces every participant's mix. |
| SC-49 | 4.3 | 85 | If the mixer cannot deliver audio a listener should be receiving, it says so on the data channel | MIXER | | UNMET | Criterion: nothing is emitted on the data channel. `git -C D:/legion-voice-mixer grep -n -i -E '"diag"\|undeliver\|cannot deliver\|no_audio\|media_dead\|not receiving\|downstream' -- src` → 0 matches. The data channel carries only presence (`MIXER src/janus_slvoice.c:829` "gateway->relay_data(s->handle, &data);") and the power/VAD batch (`:2626`, `:2644`). The no-media reap writes only a server log line, `:3580` "reaped from room %"PRIu64": no media %us after join". |
| SC-50 | 4.3 | 85 | The viewer can surface the mixer's report that audio cannot be delivered | VIEWER | | BLOCKED | Surfacing a report in the viewer is a viewer change. Constraint: stock SL/Firestorm viewer compatibility; viewer changes only when unavoidable, and then fork-only. |
| SC-51 | 4.3 | 85 | The system must never render the appearance of working voice over a dead media path | BOTH | | PARTIAL | **Met:** an inactive source's indicator is zeroed, `MIXER src/janus_slvoice.c:2847` "g_atomic_int_set(&s->power_p, 0);", and a downed duplicate handle cannot override a live one, `:2605` "(comment) a downed handle never overwrites a live one". **Not met:** power/VAD come from the *source's* inbound decode, `:2886` "g_atomic_int_set(&s->vad, rms > SLV_VAD_RMS ? 1 : 0);". The batch goes to every listener whose data channel is open, `:2620` "if(!g_atomic_int_get(&p->dc_open))", with no check of that listener's outbound audio. A listener whose downstream audio is dead while the data channel lives still sees speaking indicators animate. |
| SC-52 | 4.4 | 89 | Structured per-session event log (join, ICE transitions, first media, stalls, teardown cause) | BOTH | | UNMET | `git -C D:/legion-voice-mixer grep -n -i -E 'notify_event\|events_is_enabled\|janus_events' -- src` → 0 matches. `git -C D:/legion-voice-mixer grep -n -i -E 'broadcast\|eventhandler\|evh' -- docker-entrypoint.sh Dockerfile docker-compose.yml env.sample etc` → 0 matches. `git -C D:/tranq-ais grep -n -i -E 'session ?event\|event ?log\|SessionLog\|teardown ?cause\|first ?media\|ice ?transition' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. Janus core event broadcast is off by default, `MIXER vendor/janus-gateway/src/janus.c:5552` "gboolean enable_events = FALSE;", and the entrypoint adds nothing, `MIXER docker-entrypoint.sh:274` "exec "$JANUS_BIN" "$@"". |
| SC-53 | 4.4 | 89 | The session event log is retained short-term for postmortems | OPS | | UNMET | Criterion: no bounded short-term retention of a session event log. `git -C D:/legion-voice-mixer grep -n -i -E 'session ?event\|event ?log\|teardown ?cause\|retention\|postmortem\|ring ?buffer' -- src docker-entrypoint.sh docker-compose.yml etc` → 0 matches. `git -C D:/legion-voice-mixer grep -n -i -E 'logging\|max-size\|max-file' -- docker-compose.yml` → 0 matches. |
| SC-54 | 4.5 | 93 | Fleet observability (activates at scale): per-mixer health, session counts, mix-deadline overrun rates, relay-vs-direct path ratios | BOTH | | PARTIAL | Criterion applied per metric. **Not met:** per-mixer health and relay-vs-direct path ratios; overrun rates and mixer-wide session counts are only partly present. **Mix-deadline overruns — partly:** a cumulative count per room, not a rate and not mixer-wide, `MIXER src/janus_slvoice.c:3217` "room->tick_overruns++;", exposed at `:1463` "json_object_set_new(hist, "overruns",". **Session counts — partly:** per room, inside each session's query only, `:1455` "json_object_set_new(info, "room_participants",". **Per-mixer health — not found:** `git -C D:/legion-voice-mixer grep -n -i -E 'health\|session_count\|num_sessions' -- src` → 1 match, an unrelated comment at `:1713`. The admin handler accepts only `peer_ctl_batch`, `:1958` "json_string("unknown_request")". **Relay-vs-direct ratio — not found:** `git -C D:/legion-voice-mixer grep -n -i -E 'srflx\|prflx\|relay_ratio\|candidate_type\|selected_pair\|relay.vs\|turn_used\|"relay"' -- src docker-entrypoint.sh etc` → 0 matches; the same pattern plus `\|overrun` over `git -C D:/tranq-ais … -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. |
| SC-55 | 4.5 | 93 | On a single-mixer grid fleet observability is one status page; nothing else changes | OPS | | UNMET | No status page in either tree. `git -C D:/legion-voice-mixer grep -n -i -E 'text/html\|<html\|status page\|dashboard' -- src etc connectors docker-entrypoint.sh docker-compose.yml Dockerfile` → 0 matches. `git -C D:/tranq-ais grep -n -i -E 'text/html\|<html\|status page\|dashboard' -- Addons/os-webrtc-janus ':!*.md'` → 0 matches. |
| SC-56 | 5 | 101 | Internal pipeline at 48 kHz float end-to-end; no resampling after ingest | MIXER | | | |
| SC-57 | 5 | 102 | No server-side noise suppression or AGC, ever | MIXER | | | |
| SC-58 | 5 | 103 | Talker ingest ≥ 32 kbps Opus | MIXER | | | |
| SC-59 | 5 | 103 | Listener mix 64–96 kbps stereo Opus | MIXER | | | |
| SC-60 | 5 | 103 | 20 ms frames | MIXER | | | |
| SC-61 | 5 | 104 | DTX/VAD-gated decode: silent talkers cost nothing before the RTP layer | MIXER | | | |
| SC-62 | 5 | 104 | A 100–200 ms release hold prevents word-onset chopping from DTX hangover | MIXER | | | |
| SC-63 | 5 | 105 | Encode-skip: listeners whose entire audible set is silent receive DTX and cost near-zero encode | MIXER | | | |
| SC-64 | 5 | 105 | Encode cost scales with audible listeners, not connected listeners | MIXER | | | |
| SC-65 | 5 | 106 | Degradation ladder under CPU pressure, in order: frame size up (20→30 ms) → distance tiers widened → far-field talkers shed from mixes → admission refusal (last resort) | MIXER | | | |
| SC-66 | 5 | 106 | Per-region tick isolation so one saturated region cannot starve its neighbors | MIXER | | | |
| SC-67 | 6 | 112 | No global spatial index | MIXER | | | |
| SC-68 | 6 | 112 | Culling and per-listener rendering with load-adaptive aggregation | MIXER | | | |
| SC-69 | 6 | 114 | Cull first: distance, VAD, inbound level | MIXER | | | |
| SC-70 | 6 | 115 | Direct per-source HRTF below a talker-count threshold | MIXER | | | |
| SC-71 | 6 | 115 | Azimuth binning engages only above the talker-count threshold | MIXER | | | |
| SC-72 | 6 | 115 | Crossfades over several 10 ms frames at bin crossings, pre-staged from position derivatives | MIXER | | | |
| SC-73 | 6 | 115 | At low occupancy the binning path and its artifacts don't run | MIXER | | | |
| SC-74 | 6 | 116 | Distance tiers: full HRTF with ITD near; amplitude panning + lowpass mid; single mono ambience sum far | MIXER | | | |
| SC-75 | 6 | 117 | Dirty-flagged coefficient recompute: stationary listener + stable talker set skips HRIR selection/interpolation setup per frame | MIXER | | | |
| SC-76 | 6 | 118 | Listener orientation honored from the SLData `lh` quaternion | MIXER | | | |
| SC-77 | 6 | 119 | Per-(listener, source) crossfade state kept in flat preallocated arrays sized at session setup; no per-transition allocation | MIXER | | | |
| SC-78 | 7.1 | 127 | Restores the Vivox-era "hear from camera" behavior without reopening the eavesdropping hole | BOTH | | | |
| SC-79 | 7.1 | 129 | Attenuation and HRTF may follow client-reported listener position within an estate-configurable leash radius of the avatar | BOTH | | | |
| SC-80 | 7.1 | 130 | Parcel/estate audibility is always computed from the avatar's authoritative position; camming never grants audio access the avatar's position wouldn't | SIM | | | |
| SC-81 | 7.2 | 134 | Performer mode: a per-avatar, estate-grantable flag | SIM | | | |
| SC-82 | 7.2 | 134 | Performer mode: high-bitrate stereo ingest | BOTH | | | |
| SC-83 | 7.2 | 134 | Performer mode: client signaled to disable NS/AGC for that stream | VIEWER | BLOCKED-BY-CONSTRAINT | | |
| SC-84 | 7.2 | 134 | Performer mode: exempt from far-tier degradation | MIXER | | | |
| SC-85 | 7.3 | 138 | Per-listener visibility matrix over a shared estate/region mix: avatars can be hidden from each other based on parcel access, enforced in the mix (§3.4) | BOTH | AMENDED | | |
| SC-86 | 7.3 | 138 | Participant lists, voice dots, and power levels are driven by the same SLData visibility set, so a hidden avatar is hidden in both audio and UI consistently, from one source of truth | MIXER | | | |
| SC-87 | 7.3 | 138 | A dot never lights with no audio behind it | MIXER | | | |
| SC-88 | 7.3 | 138 | Parcel privacy is enforced in the mix rather than as connection topology, removing SL's parcel-boundary reconnection stutter | BOTH | | | |
| SC-89 | 7.4 | 142 | Voice morphing: per-talker pitch/formant shifting applied at decode, before spatial encode | MIXER | | | |
| SC-90 | 7.4 | 142 | Morphing cost scales with morphed active talkers (typically 0–3), not listeners | MIXER | | | |
| SC-91 | 7.4 | 145 | Morphing's analysis-window latency (20–60 ms) is added to morphed talkers only | MIXER | | | |
| SC-92 | 7.4 | 147 | Recommendation: write PSOLA in-house (~few hundred lines) to keep the distribution unencumbered (SoundTouch is LGPL, Rubber Band is GPL/commercial) | MIXER | | | |
| SC-93 | 7.4 | 148 | Estates get a `no_morphing` flag | SIM | | | |
| SC-94 | 7.4 | 148 | Morph state is visible to moderators | BOTH | | | |
| SC-95 | 7.5 | 152 | A tap-and-inject API on the mixer (plain RTP or WebSocket), generalizing the requested post-processing hooks | MIXER | AMENDED | | |
| SC-96 | 7.5 | 154 | Taps: recording (consent-gated per §3.5) | BOTH | | | |
| SC-97 | 7.5 | 154 | Taps: transcription | MIXER | | | |
| SC-98 | 7.5 | 154 | Taps: future translation | MIXER | NOT-A-COMMITMENT | | |
| SC-99 | 7.5 | 155 | Injectors: NPC/bot TTS voices as first-class positioned sources | BOTH | | | |
| SC-100 | 7.5 | 155 | Injectors: external DJ/stream sources entering spatial voice as positioned audio rather than parcel media URLs | BOTH | | | |
| SC-101 | 7.5 | 156 | Effects sends: parcel-property environmental processing (reverb, echo zones), configurable by land settings | BOTH | | | |
| SC-102 | 7.6 | 160 | Estate-level mute/gain enforced in the mix | BOTH | | | |
| SC-103 | 7.6 | 160 | "Podium" mode granting designated speakers gain priority | BOTH | | | |
| SC-104 | 7.6 | 160 | Per-parcel voice zones (§7.3) as part of the moderation surface | BOTH | | | |
| SC-105 | 7.6 | 160 | All moderation is mixer-enforced and therefore not bypassable by modified viewers | MIXER | | | |
| SC-106 | 8 | 166 | Most deployments will be small; the small case is the default case | OPS | | | |
| SC-107 | 8 | 166 | A single-region standalone or small grid runs one mixer process alongside the simulator or grid services | OPS | | | |
| SC-108 | 8 | 166 | The small deployment is configured by one INI section | OPS | | | |
| SC-109 | 8 | 166 | The small deployment has no allocator, no fleet, and no external dependencies beyond the mixer itself | OPS | | | |
| SC-110 | 8 | 166 | Everything in §§3–7 works identically in the small mode | BOTH | | | |
| SC-111 | 8 | 166 | Scale features activate by configuration, not by code path | BOTH | | | |
| SC-112 | 8.1 | 170 | One mixer process serves spatial + group + P2P | MIXER | | | |
| SC-113 | 8.1 | 170 | Small-deployment diagnostics are the console command and one status page | OPS | | | |
| SC-114 | 8.1 | 170 | STUN only; TURN optional | OPS | | | |
| SC-115 | 8.1 | 170 | This must install with: build, one INI section, and nothing further | OPS | | | |
| SC-116 | 8.1 | 170 | "Voice works out of the box and tells you why when it doesn't" | BOTH | DERIVED | | |
| SC-117 | 8.2 | 174 | Spatial mixers scale horizontally by region | OPS | | | |
| SC-118 | 8.2 | 174 | The group/adhoc pool scales independently | OPS | | | |
| SC-119 | 8.2 | 174 | A simple placement map (region → mixer) replaces the single process | BOTH | | | |
| SC-120 | 8.2 | 174 | Adjacent regions preferentially co-located on one mixer | OPS | | | |
| SC-121 | 8.2 | 174 | Cross-region listening via multiple neighbor sessions summed client-side per the protocol | BOTH | | | |
| SC-122 | 8.3 | 178 | Placement service: bin-packing regions onto mixers by predicted load | OPS | | | |
| SC-123 | 8.3 | 178 | Sessions carry enough state to migrate | BOTH | | | |
| SC-124 | 8.3 | 179 | Admission control with backpressure: saturated mixers shed new sessions to other capacity; established sessions are protected | BOTH | | | |
| SC-125 | 8.3 | 180 | TURN fleet as its own capacity plan | OPS | | | |
| SC-126 | 8.3 | 180 | The diag vector (§4.1) distinguishes relay-path from direct-path sessions | MIXER | | | |
| SC-127 | 8.3 | 181 | Optional first-order ambisonic delivery: orientation-independent B-format mixes, rotated and binauralized viewer-side | VIEWER | | | |
| SC-128 | 8.3 | 181 | First-order ambisonic delivery is SDP-negotiated with fallback to server-side binaural stereo for stock viewers | MIXER | | | |
| SC-129 | 8.3 | 181 | B-format mixes dedupe across co-located listeners | MIXER | | | |
| SC-130 | 8.3 | 181 | The ambisonic delivery mitigation is wired in early (brutal to retrofit) | MIXER | | | |
| SC-131 | 8.3 | 182 | Abuse controls: rate-limited provisioning | SIM | | | |
| SC-132 | 8.3 | 182 | Abuse controls: per-account session caps | SIM | | | |
| SC-133 | 8.3 | 182 | Abuse controls: RTP source validation | MIXER | | | |
| SC-134 | 9 | 188 | Caps: `ProvisionVoiceAccountRequest` (JSEP offer/answer, `channel_type` local/multiagent, `parcel_local_id`, logout) | SIM | | | |
| SC-135 | 9 | 188 | Caps: `VoiceSignalingRequest` (trickled ICE, completion marker) | SIM | | | |
| SC-136 | 9 | 189 | SDP: fmtp mangle honored (`minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000`) | MIXER | | | |
| SC-137 | 9 | 190 | SLData channel client→mixer `j/l/sp/sh/lp/lh/m/ug` per the published format | MIXER | | | |
| SC-138 | 9 | 190 | SLData channel mixer→client per-peer `p/V/j/l` batched ~100 ms | MIXER | | | |
| SC-139 | 9 | 190 | SLData extensions, all optional and ignored by stock viewers: `diag` (§4.1), echo-test control (§4.2), morph state (§7.4), trust-domain disclosure (§3.2) | MIXER | | | |
| SC-140 | 9 | 191 | Cross-region: neighbor connections with primary flag, client-side summing, per the published model | BOTH | | | |
| SC-141 | 9 | 191 | Parcel changes within a region do not trigger connection changes (§7.3) | BOTH | | | |

## Flag rulings (John, 2026-09-14)

Every row flagged in pass 0, with the ruling now in its Flag column, the ruling's reason, and where set,
the criterion pass 1 must apply. Rulings are final.

| ID | Ruling | Reason / pass-1 criterion |
|---|---|---|
| SC-7 | DERIVED | Umbrella goal with no separate finding of its own. It derives from SC-41 (the admin web view) and the §4.4 session event log claims (SC-52, SC-53). |
| SC-23 | blank | Criterion: does the provisioning response carry the trust-domain classification as a field the client can read? |
| SC-26 | blank | Criterion: is any disclosure text or flag emitted to local users? |
| SC-30 | blank | Criterion: does the sim→mixer state carry a version epoch? |
| SC-31 | blank | Criterion: when the state is stale or none has arrived, does the mixer withhold audio rather than pass it? |
| SC-32 | blank | The "§6.1" cross-reference is a spec error; read it as §7.1 (camera-position listening). Criterion: is client-reported position used anywhere as a permission or authorization input? |
| SC-38 | blank | Criterion: is capture consent-gated at the hook, per §3.5? |
| SC-41 | blank | The criterion is literal: a per-region and per-mixer web view. An admin API or console command is not a web view; pass 1 records what it finds either way. |
| SC-49 | blank | Criterion: does the mixer emit anything on the data channel when it cannot deliver audio a listener should be receiving? |
| SC-53 | blank | Criterion: any bounded short-term retention of the session event log. |
| SC-54 | blank | Criterion: are the four named metrics (per-mixer health, session counts, mix-deadline overrun rates, relay-vs-direct path ratios) found anywhere? |
| SC-58 | blank | Criterion: the configured or negotiated Opus ingest bitrate floor. |
| SC-65 | blank | Not superseded. A real claim about load-adaptive degradation. |
| SC-68 | blank | Same family as SC-65: a real claim about load-adaptive behaviour. |
| SC-72 | blank | "10 ms frames" is a spec error against §5's 20 ms (SC-60); read it as "several frames". Criterion: are there crossfades at bin crossings at all? |
| SC-79 | blank | An ordinary claim. John points pass 1 at the Phase 3b camera leash (`SLV_LEASH_DIST`). |
| SC-83 | BLOCKED-BY-CONSTRAINT | Needs a viewer-side change. The standing constraint is stock SL/Firestorm compatibility, with viewer changes only when unavoidable and fork-only. Pass 1 records this row as blocked by that constraint. |
| SC-85 | AMENDED | The mechanism changed to per-parcel rooms with an estate-room fallback. The invariant (per-listener, enforced in the mix) stands and is still binding. |
| SC-88 | blank | Not superseded. The claim stands as written and is the one to test hardest: per-parcel rooms mean a parcel crossing is a room change, which is the connection-topology behaviour this claim says was removed. |
| SC-95 | AMENDED | Tap-and-inject takes the form of aiortc WebRTC peers joining a room normally, not plain RTP or WebSocket. Plain-RTP connectors were deferred by decision. |
| SC-98 | NOT-A-COMMITMENT | The spec itself says "future translation". |
| SC-108 | blank | A real claim about the small-deployment default case; not superseded. |
| SC-115 | blank | Same as SC-108. Pass 1 tests it honestly against what installing actually requires today. |
| SC-116 | DERIVED | Umbrella goal, like SC-7, with no separate finding of its own. |
| SC-130 | blank | A real claim, deferred by decision. The spec's own rationale is that retrofitting is expensive, so its status is a decision John owes rather than a defect. |
| SC-138 | blank | A real claim. Pass 1 measures the actual cadence against "~100 ms". |
| SC-141 | blank | Not superseded. Same family as SC-88 and equally binding. |

### Wording notes

These adjustments are not meant to change meaning:
- **SC-19:** the spec's verb phrase before "as two-party sessions" is omitted.
- **SC-92:** the spec's verb before "PSOLA in-house" is rendered "write".
- **SC-115:** the spec's final word after "one INI section" is rendered "and nothing further".

## Counts

### Per section

| Spec § | Claims | IDs |
|---|---|---|
| Header (line 4) | 2 | SC-1 – SC-2 |
| 1 Goals and non-goals | 14 | SC-3 – SC-16 |
| 2 Requirements provenance | 0 | — |
| 3.1 All media is server-relayed | 5 | SC-17 – SC-21 |
| 3.2 Voice-server trust domains | 7 | SC-22 – SC-28 |
| 3.3 Sim-authoritative permission enforcement | 4 | SC-29 – SC-32 |
| 3.4 Mixer output is the permission boundary | 3 | SC-33 – SC-35 |
| 3.5 Capture requires consent | 3 | SC-36 – SC-38 |
| 4.1 Per-connection state vector | 5 | SC-39 – SC-43 |
| 4.2 Built-in echo test | 5 | SC-44 – SC-48 |
| 4.3 Fail loud | 3 | SC-49 – SC-51 |
| 4.4 Session event log | 2 | SC-52 – SC-53 |
| 4.5 Fleet observability | 2 | SC-54 – SC-55 |
| 5 Media plane quality | 11 | SC-56 – SC-66 |
| 6 Spatial engine | 11 | SC-67 – SC-77 |
| 7.1 Camera-position listening | 3 | SC-78 – SC-80 |
| 7.2 Performer mode | 4 | SC-81 – SC-84 |
| 7.3 Parcel and estate voice zones | 4 | SC-85 – SC-88 |
| 7.4 Voice morphing | 6 | SC-89 – SC-94 |
| 7.5 Connector layer | 7 | SC-95 – SC-101 |
| 7.6 Moderation surface | 4 | SC-102 – SC-105 |
| 8 Deployment and scaling model | 6 | SC-106 – SC-111 |
| 8.1 Small | 5 | SC-112 – SC-116 |
| 8.2 Medium | 5 | SC-117 – SC-121 |
| 8.3 Large | 12 | SC-122 – SC-133 |
| 9 Protocol compatibility summary | 8 | SC-134 – SC-141 |
| 10 Open questions | 0 | — (see the appendix) |
| **Total** | **141** | SC-1 – SC-141 |

### Per Scope

| Scope | Claims |
|---|---|
| MIXER | 57 |
| SIM | 11 |
| BOTH | 44 |
| OPS | 24 |
| VIEWER | 5 |
| **Total** | **141** |

### Per flag ruling

| Flag | Claims | IDs |
|---|---|---|
| blank | 135 | all others |
| AMENDED | 2 | SC-85, SC-95 |
| BLOCKED-BY-CONSTRAINT | 1 | SC-83 |
| DERIVED | 2 | SC-7, SC-116 |
| NOT-A-COMMITMENT | 1 | SC-98 |
| **Total** | **141** | |

### Pass 1a verdicts (§1, §3, §4: SC-3 – SC-55)

| Section | MET | PARTIAL | UNMET | BLOCKED | DERIVED | UNKNOWN | Rows |
|---|---|---|---|---|---|---|---|
| §1 (SC-3 – SC-16) | 9 | 3 | 1 | 0 | 1 | 0 | 14 |
| §3 (SC-17 – SC-38) | 6 | 6 | 10 | 0 | 0 | 0 | 22 |
| §4 (SC-39 – SC-55) | 2 | 5 | 7 | 3 | 0 | 0 | 17 |
| **Total** | **17** | **14** | **18** | **3** | **1** | **0** | **53** |

## Appendix — §10 open questions carried from the spec

These are unresolved questions carried from the spec, not claims. They are not inventoried and not
answered here.

| Line | Question (verbatim) |
|---|---|
| 195 | 1. Hypergrid group/P2P policy: which grid's pool hosts a call between users of two federated grids, and what does each party's client disclose? |
| 196 | 2. FOA viewer-side decode: target viewer(s) and negotiation details; who carries the viewer patch. |
| 197 | 3. Recording/consent defaults per jurisdiction for the connector layer. |
| 198 | 4. Session migration mechanics for live mixer drain at scale (needed for §8.3; over-engineering for §8.1 — gate behind the placement service). |
