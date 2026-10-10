# Jolt physics

The Jolt module is an optional physics engine for the region server, built on
[Jolt Physics](https://github.com/jrouwe/JoltPhysics) through the joltc native library and the
JoltPhysicsSharp binding. Each region gets its own Jolt physics system, and LSL vehicles run on
Second Life's documented vehicle model: the motors, friction, hover, the vertical attractor, banking
and deflection (see "Vehicles" below). The sled's slope assist is not a documented Second Life
behaviour; it is carried over from the InWorldz Halcyon sled code. The vehicle types' earlier
defaults, from the InWorldz Halcyon vehicle code, are kept behind `VehiclePresets = legacy`.

This guide is for operators. It covers selecting Jolt, its settings, its console commands, and
the platforms it runs on today.

## Platforms

The module runs on joltc, the native library of the `JoltPhysics.Native` package (version 1.0.4,
the one JoltPhysicsSharp 2.19.1 depends on). The build puts the package's file for each platform
in the output:

| Platform | File in the build output | Tested by this project |
|---|---|---|
| Windows x64 | `runtimes/win-x64/native/joltc.dll` | yes |
| Linux x64 (glibc) | `runtimes/linux-x64/native/libjoltc.so` | yes |
| Windows Arm64 | `runtimes/win-arm64/native/joltc.dll` | no |
| Linux Arm64 (glibc) | `runtimes/linux-arm64/native/libjoltc.so` | no |
| macOS (x64 and Arm64) | `runtimes/osx/native/libjoltc.dylib` | no |

On a platform this project does not test, the module starts and logs a notice saying so.
musl-based Linux (Alpine) has no native: there, keep another physics engine.

The stock joltc runs **one job pool** (`JobPools`, below): its physics systems share one scratch
allocator, so two physics updates at once would stop the process. For more than one pool, replace
the file with this project's patched build (Windows x64 and Linux x64 only); how, and why, is in
[native/joltc/README.md](../native/joltc/README.md).

A build with no runtime identifier carries every platform's file, so the same output runs on each.
A `dotnet publish -r <rid>` puts that platform's file beside the assemblies instead, where the
module also finds it. Copy the whole output, including its `runtimes` folder.

### What is supported

| Setup | On the stock joltc | On the patched joltc |
|---|---|---|
| One region per region server process | Tested and supported | Tested and supported |
| Several regions in one process, on one job pool (`JobPools = 1`, the default) | Tested and working: the regions take turns on the pool | Tested and working |
| More than one job pool (`JobPools` above 1) | Not available: the module runs one pool (below) | Tested and working; Windows x64 and Linux x64 only |

When `JobPools` asks for more than one pool on the stock joltc, the region server still starts and
every region runs. The module runs one pool, sized as `JobPools = 1` would size it, logs one warning
at start with the setting, the reason and the remedy (shown below, under "The native check at start"), and
`jolt capacity` shows the pools in use and why. All the process's regions then take turns on that
pool, so a region whose physics is busy can make the others wait for their step; no step is skipped.

### Selecting the patched build

The patched joltc is this project's build of the same joltc source with two patches: each physics
system gets its own scratch allocator, and the map of systems gets a lock. It is built on GitHub's
runners by `.github/workflows/joltc-native.yml` from pinned joltc and Jolt Physics commits and the
patches in `native/joltc/`, so anyone can rebuild it and compare the hashes.
[native/joltc/README.md](../native/joltc/README.md) gives the recipe and the hashes.

The repository does not keep the compiled file. To get it, run the `joltc native` workflow
(`workflow_dispatch`, on a fork or a branch that has it) and download its `joltc-win-x64` or
`joltc-linux-x64` artifact: the file, its `SHA256SUMS`, its exports and the toolchain it was built
with. Check its hash against the table in the README; a file with another hash is refused at start
(see "The native check at start").

The module has no setting for the file's path: it loads the file it finds in the output. To select
the patched build, replace the stock file in the build or publish output with the patched file for
the platform, under the same name (`runtimes/win-x64/native/joltc.dll` or
`runtimes/linux-x64/native/libjoltc.so`; for a build or publish for one runtime identifier, the
file beside the application's assemblies), then check the output:

```
pwsh -File assert-joltc-native.ps1 -PublishDir "<publish directory>" -RequirePatched
```

At start the module's line says `patched build (native/joltc); safe for more than one job pool`, and
`JobPools` takes effect. A rebuild of the output puts the stock file back, so replace it again after
each build. Other platforms have no patched build and run one pool.

### When the JoltPhysics.Native version changes

The module knows each native file by its SHA-256, so a new `JoltPhysics.Native` version (usually
through a JoltPhysicsSharp update) is refused at start until its files are recorded, and
`JoltNativePackageTests` fails on it with what to check. Before recording it:

1. Re-check the rules that make one shared scratch allocator safe on one pool, against the joltc
   and Jolt sources the new package is built from: the list in `JoltNativePackageTests`
   (`ShapeAndAllocatorRuleTests` checks the module's side of each rule).
2. Record the new files: `JoltNative.PackageVersion` and the stock entries of `JoltNative.Known`,
   the stock table of `assert-joltc-native.ps1` and the stock table of `native/joltc/README.md`.
3. Run the whole Jolt suite and the harness on the new files, and record the harness baselines of
   the new version on Windows x64 and Linux x64 ("Physics harness", below). Until they are
   recorded, the regression check reports the new files as having no baseline.

The patched build is made from the joltc source behind `JoltPhysics.Native 1.0.4` and replaces the
stock file only under JoltPhysicsSharp 2.19.1. With a newer package, rebuild it from the joltc
source of that package (the recipe in native/joltc/README.md) and record its hashes before using
it for more than one pool.

### The native check at start

When `physics = Jolt`, the module picks the file for the platform it runs on, computes its SHA-256
and compares it with the builds it has a record of. With a good file it logs one line, once per
process, naming the build:

```
[JOLT SCENE] joltc for linux-x64: /opt/opensim/bin/runtimes/linux-x64/native/libjoltc.so sha256 5FC05170... (stock JoltPhysics.Native 1.0.4; one job pool)
```

or, with the patched build in place:

```
[JOLT SCENE] joltc for linux-x64: /opt/opensim/bin/runtimes/linux-x64/native/libjoltc.so sha256 EEAD7C1A... (patched build (native/joltc); safe for more than one job pool)
```

With `JobPools` above 1 on a native that runs one pool, it also logs one warning, and runs one
pool:

```
[JOLT SCENE] [Jolt] JobPools = 3, but the module runs ONE job pool: the loaded joltc (stock JoltPhysics.Native 1.0.4) is not safe for more than one job pool: its physics systems share one scratch allocator, and two physics updates at once would stop the process. Regions take turns on that pool. To run more than one pool, replace runtimes/linux-x64/native/libjoltc.so with this project's patched build (native/joltc/README.md); otherwise set [Jolt] JobPools = 1.
```

In the cases below the module logs one error line and throws from its `Initialise`, before any
region's physics exists, the same way as the meshing check under "Selecting Jolt"; the exception
is not caught on the way up, so the simulator does not finish starting. The cases:

- the platform has no native:
  `Jolt physics has no native library for this platform (linux-musl-x64). Supported platforms: win-x64, linux-x64, win-arm64, linux-arm64, osx-x64, osx-arm64. Choose another physics engine in [Startup] physics.`
- the file is missing: `Jolt physics: the native library for linux-x64 is missing: <path>. ...`
- the file's hash is not one the module has a record of:
  `Jolt physics: <path> has sha256 <hash>, which is not a joltc build this module has a record of for linux-x64 (...). ...`

`[Jolt] AllowUnrecordedNative = true` (default `false`) loads a file whose hash the module does not
know, with the start line logged as a warning instead. Use it only for a joltc you built yourself;
the module runs one job pool on it.

## Selecting Jolt

Jolt is chosen per simulator in `[Startup]`, and it needs the Meshmerizer mesher:

```ini
[Startup]
    physics = Jolt
    meshing = Meshmerizer
```

- With any other `physics` value, or none, the module does nothing for any region: the native
  is not loaded or hashed, so a missing native, an unrecognised one or a platform with none
  (musl Linux) makes no difference; no `[Jolt]` key is read, so a missing or invalid `[Jolt]`
  section is not reported; no job pool, thread or timer starts; no `jolt` console command is
  registered, test commands included; and the module logs nothing. The host itself still loads
  the Jolt assemblies, as it loads every plugin assembly in its folder to look for modules, and
  its own debug lines name the module (`[REGIONMODULES]` finding it and adding each region to
  it).
- `physics` is read from the simulator's configuration, as every engine reads it, so all the
  regions of one simulator run the same engine.
- With `physics = Jolt` and any `meshing` other than `Meshmerizer`, the module logs that meshing
  must be Meshmerizer and throws "Invalid physics meshing option for Jolt" when it initialises.
- The shipped default stays `physics = ubODE`.

`assert-joltc-native.ps1` (next to the module's project file) checks a build or publish directory
before it is deployed: every `runtimes/<rid>/native/` joltc must be a build the module has a
record of for its platform (with `-RequirePatched`, the patched build where there is one), and no
other joltc file may be there (`-AllowStray` reports such files without failing, for an
installation that still holds files from an older deploy). It runs in Windows PowerShell and in
PowerShell 7 on Linux:

```powershell
powershell -File assert-joltc-native.ps1 -PublishDir "<publish directory>"
```

```
pwsh -File assert-joltc-native.ps1 -PublishDir "<publish directory>"
```

## Settings

All settings are in the `[Jolt]` section. `OpenSimDefaults.ini` lists every key with its default
and what it does; copy a key into `OpenSim.ini` to change it. An invalid value logs a warning that
names the value and the default used instead. The keys cover gravity, solver sub-steps and iterations, the worker threads
and job pools shared by all regions in the process, body / pair / contact capacities (optionally
scaled with region area for var regions), the per-frame update buffers, the avatar jump speed,
the avatar walk, run and fly speeds (see "Avatar speeds"),
how often capacity warnings are logged, the physics step rate (below), the vehicle settings (the
share of gravity on a ground vehicle, the type presets, the limits and the sled's assist; see
"Vehicles"), the engine's body speed caps, the prims' damping (see "Physics material on objects"), the limits on script ray casts and pushes (below),
`AllowUnrecordedNative` (above), and `TestCommands` (below).

### Physics step rate

`[Jolt] PhysicsStepRate` runs physics at a set rate inside each region heartbeat. The default is 45:
fixed 45 Hz physics steps, whatever the heartbeat (`[Startup] FrameTime`, about 11 per second).
`PhysicsStepRate = 0` runs one physics step per heartbeat instead, as earlier versions did by default:

```ini
[Jolt]
    PhysicsStepRate = 45            ; the default; 0 = one physics step per heartbeat
    PhysicsStepCollisionSteps = 2
```

`OpenSimDefaults.ini` leaves `PhysicsStepRate` commented out, so the key is set only where an
operator sets it; that decides how a rate the heartbeat cannot honour is reported (below).

- Each step is exactly 1/45 s. An 11 Hz heartbeat runs 4 or 5 steps; the time left over carries
  into the next heartbeat, so the long-run rate is exact.
- In every step: the vehicle controllers, the avatar step, and forces and changes queued by
  scripts. Once per heartbeat: positions and velocities sent to the scene (and from there to
  viewers), the settle update of a body that came to rest, and collision events.
- Collision events cover every step of the heartbeat. A touching pair counts once per heartbeat,
  as before. A contact that begins and ends inside one heartbeat gives `collision_start` in that
  heartbeat and `collision_end` in the next.
- `llApplyImpulse` and `llApplyRotationalImpulse` on a physical object give the same velocity
  change as with one step per heartbeat, and so does a force the scene marks as a push. A force or
  torque set with `llSetForce` or `llSetTorque` acts in every step.
- `PhysicsStepCollisionSteps` replaces `CollisionSteps` while the rate is on: the solver's
  sub-steps per physics step. 2 at 45 steps per second slices the solver at 90 Hz.
- A heartbeat runs at most 16 steps. A heartbeat that would need more runs 16 and drops the rest
  of its time; `jolt capacity` shows how many heartbeats did ("physics steps").
- A rate set below the heartbeat's own rate is refused with one warning at region start, and the
  region runs one step per heartbeat. With the key not set, a heartbeat faster than 45 Hz runs one
  step per heartbeat too, and the region logs one information line saying so, not a warning.
- A value that is not a number from 0 to 1000 (text, a negative number, above 1000) logs one
  warning naming the value and the rate used, and the region steps at the default 45. It counts as
  not set.
- The keys are read once, when the region starts.

What changes at 45: vehicles and avatars integrate in 1/45 s steps, so anything whose behaviour
still depends on the step changes. The vehicle motors, friction, gravity, hover, the vertical
attractor, banking and deflection, a jump's rise and walking speed do not: each is stepped exactly
(see "Vehicle motors and friction" and the sections after it), so in the harness the test car on level ground leaves the key at 3.79 m/s
either way and an avatar jump rises 0.82 m. So does the sled's slope assist.
Position updates to viewers, timers and sensors stay at the heartbeat rate. Physics costs more:
in the harness the test car takes about 1.5 times the step time of one step per heartbeat
(`jolt metrics` shows each region's step time).

### Script ray casts and pushes

Scripts written by anyone can cast rays and push avatars. These keys bound what they can make one
region do. Each guards against abuse, so each defaults to its safe value; all are read once, when
the region starts.

| Key | Default | What it limits |
|---|---|---|
| `RayCastBudgetMs` | 5 | Time (ms) one region's script ray casts may take in one heartbeat, at any `PhysicsStepRate`: the casts the module can tell are a script's (Phlox's `llCastRay`). |
| `RayCastSimulatorBudgetMs` | 5 | Time (ms) every other ray cast may take in one heartbeat: the simulator's own (rez placement, landing after login or teleport) and YEngine's `llCastRay`. |
| `RayCastMaxTestedHits` | 1024 | Hits the engine may report to one cast before it is cut short (a mesh reports each triangle the ray crosses). |
| `RayCastMaxHits` | 256 | Hits one cast returns, Second Life's documented maximum for `RC_MAX_HITS`. |
| `AvatarPushMaxSpeed` | 10 | Speed (m/s) pushes can give an avatar. 0: pushes do not move avatars. |
| `AvatarPushRecovery` | 5 | How fast (m/s per second) an avatar's push allowance refills. |

**Ray casts.** A cast holds the region's physics lock, and the region's physics step waits for it
while holding its job pool, so a slow cast delays that region and every region sharing its pool.
The module bounds a script cast three ways:

- It keeps only the closest hits the script asked for, and once it has them the engine skips
  everything further along the ray. A long ray through a pile of prims costs about what the
  part of it up to those hits costs.
- A ray is cut to the region widened by 8 m on each side (Second Life: "The random failures seem
  to happen if the ray begins or ends more than 8 meters outside of current region bounds"), and
  between heights -128 and 10000 m. A ray with a non-finite start, direction or length hits nothing.
- A cast that runs past what is left of its budget in the heartbeat, or past `RayCastMaxTestedHits`,
  is cut short; once the region has spent a budget in a heartbeat, further casts charged to it are
  refused until the next one. Either way the script gets `RCERR_CAST_TIME_EXCEEDED` (-3), which Second
  Life documents as "the parcel or agent has exceeded the maximum time allowed for raycasting. This
  resource pool is continually replenished, so waiting a few frames and retrying is likely to
  succeed." That holds for Phlox's `llCastRay`, which reports an error from the physics query as
  that code (it logs each one as a warning). OpenSim's `llCastRay` (LSL_Api, used by YEngine) cannot
  be told: it gets no hits from the physics engine, and returns only what it tests itself (avatars,
  phantoms and, for long rays, the ground) with a status of that many hits.

The two budgets are drawn separately each heartbeat, and running out of one never refuses a cast
charged to the other. `RayCastMaxTestedHits` and `RayCastMaxHits` apply to every cast in both.

- `RayCastBudgetMs` covers the casts the module can tell are a script's. Today that is Phlox's
  `llCastRay`, the only caller of the physics scene's 4-argument `RaycastWorld`. Under Phlox, a
  script flood cannot affect rezzing or landing.
- `RayCastSimulatorBudgetMs` covers every cast on the 5-argument `RaycastWorld`: where the simulator
  places a rezzed object (`Scene.GetNewRezLocation`), where an avatar lands after login or teleport
  (`ScenePresence.MakeRootAgent`), and YEngine's `llCastRay` (`LSL_Api.llCastRay` through
  `Scene.RayCastFiltered`). The module cannot tell these apart: a script can ask for exactly the
  filter flags and hit count the simulator's own casts use. So under YEngine, script casts share
  this allowance with rezzing and landing, and a script flood there can still make a rez or a
  landing miss its surface, until the script engine marks its casts as a script's.
- The viewer camera's collision ray and the sit ray do not reach the module, which leaves those
  queries to the base physics scene.

The two budgets together bound how long ray casts can hold the region's physics lock in one
heartbeat: with the defaults, 10 ms plus the overrun of the casts in flight when each ran out.

The first ray cast in a simulator process pays one-time costs (compiling the cast code, binding the
native call, first use of the engine binding's filters): measured at 3-4 ms inside the module,
against a few microseconds for a later cast. Charged to `RayCastSimulatorBudgetMs`, that would let
the first landing casts after a start run the budget out. So when a region loads, the module makes
a few casts of its own through the same code, charged to neither budget and not counted in
`jolt capacity`, and logs `ray casts warmed up in N ms`: about 8 ms for the first region of a
process, a few hundredths of a millisecond for each later one.

What the script engines already limit before a cast reaches the module: both cap `RC_MAX_HITS`
at 16 (OpenSim's asks the physics engine for twice that), and Phlox refuses fewer than 1. Neither
limits the ray's length or how often a script casts.

`jolt capacity` shows the casts made, refused and cut short, and their time, for each budget and
for both together; the harness's `raycast-cost` scenario shows them for its casts. The metrics log's
interval line (see "Job pools") gives each interval's refused casts for each budget. The module
writes no log line for a refused cast.

**Pushes.** `llPushObject` on an avatar, and an attachment's `llApplyImpulse` on its wearer, change
the avatar's velocity by the impulse divided by its mass (80 kg). The script engine checks the
region's and parcel's push restrictions and weakens a push from more than 17 m away before it
reaches the module ("The push impact is diminished with distance", llPushObject). Second Life also
limits a push by the pushing object's script energy, which depends on its mass; the physics module
is not told which object pushed, so it bounds the avatar's side instead:

- Each push spends the speed it adds from the avatar's allowance, `AvatarPushMaxSpeed`, which
  refills at `AvatarPushRecovery` per second.
- Pushes never take the avatar's speed past `AvatarPushMaxSpeed`, or past the speed it already had
  (a falling avatar can be slowed by a push, not sped up).
- With the refill below gravity, repeated pushes cannot hold an avatar in the air: with the defaults
  it rises at most 10^2 / (2 (9.8 - 5)) = 10.4 m, however many pushes it gets.
- The angular part is ignored, as Second Life does for avatars. Push speed fades on the ground
  (0.25 s) and when flying (1 s), is kept in the air, and is taken away by what the avatar runs into.

A push on an object goes to the physics engine as an impulse and wakes the object if it sleeps;
a parked vehicle is not held still against it. It is limited by `BodyMaxLinearSpeed`.

### Job pools

All regions in a simulator share Jolt's worker threads through `JobPools` job pools (default 1).
`ThreadCount` is the total number of worker threads, split evenly across the pools, and each
region is given the pool with the fewest regions when it starts.

More than one pool needs the patched joltc (see "Platforms"). On the stock joltc the module runs
one pool, sized as `JobPools = 1` would size it, logs a warning at start naming the setting, and
`jolt capacity` shows the pools in use and why.

`ThreadCount = 0` (the default) is automatic: each pool gets its share of the processor count
less one, at most 4 threads and at least 1. On a 20-thread machine with one pool that is 4 threads;
with 2 pools, 4 each; on a 4-thread machine with one pool, 3. A positive `ThreadCount` is the total,
used as given. To get the behaviour from before the cap, every processor but one in the pools, set
`ThreadCount` to the processor count less one. The startup log and `jolt capacity` give the pools,
the threads per pool, and whether the count is automatic or set.

The automatic count is per process: each region server process sizes its own pools, at most 4
threads a pool, from the processor count. A host that runs many region server
processes should set a lower `ThreadCount` in each, since small scenes run best on 1 or 2 threads
(one moving body stepped in 0.5 ms on 1 or 2 threads, against 1.0 ms on 4; figures below).

Why at most 4: a region's physics step is spread over its pool's threads, and past a few threads
handing the work out and waking the threads costs more than they save. In the harness's pool
benchmark on a 20-thread desktop processor, one step per heartbeat: a pile of 1000 moving boxes
took 13 ms a heartbeat on 4 threads and 34 ms on 19; 300 boxes 8 ms against 19 ms; one moving body
1.0 ms against 2.1 ms (0.5 ms on 1 or 2 threads). With 300 boxes sharing the process with two idle
regions, 4 pools of 4 threads stepped the busy region in 10 ms against 25 ms for one pool of 19. A pool runs one region's physics
step at a time, so a region whose pool is busy waits for it. The wait is in the heartbeat: the
region's physics still advances by the frame time, and no step is skipped.

Choosing `JobPools`:

- Set it to about the number of regions that carry physics load at the same time (moving vehicles,
  piles of physical prims), not to the total number of regions. A region with nothing moving holds
  its pool only briefly.
- With `ThreadCount` set, give each pool about 4 threads, so `JobPools` about `ThreadCount / 4`: a
  pool's threads are all that one busy region's physics gets, and more than about 4 made it slower.
  With the automatic count each pool gets at most 4 already, so more pools use more of the cores.
- The log warns when a region spent more than 20% of its frame time waiting for its pool over a
  `CapacityLogIntervalSeconds` interval, naming the key to raise.

`JobPoolFairHandoff` (default false) sets how a pool passes from one region to the next. With
`PhysicsStepRate` on, a region runs several physics steps per heartbeat and takes the pool once per
step. With the default, a plain lock that does not queue its waiters, the holder usually takes the
pool straight back for its next step, so a waiting region can wait through the rest of the holder's
heartbeat. With `true` the pool is granted first come, first served: a region that starts waiting
during another's step runs as soon as that step ends. While regions wait, each step then costs a
thread handoff, and the busy region's heartbeat takes longer by the waiting regions' steps. Like
`JobPools`, the first region to start sets it for the process.

Watching the pools:

- `jolt capacity` shows the pools, the handoff, the region's waits for its pool since start (count,
  total and longest), its steps' waits for its own region lock (held by a body change or a query on
  another thread; the step holds its pool while it waits), and the last metrics interval.
- Every 30 s the log prints a `[JOLT METRICS]` summary line and, after it, a `[JOLT METRICS] last
  <seconds>s` line with each region's figures for that interval: heartbeats, the longest heartbeat's
  physics time and the longest single physics step (both include region lock waits and leave out
  pool waits), the pool waits (count, total, longest, and which region held the pool when the
  longest began, or, if the pool was changing hands at that moment, the region that had it just
  before this one; never the region itself), the region lock waits (count, total, longest), the longest gap between two
  heartbeats' physics calls against the frame time, and the ray casts refused in the interval in
  each budget (`ray casts refused script=<n> simulator=<n>`). `jolt metrics` shows both lines.
- The harness's `--pool-bench` (see "Physics harness") measures waits and step times for a heavy
  region sharing pools with light ones, for any `JobPools` and handoff.

## Script forces on objects

What a script does to a physical prim or linkset (avatars are not covered here):

- `llSetForce`, `llSetTorque`, `llSetForceAndTorque`: the force (N) and torque (N m) act in every
  physics step until the script sets them to `ZERO_VECTOR`, and wake a sleeping object. The script
  engine turns a local vector into region axes once, when the call is made, so a local force keeps
  the direction the object faced at that moment; it does not turn with the object.
- `llApplyImpulse`: the velocity changes by the impulse divided by the mass (`llGetMass`), at once.
- `llApplyRotationalImpulse`: the angular velocity changes by the impulse divided by the inertia
  about that axis, at once.
- `llSetBuoyancy`: the object feels (1 - buoyancy) of the region's gravity, as in ubODE: 0 falls
  normally, 1 floats where it is, 2 rises at 1 g. Changing it wakes a sleeping object.
- `llSetStatus` with `STATUS_ROTATE_X`, `_Y` or `_Z` set to `FALSE`: the object cannot turn about
  that axis of its own; a torque, impulse or collision turns it only about the axes left free.
  Locking stops its turning, as ubODE does. ubODE fixes a locked axis in the region where it pointed
  when it was locked; Jolt keeps it on the object. The two agree with all three axes locked and
  with only one free.
- `llGetMass` and `llGetObjectMass`: the volume times the density, in Second Life's lindograms
  (kg / 100): a 1 m cube at the default density weighs 10, a 0.5 m sphere 0.65, a linkset the sum
  of its prims. A non-physical object weighs what it would weigh physical: the volume is the
  shape's physical one (the convex hull for a cut, twisted or mesh prim). A resized prim gets the
  mass of its new size.
- `llGetCenterOfMass`: in region coordinates. A physical linkset reports the centre of all its
  prims, each weighted by its own mass; called from a child prim, that prim's own centre; a lone
  prim, its own centre. For a non-physical object the simulator takes the mass-weighted mean of its
  prims' centres; the Second Life wiki says it should return the value last computed while the
  object was physical, or the position (`llGetPos`) when there is none.
- A call that reaches a linked child prim acts on the whole linkset.
- A vehicle keeps its own forces: a set force, torque or buoyancy does nothing while the object is a
  vehicle (as in ubODE), and takes effect again when the vehicle type is removed. Impulses still act.
- A value that is not a number is refused and logged. A finite force, torque or buoyancy too large
  for any step to follow is cut to what takes the object to the engine's speed cap
  (`BodyMaxLinearSpeed`, `BodyMaxAngularSpeed`) in one step.
- Prims carry a linear and angular damping, by default 0.05 per second: a moving object loses
  about 5 percent of its speed each second on top of what forces do (see "Physics material on
  objects").

## Editing and moving physical objects

- Selected in the build tool, a physical object stops where it is and stays there: gravity, a set
  force, an impulse, a push, a set velocity and a vehicle's own motion do not move it, and an object
  that hits it or lands on it does not move it. Deselected, it carries on from rest. A selected
  linkset holds as one object, and a phantom or volume-detect object holds the same way. ubODE also
  lets other objects pass through a selected one; in Jolt a solid object stays solid, as in
  BulletSim.
- Moved or turned while it sleeps (by the build tool, `llSetPos`, `PRIM_POSITION`, or a rotation
  the script engine lets through for a physical object), a physical object wakes and carries on
  from where it was put: lifted, it falls back; turned, it settles. Not while the region is loading.
- Given a velocity while it sleeps (`llSetVelocity`, `llSetAngularVelocity`), a physical object wakes
  and moves at once, as in ubODE.

## Physical objects that leave the region

The simulator decides what happens to an object at the region's edge: `STATUS_DIE_AT_EDGE` deletes it
and `STATUS_RETURN_AT_EDGE` returns it ("if it goes off world", `llSetStatus`), and otherwise it is
handed to the neighbouring region if there is one. The physics module does what ubODE does:

- Past the edge, the object waits 0.1 to 2 m outside it, at the height it crossed and with the
  velocity it left with, while the simulator looks for a neighbour. Handed over, the neighbour gets
  that velocity.
- With no neighbour, the simulator puts it back half a metre inside the edge; it comes back 0.2 m
  higher, at rest, and falls or settles from there. A vehicle comes back with its motors off, so it
  does not drive out again until its script sets a motor.
- Below -100 m or above 100 000 m it is stopped there and the simulator is told it went out of
  bounds, which makes it non-physical (the simulator logs `went out of bounds`).
- A linkset leaves as one object. An avatar seated on it has no physics body of its own while seated;
  the simulator crosses it with the object.
- While it waits outside, and from going out of bounds until it is made non-physical, the object has
  no physics body: it costs no step time, touches nothing and sends no updates.

## Physical linksets

A physical linkset is one rigid body: the root prim's body carries every prim as part of one shape,
and no prim of it has a body of its own. These keep it one body, with the mass of all its prims:

- Linking a prim in, unlinking one, deleting one, or setting a prim's `PRIM_PHYSICS_SHAPE_TYPE` to
  `PRIM_PHYSICS_SHAPE_NONE` and back: the linkset's shape is rebuilt with the prims it now has, once,
  before the next physics step. An unlinked physical prim becomes a body of its own where it was.
- Resizing a prim, changing its shape, or moving or turning it within the linkset (the build tool's
  "Edit linked parts", `PRIM_SIZE`, `PRIM_TYPE`, `PRIM_POSITION` or `PRIM_ROTATION` on a child): the
  same rebuild, with the prim at its new size, shape or place. A move or turn smaller than 1 mm or
  1 milliradian rebuilds nothing.
- Changing a prim's density: the same rebuild, which moves the centre of mass (see "Physics material
  on objects").
- Turning the whole object non-physical: each prim gets a fixed body of its own where it is on the
  object now, wherever the linkset has moved since it was linked. Turning it physical again makes it
  one body.
- A ray cast reports the prim it hit, not the root: `llCastRay` then returns that prim's key and,
  with `RC_GET_LINK_NUM`, its link number, and the root's key only with `RC_GET_ROOT_KEY` ("The hit
  uuid will be replaced by the object's root instead of any child.", wiki, llCastRay). Both script
  engines look the prim up from what the physics engine reports.
- A linkset meets an avatar as one object with the mass of all its prims, by the rule for a single
  object (see "Avatars and physical objects"): thrown at an avatar it stops against it or carries it
  along by their masses, and an avatar walking into it pushes it along whole.
- Collision events: the prim of the linkset that was touched gets the event, so a script in it gets its
  own link number from `llDetectedLinkNumber`, and the simulator passes the event to the root's script
  by `llPassCollisions`. Whatever touches the linkset, a prim, another linkset or an avatar, names it by
  its root prim: `llDetectedKey` and `llDetectedName` are the object's, and a collision filter set by
  name matches the object's name. Second Life documents no rule for this; its `llCollisionFilter`
  example has a filter on an object named "Post" detect "A child prim named "Object"" of it (wiki,
  llCollisionFilter). ubODE names every collider by its root likewise. A non-physical linkset is not
  one body (the simulator links only a physical one), so each of its prims is named as itself, as in
  ubODE.
- A linkset asleep on something keeps touching it when one of its prims is resized: it stays one body
  at rest, and what it rests on gets no end event. A script in that surface gets one
  `collision_start` for the linkset, naming its root, rather than one for each prim that rests on it.
- The simulator does not pass a sculpt or mesh change made through the viewer's extra parameters to
  the physics engine (`SceneObjectPart.UpdateExtraParam`), on any engine; a prim keeps its old shape
  in physics until something else rebuilds it.

## Physics material on objects

`llSetPhysicsMaterial` (and `PRIM_PHYSICS_MATERIAL`) and `PRIM_MATERIAL` on a prim, as the Second
Life wiki documents them:

- Friction (0 to 255), restitution (0 to 1), the gravity multiplier (-1 to 28) and density (1 to
  22587 kg/m^3) each act on the object; a value outside its range is clamped to it, and one that is
  not a number is refused and logged. A change wakes a sleeping object.
- `PRIM_MATERIAL` sets the material's friction and restitution: stone 0.8 / 0.4, metal 0.3 / 0.4,
  glass 0.2 / 0.7, wood 0.6 / 0.5, flesh 0.9 / 0.3, plastic 0.4 / 0.7, rubber 0.9 / 0.9, light
  0.6 / 0.5. A new prim gets the friction and restitution the simulator keeps for it (wood by
  default). The simulator's own table gives rubber a restitution of 0.95 and light 0 / 0, so a
  rubber or light prim rezzed or loaded has those until a script sets its material again.
- Two touching surfaces combine as in ubODE (the wiki gives no rule): friction is the square root of
  the product of the two, restitution the product. A box sliding on a surface of the same friction
  `f` therefore feels `f`. A bounce needs a closing speed of at least 1 m/s (Jolt's
  `MinVelocityForRestitution`). The terrain has friction 0.6 and restitution 0, so nothing bounces
  off the ground.
- A dropped object rebounds to its restitution squared times the height it fell (less its damping),
  within 5 percent, for restitution 0.1 to 0.9, drops of 0.5 to 10 m and step rates of 11 to 90 Hz.
  Jolt bounces a body from wherever its step finds it, up to one step's travel above or into the
  surface, so on its own the height would be off by that much (a 1 m box dropped 2 m with restitution
  0.3 came back 18 percent low to 11 percent high at 45 Hz, by where the step fell). The module hands
  Jolt, for each impact, the restitution that sends the body as high as a bounce from the surface would.
  A rebound lower than Jolt's speculative contact distance (2 cm) can still come out anywhere from
  none to that distance, because the step may find the body already above the top of its bounce.
- The gravity multiplier scales the object's gravity: the object falls at (1 - buoyancy) times the
  multiplier times the region's gravity, as in ubODE. 0 floats, 2 falls at twice the rate.
- Density sets the mass: volume times density, in lindograms (kg / 100) for `llGetMass`.
- In a linkset each prim keeps its own friction, restitution and density ("Can individual prims in
  a linked set have different Physics settings? Yes.", wiki, Physics Material Settings test): a
  contact uses the struck prim's friction and restitution, and the mass is the sum of each prim's
  volume times its own density. The centre of mass the engine turns the linkset about, and its
  inertia, follow each prim's density too, so a heavy prim at one end pulls the centre of mass
  toward it, and `llGetCenterOfMass` reports that same point. The root prim's gravity multiplier
  applies to the whole linkset, as in ubODE.
- A vehicle sets its own contact friction (`VehicleContactFriction`), restitution (0) and damping
  (0) and ignores the gravity multiplier; the prim's values come back when the vehicle type is
  removed.

Damping is two `[Jolt]` keys:

| Key | Default | What it sets |
|---|---|---|
| `PrimLinearDamping`, `PrimAngularDamping` | 0.05, 0.05 | Linear and angular damping of a physical prim that is not a vehicle, per second (0 to 100): a moving object loses about that share of its speed and spin each second. The same at every step rate. ubODE's, as a rate, are 0.1001 and 0.0250 (dBodySetDamping .002 and .0005 per 0.020 s step). |

## Move to target and hover on objects

`llMoveToTarget` and `llSetHoverHeight` on a physical prim or linkset (avatars are not covered here).
The Second Life wiki says each "critically damps" to its target "in tau seconds". In Jolt each is a
critically damped spring with tau as its timescale: from rest, the distance left after t seconds is
d0 (1 + t / tau) e^(-t / tau), so it is about a quarter of the way in after tau, 91 percent of the
way after 4 tau, and it does not pass the target. The motion is the same at every
`PhysicsStepRate`, the engine's damping included.

- `llMoveToTarget(target, tau)`: the object goes to the target and holds there, until
  `llStopMoveToTarget`, which lets it fall from where it was held. A target 65 m or more away does
  nothing ("must be less than 65, or no movement will occur"). A tau of 0 or less moves nothing (the
  simulator also stops an earlier target then); a tau under 2/45 s acts as 2/45 s, the wiki's
  "smallest functional tau".
- `llSetHoverHeight(height, water, tau)`: the object holds its centre `height` above the ground, or
  with `water` TRUE above the ground or the water, whichever is higher. It follows the ground as it
  moves, so it keeps its height over a slope, and it pulls the object down if it is above the height.
  `llStopHover` or a height of 0 lets it go. Without volume detect it does not go under the ground;
  a negative height leaves it on the ground. Heights are cut to 4096 m, the wiki's limit. A tau of 0
  or less is refused.
- While either acts, gravity does not, as in ubODE, so buoyancy changes nothing. A set force still
  acts, and holds the object `F tau^2 / m` from its target, where the spring balances it. A set
  torque still turns it. With both asked for, move to target acts and hover waits.
- Each call wakes a sleeping object, and a call that reaches a linked child prim acts on the whole
  linkset. The object may sleep once it has settled where it is held.
- On a non-physical object neither acts, but the request is kept and acts once the object is
  physical ("A llMoveToTarget call seems to persist even if physics is turned off").
- A vehicle keeps its own hover and motion: neither acts while the object is a vehicle.
- Selected in the build tool, the object is held still and neither acts. The request is kept:
  deselected, the object goes on to its target from rest where it was held, or falls if the script
  stopped it meanwhile.
- `llGroundRepel` reaches the physics engine as the same request as `llSetHoverHeight`, so in Jolt
  it acts as `llSetHoverHeight` does: it also pulls an object down to the height, which Second Life's
  `llGroundRepel` does not.
- Under Phlox, `water` TRUE asks for a height above the water level alone, also over land that is
  higher than the water.

## Avatar speeds

For a held forward or back key the simulator asks every physics engine for the same speed, 4.096 m/s, walking
or running: the viewer sends running as a separate flag (its SetAlwaysRun message), and each
engine decides what running does. Flying, it asks for four times that, 16.384 m/s. Each engine turns
the request into a speed of its own: ubODE divides it by `av_movement_divisor_walk` (1.3) or `av_movement_divisor_run` (0.8);
BulletSim multiplies it by `AvatarWalkVelocityFactor` (1) or `AvatarAlwaysRunFactor` (1.3) and does not
scale a flight. Jolt multiplies it by one of four `[Jolt]` factors, whose defaults give Second Life's
documented speeds on level ground, and flying up and down ([Default Avatar Movement Speeds](https://wiki.secondlife.com/wiki/Default_Avatar_Movement_Speeds)).
The viewer sends the same fast flag for flying up and for flying down, so the simulator asks 16.384 m/s for
each, as for flying level:

| Key | Default | Applies to | Level-ground speed at the default |
|---|---|---|---|
| `AvatarWalkSpeedFactor` | 0.78125 | walking (horizontal part) | 3.20 m/s |
| `AvatarRunSpeedFactor` | 1.2524414 | always run on (horizontal part) | 5.13 m/s |
| `AvatarFlySpeedFactor` | 0.9765625 | flying (all three axes, so up and level match) | 16.00 m/s |
| `AvatarFlyDownSpeedFactor` | 1.395874 | the downward part of a flight, in place of `AvatarFlySpeedFactor` | 22.87 m/s straight down |

Each takes a number from 0.1 to 10; an invalid value logs a warning and the default is used. The
factor multiplies what the simulator asks, so a script's or region's speed change on the avatar
(`osSetSpeed`) still scales the speed. A velocity handed over at a region crossing or teleport is
kept as it is. Jump height is set by `AvatarJumpSpeed`, not by these. All four are listed, commented
out with their defaults, in the `[Jolt]` section of `OpenSimDefaults.ini`.

A sideways step on its own asks less: the simulator's full speed needs the fast flag of the forward key
or of the up key, and the viewer marks a sideways key with a fast flag of its own that the simulator does
not test, so a sideways step asks 0.6 x 4.096 = 2.4576 m/s (with always run on, 4.096). Jolt applies the
walk or run factor to that like any walk, giving 1.92 m/s walking and 5.13 running. The Second Life page
above lists no sideways speed.

What each engine gives for the same request on level ground (Jolt measured in the harness at
`PhysicsStepRate` 45 and at one step per heartbeat; ubODE's walk measured in a region, its run and
fly worked out from its code; BulletSim from its code):

| | Asked | Jolt before these keys | Jolt now | ubODE | BulletSim | Second Life |
|---|---|---|---|---|---|---|
| Walk | 4.096 | 4.096 | 3.200 | 3.15 (3.13 measured) | 4.10 | 3.20 |
| Run | 4.096, always run on | 4.096 | 5.130 | 5.12 | 5.32 | 5.13 |
| Fly, level | 16.384 | 16.384 | 16.000 | 12.60 (20.48 with always run on) | 16.38 | 16.00 |
| Fly, straight up | 16.384 | 16.384 | 16.000 | not scaled | not scaled | 16.00 |
| Fly, straight down | 16.384 | 16.384 | 22.870 | not scaled | not scaled | 22.87 |

## Arriving avatars

An avatar's physics body is made at login, at a teleport, when it arrives from another region or
crosses a border, and when it stands up from a seat or a vehicle. Jolt puts it at the position the
simulator gives, as BulletS and ubODE do: on a prim platform it stands on the platform, in the air it
falls (or stays, when flying) until it lands on what is below. Flying and velocity are kept as given.
The one correction: a position that would put the body below the terrain is lifted to stand on the
terrain. An arriving avatar is never moved down.

## What an avatar stands on

An avatar standing on the terrain reports ground (`CollidingGround`), and one standing on a prim,
including a prim lying on the terrain, reports an object (`CollidingObj`); in the air it reports
neither. The avatar's collisions reach the simulator each heartbeat as they do with BulletS and
ubODE: the terrain as land, so scripts in its attachments get `land_collision_start`,
`land_collision` and `land_collision_end`, and a prim or another avatar as an object, giving
`collision_start`, `collision` and `collision_end`. The floor contact also sets the collision plane
under the avatar's feet. Each contact carries the speed at which the two met (see "Avatars and physical
objects"), from which the simulator plays collision sounds and works out impact damage. Where damage is
on, a prim with a damage value set damages the avatar it touches and is removed, as the simulator does
with every physics engine.

## Avatars and physical objects

Second Life documents that "You can push physical object by walking or flying your avatar into them",
that an object dragged into an avatar should move it, and that "Residents take damage from collisions
with physical objects" (wiki.secondlife.com, Push and Damage). It gives no figures, so Jolt follows
ubODE, where an avatar is a body that meets an object with no bounce:

- An object that strikes an avatar never goes into or through it. The two go on along the line of the
  hit at their common speed, weighted by mass, with the avatar's mass of 80 kg: a 1 kg box thrown at
  5 m/s stops against it and barely moves it, and a 100 kg box carries it along at up to 2.8 m/s. The
  avatar's share is a push, held to `AvatarPushMaxSpeed` and the push allowance like any other; what
  it cannot take the object loses. An avatar standing on the ground is not pushed down into it: an
  object that lands on its head stops there and comes to rest on it, and falls once they part.
- An avatar walking or running into an object pushes it along, level, with at most its push force
  (`PushStrength` x 100 N) and never faster than the avatar moves that way. A light box goes along ahead
  of it at its pace; a heavy one, held by its friction, barely moves and stops the avatar. The avatar
  does not step up onto an object it is pushing.
- What an avatar stands on carries it as before: it can stand on a physical object without sinking or
  sliding it away.
- Two avatars still push each other as before, as in ubODE.
- Every contact report carries the speed at which the two sides met along the contact normal
  (`ContactPoint.RelativeSpeed`, below zero while they close), for prims, avatars and the land. The
  simulator plays a prim's collision sound above 0.2 m/s, and an avatar takes impact damage where
  damage is on below -5 m/s, from the land (a fall from about 1.3 m or more) or from a prim.
- An avatar names an object it touches by the object's root prim, as a prim does (see "Physical
  linksets") and as ubODE names every collider: an attachment's `llDetectedKey` and `llDetectedName` in
  its collision events are the object's, a collision filter set by name matches the object's name, and
  an avatar touching two prims of one linkset has one collision with it, carrying the harder strike. The
  prim it touched names the avatar, so a script in that prim gets its own link number from
  `llDetectedLinkNumber`.
- Two avatars that meet are both told, each naming the other, as in ubODE. Jolt finds the contact in
  the update of the avatar that meets the other; before, at one step per heartbeat, an avatar walking
  into another could push it along and never get a collision event for it.

A flying avatar meets objects by the same rule. Second Life documents nothing on a flying avatar that
is hit; Jolt follows ubODE, where it stays flying:

- An object thrown at a flying avatar moves it along the line of the hit at their common speed by
  mass, within the push limits; it keeps its height and stays flying, and the push fades over about a
  second, as any push on a flying avatar does. A 100 kg box thrown at 5 m/s carries a flying avatar
  along at up to 2.8 m/s, about 3 m in all; a 1 kg box moves it about 6 cm at 5 m/s and up to 25 cm
  at 20 m/s.
- A flying avatar flying into a fixed prim or a heavy object stops at it. Flying into a light object
  pushes it along ahead at about the avatar's own speed. A flying avatar does not press what it
  touches with its weight: an object it brushes or passes over is not driven into the ground.

The simulator takes a seated avatar out of physics (`ScenePresence.RemoveFromPhysicalScene`, on every
engine), and the physics engine is not told who sits where. So on Jolt:

- A seated avatar adds no mass or shape to what it sits on, as on ubODE: a physical seat or vehicle
  keeps its own mass, and objects pass through where the avatar sits. Second Life documents "Sitting avatars add
  their mass to the object" (wiki, llGetObjectMass) and a seated avatar's collision volume
  (`SIT_FLAG_NO_COLLIDE`, wiki, llSetLinkSitFlags); neither can be done in the physics engine alone.
- The seat's prims get a `collision_end` for the avatar when it sits, and the seated avatar's
  attachments get no collision events until it stands.
- Standing up, the simulator puts the avatar back where `ScenePresence.StandUp` places it, without
  looking at what is there. Next to an object or on top of one, it stands where it is put. Inside a
  fixed object it is set out at the nearest side, at once, on the ground beside it, and stays there:
  it is not thrown out (ubODE's contacts correct an overlap at up to 60 m/s). Being set out is not
  moving: it reports no speed for that step (before, 13.7 m/s for 1.2 m at one step per 11 Hz
  heartbeat). Inside a light physical object both give way: the avatar is set out and the object
  nudged aside (0.4 m for a 1 kg cube).

## Buoyancy and hover on an avatar

A script in an attachment reaches its wearer: core hands `llSetBuoyancy` to the avatar's physics body
as its buoyancy, and `llSetHoverHeight` and `llStopHover` as its hover (SceneObjectGroup.SetBuoyancy,
SetHoverHeight). Jolt acts on both, following ubODE where Second Life documents nothing:

- Buoyancy scales the gravity on the avatar by (1 - buoyancy), as on a prim (`llSetBuoyancy`: "when
  buoyancy equals 1.0 it floats", "when buoyancy is > 1.0 the object rises"). At 1 an avatar that walks
  off a ledge goes on level in the air; at 0.5 it falls at half gravity; above 1 it rises off the ground.
  As in ubODE it does nothing while the avatar flies or hovers. A floating avatar (buoyancy 1 or more) is
  not drawn down onto the floor as it walks off an edge.
- Hover holds the avatar's position at the height above the ground, the higher of ground and water, the
  water, or the region's zero, as the call asks (core's hover type), with the critically damped spring a
  prim's hover uses and tau as its timescale ("Critically damps to a height above the ground (or water)
  in tau seconds", `llSetHoverHeight`). The height follows the ground along the avatar's path, so it
  holds over a slope while the avatar walks. Gravity is off and a jump does nothing while it hovers, as
  in ubODE; walking and flying across still work. It never holds the avatar lower than standing on the
  ground. A height of 0 or `llStopHover` ends it and the avatar falls. Hover moves the avatar up or down
  at no more than 50 m/s (ubODE's limit).
- Neither is kept when the avatar's physics body is made again (a teleport, a region crossing, standing
  up); core does not hand them over again, with any engine.

Script engines differ (no change made to either): YEngine reaches the wearer for both calls. Phlox
returns from `llSetHoverHeight` and `llStopHover` when the script's own prim has no physics body, which
an attachment never has, so under Phlox they do nothing on an avatar; its `llSetBuoyancy` reaches the
wearer. `llGroundRepel` from an attachment does nothing on either engine.

## The velocity an avatar reports

An avatar reports the velocity it really moved at in its last physics step, with the gravity it gained
after the move: what a wall or the ground stopped is not in it. Walking into a wall it reports about 0;
walking freely, its walk speed; standing on a moving object, the object's speed; pushed, the speed the
push gives it. ubODE reports its avatar body's velocity, which a wall stops likewise. Before, Jolt
reported the velocity the avatar was asked to move with, so an avatar held at a wall reported its full
walk speed. Core sends this velocity to viewers to move the avatar between updates, returns it to
scripts (`llGetVel` in an attachment, `llDetectedVel`, sensors' ACTIVE flag), carries it into a teleport
or a region crossing, and the animator reads its vertical part to choose falling.

Jolt's character also moves an avatar out of anything it overlaps, all at once in one step. That is
not speed, and is not reported as such:
- In the step after the simulator puts the avatar somewhere (it arrives, stands up, or its position is
  set), an avatar moved further than its own velocity takes it was set out of what it was put inside,
  and reports no speed for that step.
- An object coming at the avatar that may push it carries it at the object's speed; what the avatar is
  moved beyond that in the same step was the object's overlap, and is not reported. A heavy linkset
  catching up a flying avatar as its push fades went a little into it before it could push it, and
  the avatar reported 14.05 m/s for one step at PhysicsStepRate 45 while carried at 8.7; it now reports
  the 8.7.
Where the avatar moves is unchanged by this, and so is everything an avatar hit by nothing reports.

## Fast objects

A physical prim that moves fast is checked along its path, so it does not pass through a thin wall,
another object or the ground between two steps. In each of Jolt's collision steps (`[Jolt]
CollisionSteps` per physics step), a physical prim that would move further than 0.75 times its shape's
inner radius (the radius of the largest sphere that fits inside it; Jolt's `mLinearCastThreshold`) is
cast along its motion, Jolt's `LinearCast` motion quality, and stops at the first thing it would hit.
At one physics step per 11 Hz heartbeat (six collision steps) that is a 0.2 m ball from about 5 m/s
and a 1 m box from about 25 m/s; the faster the steps, the higher the speed. A prim slower than that
is not cast and moves exactly as it did before. The test is made with the prim's velocity at the
start of each physics step, so a prim knocked to a high speed inside a step is cast from the next one.

A cast prim that hits something gets one contact from it, with the same combined friction and
restitution as any other contact (friction the square root of the two prims' product, restitution
their product), so it bounces or slides as a slower prim would. The cast follows the prim's motion in
a straight line: it does not cover what the prim's turning sweeps through in a step.

A vehicle is cast in every collision step in which it is fast enough, whatever its speed was at the
start of the step, as before.

Jolt's cast does not see avatars: an avatar meets objects in its own update, once per physics step,
before the simulation's update. So before each update, a physical prim that would go on more than an
avatar's radius past where it first touches the avatar in that update, with nothing nearer in its way,
is moved to that touch and strikes the avatar there, by the rule in "Avatars and physical objects":
both go on at their common speed by mass, the avatar's share held to its push limits, and both are told
of the contact with the speed at which they met. A 0.2 m ball shot at 50 m/s at a standing avatar
stops against it and barely moves it. A slower prim reaches the avatar in its own update, as before.
A physical linkset is checked the same way, as one object: a three-prim linkset of 100 kg thrown at
20 m/s at a standing, walking or flying avatar stops against it or carries it along within the push
limit, at either physics step rate. A flying avatar it has struck can show one physics step's velocity
above the limit (14 m/s at a 45 Hz step rate) when the linkset catches it up again as its push fades.

Phantom and volume-detect prims still let a fast prim through: Jolt does not cast against sensors,
and a phantom prim touches only the terrain. A volume-detect prim finds what is inside it at the
start of each collision step, so a fast prim that is never inside it at such a start, which can
happen when it crosses it in less than a collision step, raises no `collision_start` from it.

`FastObjectTests` shoots 0.05, 0.2 and 1 m balls at 10 to 500 m/s (`[Jolt] BodyMaxLinearSpeed`, 500 m/s
by default, is the most a prim can move) at a fixed wall 0.01 m and 0.1 m thick, at a resting 0.5 m
box and down at the ground, at each heartbeat rate of the harness and at 11 Hz with
`PhysicsStepRate` 45, and none passes through. `FastObjectsLinksetsAndAvatarsTests` covers where fast
prims, linksets, resting contacts and avatars meet: a fast ball at an avatar, a fast ball's bounce, a
fast ball on a sleeping box, a linkset thrown at or walked into by an avatar, a sleeping linkset with a
resized prim, and a ray at a sleeping linkset. `FlyingSeatedAndFastLinksetAvatarTests` covers a flying
avatar hit by a box and flying into a wall, a light post and a heavy one, a seated avatar, an avatar
standing up next to, on and inside an object, and a fast linkset at a standing, walking and flying
avatar.

## Phantom and volume-detect prims

Second Life documents both (wiki.secondlife.com): a phantom object lets "objects and avatars ... pass
through it", and a physical one collides "with the ground but will not pass through" and queues land
collision events; with llVolumeDetect "physical object and avatars can pass through the object", which
raises `collision_start` and `collision_end` "when interpenetrating". Jolt does this as follows:

- A volume-detect prim is a sensor. Avatars and physical objects pass through it without slowing, and
  it reports what is inside it; the simulator turns that into one `collision_start` when each enters and
  one `collision_end` when it leaves. What passes through is not told, so an avatar's attachments get
  no event from it. A non-physical one detects avatars and physical objects; a physical one falls
  through the ground.
- A physical phantom prim collides with the terrain and nothing else: it rests on the ground, raises
  land collision events, and passes through prims and avatars. A non-physical phantom prim is kept out
  of physics by the simulator, with every engine.
- Switching phantom or volume detect on or off on a prim, or on a linkset, changes the prim's body in
  place: it keeps its position and its physics body, and a physical one that was resting on something
  it can now pass through falls.
- A ray cast finds phantom and volume-detect prims only when it asks for them: YEngine's `llCastRay`
  with `RC_DETECT_PHANTOM`. Phlox's `llCastRay` does not pass that option to the physics engine, so its
  casts do not find them.
- `PRIM_PHYSICS_SHAPE_CONVEX` on a non-physical prim that is not a mesh makes it collide as its convex
  hull. A non-physical mesh prim still collides as its triangle mesh whatever its shape type, because
  Jolt does not read a mesh's own hull list yet.

The harness scenarios beginning `vd-` and `phantom-` show each of these, and print the collision events
each part raised.

## Resting contacts and collision events

Second Life documents `collision_end` as "Triggered when task stops colliding with another task" and
`land_collision_end` when it "stops colliding with land", and that "A collision with a physical object or
avatar resting on object does not continuously trigger collisions but for a few times, unless there is
movement" (wiki.secondlife.com, the collision events). Jolt does this as follows:

- An object that comes to rest on another object, on the ground or inside a volume-detect prim and falls
  asleep keeps touching it: no end event comes when it falls asleep, and one comes when it is moved off.
  Jolt looks for contacts only where a body is awake, so the module keeps the contacts a body had when
  neither side of them is awake, as ubODE does with its sleeping prims.
- `collision` comes each heartbeat while the object settles and stops once it is asleep.
  `land_collision` goes on while it sleeps, as in ubODE; the wiki says nothing about it at rest.
- Only prims whose scripts have a collision event are tracked, so the rest cost nothing extra.
- `VelocityIterations` and `PositionIterations` in `[Jolt]` set the engine's velocity and position
  steps (defaults 10 and 2, Jolt's own). A tall stack needs more velocity steps to come to rest: ten
  stacked 0.5 m boxes do not fall asleep at 10 at 11 or 45 Hz, and fall over at 22.5 Hz; at 40 they
  stand and sleep at 11 to 90 Hz.
- A sleeping object moved by setting its position wakes (see "Editing and moving physical objects"), and
  an object asleep on a fixed prim wakes when that prim is moved or turned, so it falls instead of
  hanging in the air. Either way the contact ends once they part: one end event on each side.
- A selected object keeps its contacts while it is held, and gets no end event for them.

## Vehicles

LSL vehicles run on a controller that steps each behaviour Second Life documents on that documented
model. The sled's slope assist (`VehicleSledAssist` below) is not a documented Second Life behaviour;
it is carried over from the InWorldz Halcyon sled code. The controller's frame structure and parameter handling come from the InWorldz Halcyon vehicle code, whose type
defaults remain available as `VehiclePresets = legacy`. Every behaviour is stepped as the exact solution over the step
the engine takes, so a vehicle drives the same at one step per heartbeat and at any
`PhysicsStepRate`. The sections below give the model, the `[Jolt]` keys that govern vehicles, the
known gaps against Second Life, and how to try a vehicle in the harness before a region.

### Ground vehicles and gravity

`[Jolt] VehicleGroundGravityFactor` (0 to 1, default 1) is the share of gravity a car or sled
(`VEHICLE_TYPE_CAR`, `VEHICLE_TYPE_SLED`) gets while it touches the terrain or another object.
"Touching" is the physics engine's contact in the last step. The default 1 leaves gravity whole,
as it was before the key existed. BulletSim's vehicle code uses 0.2 (`[BulletSim]
VehicleGroundGravityFudge`).

```ini
[Jolt]
    VehicleGroundGravityFactor = 0.2
```

A car that only its linear friction slows rolls down a slope at a steady speed in proportion to the
factor: `f * g * sin(angle) * Tf`. In the harness the test car (linear friction timescale 1 s)
rolls 0.86 m/s down 5 degrees with 1, which is the formula's value, and about a fifth of that
with 0.2.

### Vehicle type presets

`llSetVehicleType` gives each type its defaults. `[Jolt] VehiclePresets` chooses which:

- `documented` (the default): Second Life's documented values, from the wiki page of each type
  (`VEHICLE_TYPE_SLED`, `_CAR`, `_BOAT`, `_AIRPLANE`, `_BALLOON`). The sled page's hover efficiency
  of 10 is held to 1, as `llSetVehicleFloatParam` holds any efficiency, and the sled has hover off:
  the page's hover timescale of 10 s would turn hover on, against the page's own "no hover", so the
  sled's is 1000 s.
- `legacy`: the InWorldz Halcyon values the module used before, for vehicles tuned against them.

```ini
[Jolt]
    VehiclePresets = legacy
```

A script's own `llSetVehicleFloatParam`, `llSetVehicleVectorParam` and `llSetVehicleFlags` calls
override either set; most scripted vehicles set their own values after the type, and differ only
in what they leave at the preset. Choosing a type sets that type's flags and clears every other
flag, also one a script set before `llSetVehicleType`. The module's own extension parameters (wind, mouselook,
motor disabling) are not Second Life parameters and are the same in both sets. The legacy car,
boat and airplane also carry the module's torque-about-world-z flag, which no documented type sets.

### Vehicle limits

The limits a script's vehicle parameters are held to are `[Jolt]` keys, read once when the region
starts, so a vehicle maker can tune them. Each default is the value the module always had, except
the motor decay cap, which is Second Life's documented 120 s.

| Key | Default | What it limits |
|---|---|---|
| `VehicleMaxLinearSpeed` | 200 | Linear motor speed a script can set (m/s, each axis) and the motor's result. Second Life documents about 30. |
| `VehicleReferenceSpeed` | 30 | The forward speed (m/s) at which dynamic banking and angular deflection reach full strength. |
| `VehicleMaxAngularSpeed` | 12.566371 (4 pi) | Angular motor speed a script can set (rad/s, each axis) and the angular result. |
| `VehicleMinTimescale` | 0.0156 | Shortest timescale a script can set (s), 0.001 to 1. |
| `VehicleMaxTimescale` | 1000 | Longest friction, motor or deflection timescale (s), at most 1000. 1000 s or more is "off" for friction and deflection. |
| `VehicleMaxDecayTimescale` | 120 | Longest motor decay timescale (s). It was 1000 before this key. |
| `VehicleMaxHoverTimescale` | 300 | Hover is off at or above this hover timescale (s). |
| `VehicleMaxAttractTimescale` | 500 | The vertical attractor and banking are off at or above this timescale (s). |
| `VehicleMaxMotorOffset` | 100 | Linear motor offset (m, each axis). |
| `VehicleMinHoverHeight`, `VehicleMaxHoverHeight` | -128, 10000 | Hover height range (m). Second Life documents a maximum of 100. |
| `VehicleSledAssist` | 0.045 | The sled's slope assist: a sled with its nose down is pushed along it at this share of g times the square root of the sine of its pitch (a tenth of it, back down the slope, nose up). 0.045 is what the InWorldz sled code gave at its 15 ms step; it is now the same at any step rate. 0 turns it off. |
| `VehicleContactFriction` | 0 | The contact friction of a vehicle body (BulletSim's `VehicleFriction`, also 0 by default): 0 leaves the vehicle's friction timescales as the only friction on it. |
| `VehicleRestSpeed` | 0.1 | The rest rule (m/s), see "Parked vehicles sleep" below. 0 turns it off. |
| `BodyMaxLinearSpeed`, `BodyMaxAngularSpeed` | 500, 47.12389 | The physics engine's own cap on every moving body, vehicle or not (Jolt's defaults). Keep it above `VehicleMaxLinearSpeed`. |

### Vehicle motors and friction

The linear motor and linear friction act on each axis of the vehicle's frame as one equation, as the
Second Life wiki's vehicle tutorial describes them:

    dv/dt = g * (M - v) / Tm  -  v / Tf  +  a          g = e^(-s / Td)

`M` is `VEHICLE_LINEAR_MOTOR_DIRECTION`, `Tm` the motor timescale, `Td` the motor decay timescale,
`s` the time since the script last set the motor, `Tf` the friction timescale, and `a` the vehicle's
gravity along the axis (its share: 1 - buoyancy, times the ground factor above). The angular motor
and angular friction follow the same equation on the angular velocity, with no gravity term. What
follows from it:

- Each step is the exact solution of the equation over that step, so a vehicle drives the same
  at any heartbeat or physics step rate.
- The velocity approaches the motor's exponentially from whatever it starts at; a few mm/s at the
  start (a rezzed car settling) make no visible difference.
- Friction acts on every axis all the time, the motor's axis included and the vertical axis
  included. With the motor at full grip a vehicle settles at `M * Tf / (Tf + Tm)`: the test car
  (motor 8 m/s, timescale 1 s, friction 1 s) at about 4 m/s, less the motor's decay between
  key events.
- The motor's grip fades exponentially from each set and is never cut off. A motor set to zero
  brakes toward zero; a motor left to decay stops acting.
- A friction timescale of 1000 s (the largest a script can set) is no friction on that axis.
- `VEHICLE_FLAG_LIMIT_MOTOR_UP` keeps the motor from pushing up; friction's own upward share
  (slowing a fall) stays. A car running down a slope faster than its motor is braked by its motor,
  and that push back up the slope loses its upward part.
- Gravity is inside the equation, so on a slope a vehicle settles where motor, friction and gravity
  balance, `(M / Tm + a) / (1 / Tm + 1 / Tf)` at full grip, at any rate.

### Hover, the vertical attractor, banking and deflection

Each follows the Second Life wiki's vehicle tutorial and `llSetVehicleFloatParam`, and each is the
exact solution over the step the engine takes:

- Hover is a damped spring on the height error `e`: `e'' = -e / T^2 - 2 eff e' / T`, with
  `VEHICLE_HOVER_TIMESCALE` `T` and `VEHICLE_HOVER_EFFICIENCY` `eff` from bouncy (0) to critically
  damped (1). The hover flags choose terrain, water or the global height as before. With
  `VEHICLE_FLAG_HOVER_UP_ONLY` hover never pushes down, and buoyancy vanishes above the hover height.
  A vehicle without full buoyancy hovers below its height, where the spring holds its weight.
- The vertical attractor is the same spring on the vehicle's roll and pitch, with
  `VEHICLE_VERTICAL_ATTRACTION_TIMESCALE` and `_EFFICIENCY` (from wobbling, 0, to exponential decay,
  1); off at 500 s. `VEHICLE_FLAG_LIMIT_ROLL_ONLY` unlocks pitch for every type: the attractor then
  corrects roll only, so the vehicle can climb and dive.
- `VEHICLE_LINEAR_MOTOR_OFFSET` moves the point the linear motor pushes at away from the centre of
  mass, so the motor also turns the vehicle: each step's motor change `dv` is an impulse `m dv` at
  the offset `r`, turning the body by `I^-1 (r x m dv)`. A thrust below the centre of mass pitches
  the nose up, as a rocket's would.
- Banking turns the yaw rate about world z toward a target in proportion to the roll and
  `VEHICLE_BANKING_EFFICIENCY` (and, with `VEHICLE_BANKING_MIX` toward 1, the forward speed over
  30 m/s), with `VEHICLE_BANKING_TIMESCALE` as its time constant. It needs the attractor on.
- Linear deflection turns the velocity toward the vehicle's nose, keeping its speed, the angle between
  them decaying as `e^(-eff t / T)`; with `VEHICLE_FLAG_NO_DEFLECTION_UP` only the horizontal part
  turns. Angular deflection turns the nose toward the velocity the same way, scaled by the speed over
  30 m/s.

### Vehicles handed back from their saved settings

The simulator keeps each vehicle's type, flags, parameters and reference frame (`SOPVehicle`): in the
object's XML in inventory, in a region crossing and in an attachment, and in the region's database. It
hands them back to the engine through `PhysicsActor.SetVehicle` when a vehicle is rezzed from inventory,
loaded with the region, copied, arrives from another region, is dropped as an attachment, or is a phantom
prim whose physics is switched back on (`SceneObjectPart.AddToPhysics`). The module overrides
`SetVehicle`, as ubODE does, and the vehicle gets exactly what was saved:

- the type, and the saved flags in place of the type's;
- every saved parameter, held to the region's vehicle limits as the same value from a script is (a saved
  hover timescale of 1000 s becomes 300 s, `VehicleMaxHoverTimescale`; both turn hover off);
- the reference frame;
- the saved motor directions, with neither motor running until a script sets it, as on ubODE. A car
  saved while it was being driven does not drive off when it is rezzed.

Its velocity is the one the simulator replays onto it, so a vehicle arriving from another region at speed
keeps it. A vehicle loaded with the region has its velocity zeroed on each step of its first 0.27 s, until
a script sets its linear motor, as one whose script sets the type has.

Before this override, the base `SetVehicle` set every vehicle flag on (its first call,
`VehicleFlags(-1, false)`, sets them all, as on ubODE) and started both motors toward the saved
directions.

Physics switched off and on by a script or the build tool keeps a solid prim's actor, and its vehicle
keeps every setting, as on ubODE. A phantom prim's actor is removed when its physics goes off and built
again from the saved settings when it comes back on.

The flag calls act as on ubODE: `llSetVehicleFlags` sets the flags it names, `llRemoveVehicleFlags`
removes them, and removing `-1` removes every flag.

The simulator saves its own type presets, which differ from the documented ones in a few fields. A
vehicle that is handed back has the simulator's value in these fields, where the same vehicle just set up
by its script has the documented one; a script that sets these itself is not affected:

| type | field | simulator's preset | documented preset |
|---|---|---|---|
| sled | hover timescale | 10 s (hover on) | 1000 s (off) |
| sled | angular deflection timescale | 1000 s | 10 s (its efficiency is 0 in both) |
| sled | vertical attraction efficiency | 0 | 1 (its timescale is 1000 s, off, in both) |
| boat | `VEHICLE_FLAG_HOVER_UP_ONLY` | off | on |
| balloon | vertical attraction efficiency | 0 | 1 (its timescale is 1000 s, off, in both) |
| balloon | `VEHICLE_FLAG_LIMIT_ROLL_ONLY`, `VEHICLE_FLAG_HOVER_GLOBAL_HEIGHT` | on | off |

The car and the airplane presets agree in every field.

### Known gaps against Second Life

These documented behaviours are not simulated:

- `VEHICLE_FLAG_MOUSELOOK_STEER` and `VEHICLE_FLAG_MOUSELOOK_BANK` (steering or banking toward the
  viewer's mouselook camera): the flags are stored and do nothing.
- `VEHICLE_FLAG_BLOCK_INTERFERENCE` (passengers' attachments cannot push the vehicle): stored, no effect.
- `VEHICLE_FLAG_LIMIT_MOTOR_UP`'s effect on banking ("the strength of the banking will decay when the
  vehicle no longer experiences collisions"): the rate of that decay is not documented, so banking
  keeps its strength in the air.
- Wind (`VEHICLE_LINEAR_WIND_EFFICIENCY` and the angular one): no wind is simulated.

### Parked vehicles sleep

A vehicle's body may sleep, as any other body does, while nothing in the vehicle would move it: no
motor pulling (never set, faded, or set to zero), hover off or at its height, and the attractor done
or the vehicle resting on something. A script setting a motor or any vehicle parameter wakes it, as
does a collision.

A vehicle body has no contact friction by default (`VehicleContactFriction` 0, as BulletSim), and
Second Life's vehicle friction is a velocity decay with no static part, so without more a car that
comes to rest a fraction of a degree tilted (inside the engine's 2 cm contact allowance) slides along
the tilt at a few centimetres a second. The rest rule holds such a vehicle still: when no motor is
pulling (and hover and the attractor are done), the vehicle moves slower than `VehicleRestSpeed`
(0.1 m/s; turning slower than 0.1 rad/s), and the steady speed the motor-and-friction equation above
gives on each of its axes from its present pose is also slower than that, its velocity is set to zero
each step, and after half a second (the engine's own time before sleep) it is put to sleep. Gravity
along the axis nearest the vertical is taken by what the vehicle rests on. The steady speed is
(g M / Tm + a) / (g / Tm + 1 / Tf): with no motor grip, a Tf. So a sled let go on a slope (forward
friction 30 s) starts, and a car whose forward friction lets it roll down a slope rolls, at the same
speed as without the rule; a car let go with its motor set to zero is braked by the motor's grip and
held on a tilt where the equation gives it under 0.1 m/s. In the harness a car parked after a drive
sleeps 3.6 to 5.1 s after its key at 11, 22.5, 45 and 90 Hz and does not move after coming to rest.
A documented car (forward friction 100 s) with no motor grip is held only within about 0.006 degrees
of level along its nose: on any real slope it rolls, as the equation says it should.

### Tuning a vehicle in the harness

The harness (below) runs the module's own vehicle controller on the real engine with no region, so
a vehicle's parameters can be tried in seconds:

```
dotnet Tests/JoltPhysicsHarness/bin/Release/net10.0/JoltPhysicsHarness.dll --scenario testcar --rate all --vparam LINEAR_FRICTION_TIMESCALE=1,2,1000 --vparam LINEAR_MOTOR_TIMESCALE=0.5
```

- `--vparam` and `--vflag` apply a script's own parameters and flags after the scenario's, by their
  LSL names, so a script's values can be pasted in.
- `--jolt` sets any `[Jolt]` key for the run, e.g. `--jolt VehiclePresets=legacy` to compare the
  preset sets, or `--jolt VehicleMaxLinearSpeed=400` to try a limit.
- `--rate all` runs 11, 22.5, 45 and 90 Hz heartbeats: a figure that differs between them points to
  something that still depends on the step.
- Each run's summary line gives the speeds, distances, time to rest, peak height, tilt and the time
  the engine let the body sleep; `--out` also writes a CSV per run with one line per heartbeat.

## Console commands

`jolt` acts on the region selected with `change region`; at the root prompt every region answers in
turn, each under a `--- region '<name>' ---` line.

These read and report only, and are always available:

| Command | What it shows |
|---|---|
| `jolt capacity` | Bodies, characters, the pair and contact caps, solver errors, dropped contacts, job pools, the region's waits for its pool and its own lock, the last metrics interval, and buffers |
| `jolt metrics` | Process memory and threads, each region's step time and active bodies, and the last metrics interval's timing (see "Job pools") |
| `jolt terraintest` | Raycasts at points across the region, to check the terrain collision surface |
| `jolt probe <x> <y>` | The terrain collision height at one point |
| `jolt heights <x> <y>` | The collision height, the scene heightmap and the water height at one point |
| `jolt avatarstatus` | Each avatar's physics position against the terrain under it |
| `jolt charframe [secs]` | Turns on a per-frame avatar trace in the log (Debug level) for a while |
| `jolt sitstatus` | Each avatar's sit state against its physics presence |
| `jolt sensortest` | A physics overlap query around the first avatar |
| `jolt raytest` | A physics ray down through the first avatar |
| `jolt reloadcheck` | Physical prims that moved since they were loaded |
| `jolt vehiclestatus` | The vehicle state of every prim that has one |

### Test commands

`[Jolt] TestCommands = true` adds commands that rez, move or delete objects and avatars, or
reshape the terrain, to exercise the engine: `jolt linktest`, `unlinktest`, `collidetest`,
`collidelinktest`, `boattest`, `cartest`, `sledtest`, `planetest`, `balloontest`,
`terrainslope`, `terrainhill`, `hilltest`, `rezprims`, `rayprims`, `rezmesh`, `raymesh`,
`rezmeshn`, `droptest`, `dropmesh`, `dropstatus`, `sittest`, `unsit`, `sittarget`, `clearprims`,
and `jolt parity`, which runs drop and boat scenarios through the standard physics surface and
writes their figures to `parity-Jolt.txt` (and `parity-boat-Jolt.txt`), to set beside another
engine's figures for the same scenarios. Like every `jolt` command it exists only in a region on
Jolt. `help Physics` lists each one with a line of help.

They are meant for test regions. `TestCommands` is off by default, and then none of them exists.
`[Startup] JoltAutoDropTest = true`, which drops three boxes in every region at load, is honoured
only when `TestCommands` is on.

## Physics harness

`Tests/JoltPhysicsHarness` is a console tool that runs physics scenarios through the module's own
per-heartbeat step (`Simulate`: the vehicle controllers, the backend step with the avatar step
inside it, and the drains) with no region and no viewer. It needs only a built tree and the native
joltc, so it runs anywhere the Jolt tests run (Windows x64 and Linux x64). Use it to see how a vehicle or
an avatar behaves at a given heartbeat rate, or what a changed parameter does, before trying it in
a region.

Build it, then run it from the repository root:

```
dotnet build Tests/JoltPhysicsHarness -c Release
dotnet Tests/JoltPhysicsHarness/bin/Release/net10.0/JoltPhysicsHarness.dll --scenario testcar --rate 11,45 --slope 15 --out harness-out
```

| Argument | Meaning |
|---|---|
| `--list` | List the scenarios and the slopes each one uses |
| `--scenario NAME[,NAME..]` or `all` | Scenarios to run (default: all) |
| `--rate HZ[,HZ..]` or `all` | Heartbeat rates; `all` is 11, 22.5, 45 and 90 (default: 11) |
| `--physics-rate HZ[,HZ..]` | `[Jolt] PhysicsStepRate`: physics steps per second inside each heartbeat; 0 is one step per heartbeat and is set as `PhysicsStepRate = 0`, not left to the module's default of 45 (default: 0, or the `JOLT_HARNESS_PHYSICS_RATE` environment variable) |
| `--slope DEG[,DEG..]` | Ramp angles for the scenarios that use one (default: each scenario's own list) |
| `--duration S`, `--hold S` | Seconds simulated, and seconds the drive key is held |
| `--keyrepeat S` | How often a held key re-sends the motor, as a script's control event does (default 0.1) |
| `--startspeed V` | A car's forward speed (m/s) just before its key goes down, which then goes down one heartbeat later |
| `--jolt KEY=VALUE` | A `[Jolt]` setting, as in the region's ini (repeatable) |
| `--vparam NAME=V` or `NAME=X,Y,Z` | A vehicle parameter by its LSL name, applied after the scenario's own, e.g. `LINEAR_FRICTION_TIMESCALE=1,1,1000` (repeatable) |
| `--vflag NAME` or `-NAME` | A vehicle flag set (or removed) after the scenario's own, e.g. `HOVER_UP_ONLY` (repeatable) |
| `--ball M[,M..]`, `--shot-speed V[,V..]` | The `tunnel-` scenarios: the ball's diameter (m, default 0.2) and the speed it is shot at (m/s, default 25); lists run every combination |
| `--sim-defaults` | Build every prim and avatar with the values the simulator hands the engine for a new one, instead of the harness's own (see "What the harness builds differently", below) |
| `--vehicle-restore ROUTE` | Vehicle scenarios: when the setup is done, hand the vehicle back from its saved settings as the simulator does: `rez`, `region-start`, `copy`, `crossing`, `detach` or `physics-off-on` (see "Vehicles handed back from their saved settings", above) |
| `--vehicle-script-again` | After `--vehicle-restore`, the scenario's script makes its vehicle calls again: the same vehicle set up by its script, on a body with the route's history |
| `--out DIR` | Also write a CSV per run (`<scenario>-s<slope>-r<rate>.csv`, with `-p<rate>` added when the physics rate is on) and `summary.csv` there |

Scenarios: `car` (the car type's presets, motor `<8,0,0>` while a key is held, then released),
`testcar` (the same with linear friction `<1,1,1000>`, motor timescale 1 and decay 0.5),
`carturn` (the car with angular motor `<0,0,1>` held with the forward key),
`sled`, `boat`, `airplane` and `balloon` (each type's presets in one basic motion),
`avatar-stand`, `avatar-walk`, `avatar-run`, `avatar-fly` (level, 30 m up), `avatar-jump`, `avatar-platform` (an avatar arriving on a fixed platform
3 m above the ground) and `avatar-platform-drop` (arriving 3 m above it, landing on it), and `drop`
(a 1 m box from 5 m);
`testcar-down` and `car-down` (key held down the ramp), `hover`, `attract-roll` and `attract-pitch`
(one behaviour alone), `park-new`, `park-faded`, `park-drive`, `park-car` and `park-wake` (sleeping), and
`crash-wall`, `crash-box`, `crash-headon` and `crash-drop`, `rollonly` (a car rolled and pitched with
`VEHICLE_FLAG_LIMIT_ROLL_ONLY`) and `motor-offset` (a floating box pushed below its centre of mass), and
the phantom and volume-detect scenarios: `vd-walk` and `phantom-walk` (an avatar walking through a fixed
volume-detect or phantom box), `vd-drop`, `phantom-drop` and `phantom-physical-drop` (a box falling through a
slab), `phantom-physical-walk`, the `-on-walk` and `-off-walk` scenarios (the flag switched before the avatar
arrives, on one box or a three-box linkset) and `phantom-physical-toggle` (a resting box or linkset made phantom
and solid again), and the resting-contact scenarios: `rest-platform`, `rest-ground` and `rest-vd` (a box comes to
rest on a platform, on the ground or inside a volume-detect box, falls asleep and is thrown off at 5 s),
`tower-10` (ten stacked boxes) and `rest-no-bounce` (a box of restitution 0 dropped 2 m), and the avatar
scenarios: `avatar-hit-1kg`, `avatar-hit-100kg` and `avatar-hit-10ms` (a box thrown at a standing
avatar), `avatar-walk-1kg` and `avatar-walk-1000kg` (an avatar walking into a box), `avatar-on-box`,
`avatar-box-drop` (a box dropped 5 m onto an avatar's head), `avatar-fall-20m`, `avatar-fly-hit-1kg`,
`avatar-fly-hit-100kg` and `avatar-fly-hit-20ms` (a box thrown at a flying avatar), `avatar-fly-wall` and
`avatar-fly-post` (flying into a fixed wall or a 1 kg post), `avatar-stand-up-inside` (an avatar put at the
middle of a fixed cube), `avatar-sit` (an avatar leaves physics on a physical seat and a box is dropped
where it was), `avatar-linkset-20ms` and `avatar-fly-linkset-20ms` (a 100 kg linkset thrown at 20 m/s at a
standing or flying avatar), and the attachment
scenarios: `avatar-buoyancy-1`, `avatar-buoyancy-half`, `avatar-buoyancy-0` and `avatar-ledge` (an avatar
walking off a 3 m platform with that buoyancy, or none set), `avatar-hover` (hover 3 m set, walked across
level ground or up the ramp, then stopped), `avatar-wall` (walking into a fixed wall) and
`avatar-moving-platform` (standing on a platform kept moving at 2 m/s). These print each
watched part's collision events under their summary line.
The shot scenarios fire a physical ball (`--ball`, `--shot-speed`) at something: `tunnel-wall-1cm` and
`tunnel-wall-10cm` (a fixed wall), `tunnel-box` (a 0.5 m box resting on the ground), `tunnel-ground`
(straight down from 3 m), `tunnel-vd-wall` (through a 1 m volume-detect slab, then at a wall),
`tunnel-phantom-box` and `tunnel-phantom-ball` (a physical phantom box, or a phantom ball at a wall);
`tunneled` is 1 when the ball went through. The summary's extra columns give when
the engine last had a body awake and, for the crashes, the impact, arrival and leaving speeds,
overlap and a pass-through check. The ground is
level at 25 m with water at 20 m; with a slope it rises northward at that angle from y 40 to y 100.

Each run prints one summary line: top speed, the release time and speed, the steady speed, the
distance before and after the release, the time to come to rest (under 0.1 m/s for 1 s), the peak
height above the ground, the largest tilt and the end position. The CSV has one line per heartbeat:
time, position, velocity, speed, tilt and height above the ground. Time is simulated, so a run takes
a fraction of real time, and the same arguments always give the same output.

`--rate` is the heartbeat: how often the module's step is called, as a region calls it every
`[Startup] FrameTime` (11 Hz by default). `--physics-rate` runs the same scenario with physics steps
inside each heartbeat, so the two can be compared, e.g.
`--scenario testcar --slope 15 --rate 11 --physics-rate 0,45`; the summary marks those rows
`<scenario>/p45`. The harness writes nothing unless `--out` is given. The module's test
project runs the same scenarios as regression tests (`HarnessTests`).

### What the harness builds differently

The harness adds its prims and avatars through the same `PhysicsScene` calls a region makes, but it
does not set everything the simulator sets on a new one. By default:

- A prim gets no material. `SceneObjectPart.AddToPhysics` gives every new actor its material (a new
  prim is wood: friction 0.6, restitution 0.5), density, gravity multiplier, friction, restitution and
  buoyancy. Without them the body keeps the backend's friction 0.6 and restitution 0.
- A fixed prim gets no density, so it reports the mass of the backend's 1000 kg/m³, 100 times the
  mass of the simulator's density 1000 (which the module scales by 0.01). A physical prim gets 1000.
- An avatar is 0.45 x 0.6 x 1.9 m. For the default appearance the simulator hands `AddAvatar`
  0.45 x 0.6 x 2.1 m (`AvatarAppearance.SetSize` adds 0.2 m to the height), and subscribes it to
  collisions every 100 ms.

`--sim-defaults` builds them as the simulator does. Two restitutions in a contact multiply and the
terrain's is 0, so this changes a bounce only between two prims: a ball shot at a fixed wall comes back
off it. A vehicle sets its own contact friction and restitution 0 while it is one, so no vehicle
scenario changes. An avatar stands 0.1 m higher.

What the option leaves as the harness has it:

- A vehicle is made as a script makes one (`llSetVehicleType`, then its parameters).
  `--vehicle-restore` hands it back from its saved settings instead, through `PhysicsActor.SetVehicle`
  as the simulator does. Every route but `physics-off-on` removes the actor and builds a new one, before
  the first step, so a route compares with a vehicle set up by its script on a body with the same
  history (`--vehicle-script-again`): the engine moves a body it is given after another was removed
  slightly differently once it slows to rest, whatever the body is (the test car on 5 degrees is up to
  0.14 m from where the first body is at the same time).
- Every prim is added before the first step, as a region adds the prims it loads at start-up, not
  after it, as a prim rezzed later is.
- There is no mesher, so a prim that is not a plain box, sphere or cylinder is a bounding box. Every
  scenario uses only plain boxes and spheres.
- The physics step rate is one step per heartbeat unless `--physics-rate` is given; the module's own
  default is 45 Hz.

### The regression check

The Jolt harness workflow runs the runs listed in `Tests/JoltPhysicsHarness/ci/runs.txt`, one
`--out` folder each, and then compares each run's `summary.csv` with a recorded baseline:

```
dotnet Tests/JoltPhysicsHarness/bin/Release/net10.0/JoltPhysicsHarness.dll --check-baseline <folder of the runs> --baselines Tests/JoltPhysicsHarness/ci/baselines
```

The baseline depends on the joltc build that was loaded, not on the operating system: the stock
files are built without Jolt's `CROSS_PLATFORM_DETERMINISTIC` option, so the stock Windows x64 file
gives different results from the stock Linux x64 file, while the patched build gives the same
results on both. Each `--out` folder holds `native.txt`, naming the build the run loaded, and the
check reads `ci/baselines/patched/` for the patched build or `ci/baselines/stock-<version>-<rid>/`
for a stock file. It leaves out `ray_us_per_cast`, which is timed on the wall clock. A build with
no baseline (the stock Arm64 and macOS files, a file the module has no record of, or a new package
version not yet recorded) is reported as `SKIPPED: no baseline for this native` with exit code 3,
neither passed nor failed. Exit 0 is passed and 1 is failed. To record a build's baseline from a set
of runs:

```
dotnet Tests/JoltPhysicsHarness/bin/Release/net10.0/JoltPhysicsHarness.dll --record-baseline <folder of the runs> --baselines Tests/JoltPhysicsHarness/ci/baselines
```

Record only runs that give the same output run after run on one machine; `--leave-out RUN[,RUN..]`
skips the others, and the check then reports those runs as skipped.

`--pool-bench` runs the job pool benchmark instead of the scenarios: a heavy scene (a pile of
`--heavy-boxes` boxes, default 300, kept moving, or with `--heavy-car` the test car driving with its
key held) and `--light` light scenes (bare ground, default 2) in one process, each heartbeat on its
own thread in real time at the first `--rate`, with `--physics-rate` physics steps per second
(default 45 here). It runs each combination of `--threads N[,N..]` (`ThreadCount`, default 0: the
key left unset), `--pools N[,N..]` (`JobPools`, default 1) and `--handoff off|on[,..]`
(`JobPoolFairHandoff`, default off) for `--seconds` (default 20, after a 2 s warm-up) and prints one
line each: the light scenes' pool waits (count, average, longest, and per heartbeat), the heavy
scene's heartbeat time (average and longest, its own pool waits included) and pool wait per
heartbeat, physics steps per second over all scenes, heartbeats that started more than a heartbeat
late, the job threads and threads per pool, and the heavy scene's average active bodies.
`--heavy-boxes 0 --light 1` times one bare scene on its own. `--heavy-scenes N` runs N heavy scenes
(default 1); the heavy columns then cover them together, and with `--out` the file
`pool-bench-scenes.csv` gives each scene's pool, heartbeat time and pool waits. `--unpaced` runs the
heartbeats back to back. Unlike the scenarios its figures are timings, so they depend on the machine
and its load; with `--out` it writes `pool-bench.csv`.
