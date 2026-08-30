# Avatar-to-Avatar voice — build plan

**Authority:** `Docs/voice/a2a-assessment-20260830.md` (sim-side ground truth against `b7fbc717fa`, with the 2026-08-30 addendum) and the viewer wire trace `docs/voice-a2a-wire-trace-20260830.md` (Firestorm `a9a34638a3`; LL-upstream call paths — the SL-compatible contract). Line refs below are those documents' refs and are valid at those tips only.

**Status:** DECIDED items are not to be relitigated. Each slice ends in a commit on `feature/voice-visibility-matrix`; deployment is batched later, from this branch, to the regionserver.

---

## 1. Decided

### 1.1 No P2P transport (structural)
Balpien's condition. A2A is another **non-spatial mixer room**: each party's WebRTC terminates at the mixer, exactly as spatial voice does; no client ever sees another client's ICE, SDP or transport. Nothing in this plan proposes a peer connection.

### 1.2 Room model: (a) — A2A never touches the visibility path
An A2A provision **never** calls `OnListenerProvisioned(agent, room)` with the A2A room and never records into `AgentRoomTable`; spatial exclusion/mute columns stay addressed at the spatial room. Consequence, accepted for v1: no visibility or moderation enforcement inside an A2A room — the two parties hear each other unconditionally, and moderation mutes do not follow them into the call. **Session-scoped moderation is deferred**, and is recorded here as the known trigger for revisiting room model (b) (session-keyed room table). See assessment §6 and "Room-model fork".

### 1.3 Authorization: the in-memory invitation registry is primary
`sessionID → { caller, callee, token, state, created }`, per region-server instance.
- Recorded at `ChatSessionRequest` `"start p2p voice"`: `params` is the callee. **`params` absent → HTTP 400**, replacing today's `UUID.Random()` fallback (`WebRtcVoiceRegionModule.cs:735-736`). The session id is the viewer's XOR (`llimview.cpp:2530-2570`); the sim re-derives and must find it equal.
- Unanswered records expire on a TTL.
- **The O-29 deny is REPLACED for `multiagent` only.** A `multiagent` provision is admitted **iff** the registry holds the request's `channel` value as a session id, the requesting `agentID` is one of the two named parties, **and** the request's `credentials` equals the record's token. Every other `channel_type` (and a missing one) stays fail-closed exactly as `d9fa72c351` left it (`WebRtcVoiceRegionModule.cs:439-443`, `:495-501`).

### 1.4 Server-minted channel and token
The server mints `channel_uri` = the XOR session id as a string, and a per-session random token, and returns them in the **HTTP response body of `"call"`** as `voice_credentials { channel_uri, channel_credentials }` (what `llvoicechannel.cpp:687` expects). The callee receives the same pair inside the invitation's `voice` map. The `multiagent` room number derives from **`channel` + the grid id** — closing O-35 for `multiagent`; the `local` arm of `CalcRoomNumber` is unchanged.

### 1.5 Accept, decline, cleanup
- Callee accept **is** their `multiagent` provision — there is no `ChatSessionRequest` on accept (`llimview.cpp:3316-3337`).
- `"decline p2p voice"` removes the registry record (the viewer's decline string for P2P, `llimview.cpp:3419-3425`). `"decline invitation"` remains a stub (ad-hoc/group path).
- Cleanup: the record is removed when **both** parties have sent their logout provision (`llvoicewebrtc.cpp:2809-2811`), or on TTL.

### 1.6 `viewer_session` binding fix rides along
Both `TryGetViewerSession` lookup sites (`WebRtcVoiceServiceModule.cs:359` provisioning, `:404` signaling) verify `vSession.AgentId == pUserID`; a mismatch is treated as "session not found". Independent of A2A but on the same authorization surface (assessment §4).

### 1.7 Scope statement: single instance
The registry is per-instance, in-memory. **Cross-instance A2A (caller and callee on different region servers) is out of scope for v1** and is stated so in the operator-facing docs; a call between two agents on different simulators will not find the record on the callee's side and will be refused (fail-closed), not half-provisioned.

### 1.8 Permanent instrument, ships in slice 1
A DEBUG-level body instrument on `ChatSessionRequest` (method, session id, params, alt_params) and on the `multiagent` deny/allow decision (channel, requesting agent, named parties, token match, verdict) is a **permanent** instrument, not a capture build. There is no separate capture deploy; the first two-party live test both confirms the wire against the trace's predictions and exercises the feature.

---

## 2. Slices

Baseline for every slice: `dotnet test` over the two voice test projects (`Tests/WebRtcJanusService.Tests`, `Tests/WebRtcVoiceRegionModule.Tests`) at **146 passed / 148**, the 2 failures being the known stall-guard cases. Each slice must leave that at 146+N / 148+N with the same two failures and no new ones.

### S-A2A-1 — registry, `"call"` arm, `"start p2p voice"` records the pair, instrument
**Files:** `Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs` (`ChatSessionRequest` switch `:719-763`: new `"call"` arm returning `voice_credentials` in the HTTP body; `"start p2p voice"` arm records the pair, 400 on missing `params`, keeps the `ChatterBoxSessionStartReply` echo — `temp_session_id` must echo the viewer's `session-id`); new `A2ASessionRegistry.cs` (same directory; pure, testable: record / lookup by channel / party check / token check / decline / logout-both / TTL sweep / instrument formatting); the DEBUG instrument in the handler.
**Tests:** registry unit tests — record, lookup by channel string, party membership, token mismatch, TTL expiry, both-logout removal, decline removal, second `start p2p voice` for the same pair (idempotent, same token or rotated — decide and test); handler tests — `params` absent → 400; `"call"` → 200 with `voice_credentials { channel_uri == XOR string, channel_credentials == token }`; `"call"` for an unknown session → 404 (the viewer maps non-403 failures to `VoiceCallGenericError`, `llvoicechannel.cpp:668-673`).
**Live test watches:** on the caller's console, the DEBUG lines for `start p2p voice` (with `params`) and `call`; the viewer no longer shows `VoiceCallGenericError` (the `"call"` 400 is gone) and proceeds to a `multiagent` provision, which is **still denied** by O-29 in this slice — the deny line is the confirmation that the wire matches the trace (channel key = `channel`, credentials = token, no `parcel_local_id`).

### S-A2A-2 — invitation via generic `BuildEvent` + callee scene resolution
**Files:** `WebRtcVoiceRegionModule.cs` (`"start p2p voice"` arm: after recording, resolve the callee's scene and enqueue a `ChatterBoxInvitation` event built with `queue.BuildEvent("ChatterBoxInvitation", body)` + `Enqueue`, following `Addons/OpenSim.Addons.Groups/GroupsMessagingModule.cs:706`; body = `session_id`, `session_name` (caller's name), `from_id`, `from_name`, and a `voice` map `{ invitation_type: 2, channel_uri, channel_credentials }` — the shape `llimview.cpp:5196-5214` requires); callee scene resolution follows the group-module pattern (`GroupsMessagingModule.cs:589`, `GetActiveClient`) — callee not on this instance → no invite, record left to TTL, DEBUG line says why (1.7).
**Tests:** event-body builder unit test (exact keys, `invitation_type == 2`, no `instantmessage` block — its presence would route the viewer to the IM branch, `llimview.cpp:5047`); callee-not-present path leaves the record and does not throw.
**Live test watches — the first thing to watch in the whole programme:** **does the callee's incoming-call floater appear at all** (`VoiceInviteP2P`, `llimview.cpp:4150-4153`)? If the callee instead sees an IM window and no call UI, the body carried `instantmessage` or lacked `voice`. Second: the callee's accept produces a `multiagent` provision on the sim (still denied in this slice).

### S-A2A-3 — O-29 predicate replacement, read `channel`, decline, TTL
**Files:** `WebRtcVoiceRegionModule.cs` (`IsProvisionableChannelType` `:439-443` becomes: `local` → true as today; `multiagent` → registry lookup by the request's **`channel`** + party check + `credentials == token`; anything else → false; the `:495-501` refusal unchanged for the false case; `"decline p2p voice"` arm removes the record; TTL sweep wired to an existing timer or the request path); `Addons/os-webrtc-janus/Janus/WebRtcJanusService.cs` (`:241` reads `channel` for `multiagent` — `channel_id` retained only as a fallback for `local`, where it is unused anyway; the stale comment at `:239` corrected); the room-model (a) guard: a `multiagent` success map must not reach `OnListenerProvisioned(agent, room)` with the A2A room (`WebRtcVoiceRegionModule.cs:608-612`) — pass null for the room on `multiagent`.
**Tests:** predicate tests — `local` unchanged (existing `ProvisionChannelTypeGuardTests` still green); `multiagent` with registry hit + party + token → true; wrong party → false; wrong token → false; unknown channel → false; missing `channel_type` / other types → false; `multiagent` provision does not record into `AgentRoomTable` (visibility service test); decline removes; TTL removes; logout-both removes.
**Live test watches:** the two parties hear each other in the A2A room; spatial voice on the same parcel is unchanged during and after the call (room model (a)); hangup on either side ends the call for both (`hangup_on_last_leave`); `hgtrust`-style DEBUG allow line shows verdict + reason.

### S-A2A-4 — grid id in the `multiagent` hash
**Files:** `Addons/os-webrtc-janus/Janus/JanusAudioBridge.cs` (`CalcRoomNumber` `:207-212`: `multiagent` arm adds the grid id — source it from config the same way the gatekeeper does, `GatekeeperURI` or `GridInfo`; `local` arm untouched); its caller `SelectRoom` `:231-246` passes the grid id through; `JanusPeerCtlBatchSink.cs:123-124` (the `local` fallback-room computation) is unchanged.
**Tests:** `RoomNumberFoldTests` extended — `local` room numbers byte-identical before/after; `multiagent` differs across two grid ids for the same channel; deterministic across processes.
**Live test watches:** the Janus room list (`janus list rooms`) shows the A2A room with a `spatial_audio=false` description containing the grid id.

### S-A2A-5 — `viewer_session` binding fix
**Files:** `Addons/os-webrtc-janus/WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.cs` (`:356-363` and `:401-411`: after `TryGetViewerSession`, `vSession.AgentId == pUserID` or treat as not found, with a WARN naming both ids).
**Tests:** provision and signaling with another agent's `viewer_session` id → not found; same agent → found (existing session tests unchanged).
**Live test watches:** no behaviour change in normal use; the WARN never appears during a clean two-party call.

---

## 3. Open verification items
- **Pending re-send stays benign under room model (a):** `VisibilityBatchSender.OnListenerProvisioned` (`:102-107`) is still invoked for a `multiagent` provision (the sender's pending path is deliberately unchanged); with no A2A room recorded, the drained replace targets the agent's spatial room — verify it is a no-op there (empty-column guard `:210-218`) or a harmless re-send, not a spurious mute. Confirm in S-A2A-3's tests and the live test.
- **Mixer caps suit small non-spatial rooms:** the A2A room is created with `spatial_audio:false`, `sampling_rate:48000`, `is_private:false` (`JanusMessages.cs:508-519`); confirm the mixer's per-room participant cap and the peer_ctl plugin tolerate a two-participant room, and that ROOM_FULL (495) cannot fire for the second party.
- **Cross-instance (1.7):** confirm the fail-closed behaviour when the callee is elsewhere is what an operator sees (a clean decline-equivalent), not a hang.

---

## 4. Live test protocol (first two-party test, after S-A2A-2 or later)
Two avatars on one region server, both on Firestorm ≥ `a9a34638a3` (any LL-upstream WebRTC viewer should behave identically). Caller starts a call from the IM floater. Watch, in order: (1) caller console — `start p2p voice` DEBUG with `params`; (2) `call` DEBUG and its 200 with `voice_credentials`; (3) **callee incoming-call floater appears**; (4) callee accepts → `multiagent` provision DEBUG on the sim with `channel` == XOR string and `credentials` == token; (5) allow verdict (S-A2A-3+) or the expected deny (earlier slices); (6) audio both ways; (7) hangup either side → two logout provisions, record removed. Any deviation from the wire trace's predictions is recorded against `docs/voice-a2a-wire-trace-20260830.md` before code changes.
