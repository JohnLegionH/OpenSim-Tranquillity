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

The module ships its native joltc library for:

| Platform | File in the build output |
|---|---|
| Windows x64 | `runtimes/win-x64/native/joltc.dll` |
| Linux x64 (glibc) | `runtimes/linux-x64/native/libjoltc.so` |

Both are patched builds; the stock joltc from NuGet must not be used with this module. Why, and
how they are built, is in [native/joltc/README.md](../native/joltc/README.md). Arm64 (Linux and
Windows), macOS and musl-based Linux (Alpine) have no native yet: there, keep another physics
engine.

A build with no runtime identifier carries both files, so the same output runs on either
platform. A `dotnet publish -r win-x64` or `-r linux-x64` carries only that platform's file.
Nothing goes in the output root: copy the whole output, including its `runtimes` folder.

### The native check at start

When `physics = Jolt`, the module picks the file for the platform it runs on, computes its SHA-256
and compares it with the builds it ships. With a good file it logs one line, once per process:

```
[JOLT SCENE] joltc for linux-x64: /opt/opensim/bin/runtimes/linux-x64/native/libjoltc.so sha256 EEAD7C1A... (the patched build this module ships)
```

In the cases below the module logs one error line and throws from its `Initialise`, before any
region's physics exists, the same way as the meshing check under "Selecting Jolt"; the exception
is not caught on the way up, so the simulator does not finish starting. The cases:

- the platform has no native:
  `Jolt physics has no native library for this platform (linux-arm64). Supported platforms: win-x64, linux-x64. Choose another physics engine in [Startup] physics.`
- the file is missing: `Jolt physics: the native library for linux-x64 is missing: <path>. ...`
- the file's hash is not one the module ships (a stock joltc, or another build):
  `Jolt physics: <path> has sha256 <hash>, which is not the patched build this module ships for linux-x64 (...). ...`

`[Jolt] AllowUnrecordedNative = true` (default `false`) loads a file whose hash the module does not
know, with the start line logged as a warning instead. Use it only for a joltc built from the
recipe in native/joltc/README.md; a stock joltc aborts the process when two regions step at once.

## Selecting Jolt

Jolt is chosen per simulator in `[Startup]`, and it needs the Meshmerizer mesher:

```ini
[Startup]
    physics = Jolt
    meshing = Meshmerizer
```

- With any other `physics` value the module stays loaded but does nothing.
- With `physics = Jolt` and any `meshing` other than `Meshmerizer`, the module logs that meshing
  must be Meshmerizer and throws "Invalid physics meshing option for Jolt" when it initialises.
- The shipped default stays `physics = ubODE`.

`assert-patched-joltc.ps1` (next to the module's project file) checks a build or publish directory
before it is deployed: every `runtimes/<rid>/native/` file must be the patched build for its
platform, and no other joltc file may be there (`-AllowStray` reports such files without failing,
for an installation that still holds files from an older deploy). It runs in Windows PowerShell
and in PowerShell 7 on Linux:

```powershell
powershell -File assert-patched-joltc.ps1 -PublishDir "<publish directory>"
```

```
pwsh -File assert-patched-joltc.ps1 -PublishDir "<publish directory>"
```

## Settings

All settings are in the `[Jolt]` section. `OpenSimDefaults.ini` lists every key with its default
and what it does; copy a key into `OpenSim.ini` to change it. An invalid value logs a warning and
the default is used. The keys cover gravity, solver sub-steps and iterations, the worker threads
and job pools shared by all regions in the process, body / pair / contact capacities (optionally
scaled with region area for var regions), the per-frame update buffers, the avatar jump speed,
how often capacity warnings are logged, the physics step rate (below), the vehicle settings (the
share of gravity on a ground vehicle, the type presets, the limits and the sled's assist; see
"Vehicles"), the engine's body speed caps, the limits on script ray casts and pushes (below),
`AllowUnrecordedNative` (above), and `TestCommands` (below).

### Physics step rate

By default the module runs one physics step per region heartbeat (`[Startup] FrameTime`, about 11
per second). `[Jolt] PhysicsStepRate` runs physics at a set rate inside each heartbeat instead:

```ini
[Jolt]
    PhysicsStepRate = 45
    PhysicsStepCollisionSteps = 2
```

- Each step is exactly 1/45 s. An 11 Hz heartbeat runs 4 or 5 steps; the time left over carries
  into the next heartbeat, so the long-run rate is exact.
- In every step: the vehicle controllers, the avatar step, and forces and changes queued by
  scripts. Once per heartbeat: positions and velocities sent to the scene (and from there to
  viewers), the settle update of a body that came to rest, and collision events.
- Collision events cover every step of the heartbeat. A touching pair counts once per heartbeat,
  as before. A contact that begins and ends inside one heartbeat gives `collision_start` in that
  heartbeat and `collision_end` in the next.
- `llApplyImpulse` on a physical object gives the same velocity change as with one step per
  heartbeat, and so does a force the scene marks as a push.
- `PhysicsStepCollisionSteps` replaces `CollisionSteps` while the rate is on: the solver's
  sub-steps per physics step. 2 at 45 steps per second slices the solver at 90 Hz.
- A heartbeat runs at most 16 steps. A heartbeat that would need more runs 16 and drops the rest
  of its time; `jolt capacity` shows how many heartbeats did ("physics steps").
- A rate below the heartbeat's own rate is refused with one warning at region start, and the region
  runs one step per heartbeat.
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
| `RayCastBudgetMs` | 5 | Time (ms) one region's script ray casts may take in one heartbeat, at any `PhysicsStepRate`. |
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
- A cast that runs past the region's time left in the heartbeat, or past `RayCastMaxTestedHits`, is
  cut short; once the region has spent `RayCastBudgetMs` in a heartbeat, further casts are refused
  until the next one. Either way the script gets `RCERR_CAST_TIME_EXCEEDED` (-3), which Second
  Life documents as "the parcel or agent has exceeded the maximum time allowed for raycasting. This
  resource pool is continually replenished, so waiting a few frames and retrying is likely to
  succeed." That holds for Phlox's `llCastRay`, which reports an error from the physics query as
  that code (it logs each one as a warning). OpenSim's `llCastRay` (LSL_Api, used by YEngine) cannot
  be told: it gets no hits from the physics engine, and returns only what it tests itself (avatars,
  phantoms and, for long rays, the ground) with a status of that many hits.

What the script engines already limit before a cast reaches the module: both cap `RC_MAX_HITS`
at 16 (OpenSim's asks the physics engine for twice that), and Phlox refuses fewer than 1. Neither
limits the ray's length or how often a script casts.

`jolt capacity` and the harness's `raycast-cost` scenario show the casts made, refused and cut
short, and their time.

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

`ThreadCount = 0` (the default) is automatic: each pool gets its share of the processor count
less one, at most 4 threads and at least 1. On a 20-thread machine with one pool that is 4 threads;
with 2 pools, 4 each; on a 4-thread machine with one pool, 3. A positive `ThreadCount` is the total,
used as given. To get the behaviour from before the cap, every processor but one in the pools, set
`ThreadCount` to the processor count less one. The startup log and `jolt capacity` give the pools,
the threads per pool, and whether the count is automatic or set.

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
  pool waits), the pool waits (count, total, longest, and which region had last taken the pool when
  the longest began), the region lock waits (count, total, longest), and the longest gap between two
  heartbeats' physics calls against the frame time. `jolt metrics` shows both lines.
- The harness's `--pool-bench` (see "Physics harness") measures waits and step times for a heavy
  region sharing pools with light ones, for any `JobPools` and handoff.

## Vehicles

LSL vehicles run on a controller that steps each behaviour Second Life documents on that documented
model. The sled's slope assist (`VehicleSledAssist` below) is not a documented Second Life behaviour;
it is carried over from the InWorldz Halcyon sled code. The controller's frame structure and parameter handling come from the InWorldz Halcyon vehicle code, whose type
defaults remain available as `VehiclePresets = legacy`. Every behaviour is stepped as the exact solution over the step
the engine takes, so a vehicle drives the same at the default heartbeat and at any
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
and `jolt parity`, which runs the same drop and boat scenarios under any physics engine so two
engines can be compared. `help Physics` lists each one with a line of help.

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
| `--physics-rate HZ[,HZ..]` | `[Jolt] PhysicsStepRate`: physics steps per second inside each heartbeat; 0 is one step per heartbeat (default: 0, or the `JOLT_HARNESS_PHYSICS_RATE` environment variable) |
| `--slope DEG[,DEG..]` | Ramp angles for the scenarios that use one (default: each scenario's own list) |
| `--duration S`, `--hold S` | Seconds simulated, and seconds the drive key is held |
| `--keyrepeat S` | How often a held key re-sends the motor, as a script's control event does (default 0.1) |
| `--startspeed V` | A car's forward speed (m/s) just before its key goes down, which then goes down one heartbeat later |
| `--jolt KEY=VALUE` | A `[Jolt]` setting, as in the region's ini (repeatable) |
| `--vparam NAME=V` or `NAME=X,Y,Z` | A vehicle parameter by its LSL name, applied after the scenario's own, e.g. `LINEAR_FRICTION_TIMESCALE=1,1,1000` (repeatable) |
| `--vflag NAME` or `-NAME` | A vehicle flag set (or removed) after the scenario's own, e.g. `HOVER_UP_ONLY` (repeatable) |
| `--out DIR` | Also write a CSV per run (`<scenario>-s<slope>-r<rate>.csv`, with `-p<rate>` added when the physics rate is on) and `summary.csv` there |

Scenarios: `car` (the car type's presets, motor `<8,0,0>` while a key is held, then released),
`testcar` (the same with linear friction `<1,1,1000>`, motor timescale 1 and decay 0.5),
`carturn` (the car with angular motor `<0,0,1>` held with the forward key),
`sled`, `boat`, `airplane` and `balloon` (each type's presets in one basic motion),
`avatar-stand`, `avatar-walk` and `avatar-jump`, and `drop` (a 1 m box from 5 m);
`testcar-down` and `car-down` (key held down the ramp), `hover`, `attract-roll` and `attract-pitch`
(one behaviour alone), `park-new`, `park-faded`, `park-drive`, `park-car` and `park-wake` (sleeping), and
`crash-wall`, `crash-box`, `crash-headon` and `crash-drop`, `rollonly` (a car rolled and pitched with
`VEHICLE_FLAG_LIMIT_ROLL_ONLY`) and `motor-offset` (a floating box pushed below its centre of mass). The summary's extra columns give when
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
