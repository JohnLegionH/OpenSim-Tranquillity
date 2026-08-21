# Design Brief — Voice Moderation

**Status:** DRAFT. Two open questions must be answered in-world before slice 1 freezes.
**Date:** 2026-08-21.
**Basis:** CC recon 2026-08-21 against `tranquillity-develop @ e2444037d7` and
`D:\phoenix-firestorm` (read-only reference); SL viewer release notes 26.1.0 and 26.2.0.
**Target:** parity with the voice moderation LL shipped in viewer 26.1, Dec 2025.

## Purpose

Let an authorised user silence voice on their land. An event host mutes the room; a
parcel owner deals with a griefer; a group officer moderates their own venue. Not in
the original spec — added because SL shipped it and a social grid will be expected to
have it.

## SETTLED: the viewer is already built and the transport is fixed

**Firestorm 26.2.0 has the moderation UI and already sends the command.** Right-click a
name in the Nearby chat participant list → Moderator Options → Mute everyone / Unmute
everyone / individual mute, wired through `LLNearbyVoiceModeration` to a CAP POST
(`llnearbyvoicemoderation.cpp:73`, `:92`-`:95`, `:127`-`:130`).

**Capability:** `SpatialVoiceModerationRequest`, HTTP POST.
**Body:** `{ "operand": "mute" | "unmute", "agent_id": <uuid> }` for individual,
`{ "operand": "mute_all" | "unmute_all" }` for everyone.

**The body carries no parcel identifier.** Neither operand form names a parcel or region.
The server therefore resolves the target parcel from the requesting agent's
`ScenePresence` position at request time (see slice 1). This is not a defect in the
protocol — it is what makes parcel scope achievable *without trusting the viewer*: the
sim decides which parcel the moderator is standing on, so the viewer cannot widen the
scope by lying about it.

**The server does not implement it.** Zero matches for `SpatialVoiceModerationRequest`
across `Source/`. The command is sent and silently dropped.

**This is the inverse of the channel-full case.** There, the server produced a
condition the client could surface but never reached. Here the client is ready and the
server is the missing half — so the feature is verifiable end to end the moment the
handler exists, with no viewer work at all.

**The server does not get to design the transport.** The viewer has settled it. The
server conforms.

## Distinct from group-chat moderation

The tree already contains an older, unrelated feature: `ChatterBoxSessionAgentListUpdates`
with `is_moderator` and `can_voice_chat` (`EventQueueGetHandlers.cs:230`-`:249`). That
is IM-session moderation, not parcel voice. Do not extend or reuse it.

## SETTLED: moderation is a source-side rule

`VisibilityRules.IsExcluded` is four short-circuit rules: source voice audibility
(`:23`), estate ban (`:27`), parcel ban and restrict (`:34`), SeeAVs (`:38`). The first
is source-side; the rest are symmetric.

**Moderation is source-side** — a moderated avatar is inaudible to everyone, so the rule
reads only the source and the source's parcel and never the listener. It is the simplest
rule in the set.

It slots in after the estate ban and before the pairwise parcel ban, keeping cheap
source-side checks first. The rules are independent early-returns, so placement does not
affect correctness.

**What it needs:** one new predicate on `ParcelView` — an `IsVoiceModerated(UUID)`
delegate mirroring the existing ban delegate (`FeederWorld.cs:53`), fed by the adapter.
Exemptions live inside the delegate, exactly as the ban delegate does. Nothing new is
needed from `AgentView`; moderation is keyed by avatar id, already present as `source.Id`.

**The semantics are parcel-sticky, not avatar-sticky — and this is correct, not a bug.**
Because the rule tests the source's *current* parcel (`sourceParcel.IsVoiceModerated(source.Id)`),
a moderated avatar who walks off the parcel is no longer moderated (its `sourceParcel`
changes), and an avatar who arrives onto the parcel is (mute-everyone is parcel state,
checked against whoever is currently there). The mute follows the *parcel*, not the
avatar. That matches SL and is the desired behaviour; a later reader must not "fix"
`leaving the parcel escapes the mute` into avatar-stickiness.

**Fan-out cost.** A source-side mute excludes that source in *every* listener's set — the
same shape as an estate ban. So `mute_all` on a crowded parcel is an N-per-source fan-out
into the peer_ctl feed. This is expected and is what acceptance §1's `excluded_entries`
check observes; it is not a new cost class, but it should not surprise anyone.

## SETTLED: authorisation composes from existing pieces

SL authorises three ways, and all three have server-side equivalents already in use by
the ban path:

| Case | Mechanism |
|---|---|
| Land owner | `avatar.Equals(LandData.OwnerID)` |
| Estate manager or owner | `EstateSettings.IsEstateManagerOrOwner(avatar)` |
| Group Moderate Group Chat | `IsGroupMember(LandData.GroupID, user, (ulong)GroupPowers.ModerateChat)` when `IsGroupOwned` |

No combined "may this user moderate voice here" helper exists; it is composed, the same
way the ban path composes owner, estate-manager and admin exemptions.

**The server must re-authorise independently.** The viewer's own
`isNearbyChatModerator()` gate (`llnearbyvoicemoderation.cpp:196`-`:220`) is UI-only and
spoofable. This is the same reasoning the ban path already applies. Because the target
parcel is resolved from the requester's position (above), the authorisation is always
against the parcel the moderator actually occupies.

## Scope

**Slice 1 — the feature, in memory:**

1. **Advertise and handle** the `SpatialVoiceModerationRequest` CAP, parsing all four
   operands. Advertising is not optional: the viewer calls
   `region->getCapability("SpatialVoiceModerationRequest")` (`llnearbyvoicemoderation.cpp:73`)
   and will not POST if the capability is absent, so it must appear in the region's
   seed-capabilities list — handling the route alone is not sufficient and the acceptance
   test cannot fire until the cap is advertised.
2. **Resolve the target parcel** from the requesting agent's `ScenePresence` position, and
   reject the request if it cannot be resolved. The CAP body names no parcel, so this
   resolution is the only trustworthy source of scope; it is also what pins `mute_all` to
   the moderator's parcel rather than the region.
3. **Authorise server-side** by composing the three checks above, against the resolved
   parcel.
4. **Hold sticky per-parcel state** — a mute-everyone flag plus a per-avatar muted set —
   in memory, so late joiners are muted for the process lifetime.
5. **Feed the matrix** through the new source-side `ParcelView` predicate.

**Slice 2, deferred:**

- **Persistence across restart.** `LandData` has no extensible field, so this means a
  scalar on the land row plus a `landvoicemoderation` table mirroring `landaccesslist` —
  a schema change across MySQL, PGSQL and SQLite. `landaccesslist` is the proven pattern:
  a separate `LandUUID`-keyed table with delete-and-reinsert on store
  (`MySQLSimulationData.cs:740`, `:745`, load at `:960`).
- **The group-owned-parcel-with-its-own-voice-channel case** from SL's spec.

## Constraints

- **The viewer's body shape is fixed.** Conform to it exactly; do not invent fields. In
  particular it carries no parcel id — do not add one; resolve scope from position.
- **Never trust the viewer's authorisation.** Re-check server-side on every operand,
  against the position-resolved parcel.
- **The matrix is the single enforcement point.** Moderation must not be enforced at
  provision time or anywhere else, for the same reason §E's TaxFree fix lives only in
  the matrix — one enforcement point or none.
- **In-memory state is deliberate for slice 1** and must be recorded as non-persistent,
  so nobody assumes a restart preserves a mute.

## Acceptance

Verifiable end to end in Firestorm 26.2.0 with no viewer change:

1. Moderator right-clicks a name, chooses Mute everyone — the room goes silent for
   non-exempt speakers, confirmed by `excluded_entries` on the mixer and by ear.
2. A late joiner arrives and is also muted, confirming stickiness.
3. Unmute everyone restores speech.
4. A non-authorised user attempting the same is refused, and the server logs the refusal
   rather than silently dropping it — per the ban-add instrumentation precedent, a silent
   refusal is a defect.

## Open questions — answer in-world before freezing slice 1

1. **Does SL's `mute_all` scope agree with parcel?** The scope is already *settled by the
   body shape*: the CAP names no parcel, so the server resolves it from the moderator's
   position and pins `mute_all` to that parcel — a parcel owner cannot silence a region.
   The viewer logs the operand as "all residents in this region"
   (`llnearbyvoicemoderation.cpp:132`), while SL's documentation says parcel and the
   authorisation gate is parcel-based. What remains is not a design choice but a
   confirmation: verify that SL's live behaviour matches parcel scope rather than assuming
   the viewer's log string is literal.

2. **Who is exempt from `mute_all`?** Is the moderator self-exempt? Are estate managers
   and the parcel owner exempt? SL's exact exemption set is not documented in the release
   notes and should be confirmed in-world. The ban delegate's exemption pattern is the
   model, but the answer is a behaviour question, not a code one.
