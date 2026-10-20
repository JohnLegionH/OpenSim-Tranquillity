# WebRTC voice: the position feed

With a spatial mixer, a viewer tells the mixer where its avatar is, and the mixer places the avatar's voice there. A
modified viewer could claim a false position. The position feed lets the region send the mixer where each voice participant
really is, so the claim can be compared with it. This page describes what exists today: **observe mode**. An `enforce` mode
is accepted by the setting but does not act yet.

The wire messages are described in [mixer-feed-protocol.md](mixer-feed-protocol.md) (section 6.6). Settings are in
[configuration.md](configuration.md).

## What it needs

- The slvoice mixer, which must advertise `vis_protocol` 3. A mixer that advertises a lower version is sent nothing. With the
  stock AudioBridge `PositionFeedEnabled` is switched off at start with a warning.
- `VisibilityFeederEnabled` and `VisibilityEmitEnabled`, because positions go through the same admin connection as the
  visibility messages (`JanusGatewayAdminURI` and `AdminAPIToken`). Without them the region logs a warning and the feed
  stays inactive.
- `JoinCapabilityEnabled` is not required, but the feed logs a warning without it: a viewer could then join the mixer as
  another agent, and positions are matched to the agent id a viewer joins as, so the comparison is only as strong as that
  binding.

## Observe mode

`PositionFeedMode = observe` is the default. In observe mode the region sends positions and the mixer counts what it would
have changed and changes nothing: nobody's audio is moved or muted. Use it to see how often claimed positions differ from the
region's before anything depends on it. The mixer keeps those counts; where it reports them is described by the mixer, not by this addon.

`PositionFeedMode = enforce` is accepted and logged, and runs as observe. Enforcement is planned for a later release.

## What the region sends

Per voice participant: the position the region has for the avatar in global centimetres, a speed allowance (how fast the
avatar can really be moving, never below `PositionFeedSpeedFloorCmS` and never above what the physics engine allows any body;
`PositionFeedSpeedMaxCmS` when the engine declares no limit), and a tolerance (the margin the mixer allows at the moment of
the sample, `PositionFeedMarginCm`, widened for an avatar whose position is the stale one core passes across a region border).

- A root agent's position is measured by its own region every frame. A child agent in a neighbouring region has the
  position its root region last sent across, which core sends only after the avatar has moved about 10 metres and at
  least 2 seconds have passed, so a child's tolerance is wider.
- Every `PositionFeedTickMs` (500 ms) the region works out what to send. It sends a participant that is new to a room or has
  moved at least `PositionFeedMoveThresholdCm` (100) since the last point sent, and a full snapshot of each room at least every
  `PositionFeedKeepAliveMs` (1000 ms), so an avatar that stands still is never silent for long and a lost message is repaired
  within one interval.
- A room whose participants have all left gets one empty full snapshot, then nothing.
- An agent that cannot be placed in a room is left out and counted.
- Nothing is computed or sent while `PositionFeedEnabled` is `false`.

## What it logs

At start, per region:

- `[REGION WEBRTC VOICE][Positions]: position feed enabled, mode observe` (Information).
- With `PositionFeedMode = enforce`: `PositionFeedMode enforce is accepted but not active yet; positions are sent in observe mode`.
- A warning if the feed is on without the feeder and emission (`position feed is inactive`), without `JoinCapabilityEnabled`
  (the binding warning above), or with a mode that is neither `observe` nor `enforce` (`using observe`).
- `[VOICE VISIBILITY] position feed started for <region> every <n> ms; positions are sent once the mixer advertises vis_protocol 3`.

While running (`[VOICE POSITIONS]`):

- `the mixer advertises vis_protocol 3; sending positions in observe mode`: the feed is live.
- `waiting to learn the mixer's protocol version from its replies to peer_ctl requests; no positions are sent until it advertises
  vis_protocol 3`: normal for the first moments after start.
- `the mixer advertises vis_protocol <n>, and peer_pos needs 3: no positions are sent`: the mixer is too old. A
  `the mixer no longer advertises a protocol version; positions stop` warning means it stopped answering.
- `the mixer did not accept a peer_pos request for room <n>; the next full snapshot repeats it`: logged at most once per
  interval, with the count of failures so far. Other failures are logged at Debug.

The counts of what the mixer compared are kept by the mixer, not in the region log.
