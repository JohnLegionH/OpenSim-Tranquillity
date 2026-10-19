# Mixer Feed Protocol

This document describes the messages the region server (the `os-webrtc-janus` addon) sends to a
spatial voice mixer, and the replies it expects. It is written for people who build or maintain a
mixer, and for operators who need to read the logs when a mixer and a region disagree.

The mixer is a Janus plugin. The region server drives it in two ways:

1. **Plugin handle messages** over the normal Janus session (create room, join, leave, trickle ICE).
   These are the same messages the stock AudioBridge plugin understands, with a few additive fields.
2. **Admin API messages** (`message_plugin`) that carry the per-listener visibility feed:
   `peer_ctl_batch` and `peer_ctl_heartbeat`. Viewers never see or send these.

Everything in this document is sim-to-mixer. The viewer-to-mixer data channel format is a separate
matter and is not covered here.

Statements below come from the region-server code in `Addons/os-webrtc-janus/`. A few statements
describe what the region server expects a mixer to do; those are marked *expected mixer behaviour*
because the mixer is not part of this repository and they cannot be verified from this code.

## 1. Why the feed exists

A voice room is a shared set of participants. The room itself has no way to say "listener A may not
hear source B". The region server knows exactly that (estate bans, parcel bans and access lists,
parcels that hide avatars, parcel voice moderation), and it must be enforced in the mix: a hidden or
muted source has to be missing from the listener's audio and from the participant list the mixer
sends to that listener's viewer. If the mixer derived the participant list from raw room membership
while deriving audio from the feed, a voice indicator could light up with no audio behind it, or an
avatar the listener may not hear could still appear in the list. So:

- The region server computes one **visibility matrix** per region: for each listener, the set of
  sources that listener may not hear (the *exclusion* set) and the set of sources that are silenced
  but stay in the participant list (the *mute* set).
- The mixer must drive both audio and the participant list from the same per-listener sets.

The viewer is never trusted for this. The matrix is built only from region-server state.

## 2. Transport

### 2.1 Admin API envelope

Feed messages go to the Janus Admin API (`JanusGatewayAdminURI`) as an HTTP POST with a JSON body:

```json
{
  "janus": "message_plugin",
  "transaction": "<random string>",
  "admin_secret": "<AdminAPIToken>",
  "plugin": "janus.plugin.slvoice",
  "request": { "request": "peer_ctl_batch", "...": "..." }
}
```

- The secret is carried in the body as `admin_secret`. The `apisecret` field used on the normal API
  is not accepted by the admin API.
- The plugin name for the feed is `janus.plugin.slvoice`. The feed is only sent to a mixer that
  implements this plugin; set `[JanusWebRtcVoice] PluginName = janus.plugin.slvoice` and
  `[WebRtcVoice] VisibilityEmitEnabled = true` to turn it on.
- The reply is read inline from the HTTP response; there is no ack/long-poll step.

### 2.2 How the region server interprets the HTTP result

| Result | Meaning |
|---|---|
| HTTP 2xx with `"janus":"success"` | Delivered to the plugin. This does **not** mean the batch was applied. |
| HTTP 2xx with `"janus":"error"`, or a 2xx body that cannot be parsed | Protocol error (for example a wrong `admin_secret`, an unknown plugin). |
| Non-2xx, connection failure or timeout | Transport error. |

The per-call timeout is `[JanusWebRtcVoice] AdminTimeoutMs` (default 5000).

The plugin's own answer is nested in the reply under the top-level key `response` (section 4.4). A
sender that wants to know whether a batch took effect must read that inner object; `janus:"success"`
alone is not enough.

### 2.3 Error latch

Three consecutive protocol errors on a region latch feed emission off for that region until the
region server restarts, with one prominent log line. Any success resets the count. A single stray
error does not disable emission.

## 3. Room messages the feed depends on

### 3.1 Room numbers

Room numbers are positive 32-bit integers computed identically by the region server and the mixer
operator's tooling from a deterministic hash (djb2 over the inputs, folded to a positive value):

- Spatial (`channel_type` = `local`): hash of region id, `"local"`, and the parcel local id. The
  estate-wide channel uses parcel local id `-999`.
- Non-spatial (`channel_type` = `multiagent`): hash of the grid id (when configured), the channel
  id, and `"multiagent"`.

An unknown room is not an error that the transport reports, so confirm room numbers out of band.
The region server logs the estate room number once at start-up for comparison with the mixer's
`handle_info`.

### 3.2 Create

Before every viewer join the region server sends a create for the room; the mixer answers `created`
for a new room or error 486 ("already exists"), and 486 counts as success. Body:

```json
{
  "request": "create", "room": 12345, "is_private": false, "permanent": false,
  "sampling_rate": 48000, "spatial_audio": true, "denoise": false, "record": false,
  "description": "<region id>/<channel type>/<parcel id>/<channel id>",
  "vis_authority": true,
  "sim_created": true
}
```

- `spatial_audio` is true for `local` rooms and false for `multiagent` rooms.
- `vis_authority: true` is sent only for `local` rooms and only when the region runs with
  `VisibilityArmingEnabled`, `VisibilityFeederEnabled` and `VisibilityEmitEnabled` all set. It
  declares that a region server will arm the listeners of this room (section 6). Non-spatial rooms
  never get it, because they receive no visibility batches.
- `sim_created: true` is sent on every create. A mixer that requires join capabilities should key
  that requirement on this field. A mixer that does not know the field ignores it.

### 3.3 Join

```json
{ "request": "join", "room": 12345, "display": "<agent id>",
  "jsep": { "type": "offer", "sdp": "..." },
  "join_cap": "v1....", "session_id": "<viewer session id>" }
```

`join_cap` and `session_id` are present only when `JoinCapabilityEnabled` is set and a secret is
configured. The viewer's SDP offer is forwarded unchanged. A successful join answers `joined` with a
participant id and a `jsep` answer.

Error codes the region server acts on:

| Code | Meaning | Region server behaviour |
|---|---|---|
| 485 | No such room | Provision fails back to the viewer (logged once). |
| 486 | Room already exists (create) | Treated as success. |
| 495 | Room full | Reported to the viewer as a channel-full error (HTTP 409). |
| 496 | Join refused by the capability check; carries a machine-readable `reason` (for example expired or replayed) | The reason is logged. |

### 3.4 Join capability

When enabled, the region server signs one capability per join:

```
join_cap = "v1." + b64url(payload) + "." + b64url(HMAC-SHA256(secret, "v1." + b64url(payload)))
payload  = "<agent>|<session>|<room>|<epoch>|<generation>|<iat>|<exp>|<nonce>"
```

- `b64url` is base64 with `+` and `/` replaced by `-` and `_` and no padding.
- `iat` and `exp` are Unix seconds; the lifetime is 60 seconds. The mixer is expected to tolerate a
  small clock skew (the region server assumes about 120 seconds).
- `nonce` is 16 random bytes in lowercase hex (one-shot).
- `epoch` is the 16-hex-digit authority epoch of the room (section 6), or `0000000000000000` when
  no authority state has been published for the room. `generation` is the room's latest
  `policy_generation` as known to the region server, or 0.
- No field may contain `|`. If a field would, or the secret is empty, no capability is minted and the
  join proceeds without one.
- The secret is `[JanusWebRtcVoice] JoinCapabilitySecret`. It must match the mixer's configured
  secret and should be a different value from the API tokens. It is never logged.

The capability travels only from the region server to the mixer. A capability admits one join; it
grants no audibility.

### 3.5 Connector join capability endpoint

External connector peers (recorder or injector processes) join the mixer themselves, so the region
server offers them a capability over HTTP when a connector record carries a `CapabilitySecret`
(at least 32 characters):

```
POST /voice/connector/<name>/join-cap
Authorization: Bearer <CapabilitySecret>
```

A success returns `200` with `{"display","room","session_id","join_cap","expires","position"}`;
`position` is `{"x","y","z"}` in integer centimetres. Every failure before authentication succeeds
returns an empty `404`, so the endpoint does not reveal which connector names exist. When the region
server cannot mint, it answers an empty `503` after authentication. No handler is registered at all
unless some connector record has a `CapabilitySecret`.

## 4. `peer_ctl_batch`

### 4.1 Shape

```json
{
  "request": "peer_ctl_batch",
  "op": "add",
  "room": 12345,
  "excl": { "<listener uuid>": ["<source uuid>", "<source uuid>"] },
  "mute": { "<listener uuid>": ["<source uuid>"] }
}
```

- `op` is `add`, `remove` or `replace`.
- `room` is the mixer room the batch applies to. One request addresses exactly one room.
- `excl` maps a listener to the sources excluded for that listener: removed from the listener's mix
  and from its participant list.
- `mute` maps a listener to the sources silenced for that listener but left in its participant list
  (greyed out, with no leave notice).
- All UUIDs are lowercase hyphenated strings and are never the zero UUID. The sender refuses to
  build a batch that contains a zero UUID.
- No reason codes are on the wire. The mixer only needs the boolean.

### 4.2 Operations

| `op` | Effect on each named listener |
|---|---|
| `add` | Union the listed sources into that listener's set. |
| `remove` | Subtract the listed sources from that listener's set. |
| `replace` | Set that listener's set to exactly the listed sources. An empty array clears it. |

Rules:

- **An operation touches only the listeners named in the batch.** A `replace` for listener A leaves
  every other listener's sets alone. To clear a listener, name it with an empty array; omission is
  never a clear. There is no room-wide reset operation.
- `excl` and `mute` are independent channels, each with its own `add`/`remove`/`replace` state per
  listener, and an update to one never alters the other.
- For one listener, a source is never in both channels in the same state: exclusion wins, so a source
  that is excluded is never also offered as muted.
- Within one batch, a (listener, source) pair is never in both an `add` and a `remove` for the same
  channel; the sender treats that as a programming error and sends nothing.
- A `replace` always carries the `mute` key, even as `{}`. An empty `{}` names no listener and so
  clears nothing. `add` and `remove` carry `mute` only when it is non-empty, so a batch that changes
  only exclusions has no `mute` key. A mixer that does not know `mute` ignores it.
- A single feeder tick sends at most one `add` request and one `remove` request per room, never one
  per listener. A `replace` is its own request.

### 4.3 When the region server sends what

- **Steady state:** one tick every `VisibilityTickMs` (default 250 ms) recomputes the matrix and
  sends the difference from the previous tick as `add` and `remove`. A tick with no change sends
  nothing. A tick that changes a parcel that hides avatars can change many listeners at once (every
  avatar outside the parcel gains an entry for the mover and the reverse), so a single batch can be
  large; a crossing between two ordinary parcels usually changes nothing.
- **Snapshot:** on first start, after a transport error, after a skipped tick, or after the mixer is
  seen to have restarted, the region server sends `replace` for every listener. A listener that
  disappeared from the matrix gets an explicit empty `replace` so its old entries are cleared (when
  authority is not in use; see section 6).
- **Listener join:** after a viewer's provision succeeds, that listener's full columns are sent as a
  `replace`. The listener may not yet be in the room when the message arrives, so the region server
  re-sends it once per tick, up to 6 attempts, then logs a give-up line. A listener whose columns are
  both empty is not sent at all, since there is nothing to enforce.
- **Rooms:** each agent is addressed at the room recorded when it last provisioned a spatial voice
  session, or else the room its current parcel resolves to. An agent that can be placed in neither is
  left out of the batch and counted (see `show voice visibility`). For each room the sender keeps, for
  every listener, only the sources that are also in that room. Rooms are sent concurrently, up to
  `VisibilityRoomSendConcurrency` (default 4) at a time, and one room's failure does not stop the
  others. Only one send is in flight at a time; if a tick finds the previous send still running it is
  skipped and a snapshot is forced on the next tick. A send that has been in flight for 8 times the
  admin timeout is abandoned and logged.
- **Child agents:** agents that are only neighbours of the region (child agents) are included.
  Their parcel is derived from their position on every tick, since they receive no parcel crossing
  events.

### 4.4 Reply

A reply to `peer_ctl_batch` has this inner object under `response`:

```json
{ "janus": "success",
  "response": { "slvoice": "applied", "op": "add", "room": 12345,
                "entries": 3, "mute_entries": 1, "skipped": 0,
                "deferred_listeners": 0 } }
```

| Field | Meaning |
|---|---|
| `slvoice` | `applied` on success. Anything else, or a missing value, is logged as a warning and counted as an anomaly. |
| `entries`, `mute_entries` | Listener entries parsed in each channel. |
| `skipped` | Malformed items the mixer dropped at parse time. Greater than zero is real loss and is logged as a warning. |
| `deferred_listeners` | Entries the mixer kept for listeners that have not joined yet (logged at info level; not a fault). |

All inner fields are optional. A mixer that omits the whole inner object is treated as
"no information" and nothing is logged. The region server never changes a send's transport result
because of the inner reply; anomalies are logged and counted only.

Additional fields appear in the reply when authority is in use (section 6): `vis_protocol`,
`mixer_instance`, `status` (`ok`, `stale_epoch`, `unknown_room`, `undeclared_room`), `reason`,
`authority_epoch`, `stale_listeners` and `unarmed_listeners` (arrays of UUIDs).

## 5. Expected mixer behaviour

These points describe how the region server expects a mixer to treat the feed. They are not
verifiable from this repository.

- **Mix:** a source contributes to a listener's mix only if it is not excluded and not muted for that
  listener. Exclusion is applied before any other per-source processing.
- **Participant list:** the per-peer updates sent to a listener's viewer skip excluded sources:
  no volume or power entry, no join notice, and a leave notice for a source that becomes excluded, so
  the listener's indicator for it disappears. Muted sources stay in the list.
- **Join window:** a batch entry for a listener who is not yet in the room should be held and applied
  when that listener joins, before the join roster is built, and reported in `deferred_listeners`.
  An un-ban sent while the listener was away must not be resurrected on join, so held entries are
  merged with the same `replace`/`add`/`remove` semantics as live ones.
- **Unknown rooms:** a mixer that predates the authority fields answers `applied` even for a room it
  does not have, so a wrong room number cannot be detected from the reply. A mixer that implements
  the authority fields reports `unknown_room`.
- **Merge discipline:** updates are field-scoped. A mute update never changes an exclusion, and
  neither changes the viewer's own mute or gain settings.

## 6. Authority, epochs and heartbeats (optional)

This section applies only when `[WebRtcVoice] VisibilityArmingEnabled = true` (which also requires
`VisibilityFeederEnabled` and `VisibilityEmitEnabled`). The default is off, and with it off every
message above is sent exactly as described and none of the fields below appear.

The purpose is fail-closed enforcement: a mixer that has been told a region server is the authority
for a room silences any listener or source it has not been told to carry, and stops carrying audio in
that room if the authority goes quiet.

### 6.1 Concepts

- **Epoch** (`room_epoch`): 16 lowercase hex digits, chosen once each time the region's feed service
  starts, built from the Unix time in milliseconds shifted left 16 bits, with a random low 16 bits.
  Within one process it is strictly increasing, so a restarted feed can never reuse an earlier value.
- **Policy generation** (`policy_generation`): a per-room counter that increases for every batch sent
  to that room. The first is 1.
- **Listener generation:** the generation at which a listener was last successfully armed in a room.
  A listener is *armed* in a room when it holds a non-zero generation there.
- **Arming:** a `replace` that names a listener, with both its columns (empty ones included),
  arms it. Until a listener is armed, a mixer in authority mode carries nothing for it.

### 6.2 Fields added to `peer_ctl_batch`

When arming is on, every batch carries, after `room`:

```json
{ "room_epoch": "0184a3c2d9f1b7e4", "policy_generation": 7,
  "base": { "<listener uuid>": 6 } }
```

`base` appears only on `add` and `remove`. For each named listener it gives the listener generation
the region server believes the mixer holds, so the mixer can refuse a delta that does not follow on
from what it has (the listener is then reported in `stale_listeners` and re-armed with a `replace`).

### 6.3 `peer_ctl_heartbeat`

Sent every 1000 ms from a timer of its own, independent of the feeder tick and the batch send, and
only after a mixer reply has advertised `vis_protocol` 2 or higher:

```json
{ "request": "peer_ctl_heartbeat", "room_epoch": "0184a3c2d9f1b7e4", "interval_ms": 1000,
  "feed_age_ms": 120,
  "rooms": { "12345": { "policy_generation": 7, "as_of": 7,
                        "listeners": { "<listener uuid>": 7, "<listener uuid>": 0 } } } }
```

- `rooms` lists every room the region currently addresses, with every listener placed there and its
  listener generation (0 = not armed), empty columns included. An agent that cannot be placed is not
  named in any room.
- `policy_generation` is the highest generation allocated for the room; `as_of` is the highest the
  mixer has acknowledged as applied. A heartbeat built before a still-in-flight batch can arrive after
  it; `as_of` lets the mixer recognise that and ignore the listener map.
- `feed_age_ms` is how old the region's visibility computation is at the moment the body is built. The
  region server only reports it; the mixer decides what counts as stale. It is omitted if it cannot be
  read, which a mixer should read as an older region server.
- `"state": "stopping"` is added to the final heartbeat on a graceful stop.

The reply is `{"janus":"success","response":{"slvoice":"heartbeat","vis_protocol":2,
"mixer_instance":"...","rooms":{"12345":{"status":"ok","unarmed_listeners":[],"stale_listeners":[]}}}}`.
A reply to a heartbeat that is not a `heartbeat` response stops heartbeats until a later reply
advertises `vis_protocol` 2 again.

### 6.4 How the region server reacts to replies

| Reply | Reaction |
|---|---|
| Transport failure | Nothing recorded; the next pass sends a snapshot, which re-arms everyone. |
| `mixer_instance` changes | The mixer restarted. Every listener in every room is disarmed and re-armed. |
| `status` or `reason` `unknown_room` | That room's listeners are disarmed and retried after a delay that starts at 1 s and doubles to 300 s while the room stays absent. A provision, a room create or an applied reply for the room ends the back-off at once. Logged once per episode. |
| `stale_epoch` | Same back-off: a newer authority holds the room, so the region server retries rather than escalating. |
| `stale_listeners`, `unarmed_listeners` | Exactly those listeners are disarmed and re-armed. |
| `applied` | A `replace` arms each named listener at this generation; an `add` or `remove` advances an already armed one. |
| Any other status, or a malformed reply | The named listeners are not armed and are retried after the back-off. |

### 6.5 Join capabilities and authority

When both the join capability and arming are on, the capability's `epoch` and `generation` fields
come from the room's last published authority state, so a capability minted before an epoch change is
rejected by a mixer that checks them.

## 7. What the region server puts in the matrix

For each ordered pair (listener L, source S), with P_L and P_S their parcels, S is **excluded** for L
if any of these holds. They are evaluated in this order:

1. **Voice off for S's parcel.** The estate disallows voice, or (unless the estate is in "tax free"
   mode) the parcel's voice flag is off. A source with voice off is audible to nobody.
2. **Estate ban.** S or L is banned from the estate.
3. **Parcel ban or access restriction**, in either direction: S is barred from L's parcel, or L is
   barred from S's parcel. The check is made per pair against each one's own parcel and does not
   depend on the requested channel. The usual exemptions apply (administrators, estate owner and
   managers, the parcel owner).
4. **Parcels that hide avatars.** If L and S are on different parcels and either parcel hides
   avatars, the pair is excluded. This is applied in both directions so a listener never hears
   someone it cannot see. A viewer-side "god" flag exempts hiding from that listener's perspective.

Otherwise, if S is **moderation-muted** on its current parcel (the parcel-wide mute-everyone flag, or
S individually muted there), S goes in L's `mute` set. Parcel owners, estate managers and group
moderators cannot be muted. Moderation state is per parcel, held in memory, and cleared when the
region restarts.

Notes:

- Rule 3 deliberately does not use the simulator's "banned only below the ban line height" shortcut,
  so a banned avatar above the line is still excluded.
- In a "tax free" estate the simulator's own ban helper returns false; the matrix re-evaluates the
  ban lists directly so bans still apply to voice. Access restrictions in such an estate keep the
  simulator's behaviour.
- A parcel owner's, estate manager's and administrator's exemptions are the same as the ones the
  simulator uses elsewhere, by reusing its predicates.

Sources of change that make the region server recompute: parcel changes (flags, ban lists, access
lists, hiding), a root avatar crossing a parcel boundary, estate changes (bans, managers, the voice
switch), and every tick for child agents.

## 8. Operating notes

- `show voice visibility` (console, per region) prints the last send: rooms addressed, agents left
  out because they could not be placed, and the inner-reply totals (entries, mute entries, deferred,
  skipped, anomalies). A persistently non-zero "unplaced" count means an agent has neither a recorded
  room nor a resolvable parcel.
- `show voice moderation`, `voice moderation mute <agent> [parcel-local-id]` and
  `voice moderation unmute <agent>` inspect and change the in-memory moderation state.
- Set `VisibilityFeederEnabled = true` with `VisibilityEmitEnabled = false` to run the matrix without
  sending anything (diagnostics).
- `skipped` greater than zero and a `slvoice` value other than `applied` are the two reply conditions
  that indicate lost or mismatched messages. `deferred_listeners` greater than zero is normal.
