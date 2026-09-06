# S8 — what S0c did not cover: the listed-but-unresolvable wearable

2026-09-05. A note against the appearance-integrity work, because the two defects look like one and the
distinction is what made the second one survive the first fix.

## The two shapes of the same loss

A wearable slot can be emptied by the region in two different ways, and they enter the code at different points.

| | S0c (Ledger R-4 / Q-3) | S8 |
|---|---|---|
| Input | `AgentIsNowWearing` names **fewer slots** than the agent is wearing | the slot **is** named, and its item id will not resolve |
| Where | `Client_OnAvatarNowWearing` (`AvatarFactoryModule.cs:1262-1300`) | `SetAppearanceAssets` (`:940-947`) |
| Old behaviour | built a fresh `AvatarAppearance` and filled only the listed slots, so unlisted ones were dropped | logged "setting to default" and **removed** the wearable |
| Fix | merge the list into the existing wearables instead of replacing | keep the wearable that is in the slot |
| Fixed in | S0c | S8, `483c2d7a13` |

Both end in the same place: `SaveAppearance` persists the reduced set, and `AvatarService.SetAvatar` deletes
every row for the agent before rewriting (`AvatarService.cs:93`), so the slot leaves the stored record
altogether. Both are self-reinforcing — the next login reads back the damaged record.

## Why S0c's fix could not reach this one

S0c changed how the *viewer's list* is applied. It never touched asset resolution, which happens later and from
a different input: whatever is in `sp.Appearance.Wearables` by the time the save timer drains. An outfit that
survived `MergeNowWearing` intact could still lose four slots a few hundred milliseconds later because the
inventory service did not recognise their item ids.

The live case shows the gap clearly. Truly's `AgentIsNowWearing` was not the problem and her Current Outfit
folder (`71c3c184`) linked valid skin, eyes and hair throughout. What failed was `GetItem` for four ids that a
stale May-2 viewer cache from the previous grid still believed in — items that exist nowhere in
`inventoryitems`. S0c's merge was working exactly as designed and the slots went anyway.

## Why no test caught it

`AvatarFactoryNowWearingTests` covers S0c thoroughly, and every one of its cases uses item ids the inventory can
resolve. It therefore never enters the `else` branch at `:940`. Worse, a test that *does* wear an unresolvable
item still passes against the broken code unless the agent has an inventory root, because `SetAppearanceAssets`
skips its entire loop otherwise (`:911`) — so the naive version of the S8 test was green before the fix and had
to be rebuilt on `UserAccountHelpers.CreateUserWithInventory` to go red.

That is the transferable lesson: a test for a resolution failure has to make resolution actually run.

## Upstream

Both S8 defects are inherited unchanged from OpenSim-NGC develop `a68d59f232`: the removal at
`AvatarFactoryModule.cs:871-878` and the unguarded `SaveAppearance` at `:811-828`. Neither is a fork
regression, and neither has been reported upstream from here.

## Still open

`SetAppearanceAssets` leaves an unresolved wearable with whatever asset id it already carried. For an item from
another grid that asset id may be meaningless here, so the slot is *populated but unrenderable* rather than
*missing*. That is strictly better than the old behaviour — the record survives, and a viewer that re-reads its
inventory repairs it — but it is not the same as correct, and nothing currently reconciles such a slot on
login.
