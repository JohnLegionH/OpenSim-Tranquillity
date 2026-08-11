# Membership-tiers — working ground rules

Operating rules for the **membership-tiers** work. Read this and `docs/membership-findings.md` before
changing any code. (Written fresh for this tree — do **not** import the same-named file from the retired
fork.)

## Scope & authority
- **Target:** `feature/membership-tiers`, cut off `tranq-baseline`, in `D:\tranquillity-develop`
  (Tranquillity @ `81e5c244` + local commits).
- **`docs/membership-findings.md` is the authority** for this tree's layout, class names, and line numbers.
  Read it before changing code; **prefer it over re-deriving**, and **prefer it over anything asserted in
  chat** (chat has been wrong about this codebase more than once).
- **Do NOT read, search, or cite `D:\legion-grid-source`.** It is the retired fork. Every path/line must
  come from `D:\tranquillity-develop`.

## Architecture template
- **The Experience subsystem is the template** for a new membership/tier service. Use its **real inventory
  in findings §7** — not assumptions about stock OpenSim layout (this tree was reconciled against upstream
  and does not match stock).
- **Follow the tree's own layout convention.** Framework code here uses a `Source/…` project layout with
  **dot-separated** project names, and some interfaces live in unexpected projects — e.g.
  `IMoneyModule` is at `Source/OpenSim.Framework/IMoneyModule.cs` (not `Region.Framework/Interfaces`).
  Put new projects where this tree would put them (`Source/OpenSim.Services.MembershipService/`,
  `Source/OpenSim.Server.Handlers/Membership/`, `Source/OpenSim.Data.MySQL/…`, etc.), mirroring Experience.

## ★ LANDMINES (verified in this tree)
- **filename ≠ classname.** The Experience Robust handler is in file `ExperienceServerConnector.cs` but the
  class is **`ExperienceServiceConnector`**. Config `LocalServiceModule` / `[ServiceList]` entries must use
  the **short class name** (`…dll:ExperienceServiceConnector`). A fully-qualified/namespaced name silently
  returns **null** from `ServerUtils.LoadPlugin` — no exception, just a dead service. Name membership
  classes so the short name in config is exactly the class name.
- **`Source/OpenSim.Data.Model/` is scaffolded EF models — NOT used at runtime.** The **live data path is
  `Source/OpenSim.Data.MySQL/*`** (e.g. `Source/OpenSim.Data.MySQL/MySQLExperienceData.cs`). New data
  classes go under `Source/OpenSim.Data.MySQL/`, with the interface in `Source/OpenSim.Data/`.
  > Correction: the retired-fork path `OpenSim/Data/MySQL/*` does **not** exist here — do not use it.

## Data layer
- Experience is **MySQL-only** in this tree and **the grid runs MySQL** (findings §8).
- **`IMembershipData` must be a clean interface with NO MySQL types in its signatures** (no
  `MySqlConnection`, no MySQL-specific rows) — so a SQLite implementation can be added later without
  touching the service layer. Interface in `Source/OpenSim.Data/`, MySQL impl in `Source/OpenSim.Data.MySQL/`.

## Build / project hygiene
- **Every new `.csproj` must be added to `Tranquillity.sln`.**
- **New `.csproj` must NOT pin `<TargetFramework>` explicitly — inherit it from `Directory.Build.props`.**
  Upstream has moved to **net10** and this tree will rebase eventually; the three Jolt csprojs pin `net8.0`
  explicitly and will need manual fixing at that point. **Do not add to that debt.**

## Behaviour / safety
- **Default OFF.** With the module unconfigured, behaviour must be **byte-identical** to today. Every slice
  must be **provably inert when disabled** (no registered handlers, no charge changes, no advertised limits
  altered).
- **Do NOT touch RegionStore or any region-side persistence.** This is a services/account-side feature.
- Respect the per-agent injection seams found in the audit (attachment cap `sp.UUID` in scope §2;
  SimulatorFeatures `OnSimulatorFeaturesRequest(agentID, …)` §3; `LLLoginResponse.MaxAgentGroups` setter §4)
  rather than changing constructor signatures or shared constants.

## Remote discipline
- **origin only.** Never push to `upstream` or `archive` (their push is/should stay disabled).
