# Jolt physics

The Jolt module is an optional physics engine for the region server, built on
[Jolt Physics](https://github.com/jrouwe/JoltPhysics) through the joltc native library and the
JoltPhysicsSharp binding. Each region gets its own Jolt physics system, and LSL vehicles run on the
InWorldz Halcyon vehicle dynamics, with the linear and angular motors and friction on Second Life's
documented model (see "Vehicle motors and friction" below).

This guide is for operators. It covers selecting Jolt, its settings, its console commands, and
the platforms it runs on today.

## Platform

The native joltc library ships for **Windows x64 only** at present
(`Source/OpenSim.Region.PhysicsModules.Jolt/runtimes/win-x64/native/joltc.dll`). It is a patched
build; the stock joltc from NuGet must not be used with this module. Why, and how to rebuild it,
is in [native/joltc/README.md](../native/joltc/README.md). On other platforms keep the default
physics engine.

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

`assert-patched-joltc.ps1` (next to the module's project file) checks that every `joltc*.dll` in
a build or publish directory is the patched build:

```powershell
powershell -File assert-patched-joltc.ps1 -PublishDir "<publish directory>"
```

## Settings

All settings are in the `[Jolt]` section. `OpenSimDefaults.ini` lists every key with its default
and what it does; copy a key into `OpenSim.ini` to change it. An invalid value logs a warning and
the default is used. The keys cover gravity, solver sub-steps and iterations, the worker threads
and job pools shared by all regions in the process, body / pair / contact capacities (optionally
scaled with region area for var regions), the per-frame update buffers, the avatar jump speed,
how often capacity warnings are logged, the physics step rate (below), the share of gravity on a
ground vehicle (below), and `TestCommands` (below).

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
still depends on the step changes. The vehicle motors, a jump's rise and walking speed do not: in
the harness (below) the test car on level ground leaves the key at 3.79 m/s either way and an avatar
jump rises 0.82 m. The vehicle hover, the vertical attractor and the sled's slope assist still depend
on it: the sled on 15 degrees steadies at about 13.6 m/s instead of 15.8.
Position updates to viewers, timers and sensors stay at the heartbeat rate. Physics costs more:
in the harness the test car takes about 1.5 times the step time of one step per heartbeat
(`jolt metrics` shows each region's step time).

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
factor. In the harness the test car (linear friction timescale 1 s) rolls 0.19 m/s down 5 degrees
with 0.2, against 0.93 m/s with 1, and 0.56 m/s down 15 degrees, against 2.67.

### Vehicle motors and friction

The linear motor and linear friction act on each axis of the vehicle's frame as one equation, as the
Second Life wiki's vehicle tutorial describes them:

    dv/dt = g * (M - v) / Tm  -  v / Tf          g = e^(-s / Td)

`M` is `VEHICLE_LINEAR_MOTOR_DIRECTION`, `Tm` the motor timescale, `Td` the motor decay timescale,
`s` the time since the script last set the motor, and `Tf` the friction timescale. The angular motor
and angular friction follow the same equation on the angular velocity. What follows from it:

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
  (slowing a fall) stays.

## Console commands

`jolt` acts on the region selected with `change region`; at the root prompt every region answers in
turn, each under a `--- region '<name>' ---` line.

These read and report only, and are always available:

| Command | What it shows |
|---|---|
| `jolt capacity` | Bodies, characters, the pair and contact caps, solver errors, dropped contacts, job pools and buffers |
| `jolt metrics` | Process memory and threads, and each region's step time and active bodies |
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
joltc, so it runs anywhere the Jolt tests run (win-x64 at present). Use it to see how a vehicle or
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
| `--out DIR` | Also write a CSV per run (`<scenario>-s<slope>-r<rate>.csv`, with `-p<rate>` added when the physics rate is on) and `summary.csv` there |

Scenarios: `car` (the car type's presets, motor `<8,0,0>` while a key is held, then released),
`testcar` (the same with linear friction `<1,1,1000>`, motor timescale 1 and decay 0.5),
`carturn` (the car with angular motor `<0,0,1>` held with the forward key),
`sled`, `boat`, `airplane` and `balloon` (each type's presets in one basic motion),
`avatar-stand`, `avatar-walk` and `avatar-jump`, and `drop` (a 1 m box from 5 m). The ground is
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
