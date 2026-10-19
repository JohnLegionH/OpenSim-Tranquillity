# WebRTC voice: configuration reference

Every key read by the `os-webrtc-janus` addon, with the default the code uses when the key is absent.
The shipped file is `Addons/os-webrtc-janus/os-webrtc-janus.ini`; copy it into the `config` folder
(or whatever `[Startup] inidirectory` names) of the region server. For an overview, a minimal set up
and the feature descriptions, see [README.md](README.md).

Conventions used below:

- "Slvoice mixer" means a Janus plugin that implements the `janus.plugin.slvoice` protocol (see the README).
  The stock `janus.plugin.audiobridge` plugin does not understand the features marked this way.
- Booleans accept `true` / `false`.
- Keys that are commented out in the shipped file show their default and behave as if absent.
- Secrets are never logged. Do not commit real tokens or secrets to a repository.

## Where each section is read

| Section | Read by |
|---|---|
| `[WebRtcVoice]` | The region server (all feature switches), and Robust when it hosts the grid voice service (`Enabled`, `MessageDetails`, `SpatialVoiceService`, `NonSpatialVoiceService`, `LocalServiceModule`, `AllowNpcVoice`, `RefusalCacheSeconds`). |
| `[JanusWebRtcVoice]` | Whichever process talks to Janus: the region server for a region-local Janus, Robust for the grid service. The region server also reads `JanusGatewayAdminURI`, `AdminAPIToken` and `AdminTimeoutMs` from its own copy of this section for visibility emission, even when other voice traffic goes through Robust. |
| `[VoiceConnector.<name>]` | The region server, only when `ConnectorsEnabled = true`. |

## [WebRtcVoice]

### Core

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `Enabled` | `false` | Master switch. With it off the addon does nothing. | - |
| `SpatialVoiceService` | empty | `dll:class` of the service handling regional ("local") voice. `WebRtcJanusService.dll:WebRtcJanusService` talks to a Janus gateway directly; `WebRtcVoice.dll:WebRtcVoiceServiceConnector` forwards to a grid service. The shipped file sets the Janus one. | At least one of the two service keys must be set, or voice disables itself. |
| `NonSpatialVoiceService` | the value of `SpatialVoiceService` | Same, for non-spatial voice (groups, calls, conferences). If both keys name the same class, one service instance serves both roles. | - |
| `WebRtcVoiceServerURI` | empty | URL of the grid voice service. Used by `WebRtcVoiceServiceConnector`. | The grid service connector configured on Robust. |
| `LocalServiceModule` | `WebRtcVoiceServiceModule.dll:WebRtcVoiceServiceModule` | Robust side only (`WebRtcVoiceServerConnector`): the service that handles the requests Robust receives. Normally left alone. | - |
| `MessageDetails` | `false` | Write every message sent to and received from the viewer to the log. Very verbose; for debugging. | - |

### Provisioning and viewer-facing settings

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `StunServers` | empty | Comma-separated full ICE URIs (scheme included, no quotes), e.g. `stun:stun.l.google.com:19302`. Stock viewers need one to connect from outside the grid's network. Advertised to viewers as the `stun-servers` simulator feature. Empty: the feature is not added by the addon. Whether the addon's value or the core server's own `StunServers` setting wins when both are set has not been verified. | Works with either plugin. |
| `RefusalCacheSeconds` | `5` | After a voice provision is refused or fails, the same answer is replayed to that viewer's immediate retries for this many seconds without repeating the estate and parcel checks or calling Janus. `0` (or a negative value) turns the cache off. | - |
| `AllowNpcVoice` | `false` | Allow any NPC presence to obtain voice. When `false`, an NPC is refused unless it is a registered connector identity (see `ConnectorsEnabled`). Real avatars are never affected. | - |

### Avatar-to-avatar calls, group voice, conferences

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `A2AVoiceEnabled` | `false` | Handle the viewer's "call" and "start p2p voice" chat session requests. Off: the chat session handler answers as it does without the feature. | Verified only against the slvoice mixer. |
| `GroupVoiceEnabled` | `false` | Group voice sessions. Membership and powers are looked up through the region's Groups module. | Groups module. Verified only against the slvoice mixer. |
| `GroupVoiceRequireVoicePower` | `true` | Require the group role power `GP_SESSION_VOICE` as well as `GP_SESSION_JOIN`. `false` requires join only. | `GroupVoiceEnabled`. |
| `GroupVoiceCap` | `50` | Largest number of seats in one group session. Values above 110 are lowered to 110 (the mixer's room limit as mirrored in the code); values below 2 are raised to 2; 0 or negative means 50. | `GroupVoiceEnabled`. |
| `AdhocVoiceEnabled` | `false` | Ad-hoc voice and conference text ("start conference"). Off: "start conference" gets an empty 200 reply, as without the feature. | Groups module for the conference text session. Verified only against the slvoice mixer. |

### Visibility (who may hear whom)

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `VisibilityFeederEnabled` | `false` | Run the per-region visibility feeder: it works out who may hear whom (estate bans, parcel bans, "see avatars" hiding, voice moderation). With emission off it runs matrix-only (diagnostics). Also registers the console command `show voice visibility`. | Slvoice mixer to have any effect. |
| `VisibilityEmitEnabled` | `false` | Send the computed result to the mixer as `peer_ctl` batches over the Janus admin API. Only takes effect with the feeder on. If the admin URI or token is missing, the region logs a warning and runs matrix-only. | Feeder; `[JanusWebRtcVoice]` `JanusGatewayAdminURI` and `AdminAPIToken`; slvoice mixer. |
| `VisibilityTickMs` | `250` | Milliseconds between visibility recomputations. | Feeder. |
| `VisibilityRoomSendConcurrency` | `4` | Number of rooms addressed concurrently within one batch send. | Emission. |
| `VisibilityArmingEnabled` | `false` | Create spatial rooms declaring the sim as their visibility authority (with epochs and heartbeats). The mixer withholds audio from a room whose authority goes silent. Takes effect only when the feeder and emission are also on. | Slvoice mixer; feeder; emission. |

### Join capability and moderation

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `JoinCapabilityEnabled` | `false` | The region signs a short-lived (60 seconds) join capability for each avatar join and sends it to the mixer. If the secret is empty, an error is logged and the feature stays off. | Slvoice mixer; `[JanusWebRtcVoice]` `JoinCapabilitySecret`, equal to the mixer's secret. |
| `VoiceModerationEnabled` | `false` | Advertise the `SpatialVoiceModerationRequest` capability to viewers and register the console commands `show voice moderation`, `voice moderation mute`, `voice moderation unmute`. | Slvoice mixer; `VisibilityFeederEnabled` (moderation state is held by the feeder). |

### NPC voice connectors

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `ConnectorsEnabled` | `false` | Load the `[VoiceConnector.<name>]` records and start their NPCs. Off: no record is read. | Slvoice mixer; the NPC module. |
| `NpcNameToken` | `NPC` | Word that the first or last name of every connector NPC must be exactly equal to (case-sensitive). A record whose NPC name lacks it is refused. | `ConnectorsEnabled`. |
| `VoiceRangeMetres` | `20` | Range, in metres, used for the notice that tells nearby avatars a voiced NPC is close. | `ConnectorsEnabled`. |

`AllowNpcVoice` is also read by the connector module, but its default (`false`) is the same everywhere.

## [JanusWebRtcVoice]

Required for any process that talks to Janus. If `JanusGatewayURI`, `APIToken`, `JanusGatewayAdminURI`
or `AdminAPIToken` is empty the Janus service logs an error and disables itself; it also disables itself
if it cannot connect to Janus at startup.

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `JanusGatewayURI` | empty (required) | URL of the Janus REST API, e.g. `http://janus.example.org:14223/voice`. | A running Janus gateway. |
| `APIToken` | empty (required) | API secret for the Janus REST API. Must equal the gateway's. | - |
| `JanusGatewayAdminURI` | empty (required) | URL of the Janus admin API. Also used for visibility emission. | - |
| `AdminAPIToken` | empty (required) | Admin API secret. Must equal the gateway's. | - |
| `MessageDetails` | `false` | Write every message sent to and received from Janus to the log. Very verbose. | - |
| `PluginName` | `janus.plugin.audiobridge` | Janus plugin the region attaches voice handles to. `janus.plugin.slvoice` selects a spatial mixer implementing that protocol. | The named plugin installed in Janus. |
| `RequestTimeoutMs` | `5000` | Milliseconds an "ack"-answered Janus request waits for its result before failing. Zero or negative falls back to 5000. | - |
| `AdminTimeoutMs` | `5000` | Milliseconds a Janus admin API request waits for its answer (used by visibility emission). | - |
| `JoinCapabilitySecret` | empty | Secret used to sign join capabilities, for avatars and for connectors. Never logged. Must match the mixer. | Slvoice mixer. |

Grid identity: room numbers for non-spatial sessions are derived from the grid's gatekeeper address, so two grids
sharing one mixer do not collide. It is read from `GatekeeperURI` (in `[Hypergrid]`, `[Startup]` or `[Const]`), else
`[GatekeeperService] ExternalName`, else `[GridService] Gatekeeper`. If none is set, a warning is logged and the
rooms are derived without a grid identity.

## [VoiceConnector.\<name\>]

One section per connector, read only when `ConnectorsEnabled = true`. The section name after the dot is the
record name. Defaults apply when the key is missing; a record that breaks a rule below is refused (a warning
names the reason) and does not load at all.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | The record is skipped unless `true`. |
| `NpcFirstName`, `NpcLastName` | empty | The NPC's name. Both are required, and one of the two must equal `NpcNameToken`. Full names must be unique across records. |
| `Position` | empty (required) | Region-local position of the NPC, `<x, y, z>`. An unparsable value refuses the record. |
| `Scope` | `estate` | Only `estate` is supported; anything else refuses the record. |
| `MayInject` | `false` | `false`: recording only. The NPC is muted at registration so nothing it sends is heard. This mute is enforced through the visibility feeder, so with the feeder off a `MayInject = false` connector is not started. `true` lets it speak. |
| `AuthorisedBy` | empty | Free text naming who authorised the record. Not validated. |
| `Region` | empty | Optional scene name. Set: the record starts only in that region. Absent: it starts in every region the instance hosts. |
| `InjectSourceUrl` | empty | Optional URL the region posts the NPC's chat text to for text-to-speech injection. |
| `CapabilitySecret` | empty | Optional. At least 32 characters; a shorter value is ignored with a warning. When set, the region serves `POST /voice/connector/<name>/join-cap`, which a connector program calls with `Authorization: Bearer <secret>` to obtain a join capability. Minting also needs `JoinCapabilityEnabled` and `JoinCapabilitySecret`. If the instance hosts more than one region, also set `Region`. Over a network, put the endpoint behind TLS or a private network. |

The connector NPC's agent id is derived from the grid identity, the region name and the record name, so it is the
same after a restart. Renaming the section or the region gives the connector a new id.
The id is a version 5 (name-based) UUID in the namespace derived from `urn:opensim:webrtc-voice:connector`.

Connector disclosure cannot be turned off: connector NPCs always carry the name token, and the region always
issues its entry and proximity notices.
