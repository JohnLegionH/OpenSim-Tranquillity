# WebRTC voice (os-webrtc-janus)

This guide is for grid operators. It describes what the `os-webrtc-janus` addon does, how to switch it on,
which optional features exist and what each one needs. Every key is listed with its default in
[configuration.md](configuration.md). Build and Robust set-up details for the addon itself are in
`Addons/os-webrtc-janus/README.md`.

## What the addon does

Viewers that support WebRTC voice ask the region for a voice session. The addon decides whether that avatar
may have voice on that parcel (estate and parcel voice settings, bans), then creates the session on an
external [Janus Gateway](https://janus.conf.meetecho.com/) server and returns the connection details to the
viewer. Audio never passes through the region server: it flows between the viewer and Janus.

The addon:

- advertises `VoiceServerType = webrtc` to viewers and provides the voice capabilities they call;
- answers spatial (regional) voice requests and non-spatial requests (groups, calls, conferences);
- can run against a Janus server owned by one region, or against a grid-wide voice service hosted on Robust;
- adds console commands: `janus info` and `janus list rooms` (the room listing is of the AudioBridge plugin).

You must run Janus yourself. See the `os-webrtc-janus-docker` project for running the stock Janus.

## Choosing a Janus plugin

The region attaches to one Janus plugin, chosen by `PluginName` in `[JanusWebRtcVoice]`.

| Plugin | Setting | What you get |
|---|---|---|
| Stock AudioBridge | `janus.plugin.audiobridge` (the default) | Flat, non-spatial mixing: everyone in a room hears everyone. Nothing else in this guide's optional features is exercised against it. |
| Spatial mixer ("slvoice mixer") | `janus.plugin.slvoice` | A mixer that implements the `janus.plugin.slvoice` protocol: spatial mixing, and the per-listener features below. The protocol is defined by the mixer, not by this addon. |

Which feature works with which plugin:

| Feature | Switch | Stock AudioBridge | Slvoice mixer |
|---|---|---|---|
| Basic regional voice and non-spatial voice | `Enabled` | Works | Works |
| STUN servers advertised to viewers | `StunServers` | Works | Works |
| Avatar-to-avatar calls | `A2AVoiceEnabled` | Not verified | Exercised |
| Group voice | `GroupVoiceEnabled` | Not verified | Exercised |
| Ad-hoc voice and conference text | `AdhocVoiceEnabled` | Not verified | Exercised |
| Visibility (bans, "see avatars", moderation) | `VisibilityFeederEnabled`, `VisibilityEmitEnabled` | Ignored by the plugin | Needed |
| Visibility arming | `VisibilityArmingEnabled` | Ignored by the plugin | Needed |
| Join capability | `JoinCapabilityEnabled` | Ignored by the plugin | Needed |
| Parcel voice moderation | `VoiceModerationEnabled` | No effect on audio | Needed |
| NPC voice connectors | `ConnectorsEnabled` | Not usable | Needed |

"Exercised" means the code and its tests were written and run against the slvoice mixer. The call, group and
conference logic itself does not depend on the plugin, but it has not been verified against AudioBridge, so do
not rely on it there. Only the slvoice mixer enforces who can hear whom; the stock plugin has no way to
receive that information.

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
ones configured on the Janus server. Check the region log at start-up: it prints the plugin in use, and an error
if Janus could not be reached.

To use a slvoice mixer add `PluginName = janus.plugin.slvoice` in `[JanusWebRtcVoice]` and use that mixer's
own documentation to run it.

Grid-wide voice: on Robust add `VoiceServiceConnector = "${Const|PrivatePort}/WebRtcVoice.dll:WebRtcVoiceServerConnector"`
to `[ServiceList]` and give Robust the same `[WebRtcVoice]` and `[JanusWebRtcVoice]` sections. Regions then use
`WebRtcVoice.dll:WebRtcVoiceServiceConnector` for the service keys and set `WebRtcVoiceServerURI` to Robust.
See `Addons/os-webrtc-janus/README.md` for the full examples. The feature switches below are read by the
region server. If you use visibility emission with a grid service, the region server's own
`[JanusWebRtcVoice]` section still needs the admin URI and token.

## Optional features

Every switch is in `[WebRtcVoice]`, is commented out in the shipped file, and defaults to off. Turning one on
affects only that feature.

### STUN servers

`StunServers` is a comma-separated list of full ICE URIs. It is advertised to viewers as the `stun-servers`
simulator feature. Stock viewers need at least one STUN server to connect from outside the grid's network, and
fail to create a peer connection without a valid entry, so set it unless the core server already advertises one.
The shipped `os-webrtc-janus.ini` lists it commented out with a public server as the example
(`stun:stun.l.google.com:19302`). Empty (the default) adds nothing. Works with either plugin.

### Avatar-to-avatar calls

`A2AVoiceEnabled`. Handles the viewer's "call" and "start p2p voice" requests: the region records the
invitation, delivers it to the callee, and provisions a two-party voice session for the pair.
With the switch off the chat session handler behaves as it does without the feature. Calls are tracked per region
server process by default, or in the grid session service with `SessionStoreMode = grid`. When the callee is in a
region hosted by another process, the call rings there through a carrier message. By default it is signed with
`CarrierSecret`, set to the same value on every region server (see configuration); without it a callee in another
process is not rung and the caller's call rings out. With `SessionStoreMode = grid` no secret is needed: the
receiving region asks the session service whether the call exists and whether the join, decline or hang-up it was
told about is true, and acts on the service's answer.

### Group voice

`GroupVoiceEnabled`, with `GroupVoiceRequireVoicePower` (default `true`: the role needs both the join and the
voice power) and `GroupVoiceCap` (default 50 seats, never more than 110). Needs the Groups module. Seats are
released when a member leaves or their presence closes.

### Ad-hoc voice and conference text

`AdhocVoiceEnabled`. Supports "start conference": the region starts a conference session, handles invitations,
accepts and declines, and relays the conference text and roster. Needs the Groups module for the conference text
session. With the switch off, "start conference" is answered with an empty 200 reply, as before.

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

### Join capability

`JoinCapabilityEnabled`. The region signs a short-lived (60 seconds) capability for every join and the mixer can
refuse joins that lack one. Set `JoinCapabilitySecret` in `[JanusWebRtcVoice]` to the same value as in the mixer.
If the secret is empty the region logs an error and issues none.

### Parcel voice moderation

`VoiceModerationEnabled`. Advertises the viewer's `SpatialVoiceModerationRequest` capability so parcel owners,
estate managers and group moderators (group-owned parcels, with the moderate-chat power) can mute one avatar or
everyone on their parcel. Muted avatars stay in the participant list, greyed, rather than being removed. It needs
the feeder because the feeder holds the state.

Console commands (for operators), all acting on the selected region (`change region <name>`):

- `show voice moderation` lists the muted avatars and mute-everyone state per parcel;
- `voice moderation mute <agent-uuid-or-name> [parcel-local-id]` mutes one avatar on a parcel;
- `voice moderation unmute <agent-uuid-or-name>` clears one avatar's mute on every parcel of the region.

Moderation state is kept in memory and is cleared by a region restart. Parcel owners, estate managers and group
moderators cannot be muted.

### NPC voice connectors

`ConnectorsEnabled`. An operator-defined NPC can stand at a fixed position as a recorder (listen only) or injector
(may speak, for example text to speech). Each connector is a `[VoiceConnector.<name>]` section; the section is
the authorisation, so only someone with access to the configuration can create one. Related switches:
`NpcNameToken` (the word every connector NPC's name must contain, default `NPC`), `VoiceRangeMetres`
(default 20) and `AllowNpcVoice` (default `false`, see below). Disclosure cannot be disabled: connector NPCs
carry the name token, and the region posts notices when they appear and when avatars come close. Needs the NPC
module and the slvoice mixer. See [configuration.md](configuration.md) for the record keys.

## Upgrading

With every optional switch off, the addon handles voice as it does in NGC develop, with these differences that
are on by default:

- **Refusal cache.** A refused or failed voice provision is remembered for 5 seconds (`RefusalCacheSeconds`) so
  a viewer's immediate retries are answered without repeating the checks. Set it to `0` to turn it off.
- **NPC voice is refused.** An NPC presence is refused voice unless it is a registered connector
  (`AllowNpcVoice = true` allows all NPCs). Real avatars are unaffected.
- **Request timeout.** A Janus request answered with "ack" now fails after 5000 ms if its result never arrives
  (`RequestTimeoutMs`) instead of waiting indefinitely.

The defaults of the other keys keep the previous behaviour: the Janus plugin stays `janus.plugin.audiobridge`
and the visibility, arming, capability, moderation, group, call, conference and connector features stay off.
`Addons/os-webrtc-janus/README.md` may still describe older defaults for some of these keys; where it disagrees
with this guide or `configuration.md`, the code and the shipped ini file are what these documents follow.

## Known limits

- **TURN is not delivered.** The addon can advertise STUN servers only. It has no setting for TURN, so stock
  viewers behind networks that block direct UDP will not get a relay from the addon.
- **Sessions are held in memory, per region server process.** Call invitations, group and conference seats,
  and moderation state are not stored in a database and are lost when the process restarts. Calls, group calls
  and conferences between avatars on different region server processes need `CarrierSecret` on every region
  server; each process keeps its own copy of the call.
- **Only the slvoice mixer enforces visibility.** With the stock plugin every listener in a room hears every
  source, whatever the visibility switches say.
- **Calls, groups and conferences have only been exercised against the slvoice mixer.**
- **Cap on room size.** The mixer limits a room to 110 participants; the region clamps `GroupVoiceCap` to that.
- **STUN setting overlap.** The core server has its own `StunServers` setting. Which one is used when both are
  set has not been verified.
- **No audio from the region.** If viewers connect but hear nothing, check the Janus server's public address
  and its UDP media port range in your firewall; the region server cannot see the media path.
