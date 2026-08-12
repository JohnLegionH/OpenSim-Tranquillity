# Membership tier caps in SimulatorFeatures (M3)

`MembershipFeaturesModule`
(`Source/OpenSim.Region.CoreModules/ServiceConnectorsOut/Membership/MembershipFeaturesModule.cs`)
advertises an agent's resolved membership-tier caps in the region's **SimulatorFeatures** capability,
so viewers other than the login path see per-account limits.

## What it sets

For a local account, on each SimulatorFeatures fetch it writes the agent's resolved tier values into the
per-request features map:

| SimulatorFeatures key | Tier field |
|---|---|
| `MaxAgentAttachments` | `max_attachments` |
| `MaxProfilePicks` | `max_picks` (not in the grid-wide defaults — added here for members) |
| `AnimatedObjects.MaxAgentAnimatedObjectAttachments` (nested OSDMap) | `max_animesh` |
| `MaxAgentGroups`, `MaxAgentGroupsBasic`, `MaxAgentGroupsPremium` | `max_groups` |

The nested `AnimatedObjects` map is updated in place (sibling keys such as `AnimatedObjectMaxTris` are
preserved); this is safe because `SimulatorFeaturesModule.DeepCopy()` produces a true deep copy per request.

## Consistency with login (the M2 gap this closes)

`LLLoginService` already advertises `response.MaxAgentGroups = GetMembership(agent).max_groups`, but
SimulatorFeatures previously showed the grid-wide `Constants.MaxAgentGroups`. Both now read the same
`IMembershipService.GetMembership(agentID).max_groups`, so **for a given account the login response and
SimulatorFeatures advertise the same group number.**

`max_groups == 0` means **unlimited** and is passed through unchanged — identical to the login path. Note
current Firestorm reads only the login `MaxAgentGroups` value and ignores the SimulatorFeatures group keys;
the keys (including the Basic/Premium variants) are set for consistency and for other viewers.

## Timing caveat — fetched once on region arrival

The SimulatorFeatures cap is requested **once when an agent arrives in a region** (see
`HandleSimulatorFeaturesRequest`). A **mid-session tier change is therefore not seen until the agent
crosses to another region or relogs.** Console `membership set` updates the DB immediately and is reflected
at the next region crossing / relog, not live in the current region.

## Behaviour guarantees

- **Never throws.** `HandleSimulatorFeaturesRequest` invokes the delegate inside `try { } catch { }` and
  discards exceptions — a throw would silently degrade the agent to grid-wide defaults with no trace. The
  module catches internally and logs a warning (`[MEMBERSHIP FEATURES]`).
- **HG visitors / no local account:** the map is left untouched (grid-wide defaults). The module gates on
  a local `UserAccount` (via `IUserAccountService.GetUserAccount`), because `GetMembership` would otherwise
  resolve an unknown agent to Basic and substitute defaults — which we must not do for a visitor.
- **Inert when unconfigured or when no `IMembershipService` is registered** in the region.

## Configuration

Disabled by default. Enable per region-server:

```ini
[SimulatorFeaturesMembership]
    Enabled = true
```

Requires a membership region connector so `IMembershipService` is registered (the same
`[Modules] MembershipService` / `[MembershipService]` wiring used by M1/M2). With the section absent, or
with no membership connector, the module does nothing.

## Gate

Two avatars on different tiers in the same region receive different caps in their SimulatorFeatures fetch,
and for a given account the login response and SimulatorFeatures advertise the same `MaxAgentGroups`.
