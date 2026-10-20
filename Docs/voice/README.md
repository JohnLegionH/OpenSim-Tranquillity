# WebRTC voice (os-webrtc-janus)

This guide is for grid operators. It describes what the `os-webrtc-janus` addon does, which voice server to run
it against, how to switch it on, and what each optional feature needs. Every key is listed with its default in
[configuration.md](configuration.md).

| Read | For |
|---|---|
| This page | What the addon does, choosing a voice server, a minimal set-up, changed defaults, known limits |
| [deployment.md](deployment.md) | Region layouts, the ports a viewer must reach, calls across region server processes, the Robust call session service, sizing |
| [security.md](security.md) | Gateway secrets, https, the join capability, who may call what |
| [moderation.md](moderation.md) | Moderating parcel voice and group or conference calls |
| [positions.md](positions.md) | The position feed (observe mode) |
| [troubleshooting.md](troubleshooting.md) | Start-up lines, log lines, console commands, refusals and their HTTP codes |
| [configuration.md](configuration.md) | Every setting, with its default |
| [feature-spec.md](feature-spec.md), [architecture.md](architecture.md), [mixer-feed-protocol.md](mixer-feed-protocol.md) | What the addon implements, how it is built, the sim-to-mixer messages |
| `Addons/os-webrtc-janus/README.md` | Building the addon and the INI examples |

## What the addon does

Viewers that support WebRTC voice ask the region for a voice session. The addon decides whether that avatar
may have voice on that parcel (estate and parcel voice settings, bans), then creates the session on an
external [Janus Gateway](https://janus.conf.meetecho.com/) server and returns the connection details to the
viewer. Audio never passes through the region server: it flows between the viewer and Janus.

Through one Janus gateway the addon provides:

- **Spatial voice** for a parcel or an estate channel: everyone in the same room can hear each other.
- **Avatar-to-avatar calls**: the Call button in an IM window.
- **Group calls**: the call button in a group's chat window.
- **Ad-hoc conferences**: an IM conference of several people, with voice and with conference text.
- **Moderation**: of a parcel's voice, and of group calls and conferences.
- With a spatial mixer only: **per-listener visibility**, a **join capability**, a **position feed** and **NPC voice
  connectors**.

What each needs switched on (every optional switch is `false` unless the table says otherwise):

| Feature | Switches | Also needs |
|---|---|---|
| Spatial voice | `[WebRtcVoice] Enabled = true`, the service keys, and the four `[JanusWebRtcVoice]` values (gateway and admin URI and secret) | A Janus gateway; `StunServers` for viewers outside your network |
| Avatar-to-avatar calls | `A2AVoiceEnabled` | - |
| Group calls | `GroupVoiceEnabled` | The Groups module |
| Conference voice and text | `AdhocVoiceEnabled` | The Groups module, for the text |
| Group and conference calls between region server processes | `SessionStoreMode = grid`, or `CarrierSecret` | See [deployment.md](deployment.md) |
| Group and conference call moderation | `CallModerationEnabled` | `GroupVoiceEnabled` or `AdhocVoiceEnabled` |
| Parcel voice moderation | `VoiceModerationEnabled` | Stock AudioBridge: nothing more. Slvoice mixer: `VisibilityFeederEnabled` |
| Visibility | `VisibilityFeederEnabled`, `VisibilityEmitEnabled` | Slvoice mixer |
| Visibility arming | `VisibilityArmingEnabled` | Slvoice mixer, feeder, emission |
| Join capability | `JoinCapabilityEnabled`, `JoinCapabilitySecret` | Slvoice mixer |
| Position feed | `PositionFeedEnabled` | Slvoice mixer at `vis_protocol` 3, feeder, emission |
| NPC voice connectors | `ConnectorsEnabled` | Slvoice mixer, the NPC module |

You must run Janus yourself. See the `os-webrtc-janus-docker` project for running the stock Janus.

## Choosing a voice server

The region attaches to one Janus plugin, chosen by `PluginName` in `[JanusWebRtcVoice]`.

| Plugin | Setting | What you get |
|---|---|---|
| Stock AudioBridge | `janus.plugin.audiobridge` (the default) | Flat mixing: everyone in a room hears everyone at the same level, with no distance falloff or panning. Rooms are still separate per parcel, per estate channel, per call and per conference. |
| Spatial mixer ("slvoice mixer") | `janus.plugin.slvoice` | A mixer that implements the `janus.plugin.slvoice` protocol: spatial mixing, and the per-listener features marked below. The protocol is defined by the mixer, not by this addon. Any plugin name other than the stock one is taken to be such a mixer. |

Which feature works with which plugin:

| Feature | Switch | Stock AudioBridge | Slvoice mixer |
|---|---|---|---|
| Spatial voice | `Enabled` | Works, flat mix | Works, spatial |
| STUN servers advertised to viewers | `StunServers` | Works | Works |
| Avatar-to-avatar calls | `A2AVoiceEnabled` | Works | Works |
| Group calls | `GroupVoiceEnabled` | Works | Works |
| Conference voice and text | `AdhocVoiceEnabled` | Works | Works |
| Call moderation: text mute, roster flags, removal | `CallModerationEnabled` | Works | Works |
| Call moderation: voice mute | `CallModerationEnabled` | The plugin's own mute of one participant. A mute the plugin cannot carry out is refused (501 or 503) and not recorded | The mixer's mute channel |
| Parcel voice moderation | `VoiceModerationEnabled` | The plugin's own mute of one participant. The console commands find nothing | Carried by the visibility feeder; answered 501 and not recorded if the feeder is off |
| Visibility (bans, "see avatars", moderation) | `VisibilityFeederEnabled`, `VisibilityEmitEnabled` | Switched off at start, with a warning | Works |
| Visibility arming | `VisibilityArmingEnabled` | Switched off at start, with a warning | Works |
| Join capability | `JoinCapabilityEnabled` | Switched off at start, with a warning | Works |
| Position feed | `PositionFeedEnabled` | Switched off at start, with a warning | Works at `vis_protocol` 3 |
| NPC voice connectors | `ConnectorsEnabled` | Switched off at start, with a warning | Works |
| Removal of emptied rooms | `RemoveEmptyRooms` | The addon removes rooms it created | The mixer removes its own |

"Switched off at start, with a warning" means: with the stock plugin, a mixer-only switch that is `true` logs a
warning naming the switch and the plugin, and is treated as `false`; nothing else about it runs. Only the slvoice
mixer enforces who can hear whom. With the stock plugin every listener in a room hears every source, whatever the
visibility switches say.

The region server reads `PluginName` from its own `[JanusWebRtcVoice]` section even when Janus is reached through a
grid service, so set it there as well. Without it the region assumes the stock plugin and switches the mixer-only
features off.

## Enabling voice

The addon ships in this tree and is built with the region server. Copy `Addons/os-webrtc-janus/os-webrtc-janus.ini`
into the region server's `config` folder (the folder named by `[Startup] inidirectory`), then edit it.

Minimal region-local set-up, stock plugin:

```ini
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    ; Viewers need at least one STUN server to build a connection
    StunServers = stun:stun.example.org:3478

[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = <the Janus API secret>
    JanusGatewayAdminURI = http://janus.example.org:14225/admin
    AdminAPIToken = <the Janus admin secret>
```

The four Janus values must all be set or the Janus service disables itself, and the secrets must match the
ones configured on the Janus server. Use random secrets of at least 32 characters. Check the region log at start-up:
the lines to look for are in [troubleshooting.md](troubleshooting.md).

To use a slvoice mixer add `PluginName = janus.plugin.slvoice` in `[JanusWebRtcVoice]` and use that mixer's
own documentation to run it.

Switch the call features on one at a time, with the same file:

```ini
[WebRtcVoice]
    A2AVoiceEnabled = true
    GroupVoiceEnabled = true
    AdhocVoiceEnabled = true
```

If your regions run in more than one region server process, read [deployment.md](deployment.md) before you do.

Grid-wide Janus service: the `WebRtcVoiceServerConnector` that hosts the Janus service on Robust loads
`WebRtcVoiceServiceModule.dll` and `WebRtcJanusService.dll`, which the grid server project does not reference
(`Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj` lists `WebRtcVoice` only). The set-ups in this guide
run the Janus service in the region server process. What Robust does host is the call session service, which is
described in [deployment.md](deployment.md).

## Optional features

Every switch is in `[WebRtcVoice]`, is commented out in the shipped file, and defaults to off. Turning one on
affects only that feature.

### STUN servers

`StunServers` is a comma-separated list of full ICE URIs. It is advertised to viewers as the `stun-servers`
simulator feature. Stock viewers need at least one STUN server to connect from outside the grid's network, and
fail to create a peer connection without a valid entry, so set it unless the core server already advertises one.
If the core server's own `StunServers` setting is also set, the core server's value is the one viewers receive.
The shipped `os-webrtc-janus.ini` lists it commented out with a public server as the example
(`stun:stun.l.google.com:19302`). Empty (the default) adds nothing. Works with either plugin.

### Avatar-to-avatar calls

`A2AVoiceEnabled`. Handles the viewer's "call" and "start p2p voice" requests: the region records the
invitation, delivers it to the callee, and provisions a two-party voice session for the pair. A call is private
to its two avatars: only they, holding the call's token, are admitted to its room. With the switch off the chat
session handler behaves as it does without the feature, and the Call button does not work.

The invitation is delivered to a callee whose region is served by the same region server process as the caller. A
callee on a region of another process is not rung, in either `SessionStoreMode`.

### Group calls

`GroupVoiceEnabled`, with `GroupVoiceRequireVoicePower` (default `true`: the role needs both the join and the
voice power) and `GroupVoiceCap` (default 50 seats, never more than 110). Needs the Groups module. The powers are
checked again at the provision, because they can be revoked after the call started. Seats are released when a member
leaves or their presence closes. The first seat rings the group's members who are online and hold the powers.

### Ad-hoc conferences

`AdhocVoiceEnabled`. Supports "start conference": the region starts a conference session, rings the invitees, handles
accepts and declines, and relays the conference text and roster. Being invited is the membership: a provision is
admitted only for an invited member holding the conference's token. A conference line is delivered to the other members
who have accepted, with no echo to the sender, and is not stored for a member who is offline. Needs the Groups module for
the conference text session. With the switch off, "start conference" is answered with an empty 200 reply, as before.
`ConferenceRequestBurst` and `ConferenceRequestRefillSeconds` limit how fast one agent can start conferences and invite
people. Voice, the invitation and the text are three separate lifecycles: chat membership ends when the member leaves the
conference window, an invitation ends with a decline, and a voice seat ends when the voice connection is torn down.

### Visibility

Lets the region decide who may hear whom and tell the mixer.

- `VisibilityFeederEnabled` runs a per-region process that recomputes, every `VisibilityTickMs` (250), whether
  each listener may hear each source: estate bans, parcel bans and restrictions, parcels that hide avatars, and
  voice moderation. With emission off it only computes (useful for diagnostics). Console: `show voice visibility`
  prints the counters of the most recent send per region.
- `VisibilityEmitEnabled` sends the result to the mixer as `peer_ctl` batches over the Janus admin API. It needs
  `JanusGatewayAdminURI` and `AdminAPIToken`; without them the region logs a warning and stays compute-only.
- `VisibilityArmingEnabled` additionally makes the sim the declared visibility authority for each spatial room,
  using epochs and heartbeats. The mixer withholds audio from a room whose authority goes silent. It needs the
  feeder and emission to be on as well.

All three need the slvoice mixer.

### Join capability and position feed

`JoinCapabilityEnabled`: the region signs a short-lived (60 seconds) capability for every join and the mixer can
refuse joins that lack one. See [security.md](security.md). `PositionFeedEnabled`: see [positions.md](positions.md).
Both need the slvoice mixer.

### Moderation

`VoiceModerationEnabled` for a parcel's voice and `CallModerationEnabled` for group calls and conferences. See
[moderation.md](moderation.md).

### NPC voice connectors

`ConnectorsEnabled`. An operator-defined NPC can stand at a fixed position as a recorder (listen only) or injector
(may speak, for example text to speech). Each connector is a `[VoiceConnector.<name>]` section; the section is
the authorisation, so only someone with access to the configuration can create one. Related switches:
`NpcNameToken` (the word every connector NPC's name must contain, default `NPC`), `VoiceRangeMetres`
(default 20) and `AllowNpcVoice` (default `false`: an NPC is refused voice unless it is a registered connector). Disclosure
cannot be disabled: connector NPCs carry the name token, and the region posts notices when they appear and when avatars
come close. Needs the NPC module and the slvoice mixer. See [configuration.md](configuration.md) for the record keys.

## Upgrading from develop

With every optional switch off, the addon handles voice as the develop branch does, with these differences. Each is a fix to
something that did not work or had no limit; each has a setting where one makes sense.

| What changed | Setting and default | Reason |
|---|---|---|
| A refused or failed voice provision is remembered for 5 seconds, and the same answer is replayed to that viewer's immediate retries | `RefusalCacheSeconds = 5`; `0` turns it off | The viewer asks again at once after a failure. The cache answers those retries without repeating the estate and parcel checks or calling Janus |
| A Janus request answered with "ack" fails after 5000 ms if its result never arrives, and an admin request after 5000 ms | `RequestTimeoutMs = 5000`, `AdminTimeoutMs = 5000` | A result that never came used to hold the provision indefinitely |
| A room the addon created on the stock AudioBridge is removed once it has been empty for 60 seconds | `RemoveEmptyRooms = true`, `EmptyRoomGraceSeconds = 60` | The stock plugin never removes an emptied room, so every estate, parcel, call and conference room stayed on the gateway until Janus restarted. Rooms the addon did not create are never removed; the slvoice mixer removes its own |
| A provision whose `channel_type` is neither `local` nor `multiagent` is answered 403 | none | develop answered 500 for these. With any call feature on, a provision with no `channel_type` and no logout is also answered 403; with all call features off it goes to the voice service as before |
| A logout for a viewer session the voice service does not know is answered `closed` | none | develop answered an empty 200 |
| Voice on a parcel is decided by the parcel under the avatar, and the parcel's voice-off setting and ban list apply on every estate, including a TaxFree one | none | develop took the parcel from the `parcel_local_id` the viewer sent and did nothing when it sent none, which is the case for a parcel that uses the estate voice channel (the stock viewers' default); it also skipped the voice-off check on a TaxFree estate. The viewer's id is now only a hint, logged when it differs from the server's |
| A `multiagent` provision takes its room from the request's `channel` (with `channel_id` as the fallback), and the room number includes the grid's identity, read from `GatekeeperURI` | none | The viewer sends `channel`, so reading only `channel_id` put every such provision in one room, and two grids on one gateway shared it. If no gatekeeper address is set, the number is derived without the grid part and a warning is logged |
| A provision into a full room is answered HTTP 409 | none | The viewer maps 409 to its "channel full" message |
| API and admin secrets under 32 characters log a warning at start | none | The services start as before. A join-capability secret under 32 characters is refused when the capability is on |

Settings that keep develop's behaviour: the Janus plugin stays `janus.plugin.audiobridge`, and the visibility,
arming, capability, moderation, group, call, conference and connector features stay off. `AllowNpcVoice = false` matches
the stock server, which gives an NPC no way to reach voice. The shipped `os-webrtc-janus.ini` differs from develop's only
by commented-out keys.

## Known limits

- **TURN is not delivered.** The addon can advertise STUN servers only. It has no setting for TURN, so stock
  viewers behind networks that block direct UDP will not get a relay from the addon.
- **Call sessions are held in memory.** Call invitations, group and conference seats, and moderation state are not
  stored in a database. With `SessionStoreMode = local` they are lost when the region server process restarts; with
  `grid` they are lost when the grid server restarts. See [deployment.md](deployment.md).
- **Avatar-to-avatar calls ring only inside one process.** See above.
- **Only the slvoice mixer enforces visibility.** With the stock plugin every listener in a room hears every
  source, whatever the visibility switches say.
- **The stock plugin has no spatial audio.** It mixes a room flat.
- **Room size.** The mixer limits a room to 110 participants; the addon never seats more than 110 in a group or
  conference session, with either plugin.
- **Viewer crash.** A known viewer crash follows a Call started from the viewer's nearby-voice list. Start calls from
  the IM window or the group window. See [troubleshooting.md](troubleshooting.md).
- **No audio from the region.** If viewers connect but hear nothing, check the Janus server's public address
  and its UDP media port range in your firewall; the region server cannot see the media path. See
  [deployment.md](deployment.md).
