# Fork build notes — OpenMetaverse vendoring (LOCAL ONLY)

**Audience:** John (fork maintainer) and future-John. **Status:** permanent fork build infrastructure.

## Why this fork vendors OpenMetaverse

John is **not a member of the OpenSim-NGC org**. NGC's v1.0+ build centralizes OpenMetaverse
(`OpenMetaverse`, `OpenMetaverse.Types`, `OpenMetaverse.StructuredData`) at version **1.0.6**, served
from the **authenticated private GitHub Packages feed** `nuget.pkg.github.com/OpenSim-NGC`. That feed
returns **HTTP 403** for a non-member — **no personal access token can fix it** (the account has no access
to the org's packages). The `OpenMetaverse.Types` split does not exist on public nuget.org at all, so
there is no public fallback either.

To build feed-free, this fork **vendors OpenMetaverse 1.2.13** (the version the fork has always built
against, and the version the Jolt code was authored against) into `Library/` and overrides
`Directory.Build.props` to reference those DLLs by `HintPath` instead of the NGC feed. `1.0.6` and `1.2.13`
are the same lineage (identical package IDs / DLL names); the Jolt module + full OpenSim chain build with
**0 errors** against the vendored 1.2.13.

## ★ THE PR RULE — do NOT send this build infrastructure to NGC

When contributing Jolt work **upstream to NGC** (a PR against OpenSim-NGC), **do not include the OMV
vendoring**. NGC builds against their centralized 1.0.6 feed; the Jolt projects should inherit that, not
this fork's vendored 1.2.13. Including it would rip out their feed and add fork-local binaries they don't want.

### Local-only file checklist (exclude ALL of these from an upstream PR)

| File | What it is | For a PR |
|---|---|---|
| `Directory.Build.props` | changed from NGC's centralized OMV `PackageReference` (1.0.6, feed) to `Reference … HintPath` into `Library/` | **revert to upstream's** (keep NGC's centralized 1.0.6) — do not send the HintPath override |
| `Library/OpenMetaverse.dll` | vendored 1.2.13 binary | **omit** |
| `Library/OpenMetaverse.Types.dll` | vendored 1.2.13 binary | **omit** |
| `Library/OpenMetaverse.StructuredData.dll` | vendored 1.2.13 binary | **omit** |
| `Docs/fork-build-notes.md` | this file (fork-specific) | **omit** |

The Jolt csprojs themselves (`LegionJolt`, `Legion.Physics`, `Legion.Vehicles`, tests) are **PR-safe**:
they carry no inline OMV feed reference and simply inherit whatever `Directory.Build.props` centralizes —
NGC's 1.0.6 upstream, this fork's vendored 1.2.13 locally. They go in the PR unchanged.

### How the commits are structured to make this easy

On every branch the vendoring lives in a **single dedicated commit**, titled:

> `LOCAL FORK BUILD ONLY — vendored OMV 1.2.13 for non-org-member build; DO NOT include in NGC PRs (NGC uses centralized 1.0.6 feed)`

whose contents are exactly the checklist above. The upstream merge and the Jolt source are in **separate**
commits. To prepare an upstream PR, **drop that one commit** (`git rebase -i` and delete it, or cherry-pick
only the Jolt/merge commits) and the tree reverts to NGC's centralized OMV automatically.

## Pushing

- **Pushing to `origin` (John's own fork) is ALWAYS safe** — the vendoring belongs there; it's what makes
  the fork buildable. Push the whole stack, vendoring commit included.
- **Only an upstream NGC PR** needs the exclusion above. `origin` ≠ a PR.
