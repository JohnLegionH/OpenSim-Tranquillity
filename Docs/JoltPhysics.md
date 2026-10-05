# Jolt physics

The Jolt module is an optional physics engine for the region server, built on
[Jolt Physics](https://github.com/jrouwe/JoltPhysics) through the joltc native library and the
JoltPhysicsSharp binding. Each region gets its own Jolt physics system, and LSL vehicles run on the
InWorldz Halcyon vehicle dynamics.

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
how often capacity warnings are logged, and `TestCommands` (below).

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
| `--slope DEG[,DEG..]` | Ramp angles for the scenarios that use one (default: each scenario's own list) |
| `--duration S`, `--hold S` | Seconds simulated, and seconds the drive key is held |
| `--keyrepeat S` | How often a held key re-sends the motor, as a script's control event does (default 0.1) |
| `--jolt KEY=VALUE` | A `[Jolt]` setting, as in the region's ini (repeatable) |
| `--vparam NAME=V` or `NAME=X,Y,Z` | A vehicle parameter by its LSL name, applied after the scenario's own, e.g. `LINEAR_FRICTION_TIMESCALE=1,1,1000` (repeatable) |
| `--out DIR` | Also write a CSV per run (`<scenario>-s<slope>-r<rate>.csv`) and `summary.csv` there |

Scenarios: `car` (the car type's presets, motor `<8,0,0>` while a key is held, then released),
`testcar` (the same with linear friction `<1,1,1000>`, motor timescale 1 and decay 0.5),
`sled`, `boat`, `airplane` and `balloon` (each type's presets in one basic motion),
`avatar-stand`, `avatar-walk` and `avatar-jump`, and `drop` (a 1 m box from 5 m). The ground is
level at 25 m with water at 20 m; with a slope it rises northward at that angle from y 40 to y 100.

Each run prints one summary line: top speed, the release time and speed, the steady speed, the
distance before and after the release, the time to come to rest (under 0.1 m/s for 1 s), the peak
height above the ground, the largest tilt and the end position. The CSV has one line per heartbeat:
time, position, velocity, speed, tilt and height above the ground. Time is simulated, so a run takes
a fraction of real time, and the same arguments always give the same output.

A rate here means calling today's step at that interval: 11 Hz is a region's default heartbeat
(`[Startup] FrameTime`). The harness writes nothing unless `--out` is given. The module's test
project runs the same scenarios as regression tests (`HarnessTests`).
