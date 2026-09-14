# Voice configuration register (sim side)

The sim-side counterpart of the mixer's "Configuration compatibility rule" and
"Knob register" (`legion-voice-mixer/docs/docker-notes.md`), for the ini keys the
`os-webrtc-janus` addon reads. Written 2026-09-14 (mixer slice 8b) from the tree at
`feature/ais-v3` `c8bc0a7cfa`; every file:line below was checked by grep. The live
column is `D:\legiongrid\regionserver\config\OpenSim.ini` on that date.

## Configuration compatibility rule

Upgrading a regionserver onto a new build must not silently change what a running
grid does.

- **A new key's default reproduces the behaviour of the build before the key
  existed.**
- **Narrowing is always opt-in**: refusals, guards, timeouts that end work early,
  allow-lists. The operator sets the narrower value; an upgrade never does.
- **Security- and connectivity-relevant keys log their effective value at module
  start**, so a wrong value is visible in the region log before anyone logs in.
  Secrets (`APIToken`, `AdminAPIToken`) are logged as set/unset only. (Not yet
  audited against the ten keys below; a follow-up slice should check it.)
- **Every deploy record lists "Behaviour changes on upgrade" and "One-time
  migrations"**, even when empty. The mixer's `docs/RELEASES.md` carries the sim
  releases that affect mixer or connector operators.

A default that must break compatibility is allowed only if it is recorded below as a
**pre-rule exception, retained deliberately**, with what changed and why.

## Knob register

File legend (all under `Addons/os-webrtc-janus/`):
- **RM** = `WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs`
- **SM** = `WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.cs`
- **JS** = `Janus/WebRtcJanusService.cs`
- **VCM** = `WebRtcVoiceRegionModule/VoiceConnector/VoiceConnectorModule.cs`
- **Ex** = `os-webrtc-janus.ini.example`

The shipped `os-webrtc-janus.ini` contains none of these keys.

| Key | Section | Default (read at) | Example ini | Live | Since | Before the key existed |
|---|---|---|---|---|---|---|
| `RequestTimeoutMs` | `[JanusWebRtcVoice]` | 5000 = `JanusSession.DefaultRequestTimeoutMs` (JS:122; ≤0 falls back to 5000) | Ex:51, commented `5000` | unset → 5000 | `3ca74633df` 2026-09-13 (O-50/O-51) | No timeout: after the ack a request waited on its completion source forever. **Pre-rule exception, retained deliberately** (below). |
| `RefusalCacheSeconds` | `[WebRtcVoice]` | 5 = `ProvisionRefusalCache.DefaultSeconds` (RM:114, SM:89; negative → 0 = off) | absent | unset → 5 | `1b88989e9f` 2026-09-13 (O-72) | No cache: every viewer retry re-ran the estate/parcel checks and the provision. **Pre-rule exception, retained deliberately** (below). |
| `VisibilityTickMs` | `[WebRtcVoice]` | 250 (RM:117) | absent | 250 | `e044ee1670` 2026-08-16 (Phase 3a feeder) | Feature did not exist; no periodic visibility work. **Pre-rule exception, retained deliberately** (below). |
| `VisibilityEmitEnabled` | `[WebRtcVoice]` | false (RM:118) | absent | true | `fc1454ea3e` 2026-08-16 (peer_ctl_batch sender) | The feeder computed the matrix but nothing was sent to the mixer. Default `false` reproduces that. |
| `VisibilityFeederEnabled` | `[WebRtcVoice]` | false (RM:116) | absent | true | `e044ee1670` 2026-08-16 | No feeder thread. Default `false` reproduces that. |
| `AllowNpcVoice` | `[WebRtcVoice]` | false (SM:86 enforced; VCM:90 read for disclosure) | Ex:34, commented `false` | unset → false | `7240797fc8` 2026-08-31 (S-CON-1) | No NPC guard: NPC presences provisioned voice like avatars. **Pre-rule exception, retained deliberately** (below). |
| `VoiceRangeMetres` | `[WebRtcVoice]` | 20 = `DefaultVoiceRangeMetres` (VCM:91) | Ex:37, commented `20` | unset → 20 | `d95754509d` 2026-08-31 (S-CON-3) | Feature did not exist; the value only sizes the new connector proximity notice. |
| `StunServers` | `[WebRtcVoice]` | empty (RM:115); empty = the addon adds no `stun-servers` feature | Ex:26, active `stun:stun.l.google.com:19302` | `stun:stun.l.google.com:19302` | `5e0f289fc1` 2026-08-15 | The addon advertised nothing. The core's own `StunServers` (`GridInfo.cs:519`, since `fc607035c8`) could already advertise `stun-servers`. Empty reproduces the addon's prior behaviour. |
| `PluginName` | `[JanusWebRtcVoice]` | `janus.plugin.audiobridge` (JS:112) | absent | `janus.plugin.slvoice` | `ade2b29f6b` 2026-08-13 | Hard-coded `janus.plugin.audiobridge` (`JanusAudioBridge.cs:42` at the parent). Same literal as the default. |
| `AdminTimeoutMs` | `[JanusWebRtcVoice]` | 5000 (RM:130) | absent | unset → 5000 | `fc1454ea3e` 2026-08-16 (first read in SM, moved to RM in `de7d4ad801`) | No admin sends existed; it only bounds the new peer_ctl_batch emission, which is off by default. |

### Pre-rule exceptions, retained deliberately

- **`RequestTimeoutMs` (5000).**
  - *What changed:* a Janus request whose completion event arrives after 5 s now
    fails with a synthetic `"timeout"` instead of waiting forever.
  - *Why accepted:* the unbounded wait pinned a caps thread permanently whenever an
    event was lost (O-50). An answer later than 5 s is already a failed join to the
    viewer, so no working install relied on the old behaviour.
- **`RefusalCacheSeconds` (5).**
  - *What changed:* a refused or failed provision is replayed from cache for 5 s
    per agent instead of re-running the checks and the provision.
  - *Why accepted:* the viewer retries every non-2xx immediately with no backoff.
    Without the cache, a single refusal became a ~2 Hz provision storm against the
    sim and the mixer (O-72, seen live 2026-09-13). 5 s only delays a refusal that
    has just been lifted, and `0` restores the old behaviour.
- **`AllowNpcVoice` (false).**
  - *What changed:* NPC presences are refused voice unless they are a registered
    connector identity.
  - *Why accepted:* an unregistered NPC in voice bypasses the connector policy
    record, its moderation mute and its disclosure (S-CON-1, O-46 class). No live
    install used NPC voice before connectors, and registered connectors are
    unaffected.
- **`VisibilityTickMs` (250).**
  - *What changed:* enabling the visibility feeder brought a 250 ms periodic matrix
    pass that did not exist before.
  - *Why accepted:* the tick is inert while `VisibilityFeederEnabled=false` (the
    default), so the upgrade alone changes nothing. The feeder is itself an explicit
    opt-in, and 250 ms is the rate the visibility design was measured at.

## Other voice keys read (not yet registered)

- `[WebRtcVoice]`
  - `Enabled`: SM:82, JS:101, RM:108, `WebRtcVoiceServiceConnector.cs:60`, `WebRtcVoiceServerConnector.cs:62`
  - `MessageDetails`: RM:111
  - `SpatialVoiceService`: SM:94
  - `NonSpatialVoiceService`: SM:95
  - `WebRtcVoiceServerURI`: `WebRtcVoiceServiceConnector.cs:63`
  - `LocalServiceModule`: `WebRtcVoiceServerConnector.cs:71`
  - `VisibilityRoomSendConcurrency`: RM:121, default 4
  - `NpcNameToken`: VCM:89, default "NPC"
- `[JanusWebRtcVoice]`
  - `JanusGatewayURI`: JS:105
  - `APIToken`: JS:106
  - `JanusGatewayAdminURI`: JS:107, RM:128
  - `AdminAPIToken`: JS:108, RM:129
  - `MessageDetails`: JS:120
- Grid id (`Janus/JanusAudioBridge.cs:75-80`): `GatekeeperURI`, `[GatekeeperService] ExternalName`, `[GridService] Gatekeeper`.
- `[VoiceConnector.<name>]` (`VoiceConnector/VoiceConnectorRegistry.cs:98-153`): `Enabled`, `NpcFirstName`, `NpcLastName`, `Scope`, `Position`, `MayInject`, `AuthorisedBy`, `InjectSourceUrl`, `Region`.

## Notes

- **`StunServers` has two readers.** Both the addon's `[WebRtcVoice] StunServers`
  and the core's `StunServers` (`[Startup]` etc., `Scene.cs:1328-1331`) add
  `stun-servers` to SimulatorFeatures. Which one wins when both are set is not
  established. Live sets only the addon key.
- **The shipped ini lacks most of these keys.** Neither `os-webrtc-janus.ini` nor
  the example carries `VisibilityFeederEnabled`, `VisibilityEmitEnabled`,
  `VisibilityTickMs`, `AdminTimeoutMs` or `PluginName`, though live sets several of
  them (O-21 / O-57).
