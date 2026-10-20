# os-webrtc-janus

Addon-module for [OpenSimulator] to provide webrtc voice support
using Janus-gateway.

For an explanation of background and architecture, 
this project was presented at the
[OpenSimulator Community Conference] 2024
in the presentation
[WebRTC Voice for OpenSimulator](https://www.youtube.com/watch?v=nL78fieIFYg).

This addon works by taking viewer requests for voice service and
using a separate, external [Janus-Gateway WebRTC server].
This can be configured to allow local region spatial voice
and grid-wide group and spatial voice. See the sections below.

For running that separate Janus server, check out
[os-webrtc-janus-docker] which has instructions for running
Janus-Gateway on Linux and Windows WSL using Docker.

Instructions for:

- [Building into OpenSimulator](#Building): Build OpenSimulator with WebRTC voice service
- [Configuring Simulator for Voice Services](#Configure_Simulator)
- [Configuring Robust Grid Service](#Configure_Robust)
- [Configure Standalone Region](#Configure_Standalone)
- [Managing Voice Service](#Managing_Voice) (console commands, etc)

**Note**: Janus's stock `AudioBridge` plugin, the default, mixes every room flat: there is no
distance falloff or panning, and the addon's per-listener visibility, join capability and
connector features need a spatial mixer that implements the `janus.plugin.slvoice` protocol.
Calls, group voice, conferences and moderation work with either plugin. The table in
[Docs/voice/README.md](../../Docs/voice/README.md) lists each feature and what it needs.

<a id="Known_Issues"></a>
## Known Issues

- With the stock plugin there is no spatial audio
- One can see your own "white dot" but you don't see other avatar's white dots
- No individual volume control

And probably more found at [os-webrtc-janus issues](https://github.com/Misterblue/os-webrtc-janus/issues).

<a id="Building"></a>
## Installing voice

Working voice needs:
- the region server built with this addon;
- two INI sections in the region server's config;
- a Janus gateway reachable from the region server, running either Janus's stock AudioBridge plugin
  (the default) or a spatial mixer that implements the `janus.plugin.slvoice` protocol.

Optional features (avatar-to-avatar calls, group voice, ad-hoc voice, visibility, join capability,
moderation, connectors) are off until their switches are set; see `Docs/voice/README.md` and
`Docs/voice/configuration.md`.

### 1. Build the region server

In this tree the addon is a project reference of the region server, so the normal build includes it:

```
dotnet build Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj --configuration Release
```

### 2. Configure the region server: two INI sections

Copy `os-webrtc-janus.ini` into the `config` folder under the directory the region server runs from.
The region server reads every `.ini` file in that folder at startup; the folder name is
`[Startup] inidirectory`, default `config`. The file has two sections, `[WebRtcVoice]` and
`[JanusWebRtcVoice]`, and a region using its own Janus gateway needs both.

**Keys with no working default. You must set these:**

| Key | Section | Shipped value | Set it to |
|---|---|---|---|
| `Enabled` | `[WebRtcVoice]` | `false` | `true` |
| `JanusGatewayURI` | `[JanusWebRtcVoice]` | placeholder | the gateway's voice URI, for example `http://janus.example.org:14223/voice` |
| `APIToken` | `[JanusWebRtcVoice]` | placeholder | the gateway's API secret: a random value of at least 32 characters (a shorter one logs a warning at startup) |
| `JanusGatewayAdminURI` | `[JanusWebRtcVoice]` | placeholder | the gateway's admin URI |
| `AdminAPIToken` | `[JanusWebRtcVoice]` | placeholder | the gateway's admin secret: a random value of at least 32 characters (a shorter one logs a warning at startup) |

**Keys with defaults.** The shipped file lists them commented out with their defaults:

| Key | Section | Default | Notes |
|---|---|---|---|
| `PluginName` | `[JanusWebRtcVoice]` | `janus.plugin.audiobridge` | `janus.plugin.slvoice` selects a spatial mixer that implements that plugin. |
| `RemoveEmptyRooms`, `EmptyRoomGraceSeconds` | `[JanusWebRtcVoice]` | `true`, `60` | Remove a room this service created once it has stood empty for the grace period. The stock AudioBridge never removes an emptied room. Rooms the service did not create are never removed, and the slvoice mixer removes its own. |
| `StunServers` | `[WebRtcVoice]` | empty | Comma-separated STUN URIs advertised to viewers as the `stun-servers` simulator feature. Stock viewers need a valid entry. If the core server's own `StunServers` setting is also set, the core server's value is the one advertised. |
| `A2AVoiceEnabled`, `GroupVoiceEnabled`, `AdhocVoiceEnabled` | `[WebRtcVoice]` | `false` | Avatar-to-avatar calls, group voice, and ad-hoc conferences with conference text. Group voice and conference text need the Groups module. They work with either plugin. |
| `GroupVoiceRequireVoicePower`, `GroupVoiceCap` | `[WebRtcVoice]` | `true`, `50` | Group voice requires the role power to speak as well as to join; the most seats in one group session (never above 110, never below 2). |
| `MaxNonSpatialSessions`, `MaxSessionsPerAgent`, `ConferenceRequestBurst`, `ConferenceRequestRefillSeconds` | `[WebRtcVoice]` | `5000`, `20`, `5`, `10` | Limits on the call sessions one region server process holds, used when `SessionStoreMode` is `local`. With `grid` the same limits are set in `[WebRtcVoiceSessionService]` on the grid server. |
| `CarrierSecret` | `[WebRtcVoice]` | unset | At least 32 characters, the same on every region server of a grid that runs regions in more than one process, when `SessionStoreMode` is `local`. See "Sharing call sessions between region servers" below. |
| `RefusalCacheSeconds` | `[WebRtcVoice]` | `5` | Seconds a refused voice provision is remembered, so a viewer's immediate retries are answered without repeating the checks. `0` turns it off. |
| `RequestTimeoutMs`, `AdminTimeoutMs` | `[JanusWebRtcVoice]` | `5000`, `5000` | Milliseconds an "ack"-answered Janus request waits for its result, and a Janus admin API request waits for its answer. |
| `PositionFeedEnabled`, `PositionFeedMode` | `[WebRtcVoice]` | `false`, `observe` | The region reports each participant's position to the mixer. Needs the slvoice mixer. `enforce` is accepted but not active yet. Tuning keys are in the shipped ini file. |
| `CallModerationEnabled`, `CallModerationRevalidateSeconds`, `CallRemovalMinutes` | `[WebRtcVoice]` | `false`, `60`, `0` | Moderation of a group call from the viewer's participant list: a member holding Moderate Group Chat mutes one member's voice or text, or mutes and unmutes everyone's voice; the mutes end when the last member leaves the call. The power is checked on every request and a moderator is never muted. A voice mute is carried out by the slvoice mixer's mute channel or by the stock AudioBridge's own participant mute; one that cannot be carried out is answered 501 or 503 and is not recorded. Needs `GroupVoiceEnabled` or `AdhocVoiceEnabled`. Separate from `VoiceModerationEnabled`, which moderates a parcel's voice. Conferences, which have no moderator menu in the viewers, are moderated from the region console: `show voice calls`, `show voice call <call-id>`, `voice call mute|unmute <call-id> <agent> voice|text`, `voice call mute-all|unmute-all <call-id>`, `voice call remove <call-id> <agent> [reason]` and `voice call readmit <call-id> <agent>` (the same commands work on a group call). A removal from the console: the member's voice is dropped, their viewer is told to close the call, and a call, an accepted invitation, an invitation or a voice provision is refused until the call ends (or for `CallRemovalMinutes`, when above 0). A group's text is not touched by a removal; a conference member leaves the conference's text. A group member who loses the powers the call needs is let go on the next check, without a removal being recorded. |
| `VisibilityFeederEnabled`, `VisibilityEmitEnabled` | `[WebRtcVoice]` | `false` | Send per-listener visibility to a mixer that understands it. Need `JanusGatewayAdminURI` and `AdminAPIToken`. They, `VisibilityArmingEnabled`, `JoinCapabilityEnabled`, `ConnectorsEnabled` and `PositionFeedEnabled` need the slvoice mixer: with the stock plugin each one that is on logs a warning at start and stays off. `VoiceModerationEnabled` works on both: on the stock plugin a mute is the AudioBridge's own mute of the participant. |

**A mixer on another host needs TLS or a private network.** The API and admin secrets are sent with every
request, so over plain `http://` they cross the network in clear. Use `https://` URLs for `JanusGatewayURI` and
`JanusGatewayAdminURI`. `https://` URLs are validated as before: the certificate must chain to a trusted root and name
the host contacted. No setting turns validation off. If the certificate comes from
a private CA, set `JanusGatewayCACertificateFile` to that CA's certificate file (PEM or DER), which is trusted alongside the
system roots. Details: `Docs/voice/configuration.md`,
"Securing the connection to the mixer".

### Upgrading

With every optional switch left off, the region behaves as the previous release did, except for
the hardening of existing code (bounded Janus requests, provision refusal handling). To keep a spatial
mixer after upgrading from a build whose default plugin was `janus.plugin.slvoice`, set
`PluginName = janus.plugin.slvoice` and the visibility keys explicitly.

<a id="Configure_Simulator"></a>
## Configure a Region for Voice

Step 2 of [Installing](#Building) copied `os-webrtc-janus.ini` into the `config` directory. The
region server reads every `.ini` file in that directory, so this copy adds the configuration for
`os-webrtc-janus`. That file is what you configure for the simulator and region.

The sample `.ini` file has two sections: `[WebRtcVoice]` and `[JanusWebRtcVoice]`.
The `WebRtcVoice` section configures the what services the simulator uses
for WebRtc voice. The `[JanusWebRtcVoice]` section configures any connection
the simulator makes to the Janus server. The latter section is only updated
if this simulator is using a local Janus server for spatial voice.

The values for `SpatialVoiceService` and `NonSpatialVoiceService` point
either directly to a Janus service or to a Robust grid server that is providing
the grid voice service. Both these options are in the sample `os-webrtc-janus.ini`
file and the proper one should be uncommented.

The viewer makes requests for either spatial voice (used in the region and parcels)
or non-spatial voice (used for group chats or person-to-person voice conversations).
`os-webrtc-janus` allows these two types of voice connections to be handled by
different voice services. Thus there are two different configurations:

- all voice service is provided by the grid (both spatial and non-spatial point to a robust service), and
- the region simulator provides a local Janus server for region spatial voice while the grid service is used for group chats

#### Grid Only Voice Services

The most common configuration will be for a simulator that uses the grid supplied
voice services. For this configuration, `os-webrtc-janus.ini` would look like:

```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    NonSpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
```

This directs both spatial and non-spatial voice to the grid service connector
and `WebRtcVoiceServerURI` points to the configured Robust grid service.

There is no need for a `[JanusWebRtcVoice]` section because all that is handled by the grid services.

#### Local Simulator Janus Service

In a grid setup, there might be a need for a single simulator/region to use its own
Janus server for either privacy or to off-load the grid voice service.
In this configuration, spatial voice is directed to the local Janus service
while the non-spatial voice goes to the grid services to allow grid wide group
chat and region independent person-to-person chat.

This is done with a `os-webrtc-janus.ini` that looks like:
```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
```

Notice that, since the simulator has its own Janus service, it must configure the
connection parameters to access that Janus service. The details of running and
configuring a Janus service is provided at [os-webrtc-janus-docker] but, the configuration
here needs to specify the URI to address the Janus server and the API keys
to allow this simulator access to its interfaces. The example above
contains just sample entries.

<a id="Configure_Robust"></a>
## Configure Robust Server for WebRTC Voice

For the grid services side, `os-webrtc-janus` is configured as an additional service
in the Robust OpenSimulator server. In this tree the grid server project references only
`WebRtcVoice.dll` (`Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj`). The
connector below loads `WebRtcVoiceServiceModule.dll` and `WebRtcJanusService.dll`, which that project does not
reference, so a grid server built from this tree does not carry them. The setups the guide in `Docs/voice`
describes run the Janus service in the region server process; the grid server hosts only the call session
service (next section). The additions to `Robust.ini` for a grid-hosted Janus service are:

```
...
[ServiceList]
    ...
    VoiceServiceConnector = "${Const|PrivatePort}/WebRtcVoice.dll:WebRtcVoiceServerConnector"
    ...

[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
...
```

This adds `VoiceServiceConnector` to the list of services presented by this Robust server
and adds the WebRtcVoice configuration that says to do both spatial and non-spatial voice
using the Janus server, and the configuration for the Janus server itself.

One can configure multiple Robust services to distribute the load of services
and a Robust server with only `VoiceServiceConnector` in its ServiceList is possible.

<a id="Configure_Session_Store"></a>
## Sharing call sessions between region servers

Group, conference and avatar-to-avatar calls are sessions kept by the region server. A grid with
more than one region server process has one record of each call per process unless the processes
share a store: each process counts its own seat limit, each rings the group itself, and a group or
conference call between people on different processes depends on `CarrierSecret`. The grid voice
session service keeps one record of every call instead. In either mode an avatar-to-avatar call rings
only a callee whose region is served by the caller's own region server process. It is off by default and nothing changes until both sides
are configured.

In `Robust.ini` (or `GridServer.ini`), add the connector to `[ServiceList]` and enable the service.
The service refuses to load without HTTP basic authentication and a password of at least 32 characters:

```
[ServiceList]
    VoiceSessionConnector = "${Const|PrivatePort}/WebRtcVoice.dll:VoiceSessionServerConnector"

[WebRtcVoiceSessionService]
    Enabled = true
    AuthType = "BasicHttpAuthentication"
    HttpAuthUsername = "voice-sessions"
    HttpAuthPassword = "a-password-of-at-least-32-characters-kept-out-of-version-control"
```

In the voice settings of every region server, with the same user and password:

```
[WebRtcVoice]
    SessionStoreMode = grid
    SessionStoreServerURI = http://robust.example.org:8003
    AuthType = "BasicHttpAuthentication"
    HttpAuthUsername = "voice-sessions"
    HttpAuthPassword = "a-password-of-at-least-32-characters-kept-out-of-version-control"
```

Set it on all region servers or none: a call started on a server that keeps its own record is not
seen by the others. Calls are kept in the service's memory and end when Robust restarts.

- A region that cannot reach the service refuses new calls rather than keeping its own record. A
  viewer asking to join an avatar-to-avatar call while the service is unreachable is answered
  "service unavailable" and may try again, and only a service that says the call does not exist
  gives "not found".
- A region checks the service once when it starts and logs whether it answered. A wrong address, a
  Robust without the service and a wrong password are all reported there.
- Seats are renewed by the region that holds them and released by the service when the region stops
  renewing them, or when it restarts.
- Group and conference rings are checked by the service, and conference text lines are signed with a key the
  service keeps for each conference and gives only to its members, so neither needs `CarrierSecret`.
- A call is not ended for being idle while someone in it is still on the grid.

The console command `show voice sessions` prints where the region keeps its sessions, the service
address, whether it answered, the circuit breaker state, and the seats and queued departures this
region holds.

<a id="Configure_Standalone"></a>
## Configure Standalone Region

[OpenSimulator] can be run "standalone" where all the grid services and regions are
run in one simulator instance. Adding voice to this configuration is sometimes useful
for very private meetings or testing. For this configuration, a Janus server is set up
and the standalone simulator is configured to point all voice to that Janus server:

```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
```

This directs both spatial and non-spatial voice to the Janus server
and configures the URI address of  the Janus server and the API access
keys for that server.

<a id="Managing_Voice"></a>
## Managing Voice (Console commands)

There are a few console commands for checking on and controlling the voice system.
The current list of commands for the simulator can be listed with the
console command `help webrtc`.

This is a growing section and will be added to over time.

**webrtc list sessions** -- not implemented

**janus info** -- list many details of the Janus-Gateway configuration. Very ugly, non-formated JSON.

**janus list rooms** -- list the rooms that have been allocated in the `AudioBridge` Janus plugin

**show voice sessions** -- show where this region keeps its call sessions (in this process, or in the grid
voice session service) and, for the service, whether it is answering

[SecondLife WebRTC Voice]: https://wiki.secondlife.com/wiki/WebRTC_Voice
[OpenSimulator]: http://opensimulator.org
[OpenSimulator Community Conference]: https://conference.opensimulator.org
[os-webrtc-janus]: https://github.com/Misterblue/os-webrtc-janus
[Janus-Gateway WebRTC server]: https://janus.conf.meetecho.com/
[os-webrtc-janus-docker]: https://github.com/Misterblue/os-webrtc-janus-docker
