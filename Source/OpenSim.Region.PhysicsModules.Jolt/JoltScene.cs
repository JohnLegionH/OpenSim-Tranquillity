/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Jolt physics as an OpenSim region module (PhysicsScene).
//
// ============================ READ THIS FIRST ============================
// This is the seam between OpenSim's PhysicsScene contract and the engine-agnostic
// IPhysicsBackend (implemented for Jolt in JoltPhysicsBackend, which has its own standalone tests).
// The module registers, boots under `physics = Jolt`, and drives real physics:
//   - AddPrimShape cooks a shape (fixed-shape fast path, IMesher mesh/hull, or bbox fallback) and
//     creates a JoltPrim - STATIC when non-physical, dynamic when physical - tracked in _prims.
//   - AddAvatar has three overloads (no-localID, localID, feetOffset); the avatar gets a Jolt
//     CharacterVirtual carrying its kinematic query marker.
//   - SetTerrain cooks the real (N+1)-square heightfield and swaps it in; SetWaterLevel
//     pushes the water height to the backend.
//   - Simulate rebuilds dirty linkset compounds, activates bodies created inert, runs the
//     vehicle controllers, steps the backend once per frame, then drains.
// The batched-buffer drain (StepResult -> per-actor RequestPhysicsterseUpdate / collision dispatch)
// IS here, at the tail of Simulate.
//
// Registration mirrors BSScene: a region module (DotNetCorePlugins; see PluginRegistration.cs)
// that self-selects when [Startup]
// physics == Name. No [Startup] edit - the operator picks `physics = Jolt`; this module recognises
// its own name.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using Nini.Config;
using Microsoft.Extensions.Logging;
using OpenMetaverse;

using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
// The backend speaks System.Numerics.Vector3; OpenSim speaks OpenMetaverse.Vector3 (the unqualified
// Vector3 here). Alias the numerics one so backend calls are unambiguous.
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    public sealed partial class JoltScene : PhysicsScene, INonSharedRegionModule
    {
        internal static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        internal const string LogHeader = "[JOLT SCENE]";

        // Gate for JoltCharacter's [charjump] path trace; toggled by `jolt charframe` and kept in sync with
        // the [charframe] window by Simulate. Static so the per-avatar actor can read it without a back-ref.
        internal static bool CharJumpTrace;

        private bool m_Enabled = false;
        private IConfigSource m_Config;

        // [Jolt] TestCommands: register the console test commands (JoltTestCommands.cs). Off by default.
        private bool m_testCommands;

        // The [Jolt] section, parsed once in Initialise. The defaults reproduce the earlier hardcoded constants.
        private JoltConfig _joltConfig = new JoltConfig();
        internal float AvatarJumpSpeed => _joltConfig.AvatarJumpSpeed;
        internal float VehicleGroundGravityFactor => _joltConfig.VehicleGroundGravityFactor;

        // The vehicle settings every vehicle controller in this region shares, built from [Jolt] in Initialise.
        internal VehicleSettings VehicleSettings { get; private set; } = VehicleSettings.Default;

        // The job pool is process-wide; log its size once, whichever region creates it.
        private static readonly object s_poolLogGate = new object();
        private static bool s_poolLogged;

        // The engine-agnostic backend.
        private IPhysicsBackend _backend;

        // The region's IMesher, driving the cook path for cut/hollow/sculpt/mesh prims.
        private IMesher m_mesher;

        public string RegionName { get; private set; }

        // Terrain: the current cooked heightfield ShapeId (released + replaced on each SetTerrain),
        // and the region dimensions needed to interpret the flat float[] heightmap OpenSim hands us.
        private ShapeId _terrainShape = ShapeId.Invalid;
        private int _regionSizeX;
        private int _regionSizeY;
        private Scene _scene;

        // Vehicle-controller world inputs: the region water plane, the last cooked terrain
        // sample field (for height-at-XY without a per-frame raycast), the world gravity handed to
        // the backend, and the last Simulate dt (BulletSim's LastTimeStep, used by AddForce).
        internal float WaterLevel { get; private set; }
        internal SVector3 DefaultGravity { get; private set; } = new SVector3(0f, 0f, -9.80665f);
        internal float LastTimeStep = 0.0909f;
        // The clock new vehicle controllers read (motor reset and spike checks). Null in a region, where
        // they read the wall clock; the test harness sets a simulated one so it can step faster than real time.
        internal Func<DateTime> VehicleClock;

        // [Jolt] PhysicsStepRate: null = one backend step per heartbeat (the default). Otherwise each heartbeat runs
        // the steps this accumulator hands out, each 1 / rate seconds long. Set once, in InitialiseRegion.
        private SubstepAccumulator _substeps;
        internal bool Substepping => _substeps != null;
        internal SubstepAccumulator Substeps => _substeps;

        // A push force (AddForce with pushforce) acts for one backend step; this keeps its impulse at force x the
        // heartbeat time when a step is shorter than the heartbeat. 1 when not substepping.
        internal float PushForceScale = 1f;

        // Physics time while substepping: advanced by exactly one step per backend step, so a vehicle controller
        // sees the true interval between its steps. The wall clock does not move between the steps of one
        // heartbeat, and the controller divides by that interval (spike checks). 0 until the first step.
        private long _physicsClockTicks;
        internal DateTime PhysicsClock()
        {
            long t = Interlocked.Read(ref _physicsClockTicks);
            return t != 0 ? new DateTime(t) : (VehicleClock?.Invoke() ?? DateTime.Now);
        }

        // The clock a new vehicle controller gets: physics time while substepping, otherwise VehicleClock (null in a
        // region: the controller keeps its wall clock).
        internal Func<DateTime> ControllerClock => _substeps != null ? PhysicsClock : VehicleClock;
        private float[] _terrainField;   // the (N+1)-square field SetTerrain cooked (row = y * _terrainFieldM)
        private int _terrainFieldM;

        // Bilinear terrain height at region XY, from the same samples the collision heightfield was
        // cooked from (1 m spacing, origin at the region corner). Clamps outside the field.
        internal float TerrainHeightAt(float x, float y)
        {
            float[] f = _terrainField;
            int m = _terrainFieldM;
            if (f == null || m < 2)
                return 0f;
            x = Math.Clamp(x, 0f, m - 1.001f);
            y = Math.Clamp(y, 0f, m - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            float h00 = f[y0 * m + x0], h10 = f[y0 * m + x0 + 1];
            float h01 = f[(y0 + 1) * m + x0], h11 = f[(y0 + 1) * m + x0 + 1];
            return h00 * (1 - fx) * (1 - fy) + h10 * fx * (1 - fy) + h01 * (1 - fx) * fy + h11 * fx * fy;
        }

        // Live prims by SceneObjectPart.LocalId. RemovePrim looks up here; also the
        // Step-drain target for physical actors. Guarded because Add/RemovePrim can arrive off
        // the heartbeat thread (the backend permits concurrent Create/Remove with Step).
        private readonly Dictionary<uint, JoltPrim> _prims = new Dictionary<uint, JoltPrim>();

        // True only before the first Simulate (the region-reload window). Used by JoltPrim to drop a restored
        // physical prim's horizontal velocity on load (so a reloaded body doesn't inherit a stale coast).
        internal bool IsRegionLoading => _stepCount == 0;

        // Mirrors BulletSim's taint-deferred body creation: physical bodies are created INERT and
        // their activation is deferred to the top of the next Simulate (step thread), so a body is never
        // stepped by the engine before all its load-time state (incl. the vehicle's gravity-cancellation) is
        // applied - it cannot free-fall during the load or the reload stall. Drained in Simulate.
        private readonly List<JoltPrim> _pendingActivation = new List<JoltPrim>();
        internal void RegisterPendingActivation(JoltPrim p)
        {
            lock (_pendingActivation)
                if (!_pendingActivation.Contains(p)) _pendingActivation.Add(p);
        }

        // The logged-in avatars, keyed by their CharacterId handle (the value the character drain
        // echoes back). Keyed by handle rather than LocalID so the drain mapping is independent of when
        // ScenePresence assigns LocalID after AddAvatar returns.
        private readonly Dictionary<uint, JoltCharacter> _avatars = new Dictionary<uint, JoltCharacter>();

        // Collision-mesh LOD (matches BulletSim's BSParam.MeshLOD default), and the
        // characterization of the last RAW mesher output cooked (verts/tris/degenerate/duplicate/AABB).
        private const float MeshLod = 32f;
        private struct MeshStats
        {
            public int Verts, Tris, DegenerateTris, DuplicateVerts, OutOfRangeIndices;
            public SVector3 Min, Max;
            public float Volume;   // enclosed volume of the (closed) mesh; == convex-hull volume for a convex prim
        }
        private MeshStats _lastMeshStats;

        // Dynamics: per-frame step counter + latest active-body count (drop asserts read these), and
        // the tracked physical drops for the `jolt droptest`/`dropmesh`/`dropstatus` test commands
        // (JoltTestCommands.cs). _lastBoxRestZ/_lastMeshRestZ
        // persist across drops so a re-run can report determinism (same rest height).
        private long _stepCount;
        private int _lastActiveBodyCount;
        private float _lastBoxRestZ = float.NaN, _lastMeshRestZ = float.NaN;
        private sealed class DropTrack
        {
            public uint LocalId;
            public string Kind;                 // "box" or "mesh"
            public float StartZ;
            public long StartStep;
            public float MinZ = float.MaxValue;
            public float LastZ, LastSpeed;
            public int JustDeactivatedCount;
            public float RestZ = float.NaN;
            public long RestStep = -1;
            public float ExpectedMass;          // box: volume*density; mesh: hull(=mesh)volume*density
            public float ExpectedRestZ;         // terrain Z + half-height
        }
        private readonly List<DropTrack> _drops = new List<DropTrack>();
        private long _logStepsUntil = -1;   // window: log per-frame dt/ActiveBodyCount/liveZ after a drop
        private long _charFrameUntil = -1;  // window: log per-frame avatar Z/support/vZ ([charframe] toggle)

        // Caller-owned step buffers (backend contract: nothing allocates per frame). Simulate drains all
        // three every step. They start at these sizes and DOUBLE on overflow, up to the caps below
        // (the backend drain is fair, so an overflowing frame loses nothing, it only delays), and allocate only
        // when they grow. The contact cap defaults to the backend's ring capacity - past that, growing is useless.
        private BodyState[] _bodyBuf = new BodyState[1024];
        private CharacterState[] _charBuf = new CharacterState[256];
        private ContactReport[] _contactBuf = new ContactReport[2048];
        private int _bodyBufMax = 65536;      // [Jolt] BodyUpdateBufferMax
        private int _charBufMax = 1024;       // [Jolt] CharacterUpdateBufferMax
        private int _contactBufMax = 2048;    // [Jolt] ContactBufferMax; 0 there = the ring capacity (set in AddRegion)
        private long _overflowLastWarnTicks;

        // Collision dispatch: per-frame accumulation of colliders per subscribed prim,
        // and the set of prims that reported collisions LAST frame - so a prim that stops touching gets one
        // empty CollisionEventUpdate this frame, which is how OpenSim's SOP.PhysicsCollision fires collision_end.
        private readonly CollisionFrameTracker _collisions = new CollisionFrameTracker();
        // This frame's prims, resolved under ONE lock(_prims) rather than a lock per contact.
        private readonly HashSet<uint> _frameIds = new HashSet<uint>();
        private readonly Dictionary<uint, JoltPrim> _framePrims = new Dictionary<uint, JoltPrim>();

        // Capacity surfacing. Step-thread only. The warning window starts at the last logged
        // snapshot; a failure inside the quiet period accumulates into the next line instead of being lost.
        private long _capacityLogIntervalTicks = System.TimeSpan.FromSeconds(10).Ticks;   // [Jolt] CapacityLogIntervalSeconds
        private PhysicsCapacityStats _capBase;
        private long _capBaseTicks;
        private bool _capBaseSet;
        private long _bodyOverflowFrames, _charFullFrames, _contactOverflowFrames;

        // ---------------------------------------------------------------------
        // INonSharedRegionModule
        // ---------------------------------------------------------------------

        public string Name => "Jolt";

        // The native's path and hash are logged once per process, by the first region's module.
        private static int s_nativeLogged;

        public System.Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            // Self-selection: only enable when the operator chose us. Mirrors BSScene - we do NOT
            // hard-enable, and we never touch [Startup] ourselves.
            // Read before the physics check: `jolt parity` runs under any physics engine.
            m_testCommands = TestCommandsEnabled(source);

            IConfig config = source.Configs["Startup"];
            if (config != null)
            {
                string physics = config.GetString("physics", string.Empty);
                if (physics == Name)
                {
                    string mesher = config.GetString("meshing", string.Empty);
                    if (string.IsNullOrEmpty(mesher) || !mesher.Equals("Meshmerizer"))
                    {
                        m_log.LogError($"{LogHeader} [Startup] meshing must be set to \"Meshmerizer\" for the Jolt physics module.");
                        throw new System.Exception("Invalid physics meshing option for Jolt");
                    }

                    var warnings = new List<string>();
                    JoltConfig joltConfig = JoltConfig.FromConfig(source, warnings);
                    foreach (string w in warnings)
                        m_log.LogWarning($"{LogHeader} {w}");

                    // The native for this platform, checked and loaded before the module enables, so a platform
                    // with no native, a missing file or an unrecorded build stops startup here with one clear line
                    // instead of failing at the first physics call with a region half up.
                    JoltNativeInfo native;
                    try
                    {
                        native = JoltNative.EnsureLoaded(joltConfig.AllowUnrecordedNative);
                    }
                    catch (JoltNativeException e)
                    {
                        m_log.LogError($"{LogHeader} {e.Message}");
                        throw;
                    }
                    if (System.Threading.Interlocked.Exchange(ref s_nativeLogged, 1) == 0)
                    {
                        if (native.Recorded)
                            m_log.LogInformation($"{LogHeader} {native.Describe()}");
                        else
                            m_log.LogWarning($"{LogHeader} {native.Describe()}");
                    }

                    m_Enabled = true;
                    m_Config = source;
                    _joltConfig = joltConfig;
                    VehicleSettings = joltConfig.ToVehicleSettings();
                    m_log.LogInformation($"{LogHeader} enabled (physics = {Name}).");
                }
            }
        }

        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            RegionName = scene.RegionInfo.RegionName;
            PhysicsSceneName = Name + "/" + RegionName;

            scene.RegisterModuleInterface<PhysicsScene>(this);

            _scene = scene;
            InitialiseRegion(scene.RegionInfo.RegionSizeX, scene.RegionInfo.RegionSizeY, scene.PhysicsRequestAsset,
                () => scene.Heightmap != null ? scene.Heightmap.GetFloatsSerialised() : new float[scene.RegionInfo.RegionSizeX * scene.RegionInfo.RegionSizeY],
                () => (float)scene.RegionInfo.RegionSettings.WaterHeight, scene.FrameTime);
        }

        /// <summary>
        /// The physics test harness's way in: the same backend setup, terrain and water as
        /// <see cref="AddRegion"/>, for a scene with no OpenSim <see cref="Scene"/>. Call
        /// <see cref="Initialise"/> first with a config that selects this module.
        /// </summary>
        internal void InitialiseWithoutScene(string regionName, uint sizeX, uint sizeY, float[] heightMap, float waterHeight, float heartbeatSeconds)
        {
            if (!m_Enabled)
                throw new InvalidOperationException("Initialise with [Startup] physics = Jolt first.");
            RegionName = regionName;
            PhysicsSceneName = Name + "/" + RegionName;
            InitialiseRegion(sizeX, sizeY, null, () => heightMap, () => waterHeight, heartbeatSeconds);
        }

        // AddRegion's work once the scene is known. The heightmap and water height are read where AddRegion
        // always read them, after the backend exists.
        private void InitialiseRegion(uint sizeX, uint sizeY, RequestAssetDelegate requestAsset, Func<float[]> heightMap, Func<float> waterHeight, float heartbeatSeconds)
        {
            // Stored BEFORE base.Initialise, because that calls SetTerrain(heightMap) - which needs the
            // region dims to interpret the flat float[] and build the (N+1) field.
            _regionSizeX = (int)sizeX;
            _regionSizeY = (int)sizeY;

            // Every knob comes from [Jolt] (JoltConfig). The defaults are exactly what this used to hardcode:
            // the backend's default settings, MaxBodies = 65536 per 256 m scaled by area, and
            // CollisionSteps = 6 - the RIGID-BODY solver sub-stepped 6x inside _system.Update: at
            // OpenSim's ~11 fps a single integration lets a fast prim move ~1.5 m and tunnel through the terrain
            // heightfield. CollisionSteps slices the SOLVER only, NOT the character step (once per Step, before
            // Update), so dropped prims rest WITHOUT disturbing the avatar's known-good 1-step path.
            // [Jolt] PhysicsStepRate, checked against the heartbeat Simulate is called at ([Startup] FrameTime).
            float stepRate = _joltConfig.EffectivePhysicsStepRate(heartbeatSeconds, out string rateWarning);
            if (rateWarning != null)
                m_log.LogWarning($"{LogHeader} region '{RegionName}': {rateWarning}");
            _substeps = stepRate > 0f ? new SubstepAccumulator(stepRate) : null;

            PhysicsBackendSettings settings = _joltConfig.ToBackendSettings(sizeX, sizeY, _substeps != null);
            settings.RegionName = RegionName;   // names this region when another waits for its job pool (metrics)
            _bodyBufMax = _joltConfig.BodyUpdateBufferMax;
            _charBufMax = _joltConfig.CharacterUpdateBufferMax;
            _capacityLogIntervalTicks = System.TimeSpan.FromSeconds(_joltConfig.CapacityLogIntervalSeconds).Ticks;

            // Instrumentation: per-region RSS delta across backend init (8MB TempAllocator +
            // MaxBodies preallocation + JobSystemThreadPool), reported by `jolt metrics`.
            long rssBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            _backend = new JoltPhysicsBackend();
            _backend.Initialize(settings);
            PhysicsCapacityStats initStats = _backend.GetCapacityStats();
            _contactBufMax = _joltConfig.ContactBufferMax > 0 ? _joltConfig.ContactBufferMax : initStats.ContactRingCapacity;
            LogJobPool(initStats);
            DefaultGravity = settings.Gravity;   // the vehicle controller applies this manually
            JoltMetrics.RecordRegionInit(RegionName,
                System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 - rssBefore);

            EngineType = Name;                              // osGetPhysicsEngineType
            EngineName = $"{_backend.Name} {_backend.Version}"; // osGetPhysicsEngineName

            // The base Initialise wires the request-asset delegate and calls our SetTerrain - which
            // cooks the real heightfield - and SetWaterLevel, which pushes the water height down
            // to the backend for vehicle hover.
            base.Initialise(requestAsset, heightMap(), waterHeight());

            m_log.LogInformation($"{LogHeader} region '{RegionName}' {sizeX}x{sizeY}m: backend initialised, MaxBodies={settings.MaxBodies}. {EngineName}");
            if (_substeps != null)
                m_log.LogInformation($"{LogHeader} region '{RegionName}': physics steps at {_substeps.RateHz:0.##} Hz " +
                                     $"({settings.CollisionSteps} collision steps each, at most {SubstepAccumulator.MaxStepsPerFrame} per heartbeat).");
        }

        // The shared job pools are sized once, by the first region's request; every region reads the same
        // [Jolt] ThreadCount and JobPools, so they agree unless the pools predate this config (or a harness sized them).
        private void LogJobPool(in PhysicsCapacityStats pool)
        {
            int poolThreads = pool.JobThreadCount;
            lock (s_poolLogGate)
            {
                if (!s_poolLogged)
                {
                    s_poolLogged = true;
                    m_log.LogInformation($"{LogHeader} shared Jolt job pools: {CapacityReport.JobPoolsStartup(in pool)}");
                }
            }
            // Which pool this region steps on, once per region.
            m_log.LogInformation($"{LogHeader} region '{RegionName}' steps on Jolt job pool {pool.PoolIndex} of {pool.JobPools}.");
            int requested = _joltConfig.RequestedThreadCount;
            if (requested != poolThreads)
                m_log.LogWarning($"{LogHeader} region '{RegionName}' asked for {requested} job threads but the shared pools were sized for {poolThreads}; the first region's size wins.");
            // The pool count is process-wide too.
            if (_joltConfig.JobPools != pool.JobPools)
                m_log.LogWarning($"{LogHeader} region '{RegionName}' asked for JobPools={_joltConfig.JobPools} but {pool.JobPools} pools already exist; the first region's value wins.");
            if (_joltConfig.JobPoolFairHandoff != pool.JobPoolFairHandoff)
                m_log.LogWarning($"{LogHeader} region '{RegionName}' asked for JobPoolFairHandoff={_joltConfig.JobPoolFairHandoff} but the pools already exist with " +
                                 $"{pool.JobPoolFairHandoff}; the first region's value wins.");
            else if (pool.JobPoolFairHandoff)
                m_log.LogInformation($"{LogHeader} region '{RegionName}': Jolt job pools hand over first come, first served ([Jolt] JobPoolFairHandoff).");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            // `jolt parity` is an engine-agnostic A/B driver registered under ANY physics engine, so
            // the SAME console command runs under BulletSim and Jolt for a clean comparison. It MUST be set
            // up BEFORE the m_Enabled gate (under physics = BulletSim this module is loaded/scanned but is
            // NOT the physics engine, so m_Enabled is false and the rest of RegionLoaded early-returns). The
            // harness drives ONLY the standard Scene/SceneObjectGroup/PhysicsActor surface - no Jolt backend.
            // It is a test command, so it is registered only when [Jolt] TestCommands is true.
            RegisterParityConsole(scene);

            if (!m_Enabled)
                return;

            // The IMesher the cook path needs; without one, prims fall back to bounding boxes.
            m_mesher = scene.RequestModuleInterface<IMesher>();
            if (m_mesher == null)
                m_log.LogWarning($"{LogHeader} no IMesher available - shape cooking will need it.");

            scene.PhysicsEnabled = true;

            // Test scaffolding (gated by [Startup] JoltAutoDropTest, and only when [Jolt] TestCommands is true;
            // see AutoDropTestEnabled in JoltTestCommands.cs): auto-drop a few physical boxes so
            // fall + settle-on-real-terrain is verifiable from the log ([dropframe] liveZ, or `jolt dropstatus`)
            // with no viewer, and every region carries bodies for a concurrent-step load test. Uses the same
            // DropOne code path as `jolt droptest`. NEVER runs unless the flags are set, so it cannot affect
            // real regions.
            if (AutoDropTestEnabled(m_Config))
            {
                ClearTestPrims();
                DropOne("box", new Vector3(2f, 2f, 2f), 118f, 128f);
                DropOne("box", new Vector3(2f, 2f, 2f), 124f, 128f);
                DropOne("box", new Vector3(2f, 2f, 2f), 130f, 128f);
                m_log.LogInformation($"{LogHeader} JoltAutoDropTest: dropped 3 physical boxes in '{RegionName}' (watch [dropframe] / `jolt dropstatus`).");
            }

            // The `jolt` console commands (read-only diagnostics such as a straight-down raycast onto the
            // cooked terrain, plus the test commands when [Jolt] TestCommands is true).
            //
            // REGISTERED PER REGION, not once behind a static gate. With a single registration the
            // FIRST region to boot would own the delegate and capture ITS `this` forever, so on a
            // multi-region simulator every `jolt ...` command would run against that one region no matter
            // what the console prompt said (for example `jolt heights 512 512` reporting "no terrain" for a
            // 1024x1024 var region because it was measuring a 256x256 region that booted first).
            // Registering per region + the WrongConsoleScene() check in each handler makes
            // `change region <name>` actually select the target.
            if (MainConsole.Instance != null)
            {
                RegisterConsoleCommands(MainConsole.Instance.Commands, m_testCommands, HandleJoltConsole, HandleJoltTestConsole);
            }
        }

        // The `jolt` subcommands that only read and report. They never change the region, so they are always
        // registered. The test subcommands are listed in JoltTestCommands.cs.
        internal static readonly string[] ReadOnlyCommands =
        {
            "capacity", "metrics", "terraintest", "probe", "heights", "avatarstatus", "charframe",
            "sitstatus", "sensortest", "raytest", "reloadcheck", "vehiclestatus",
        };

        internal const string ReadOnlyUsage =
            "jolt capacity | metrics | terraintest | probe <x> <y> | heights <x> <y> | avatarstatus | charframe [secs] | sitstatus | sensortest | raytest | reloadcheck | vehiclestatus";

        /// <summary>
        /// Register `jolt` with its read-only subcommands, and the test subcommands only when
        /// <paramref name="testCommands"/> is true. A test subcommand that is not registered falls through to
        /// <paramref name="readOnly"/>, which answers with the usage line.
        /// </summary>
        internal static void RegisterConsoleCommands(ICommands commands, bool testCommands, CommandDelegate readOnly, CommandDelegate test)
        {
            commands.AddCommand("Physics", false, "jolt", ReadOnlyUsage,
                "Jolt physics diagnostics for the current region: capacity and step metrics, terrain and ray probes, "
                + "avatar, sit, vehicle and reload status. None of them changes the region.",
                readOnly);
            if (testCommands)
                RegisterTestCommands(commands, test);
        }

        /// <summary>
        /// True when the console is scoped to a DIFFERENT region than this instance, so this handler
        /// should stay quiet. Mirrors the house idiom (ExperienceModule.WrongConsoleScene).
        ///
        /// ConsoleScene == null means the ROOT prompt ("Regions #"), which addresses ALL regions - every
        /// region's handler runs. That is standard OpenSim behaviour and is what makes a root-scoped
        /// command a whole-sim sweep. See the note in HandleJoltConsole about which commands you actually
        /// want to run that way.
        /// </summary>
        private bool WrongConsoleScene()
            => !(MainConsole.Instance.ConsoleScene is null || MainConsole.Instance.ConsoleScene == _scene);

        // Raycast straight down at XY (from well above the region) and report the hit Z - proves the
        // heightfield's ACTUAL collision surface, not "it booted". `jolt terraintest` sweeps the extent
        // probes (interior + the far edge that the (N+1) field must now cover); `jolt probe x y` is ad hoc.
        private void HandleJoltConsole(string module, string[] cmd)
        {
            // Region scoping. Every region registers this command, so without this check all of them
            // would answer every invocation. `change region <name>` -> only that region's handler proceeds.
            if (WrongConsoleScene())
                return;

            if (_backend == null) { MainConsole.Instance.Output($"{LogHeader} no backend."); return; }

            // At the ROOT prompt every region runs the command, so stamp whose output follows - otherwise
            // three regions' results interleave with no way to tell them apart. When the console is scoped
            // to one region this is redundant, so it is skipped.
            //
            // NOTE: a root-scoped `jolt ...` is a SIM-WIDE sweep. Every subcommand handled here is read-only
            // (heights / reloadcheck / vehiclestatus ...), so that is safe. The world-mutating test commands
            // (rezprims / droptest / clearprims / linktest / sittest ...) live in JoltTestCommands.cs, are
            // registered only when [Jolt] TestCommands is true, and use the same region scoping - so at the
            // root prompt they too act in EVERY region. Use `change region <name>` before those.
            if (MainConsole.Instance.ConsoleScene is null)
                MainConsole.Instance.Output($"{LogHeader} --- region '{RegionName}' ---");

            if (cmd.Length >= 2 && cmd[1] == "capacity") { JoltCapacity(); return; }   // read-only
            if (cmd.Length >= 2 && cmd[1] == "metrics")   // step-time / RSS instrumentation, and the last interval's timing
            {
                MainConsole.Instance.Output(JoltMetrics.Report());
                MainConsole.Instance.Output(JoltMetrics.LastIntervalLine() ?? "[JOLT METRICS] no interval has finished yet (the log closes one about every 30 s)");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "terraintest")
            {
                int n = _regionSizeX;
                // Interior probes + the FAR METRE (n-0.5): the (N+1) field spans [0,n], so (n-0.5) - which
                // an old N-sample field would MISS (it only reached n-1) - must now HIT. (n) is the exact
                // outer vertex and may graze (float); (n+0.5) is beyond the region and must miss.
                var pts = new (float x, float y)[]
                { (1f, 1f), (n / 2f, n / 2f), (n - 1f, n - 1f), (n - 0.5f, n - 0.5f), (n, n), (n + 0.5f, n + 0.5f) };
                MainConsole.Instance.Output($"{LogHeader} terrain raycast probes (region {n}x{_regionSizeY}; (N+1) field spans [0,{n}] m):");
                foreach (var (px, py) in pts)
                {
                    bool hit = _backend.RayCast(new SVector3(px, py, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.All, out RayHit h);
                    MainConsole.Instance.Output(hit
                        ? $"  ({px,7:0.0},{py,7:0.0}) -> HIT  z={h.Point.Z:0.000}  n.z={h.Normal.Z:0.00}"
                        : $"  ({px,7:0.0},{py,7:0.0}) -> miss");
                }
                MainConsole.Instance.Output($"  interior + (n-0.5) must HIT at the flat Z; (n) exact vertex may graze; (n+0.5) beyond region misses.");
                return;
            }

            if (cmd.Length >= 4 && cmd[1] == "probe"
                && float.TryParse(cmd[2], out float x) && float.TryParse(cmd[3], out float y))
            {
                bool hit = _backend.RayCast(new SVector3(x, y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.All, out RayHit h);
                MainConsole.Instance.Output(hit
                    ? $"{LogHeader} ({x:0.0},{y:0.0}) -> HIT z={h.Point.Z:0.000} normal=({h.Normal.X:0.00},{h.Normal.Y:0.00},{h.Normal.Z:0.00})"
                    : $"{LogHeader} ({x:0.0},{y:0.0}) -> miss");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "avatarstatus")
            {
                AvatarStatus();
                return;
            }

            // Console diagnostic (`jolt reloadcheck`): snapshot every physical prim's
            // saved (birth) pos vs where it is NOW, plus terrain/water under it, and classify. Run a few
            // seconds after a region reload to SEE which physical objects were displaced (SANK/FLUNG) and
            // by how much.
            if (cmd.Length >= 2 && cmd[1] == "reloadcheck")
            {
                ReloadCheck();
                return;
            }

            // Console diagnostic (`jolt vehiclestatus`): dump the LIVE vehicle state of every prim so you can CONFIRM a
            // boat is actually TYPE_BOAT + buoyancy=1 + active BEFORE testing reload.
            if (cmd.Length >= 2 && cmd[1] == "vehiclestatus")
            {
                VehicleStatus();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "charframe")
            {
                // Toggle the per-frame avatar trace for a window (default ~20 s at 11 fps). Also enables the
                // [charjump] path trace in JoltCharacter for the same window so a jump attempt is captured.
                int secs = (cmd.Length >= 3 && int.TryParse(cmd[2], out int s)) ? s : 20;
                _charFrameUntil = _stepCount + (long)Math.Ceiling(secs / 0.0908);
                CharJumpTrace = true;   // JoltCharacter reads this to emit its [charjump] path trace
                MainConsole.Instance.Output($"{LogHeader} [charframe]+[charjump] on for ~{secs}s (until step {_charFrameUntil}). Walk/jump now; trace goes to the log at Debug.");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "sitstatus")
            {
                SitStatus();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "sensortest")
            {
                SensorTest();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "raytest")
            {
                RayTest();
                return;
            }

            if (cmd.Length >= 4 && cmd[1] == "heights"
                && float.TryParse(cmd[2], out float hx) && float.TryParse(cmd[3], out float hy))
            {
                // Line up the four heights at one XY so a "box rests at the wrong Z" is unambiguous:
                // (a) what Jolt actually collides at (heightfield raycast), (b) what OpenSim's scene
                // heightmap says, (c) where the dropped box actually is, (d) the water plane. Water and
                // buoyancy are non-colliding, so the box MUST rest on (a); if (a)!=(b) the cook is wrong,
                // if (c)!=(a) the box isn't resting on terrain.
                bool hit = _backend.RayCast(new SVector3(hx, hy, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit rh);
                float sceneH = float.NaN;
                int gx = (int)Math.Round(hx), gy = (int)Math.Round(hy);
                if (_scene?.Heightmap != null && gx >= 0 && gx < _regionSizeX && gy >= 0 && gy < _regionSizeY)
                    sceneH = (float)_scene.Heightmap[gx, gy];
                float water = (float)(_scene?.RegionInfo?.RegionSettings?.WaterHeight ?? 0.0);

                MainConsole.Instance.Output($"{LogHeader} heights at ({hx:0.0},{hy:0.0}):");
                MainConsole.Instance.Output($"  (a) Jolt heightfield raycast : {(hit ? $"HIT z={rh.Point.Z:0.000} (n.z={rh.Normal.Z:0.00})" : "MISS - NO terrain collision here")}");
                MainConsole.Instance.Output($"  (b) OpenSim scene heightmap  : {sceneH:0.000}");
                MainConsole.Instance.Output($"  (d) region water height      : {water:0.000}");
                foreach (DropTrack t in _drops)
                {
                    lock (_prims)
                        if (_prims.TryGetValue(t.LocalId, out JoltPrim jp) && _backend.TryGetBodyState(jp.BodyHandle, out BodyState st))
                            MainConsole.Instance.Output($"  (c) drop {t.Kind} id={t.LocalId} : liveZ={st.Position.Z:0.000} joltActive={(((st.Flags & BodyStateFlags.Active) != 0) ? "Y" : "N")} startZ={t.StartZ:0.00}");
                }
                MainConsole.Instance.Output($"  read: (a)==(b) => cook matches OpenSim; box rest (c) should ~= (a)+halfHeight. (c)~water while (a)!=water => box not on terrain.");
                return;
            }

            MainConsole.Instance.Output("Usage: " + ReadOnlyUsage);
        }

        // Update a tracked drop from a drained BodyState (called in the Simulate drain).
        private void UpdateDropTelemetry(in BodyState bs)
        {
            foreach (DropTrack t in _drops)
            {
                if (t.LocalId != bs.UserData) continue;
                float z = bs.Position.Z;
                t.LastZ = z;
                if (z < t.MinZ) t.MinZ = z;
                t.LastSpeed = bs.LinearVelocity.Length();
                if ((bs.Flags & BodyStateFlags.JustDeactivated) != 0)
                {
                    t.JustDeactivatedCount++;    // must be EXACTLY 1 at rest (the settle update)
                    t.RestZ = z;
                    t.RestStep = _stepCount;
                }
                break;
            }
        }

        // Report each logged-in avatar's CharacterVirtual state - position, IsSupported, ground normal/body,
        // capsule dims - and assert it spawned ON the terrain (supported, not sinking, capsule centre ~
        // terrainZ + StandHalf at the spawn XY), not at NaN or underground. Run it right after login, and
        // again after walking somewhere to confirm position tracks and IsSupported stays true on the flat.
        private void AvatarStatus()
        {
            System.Collections.Generic.List<JoltCharacter> avs;
            lock (_avatars)
                avs = new System.Collections.Generic.List<JoltCharacter>(_avatars.Values);

            if (avs.Count == 0) { MainConsole.Instance.Output($"{LogHeader} no avatars in the physics scene - log in first, then re-run."); return; }

            MainConsole.Instance.Output($"{LogHeader} avatar status ({avs.Count} in scene, step {_stepCount}):");
            foreach (JoltCharacter a in avs)
            {
                Vector3 p = a.Position;
                bool nan = float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z);

                float terrainZ = float.NaN;
                if (!nan && _backend.RayCast(new SVector3(p.X, p.Y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit th))
                    terrainZ = th.Point.Z;
                float expectedCentre = terrainZ + a.StandHalf + a.FeetOffset;
                float dZ = p.Z - expectedCentre;

                string groundBody = a.GroundBody.IsValid ? $"body({a.GroundBody.Value})" : "terrain/none";
                string verdict = nan ? "FAIL: NaN position"
                    : a.Flying ? "flying (gravity off - ground checks N/A)"
                    : (a.IsSupported && !float.IsNaN(dZ) && MathF.Abs(dZ) < 0.5f) ? "PASS: supported, seated on terrain"
                    : !a.IsSupported ? "off: not supported (in the air / falling)"
                    : "OFF: supported but not at terrain height (check dZ)";

                MainConsole.Instance.Output($"  id={a.LocalID,-6} '{a.Name}' pos=({p.X:0.00},{p.Y:0.00},{p.Z:0.000}) speed={a.Velocity.Length():0.000} m/s flying={(a.Flying ? "Y" : "N")}");
                MainConsole.Instance.Output($"        supported={(a.IsSupported ? "Y" : "N")} sliding={(a.IsSliding ? "Y" : "N")} groundNormal=({a.GroundNormal.X:0.00},{a.GroundNormal.Y:0.00},{a.GroundNormal.Z:0.00}) groundBody={groundBody}");
                MainConsole.Instance.Output($"        capsule: halfHeight={a.CapsuleHalfHeight:0.000} radius={a.CapsuleRadius:0.000} standHalf={a.StandHalf:0.000} feetOffset={a.FeetOffset:0.000}");
                MainConsole.Instance.Output($"        terrainZ={terrainZ:0.000} expectedCentreZ={expectedCentre:0.000} dZ={dZ:0.000}  [{verdict}]");
            }
            MainConsole.Instance.Output($"  PASS = supported=Y, not NaN, |dZ|<0.5 (capsule centre ~ terrain + standHalf). After walking: pos tracks, supported stays Y on flat terrain.");
        }

        // `jolt reloadcheck`: after a region reload, print each
        // PHYSICAL prim's DB-saved (birth) pos vs where it is NOW, the terrain/water under it, the drift,
        // and a verdict (OK / SANK / SANK-BELOW-TERRAIN / FLUNG). A physical
        // object that reads e.g. saved z=25.0 -> now z=-40.2 SANK-BELOW-TERRAIN is the silent loss - that
        // now-position is what OpenSim persists back, so it is invisible on the next reload.
        // `jolt vehiclestatus`: live vehicle-state dump - confirms a
        // boat is a working vehicle (TYPE_BOAT, buoyancy=1, active) LIVE, before testing reload.
        private void VehicleStatus()
        {
            System.Collections.Generic.List<JoltPrim> ps;
            lock (_prims)
                ps = new System.Collections.Generic.List<JoltPrim>(_prims.Values);
            var vs = ps.FindAll(p => p.IsVehicle);
            MainConsole.Instance.Output($"{LogHeader} vehiclestatus: {ps.Count} prims, {vs.Count} with a vehicle controller (water={WaterLevel:0.0}):");
            if (vs.Count == 0)
                MainConsole.Instance.Output($"  NO prim has a vehicle. If you set llSetVehicleType and see this, the vehicle did NOT reach physics (script/plumbing) - reset the script to re-run state_entry.");
            foreach (JoltPrim p in vs)
                MainConsole.Instance.Output($"  id={p.LocalID,-6} '{p.Name}' physical={(p.IsPhysicalBody ? "Y" : "N")}  {p.VehicleInfo()}");
            MainConsole.Instance.Output($"  EXPECT for a live boat: type=Boat active=Y buoyancy=1.00 physical=Y. Then it should HOVER at water+0.5, not fall/sink.");
        }

        private void ReloadCheck()
        {
            System.Collections.Generic.List<JoltPrim> ps;
            lock (_prims)
                ps = new System.Collections.Generic.List<JoltPrim>(_prims.Values);

            var phys = ps.FindAll(p => p.IsPhysicalBody);
            MainConsole.Instance.Output($"{LogHeader} reloadcheck: {ps.Count} prims in scene, {phys.Count} PHYSICAL (step {_stepCount}, water={WaterLevel:0.0}):");
            if (phys.Count == 0) { MainConsole.Instance.Output($"  (no physical prims - rez one physical, reload the region, then re-run.)"); return; }

            int displaced = 0;
            foreach (JoltPrim p in phys)
            {
                Vector3 b = p.BirthPos, c = p.CurrentPos;
                float dz = c.Z - b.Z;
                float dh = MathF.Sqrt((c.X - b.X) * (c.X - b.X) + (c.Y - b.Y) * (c.Y - b.Y));
                float terrZ = TerrainHeightAt(c.X, c.Y);
                bool belowTerrain = c.Z < terrZ - 0.5f;
                bool bad = belowTerrain || MathF.Abs(dz) > 2f || dh > 5f;
                if (bad) displaced++;
                string verdict = belowTerrain ? "SANK-BELOW-TERRAIN (hidden)"
                    : dz < -2f ? "SANK"
                    : dh > 5f ? "FLUNG"
                    : "OK (survived at saved pos)";
                MainConsole.Instance.Output(
                    $"  id={p.LocalID,-6} '{p.Name}' vehicle={(p.IsVehicle ? "Y" : "N")} kind={p.ShapeKind}");
                MainConsole.Instance.Output(
                    $"        saved=({b.X:0.0},{b.Y:0.0},{b.Z:0.0}) -> now=({c.X:0.0},{c.Y:0.0},{c.Z:0.0}) dz={dz:0.0} dh={dh:0.0} terrainZ={terrZ:0.0}  [{verdict}]");
            }
            MainConsole.Instance.Output($"  => {displaced}/{phys.Count} physical prims DISPLACED from their saved position. Any 'now' below terrain/at seabed is the silent loss (Return recovers it).");
        }

        // ---------------------------------------------------------------------
        // Sit / unsit. The physics core is the CHARACTER LIFECYCLE: OpenSim SITS by REMOVING the
        // physics actor (ScenePresence.RemoveFromPhysicalScene -> RemoveAvatar -> the CharacterVirtual +
        // its query marker are destroyed) and STANDS by re-adding it (AddToPhysicalScene -> AddAvatar -> a
        // fresh character at the release position). So "suspend" == the character is GONE (no gravity, no
        // ground-detection, no movement integration), and "re-engage" == the walking model rebuilt.
        // A moving seat is ridden via OpenSim scene-graph parenting (the seated avatar's world position
        // tracks the prim), independent of physics. `jolt sitstatus` OBSERVES that transition; the
        // sittest / unsit test commands (JoltTestCommands.cs) DRIVE it.
        // ---------------------------------------------------------------------

        private ScenePresence FirstRootAvatar()
        {
            if (_scene == null) return null;
            foreach (ScenePresence sp in _scene.GetScenePresences())
                if (!sp.IsChildAgent) return sp;
            return null;
        }

        // Report each root avatar's SIT state (parented to a prim) vs its PHYSICS presence (a live
        // JoltCharacter). Invariant: seated => NO character (suspended); walking => character present.
        private void SitStatus()
        {
            if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }

            var byId = new System.Collections.Generic.Dictionary<uint, JoltCharacter>();
            lock (_avatars)
                foreach (JoltCharacter a in _avatars.Values) byId[a.LocalID] = a;

            int roots = 0;
            MainConsole.Instance.Output($"{LogHeader} sit status (step {_stepCount}, {byId.Count} physics character(s) live):");
            foreach (ScenePresence sp in _scene.GetScenePresences())
            {
                if (sp.IsChildAgent) continue;
                roots++;
                bool seated = sp.IsSatOnObject;
                bool hasChar = byId.TryGetValue(sp.LocalId, out JoltCharacter jc);
                Vector3 p = sp.AbsolutePosition;

                string verdict = seated
                    ? (hasChar ? "FAIL: SEATED but a physics character is still alive (suspend did not take)"
                               : "PASS: SEATED -> character removed (no gravity / ground-detection / movement)")
                    : (hasChar ? "PASS: WALKING -> character present (walking model live)"
                               : "note: not seated and no character (not yet physical / mid-transition)");

                MainConsole.Instance.Output($"  id={sp.LocalId,-6} '{sp.Name}' seated={(seated ? "Y" : "N")} parentId={sp.ParentID} pos=({p.X:0.00},{p.Y:0.00},{p.Z:0.000}) hasCharacter={(hasChar ? "Y" : "N")}");
                if (hasChar)
                    MainConsole.Instance.Output($"        character: Z={jc.Position.Z:0.000} supported={(jc.IsSupported ? "Y" : "N")} sliding={(jc.IsSliding ? "Y" : "N")} vZ={jc.Velocity.Z:0.000}");
                MainConsole.Instance.Output($"        [{verdict}]");
            }
            if (roots == 0)
                MainConsole.Instance.Output($"  no root avatars in the region - log in first.");
            else
                MainConsole.Instance.Output($"  SEATED avatars have no physics body, so they CANNOT fall/slide - position is driven by the prim (scene-graph). Stand -> character re-created at release pos.");
        }

        // Avatar query-marker check. NOTE: OpenSim's llSensor is SCENE-GRAPH, not
        // physics - SensorRepeat.doAgentSensor/doObjectSensor iterate the ScenePresence / Entities lists and
        // compute distance/arc directly (and explicitly handle SEATED avatars), so llSensor never touches the
        // engine and finds avatars with or without a marker. The kinematic query-marker matters for the
        // PHYSICS query path (llCastRay / OverlapSphere with the Avatar filter). This console is a
        // LIVE test of that: a physics agent-overlap must find the logged-in avatar's marker, by UserData
        // (avatar presence carries UserData, not a solver BodyId), and be range-correct.
        private void SensorTest()
        {
            ScenePresence sp = FirstRootAvatar();
            if (sp == null) { MainConsole.Instance.Output($"{LogHeader} no logged-in avatar - log in first."); return; }
            Vector3 p = sp.AbsolutePosition;

            MainConsole.Instance.Output($"{LogHeader} avatar query marker - physics agent-query vs the live avatar '{sp.Name}' (LocalId={sp.LocalId}):");
            MainConsole.Instance.Output($"  (OpenSim llSensor is scene-graph and does NOT use this; the marker is what makes llCastRay/overlap agent-queries find an avatar.)");

            var hits = new BodyId[32];
            int nNear = _backend.OverlapSphere(new SVector3(p.X, p.Y, p.Z), 5f, QueryFilter.Avatar, hits);
            bool nearFound = false; uint nearUd = 0;
            for (int i = 0; i < nNear; i++)
                if (_backend.TryGetBodyState(hits[i], out BodyState bs) && bs.UserData == sp.LocalId) { nearFound = true; nearUd = bs.UserData; }

            int nFar = _backend.OverlapSphere(new SVector3(p.X + 100f, p.Y + 100f, p.Z), 5f, QueryFilter.Avatar, hits);
            bool farFound = false;
            for (int i = 0; i < nFar; i++)
                if (_backend.TryGetBodyState(hits[i], out BodyState bs) && bs.UserData == sp.LocalId) farFound = true;

            bool seated = sp.IsSatOnObject;
            MainConsole.Instance.Output($"  NEAR overlap (sphere r=5 at avatar): {nNear} agent-layer hit(s); avatar marker (UserData={sp.LocalId}) found = {(nearFound ? "Y" : "N")}{(nearFound ? $" (resolved id={nearUd})" : "")}");
            MainConsole.Instance.Output($"  FAR  overlap (sphere r=5, +100 m):   {nFar} agent-layer hit(s); avatar marker found = {(farFound ? "Y" : "N")}");
            MainConsole.Instance.Output($"  avatar seated = {(seated ? "Y" : "N")}  (SEATED => the query marker is destroyed with the character, so a PHYSICS agent-query cannot find it; OpenSim's scene-graph llSensor still finds seated avatars.)");

            string verdict = (nearFound && !farFound)
                ? "PASS: the query marker is query-visible LIVE via the physics Avatar filter - avatar found in range, identity by UserData, not found out of range."
                : seated ? "note: avatar is SEATED -> no marker -> physics agent-query can't find it (expected). `jolt unsit` and re-run to see the marker."
                : "FAIL: the physics agent-query did NOT find the avatar marker in range - the query marker is not query-visible live.";
            MainConsole.Instance.Output($"  [{verdict}]");
            MainConsole.Instance.Output($"  llSensor(AGENT) itself: works out-of-box (scene-graph) for WALKING and SEATED avatars - no physics wiring needed. This test validates the marker for the llCastRay/overlap path (see `jolt raytest`).");
        }

        // llCastRay through the real ray path. Casts a ray straight DOWN through the logged-in
        // avatar with the Avatar|Terrain filter and expects, in DISTANCE order: [0] the avatar's query marker
        // (near), [1] the terrain (far). Proves in one shot: llCastRay(AGENT) hits the avatar via the marker
        // (identity by UserData), a terrain hit, and multi-hit distance ordering. This is the SAME RayCastAll
        // path llCastRay takes (Scene.RayCastFiltered -> RaycastWorld -> backend.RayCastAll).
        private void RayTest()
        {
            ScenePresence sp = FirstRootAvatar();
            if (sp == null) { MainConsole.Instance.Output($"{LogHeader} no logged-in avatar - log in first."); return; }
            Vector3 p = sp.AbsolutePosition;

            var origin = new SVector3(p.X, p.Y, p.Z + 3f);          // 3 m above the avatar centre
            var dir = new SVector3(0f, 0f, -1f);
            QueryFilter qf = QueryFilter.Avatar | QueryFilter.Terrain;
            var hits = new RayHit[8];
            int n = _backend.RayCastAll(origin, dir, 200f, qf, hits);

            MainConsole.Instance.Output($"{LogHeader} llCastRay path test - ray DOWN through '{sp.Name}' (filter=Avatar|Terrain), {n} hit(s) in distance order:");
            bool avatarHit = false, terrainHit = false, ordered = true;
            float last = -1f;
            for (int i = 0; i < n; i++)
            {
                string what = hits[i].UserData == sp.LocalId ? "AVATAR-MARKER" : hits[i].UserData == 0 ? "TERRAIN" : $"prim({hits[i].UserData})";
                MainConsole.Instance.Output($"  [{i}] dist={hits[i].Distance:0.000} UserData={hits[i].UserData} => {what} pos=({hits[i].Point.X:0.00},{hits[i].Point.Y:0.00},{hits[i].Point.Z:0.000}) normal=({hits[i].Normal.X:0.00},{hits[i].Normal.Y:0.00},{hits[i].Normal.Z:0.00})");
                if (hits[i].UserData == sp.LocalId) avatarHit = true;
                if (hits[i].UserData == 0) terrainHit = true;
                if (hits[i].Distance < last) ordered = false;
                last = hits[i].Distance;
            }

            bool seated = sp.IsSatOnObject;
            string verdict = (avatarHit && ordered)
                ? "PASS: the physics ray HITS the walking avatar via the query marker (identity by UserData), terrain hit, multi-hit sorted by distance."
                : seated ? "note: SEATED -> the query marker is gone, so this RAW physics ray misses the avatar. That is CORRECT and SL-exact: llCastRay itself STILL hits a seated avatar via OpenSim's AvatarIntersection(skipPhys) fallback (it handles agents WITHOUT a physics body). `jolt unsit` + re-run to see the marker hit."
                : "FAIL: the agent ray did not hit the walking avatar marker.";
            MainConsole.Instance.Output($"  terrainHit={(terrainHit ? "Y" : "N")} avatarHit={(avatarHit ? "Y" : "N")} distanceOrdered={(ordered ? "Y" : "N")}");
            MainConsole.Instance.Output($"  [{verdict}]");
            MainConsole.Instance.Output($"  llCastRay is SL-exact: WALKING avatars come from this physics marker (agent->Avatar filter); SEATED avatars are added by OpenSim's AvatarIntersection(skipPhys) - so each avatar is detected exactly ONCE (no duplicate). RayFilterFlags map: agent->Avatar, physical->Dynamic, nonphysical->Static, land->Terrain, LSLPhantom(phantom|volumedtc)->Sensor.");
        }

        // ---------------------------------------------------------------------
        // PhysicsScene - actor creation (avatars; prim shape cooking and dynamics)
        // ---------------------------------------------------------------------

        // The avatar gets a physics body - a Jolt CharacterVirtual. ScenePresence calls the
        // localID overload (via the feetOffset one); overriding it here means we have the avatar's LocalID
        // up front, so the CharacterVirtual + its query marker carry the right identity. The abstract
        // no-localID overload delegates so any caller of the base contract still works.
        public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
            => CreateAvatar(0, avName, position, velocity, size, 0f, isFlying);

        public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
            => CreateAvatar(localID, avName, position, velocity, size, 0f, isFlying);

        // The overload ScenePresence actually calls carries the avatar's feetOffset - the gap between the
        // capsule centre and the visual feet. Override it (rather than let the base drop it) so the spawn
        // seat can put the FEET on the surface, not the capsule centre.
        public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 size, float feetOffset, bool isFlying)
            => CreateAvatar(localID, avName, position, Vector3.Zero, size, feetOffset, isFlying);

        private PhysicsActor CreateAvatar(uint localID, string avName, Vector3 position, Vector3 velocity, Vector3 size, float feetOffset, bool isFlying)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null)
                return PhysicsActor.Null;

            // Spawn ON the terrain. Read the terrain height at the login XY (see THREAD SAFETY below) and seat
            // the capsule so its FEET rest on the surface, so the avatar does not spawn underground.
            // If no terrain height is available (e.g. login off-region), fall back to the incoming Z.
            //
            // Seat Z (avatar root = capsule centre) = groundZ + StandHalf + feetOffset: OpenSim's avatar
            // root is the body centre, and the visual feet sit StandHalf + feetOffset below it. Omitting
            // feetOffset would sink the avatar by exactly that gap, so the feet would clip INTO terrain.
            float standHalf = JoltCharacter.StandHalfFor(size);
            float groundZ = position.Z - standHalf - feetOffset;
            // THREAD SAFETY: do NOT raycast the Jolt heightfield here. CreateAvatar runs on the
            // LOGIN/TELEPORT thread, so a native query could land inside a running _system.Update
            // and corrupt Jolt's LIFO TempAllocator -> "Freeing in the wrong order" -> std::abort(), which
            // kills the simulator when an avatar teleports into the region. TerrainHeightAt reads the SAME cooked
            // sample field the collision heightfield was built from, but it is a plain managed float[]
            // (bilinear interpolation, no native call), so it is safe from any thread and needs no lock.
            // Heights agree with the collision surface because both come from that one field.
            float terrainZ = TerrainHeightAt(position.X, position.Y);
            if (float.IsFinite(terrainZ))
                groundZ = terrainZ;
            // +1 cm so StickToFloor settles from just above rather than starting in penetration (which would
            // resolve as a shove on frame 1).
            var spawn = new Vector3(position.X, position.Y, groundZ + standHalf + feetOffset + 0.01f);

            var jc = new JoltCharacter(this, backend, localID, avName, spawn, size, feetOffset, isFlying);
            if (velocity != Vector3.Zero)
                jc.SetMomentum(velocity);

            lock (_avatars)
                _avatars[jc.CharacterHandle.Value] = jc;

            m_log.LogInformation($"{LogHeader} avatar '{avName}' id={localID} spawned at ({position.X:0},{position.Y:0}) terrainZ={groundZ:0.00} centreZ={spawn.Z:0.000} standHalf={standHalf:0.000} feetOffset={feetOffset:0.000} flying={isFlying}.");
            return jc;
        }

        public override void RemoveAvatar(PhysicsActor actor)
        {
            if (actor is not JoltCharacter jc)
                return;
            lock (_avatars)
                _avatars.Remove(jc.CharacterHandle.Value);
            jc.Destroy();   // RemoveCharacter also tears down the query marker
            m_log.LogInformation($"{LogHeader} avatar '{jc.Name}' id={jc.LocalID} removed.");
        }

        public override void RemovePrim(PhysicsActor prim)
        {
            if (prim is JoltPrim jp)
            {
                jp.Destroy();
                lock (_prims)
                    _prims.Remove(jp.LocalID);
            }
        }

        // The real OpenSim delivery boundary: SceneObjectPart.AddToPhysics -> (via the base
        // isPhantom/shapetype overloads) -> this. A non-physical, non-phantom prim becomes a STATIC
        // Jolt body; a physical one becomes a dynamic body, created INERT and woken in Simulate.
        // (Pure phantoms never reach here - ApplyPhysics skips them.)
        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                                  Vector3 size, Quaternion rotation, bool isPhysical, uint localid)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null || pbs == null)
                return PhysicsActor.Null;

            // Defence in depth: the cook path is throw-free (CookPrimShape always returns a valid shape -
            // fast-path, mesh/hull, or bbox fallback), but if body creation ever throws we accept-and-ignore
            // so one bad prim can never abort a whole region load. Returns PhysicsActor.Null on failure.
            JoltPrim prim;
            try
            {
                prim = new JoltPrim(this, backend, localid, primName, pbs, position, size, rotation, isPhysical);
            }
            catch (Exception e)
            {
                m_log.LogWarning($"{LogHeader} AddPrimShape failed for '{primName}' (localid {localid}): {e.GetType().Name}: {e.Message}; prim has no physics.");
                return PhysicsActor.Null;
            }
            lock (_prims)
                _prims[localid] = prim;
            return prim;
        }

        // Fixed-shape fast path: an UN-CUT box / sphere / cylinder cooks straight to a
        // Jolt primitive with NO meshmerizer. Classification matches what a real viewer/OAR prim
        // carries (canonical ProfileShape+Extrusion), NOT PrimitiveBaseShape.CreateCylinder() - whose
        // factory emits Square+Curve1 (an SL "tube"), a known OpenSim quirk. Anything else (cut/hollow/
        // twisted, sculpt/mesh, non-uniform sphere/cylinder) goes to the IMesher path - a
        // triangle mesh when static, a convex hull when physical - with a bounding box as the fallback
        // when there is no mesher or the geometry is unusable. `axisCorrection` (System.Numerics) is
        // folded into the body
        // orientation by JoltPrim; `kind` is for the diagnostic read-out.
        internal ShapeId CookPrimShape(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out SQuaternion axisCorrection, out string kind)
        {
            axisCorrection = SQuaternion.Identity;
            float hx = size.X * 0.5f, hy = size.Y * 0.5f, hz = size.Z * 0.5f;

            if (pbs != null && PrimHasNoCuts(pbs))
            {
                byte path = pbs.PathCurve;
                ProfileShape profile = pbs.ProfileShape;

                // BOX: square profile, straight extrusion. Half-extents = size/2.
                if (profile == ProfileShape.Square && path == (byte)Extrusion.Straight)
                {
                    kind = "box";
                    return backend.CreateBoxShape(new SVector3(hx, hy, hz));
                }

                // SPHERE: half-circle profile, curve1 extrusion. Native sphere only when uniform - a
                // non-uniform "sphere" is an ellipsoid and must go through the mesher.
                if (profile == ProfileShape.HalfCircle && path == (byte)Extrusion.Curve1
                    && Approx(size.X, size.Y) && Approx(size.Y, size.Z))
                {
                    kind = "sphere";
                    return backend.CreateSphereShape(hx);
                }

                // CYLINDER: circle profile, straight extrusion. SL cylinders are Z-height; Jolt's
                // CylinderShape axis is Y, so correct +90 deg about X (local Y -> local Z) before the
                // prim's own rotation. Circular cross-section only (X==Y); elliptical -> mesher.
                if (profile == ProfileShape.Circle && path == (byte)Extrusion.Straight
                    && Approx(size.X, size.Y))
                {
                    kind = "cylinder";
                    axisCorrection = SQuaternion.CreateFromAxisAngle(SVector3.UnitX, MathF.PI * 0.5f);
                    return backend.CreateCylinderShape(hz, hx);   // halfHeight=Z/2, radius=X/2
                }
            }

            // Not a basic fast-path shape (cut/hollow/twisted, prism, torus, sculpt, mesh): go through
            // the meshmerizer. The convex-vs-mesh decision lives HERE - our equivalent of
            // BulletSim's BSShapeCollection.CreateGeomMeshOrHull (physical && ShouldUseHulls -> hull;
            // else mesh). Contract: a triangle MeshShape has Volume 0, so a PHYSICAL prim
            // MUST use the convex hull or it would rez with mass 0 - hence physical -> hull here.
            ShapeId cooked = CookMeshShape(backend, pbs, size, isPhysical, out kind);
            if (cooked.IsValid)
                return cooked;

            // Mesher unavailable / returned nothing usable / cook threw: conservative solid bounding box.
            kind = "bbox(fallback)";
            return backend.CreateBoxShape(new SVector3(hx, hy, hz));
        }

        // The IMesher path: PrimitiveBaseShape -> IMesher.CreateMesh -> getVertexListAsFloat /
        // getIndexListAsInt -> CreateMeshShape (non-physical triangle mesh) or CreateConvexHullShape
        // (physical hull). Returns ShapeId.Invalid on any failure so the caller can fall back. Also
        // stashes a characterization of the RAW mesher output (_lastMeshStats) for the diagnostic read-out.
        private ShapeId CookMeshShape(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out string kind)
        {
            kind = "bbox(fallback)";
            if (m_mesher == null)
            {
                m_log.LogWarning($"{LogHeader} no IMesher - cannot cook mesh; bounding-box fallback.");
                return ShapeId.Invalid;
            }

            // ---- Extract geometry. CRITICAL: the Meshmerizer CACHES and SHARES the Mesh object,
            // keyed on GetMeshKey(size, lod), and returns the SAME instance for every identical prim
            // (key ignores isPhysical/convex). getIndexListAsInt()/getVertexListAsFloat() throw
            // NotSupportedException once m_triangles/m_vertices are null, and releaseSourceMeshData()
            // nulls exactly those - so calling it POISONS the cache and makes the NEXT identical prim's
            // extraction throw. Both accessors already return FRESH COPIES, so we own the arrays and must
            // NOT mutate/release the shared mesh (ReleaseMesh is a no-op anyway; the mesher owns eviction).
            // Everything the mesher/extraction can throw is inside ONE guard -> a clean bbox fallback,
            // never a propagating exception that could abort a prim rez or a whole region load.
            SVector3[] points;
            int[] indices;
            try
            {
                // isPhysical:false to the mesher = "do not substitute a bounding box for tiny prims" -
                // we always want the real triangle soup (BulletSim passes false here for the same reason).
                IMesh mesh = m_mesher.CreateMesh("jolt-prim", pbs, size, MeshLod, false, false, false);
                if (mesh == null)
                {
                    // A sculpt whose asset (texture) has not been fetched meshes to null - it needs the
                    // async asset path (the request-asset delegate) first. Bounding box for now.
                    m_log.LogDebug($"{LogHeader} IMesher returned null (unfetched sculpt asset or empty geometry); bounding-box fallback.");
                    return ShapeId.Invalid;
                }

                indices = mesh.getIndexListAsInt();          // fresh copy - do NOT release the shared mesh
                float[] verts = mesh.getVertexListAsFloat(); // fresh copy (flattened x,y,z,...)
                if (verts == null || indices == null || verts.Length < 12 || indices.Length < 3 || (indices.Length % 3) != 0)
                {
                    m_log.LogWarning($"{LogHeader} mesher geometry unusable (verts={verts?.Length ?? 0}, indices={indices?.Length ?? 0}); bounding-box fallback.");
                    return ShapeId.Invalid;
                }

                points = new SVector3[verts.Length / 3];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new SVector3(verts[3 * i], verts[3 * i + 1], verts[3 * i + 2]);
            }
            catch (Exception e)
            {
                m_log.LogWarning($"{LogHeader} mesher geometry extraction threw ({e.GetType().Name}: {e.Message}); bounding-box fallback.");
                return ShapeId.Invalid;
            }

            _lastMeshStats = CharacterizeMesh(points, indices);   // honest read-out of REAL mesher output

            // An out-of-range index would be a native out-of-bounds read in Jolt's Sanitize (the
            // backend rejects it too). Name the counts once and take the bbox fallback.
            if (_lastMeshStats.OutOfRangeIndices > 0)
            {
                m_log.LogWarning($"{LogHeader} mesher output has {_lastMeshStats.OutOfRangeIndices} out-of-range indices (verts={_lastMeshStats.Verts}, tris={_lastMeshStats.Tris}); bounding-box fallback.");
                return ShapeId.Invalid;
            }

            // Cook the Jolt shape. No shape/body exists until one of these RETURNS a handle, so a throw
            // here creates nothing to leak - caller falls back to a full bbox.
            try
            {
                ShapeId shape = isPhysical
                    ? backend.CreateConvexHullShape(points)   // physical: hull (mesh Volume=0 -> mass 0)
                    : backend.CreateMeshShape(points, indices); // non-physical: real triangle mesh
                kind = isPhysical ? "hull(mesher)" : "mesh(mesher)";
                return shape;
            }
            catch (Exception e)
            {
                m_log.LogWarning($"{LogHeader} backend cook of mesher output threw ({e.GetType().Name}: {e.Message}); bounding-box fallback.");
                return ShapeId.Invalid;   // kind stays "bbox(fallback)"
            }
        }

        // Characterize RAW mesher output (counts, bounds, enclosed volume). Duplicate-vertex count uses mm-quantized coords (O(n)); degenerate = topological
        // (shared index) or near-zero area.
        private static MeshStats CharacterizeMesh(SVector3[] points, int[] indices)
        {
            var s = new MeshStats { Verts = points.Length, Tris = indices.Length / 3 };
            var min = new SVector3(float.MaxValue); var max = new SVector3(float.MinValue);
            foreach (var p in points) { min = SVector3.Min(min, p); max = SVector3.Max(max, p); }
            s.Min = min; s.Max = max;

            var seen = new HashSet<(int, int, int)>();
            foreach (var p in points)
                seen.Add(((int)MathF.Round(p.X * 1000f), (int)MathF.Round(p.Y * 1000f), (int)MathF.Round(p.Z * 1000f)));
            s.DuplicateVerts = points.Length - seen.Count;

            double vol6 = 0.0;   // 6x the signed enclosed volume: sum of dot(v0, cross(v1,v2)) over tris
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                bool bad = a < 0 || b < 0 || c < 0 || a >= points.Length || b >= points.Length || c >= points.Length;
                if (bad) { s.OutOfRangeIndices++; continue; }
                if (a == b || b == c || a == c) { s.DegenerateTris++; continue; }
                float area2 = SVector3.Cross(points[b] - points[a], points[c] - points[a]).Length();
                if (area2 < 1e-9f) s.DegenerateTris++;
                vol6 += SVector3.Dot(points[a], SVector3.Cross(points[b], points[c]));
            }
            // For a closed, consistently-wound mesh (prim mesher output) this is the exact enclosed volume,
            // which equals the convex-hull volume for a convex shape (prism) - i.e. the physical hull mass basis.
            s.Volume = (float)(Math.Abs(vol6) / 6.0);
            return s;
        }

        // BulletSim's cut test, verbatim: an un-cut basic shape has no profile/path cut, hollow, twist,
        // taper, non-100 path scale, or shear. (PathScaleX/Y are stored as 100 = "1.0".)
        private static bool PrimHasNoCuts(PrimitiveBaseShape p) =>
            p.ProfileBegin == 0 && p.ProfileEnd == 0 && p.ProfileHollow == 0 &&
            p.PathTwist == 0 && p.PathTwistBegin == 0 && p.PathBegin == 0 && p.PathEnd == 0 &&
            p.PathTaperX == 0 && p.PathTaperY == 0 && p.PathScaleX == 100 && p.PathScaleY == 100 &&
            p.PathShearX == 0 && p.PathShearY == 0;

        private static bool Approx(float a, float b) =>
            Math.Abs(a - b) <= 1e-4f * Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));

        // ---------------------------------------------------------------------
        // Query wiring: this is the path a SCRIPT llCastRay takes.
        // llCastRay -> Scene.RayCastFiltered -> PhysicsScene.RaycastWorld (here) -> backend.RayCast.
        // Returning true from SupportsRaycastWorldFiltered flips llCastRay onto the physics engine
        // instead of OpenSim's own geometry intersection, so a script ray genuinely tests Jolt's
        // shapes. (The rest of the query family - RaycastActor, Sphere/BoxProbe - is not overridden here.)
        // ---------------------------------------------------------------------

        // llCastRay runs on SCRIPT threads, so routing it here issues a native Jolt NarrowPhaseQuery off
        // the heartbeat thread, concurrent with _system.Update. That is SAFE: the backend's RayCast/
        // RayCastAll take _simLock, the same gate that wraps the whole Step (Update + ExtendedUpdate), so a
        // script raycast and the physics step can never be inside Jolt's non-thread-safe LIFO TempAllocator
        // at once. The lock, not disabling the feature, is what makes this safe. Returning true keeps llCastRay
        // testing Jolt's real cooked shapes rather than falling back to OpenSim's own geometry intersection.
        public override bool SupportsRaycastWorldFiltered() => true;

        // TWO llCastRay entry points route here, and BOTH must be overridden or llCastRay returns 0:
        //  - the 5-arg (RayFilterFlags) overload is what OpenSim's XEngine/YEngine LSL_Api.llCastRay calls
        //    (it maps RC_* -> RayFilterFlags, then we -> QueryFilter);
        //  - the 4-arg (no filter) overload is what a Halcyon-derived llCastRay calls - it does the
        //    reject-physical/agent/land TYPE filtering on the returned list itself, so we hand it ALL solid
        //    layers + avatars (QueryFilter.Default = Terrain|Static|Dynamic|Avatar; phantom/Sensor excluded,
        //    which that caller neither requests nor filters). Without this override every such cast falls
        //    through to the base (empty list) = 0 hits.
        //
        // What a cast costs is bounded (the backend's RayCastLimited, [Jolt] RayCastBudgetMs / RayCastMaxTestedHits /
        // RayCastMaxHits). A cast the region has no time left for, or that ran out of it, fails as Second Life documents:
        // "RCERR_CAST_TIME_EXCEEDED -3: The raycast failed because the parcel or agent has exceeded the maximum time
        // allowed for raycasting. This resource pool is continually replenished, so waiting a few frames and retrying is
        // likely to succeed." (llCastRay, wiki.secondlife.com). The 4-argument caller (Phlox) turns an exception from
        // this call into that status code, so this overload throws RayCastTimeExceededException. The 5-argument caller
        // (OpenSim's LSL_Api.llCastRay, and the simulator's own placement and camera casts) has no way to be told,
        // so it gets no hits.
        public override List<ContactResult> RaycastWorld(Vector3 position, Vector3 direction, float length, int Count)
        {
            List<ContactResult> results = CastAll(position, direction, length, Count, QueryFilter.Default, out RayCastStatus status);
            if (status != RayCastStatus.Ok)
                throw new RayCastTimeExceededException(status);
            return results;
        }

        public override object RaycastWorld(Vector3 position, Vector3 direction, float length, int Count, RayFilterFlags filter)
            => CastAll(position, direction, length, Count, ToQueryFilter(filter), out _);

        // Second Life: "The random failures seem to happen if the ray begins or ends more than 8 meters outside of
        // current region bounds" (llCastRay). A ray is cut to the region widened by this much on each side, and to the
        // heights a region holds objects at, so a script cannot hand the engine a ray kilometres long.
        internal const float RayClipMargin = 8f;
        internal const float RayClipMinZ = -128f;
        internal const float RayClipMaxZ = 10000f;

        internal List<ContactResult> CastAll(Vector3 position, Vector3 direction, float length, int Count, QueryFilter qf, out RayCastStatus status)
        {
            status = RayCastStatus.Ok;
            var results = new List<ContactResult>();
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null || qf == QueryFilter.None || !(length > 0f) || !float.IsFinite(length) || !position.IsFinite() || !direction.IsFinite())
                return results;

            Vector3 dn = direction;
            dn.Normalize();
            if (!dn.IsFinite() || dn == Vector3.Zero)
                return results;
            if (!ClipRay(position, dn, length, out float from, out float to))
                return results;
            Vector3 start = position + dn * from;
            var origin = new SVector3(start.X, start.Y, start.Z);
            var dir = new SVector3(dn.X, dn.Y, dn.Z);

            int want = Math.Clamp(Count, 1, _joltConfig.RayCastMaxHits);
            var hits = new RayHit[want];
            int n = backend.RayCastLimited(origin, dir, to - from, qf, hits, out status);
            for (int i = 0; i < n; i++)
            {
                var cr = new ContactResult
                {
                    ConsumerID = hits[i].UserData,           // SceneObjectPart.LocalId (0 = terrain)
                    Pos = new Vector3(hits[i].Point.X, hits[i].Point.Y, hits[i].Point.Z),
                    Normal = new Vector3(hits[i].Normal.X, hits[i].Normal.Y, hits[i].Normal.Z),
                    Depth = from + hits[i].Distance,         // from the caller's start, not the clipped one
                };
                results.Add(cr);
            }
            return results;
        }

        /// <summary>The backend's counters (default when the region has no backend): the harness and tests read them.</summary>
        internal PhysicsCapacityStats CapacityStats() => _backend?.GetCapacityStats() ?? default;

        /// <summary>The region's backend (null before it is initialised or after teardown): tests compare against it.</summary>
        internal IPhysicsBackend Backend => _backend;

        // The part [from, to] of the ray (start p, unit direction d, length len) inside the region's box widened by
        // RayClipMargin, between RayClipMinZ and RayClipMaxZ. False when no part of it is.
        internal bool ClipRay(Vector3 p, Vector3 d, float len, out float from, out float to)
        {
            from = 0f;
            to = len;
            return Slab(p.X, d.X, -RayClipMargin, _regionSizeX + RayClipMargin, ref from, ref to)
                && Slab(p.Y, d.Y, -RayClipMargin, _regionSizeY + RayClipMargin, ref from, ref to)
                && Slab(p.Z, d.Z, RayClipMinZ, RayClipMaxZ, ref from, ref to);
        }

        private static bool Slab(float p, float d, float min, float max, ref float from, ref float to)
        {
            if (MathF.Abs(d) < 1e-9f)
                return p >= min && p <= max;
            float a = (min - p) / d, b = (max - p) / d;
            if (a > b) (a, b) = (b, a);
            from = MathF.Max(from, a);
            to = MathF.Min(to, b);
            return from < to;
        }

        // llCastRay's reject-type flags -> our layer filter. water has no body; phantom/volumedetect
        // map to the Sensor layer.
        private static QueryFilter ToQueryFilter(RayFilterFlags f)
        {
            QueryFilter q = QueryFilter.None;
            if ((f & RayFilterFlags.land) != 0) q |= QueryFilter.Terrain;
            if ((f & RayFilterFlags.nonphysical) != 0) q |= QueryFilter.Static;
            if ((f & RayFilterFlags.physical) != 0) q |= QueryFilter.Dynamic;
            if ((f & RayFilterFlags.agent) != 0) q |= QueryFilter.Avatar;
            if ((f & (RayFilterFlags.phantom | RayFilterFlags.volumedtc)) != 0) q |= QueryFilter.Sensor;
            return q;
        }


        // Linkset roots whose compound needs a (re)build, coalesced and applied once per frame in
        // Simulate. link()/unlink() add to this instead of rebuilding inline (which can hang a region load).
        private readonly HashSet<JoltPrim> _dirtyLinksets = new HashSet<JoltPrim>();

        // ---------------------------------------------------------------------
        // Vehicles: active vehicle prims, driven per-frame from Simulate BEFORE the physics
        // step (the Jolt equivalent of BulletSim's BeforeStep event). JoltPrim registers itself when
        // its controller's type is set and unregisters on TYPE_NONE/destroy.
        // ---------------------------------------------------------------------
        private readonly HashSet<JoltPrim> _vehicles = new HashSet<JoltPrim>();

        internal void RegisterVehicle(JoltPrim prim)
        {
            lock (_vehicles) _vehicles.Add(prim);
        }

        internal void UnregisterVehicle(JoltPrim prim)
        {
            lock (_vehicles) _vehicles.Remove(prim);
        }

        private void StepVehicles(float timeStep)
        {
            JoltPrim[] vehicles;
            lock (_vehicles)
            {
                if (_vehicles.Count == 0) return;
                vehicles = new JoltPrim[_vehicles.Count];
                _vehicles.CopyTo(vehicles);
            }
            foreach (JoltPrim v in vehicles)
            {
                try { v.StepVehicle(timeStep); }
                catch (Exception e)
                {
                    // Never let one vehicle's math wedge the heartbeat.
                    m_log.LogError($"{LogHeader} vehicle step EXCEPTION for prim {v.LocalID}: {e}");
                }
            }
        }

        internal void MarkLinksetDirty(JoltPrim root)
        {
            lock (_dirtyLinksets) _dirtyLinksets.Add(root);
        }

        // Wake the physical bodies created inert since the last Simulate (deferred activation - the BulletSim
        // configure-before-step barrier). Runs on the step thread so ActivateBody reliably reaches the active
        // set. A body only created inert becomes a normal live body here; a vehicle wakes with gravity already
        // cancelled, so it never free-falls. Errors are swallowed so one bad body can't wedge the heartbeat.
        private void DrainPendingActivation()
        {
            JoltPrim[] pend;
            lock (_pendingActivation)
            {
                if (_pendingActivation.Count == 0) return;
                pend = _pendingActivation.ToArray();
                _pendingActivation.Clear();
            }
            foreach (JoltPrim p in pend)
            {
                try { p.ActivatePending(); }
                catch (Exception e) { m_log.LogError($"{LogHeader} pending-activation EXCEPTION for prim {p.LocalID}: {e}"); }
            }
        }

        private void DrainDirtyLinksets()
        {
            // WELD AT LOAD: rebuild dirty linkset roots at the TOP of Simulate, BEFORE StepOnce - so a
            // persisted linkset's child parts are welded into the compound (their individual bodies removed)
            // BEFORE they ever step. This matches BulletSim's model (one compound body from the start); the
            // children never exist as separate physics-active overlapping bodies that penetrate + fling.
            JoltPrim[] dirty;
            lock (_dirtyLinksets)
            {
                if (_dirtyLinksets.Count == 0) return;
                dirty = new JoltPrim[_dirtyLinksets.Count];
                _dirtyLinksets.CopyTo(dirty);
                _dirtyLinksets.Clear();
            }
            foreach (JoltPrim root in dirty)
                root.RebuildCompoundNow();   // re-entrancy-, destroyed-, and exception-guarded internally
        }

        public override float Simulate(float timeStep)
        {
            // Read once: a teardown on another thread nulls _backend, and may do so between a check and a second read.
            IPhysicsBackend backend = _backend;
            if (backend == null)
                return 1f;
            NoteHeartbeatGap();
            backend.BeginRayCastBudget();   // [Jolt] RayCastBudgetMs is per heartbeat, at every PhysicsStepRate
            if (_substeps != null)
                return SimulateSubsteps(timeStep);

            // (Re)build changed linkset compounds ONCE per frame, here on the step thread before
            // the step. link()/unlink() only mark the root dirty (they do not rebuild inline); this
            // coalesces a whole linkset's worth of child-links into a single rebuild - rebuilding on every
            // child's link() churns the root and can hang the region load of a persisted physical linkset.
            DrainDirtyLinksets();

            // Mirrors BulletSim's taint-deferred creation: activate physical bodies that were
            // created INERT (asleep) now, AFTER the linkset weld and any load-time property/vehicle setup have
            // completed, and BEFORE StepVehicles/StepOnce. So a body enters the engine step already fully
            // configured - a reloaded vehicle wakes with its gravity already cancelled (StepVehicles asserts it
            // just below, before StepOnce), so it can NEVER free-fall during load or the reload stall. Mirrors
            // BulletSim draining ALL taints before PE.PhysicsStep().
            DrainPendingActivation();

            // Run each active vehicle's controller BEFORE the physics step, so its
            // velocity changes/forces/torques are consumed by THIS step (BulletSim's BeforeStep model).
            LastTimeStep = timeStep;
            StepVehicles(timeStep);

            // ONE backend Step per frame at OpenSim's ~11 fps cadence (Scene.FrameTime 0.0909 s). The
            // character is stepped exactly once per frame, which keeps avatar motion smooth.
            // Fast-body tunnelling through the terrain is handled NOT by sub-
            // stepping the whole Simulate (that 6x's the character/drain/terse pipeline and shows up as
            // avatar bounce/jitter and a performance cost), but by CollisionSteps=6 set at Initialize: Jolt sub-
            // steps the RIGID-BODY solver INSIDE _system.Update without re-running the character step, so a
            // dropped prim integrates in solver sub-slices and rests, while the avatar stays at 1 step/frame.
            StepOnce(timeStep, backend);

            TraceCharFrame(backend);
            return 1f;
        }

        // [charframe] live trace (toggle: `jolt charframe`): per-frame avatar Z / support / vertical
        // velocity, so avatar bounce or sinking shows up in the numbers.
        private void TraceCharFrame(IPhysicsBackend backend)
        {
            if (_stepCount <= _charFrameUntil)
            {
                List<JoltCharacter> avs;
                lock (_avatars) avs = new List<JoltCharacter>(_avatars.Values);
                foreach (JoltCharacter a in avs)
                {
                    // Identify the ground body by its UserData: 0 = TERRAIN (expected), == the avatar's own
                    // LocalID = its query marker (a bug), any other id = a prim/box. The terrain body
                    // IS a registered body, so "has a ground body" alone does NOT mean the marker.
                    string ground = "none";
                    if (a.GroundBody.IsValid && backend.TryGetBodyState(a.GroundBody, out BodyState gb))
                        ground = gb.UserData == 0 ? "TERRAIN"
                               : gb.UserData == a.LocalID ? $"OWN-MARKER({gb.UserData})"
                               : $"prim({gb.UserData})";
                    // Terrain surface directly under the avatar (raycast) vs where the feet actually are -
                    // negative & shrinking feetAboveTerrain = sinking THROUGH the collision surface.
                    Vector3 p = a.Position;
                    float terrZ = float.NaN;
                    if (backend.RayCast(new SVector3(p.X, p.Y, p.Z + 50f), new SVector3(0f, 0f, -1f), 300f, QueryFilter.Terrain, out RayHit th))
                        terrZ = th.Point.Z;
                    // FIXED-POINT terrain probe at the region centre - INDEPENDENT of the avatar's position.
                    // If this descends while the avatar stands still, the terrain surface is genuinely moving
                    // (terrain bug). If it holds constant but the avatar's own terrainZ descends, the avatar is
                    // drifting horizontally onto lower ground (a slide, not a sinking terrain).
                    float fixZ = float.NaN;
                    float cx = _regionSizeX * 0.5f, cy = _regionSizeY * 0.5f;
                    if (backend.RayCast(new SVector3(cx, cy, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit fh))
                        fixZ = fh.Point.Z;
                    m_log.LogDebug($"{LogHeader} [charframe] step={_stepCount} id={a.LocalID} XY=({p.X:0.00},{p.Y:0.00}) Z={p.Z:0.000} " +
                                   $"sup={(a.IsSupported ? "Y" : "N")} sliding={(a.IsSliding ? "Y" : "N")} vZ={a.Velocity.Z:0.000} flying={(a.Flying ? "Y" : "N")} ground={ground} " +
                                   $"terrainZ@avatar={terrZ:0.000} terrainZ@centre({cx:0},{cy:0})={fixZ:0.000} feetAboveTerrain={p.Z - a.StandHalf - a.FeetOffset - terrZ:0.000}");
                }
            }
            else if (CharJumpTrace)
                CharJumpTrace = false;   // window elapsed -> stop the [charjump] trace too
        }

        // One backend Step + drain (bodies -> prims, characters -> avatars) + the [dropframe] diagnostic. `backend` is the
        // one Simulate read: a teardown on another thread may have nulled _backend since, and it disposes the backend,
        // whose Step then returns nothing.
        private void StepOnce(float timeStep, IPhysicsBackend backend)
        {
            // Step, then DRAIN: the backend fills _bodyBuf with a BodyState per ACTIVE body (moving prims)
            // plus one final JustDeactivated state per body that slept this step. For each, push the new
            // transform/velocity into the matching actor (by UserData = LocalID) and fire its terse update
            // so the viewer sees motion; the JustDeactivated state is the settle update that stops a rested
            // object drifting. Sleeping bodies aren't reported, so idle prims cost nothing.
            StepResult r = backend.Step(timeStep, _bodyBuf, _charBuf, _contactBuf);
            _stepCount++;
            var timing = new HeartbeatTiming();
            timing.Add(in r);
            ReportStep(backend, in r, timeStep, r.ContactCount, r.ContactBufferOverflowed, r.PhysicsMilliseconds, mergeSubsteps: false, in timing);
        }

        // The time between two heartbeats' physics calls (Simulate), for the metrics' longest gap. A heartbeat that runs
        // no physics step leaves its gap pending; the next heartbeat that reports keeps the longer one.
        private long _lastSimulateTimestamp;
        private long _pendingGapTicks;
        private void NoteHeartbeatGap()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastSimulateTimestamp != 0 && now - _lastSimulateTimestamp > _pendingGapTicks)
                _pendingGapTicks = now - _lastSimulateTimestamp;
            _lastSimulateTimestamp = now;
        }

        // Everything after the backend step: metrics, capacity, the body / character drains and the collision
        // dispatch. `r` is the step whose body and character states are reported; the contact count, overflow,
        // physics time and timing cover every backend step of this heartbeat (one, unless substepping).
        private void ReportStep(IPhysicsBackend backend, in StepResult r, float timeStep, int contactCount, bool contactsOverflowed, float physicsMs, bool mergeSubsteps,
                                in HeartbeatTiming timing)
        {
            _lastActiveBodyCount = r.ActiveBodyCount;
            double gapMs = _pendingGapTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _pendingGapTicks = 0;
            JoltMetrics.RecordStep(RegionName, physicsMs, r.ActiveBodyCount, in timing, gapMs, timeStep * 1000.0);   // step-time instrumentation (`jolt metrics`)

            // Windowed per-frame diagnostic (set by a drop): is Step advancing with a REAL dt, is the
            // just-dropped body in our active set, and is its Z actually changing? It tells apart why a
            // drop might hang - dt=0 => idle-step stall; active=1 but
            // liveZ frozen => body active-but-not-integrated (deeper); active=0 => activation lost.
            if (_stepCount <= _logStepsUntil && _drops.Count > 0)
            {
                DropTrack td = _drops[_drops.Count - 1];
                float lz = float.NaN, vz = float.NaN; bool ja = false;
                lock (_prims)
                    if (_prims.TryGetValue(td.LocalId, out JoltPrim jd) && backend.TryGetBodyState(jd.BodyHandle, out BodyState sd))
                    { lz = sd.Position.Z; vz = sd.LinearVelocity.Z; ja = (sd.Flags & BodyStateFlags.Active) != 0; }
                m_log.LogDebug($"{LogHeader} [dropframe] step={_stepCount} dt={timeStep:0.0000} active={r.ActiveBodyCount} updates={r.BodyUpdateCount} box(id={td.LocalId}) liveZ={lz:0.000} vZ={vz:0.000} joltActive={ja}");
            }

            if (r.BodyBufferOverflowed) _bodyOverflowFrames++;
            if (r.CharacterUpdateCount >= _charBuf.Length) _charFullFrames++;
            if (contactsOverflowed) _contactOverflowFrames++;
            CheckCapacity(backend, timeStep);

            int n = r.BodyUpdateCount;
            for (int i = 0; i < n; i++)
            {
                BodyState bs = _bodyBuf[i];
                JoltPrim p;
                lock (_prims)
                    _prims.TryGetValue(bs.UserData, out p);
                p?.ApplyStepState(in bs);
                if (_drops.Count > 0)
                    UpdateDropTelemetry(in bs);
            }

            // Character drain: the avatar equivalent of the body drain above. The backend stepped
            // every CharacterVirtual BEFORE _system.Update and filled _charBuf with each one's post-step
            // position + ground state; push it into the matching JoltCharacter (by CharacterId handle) so
            // ScenePresence sees the new transform and the viewer gets a smooth per-frame terse update.
            int cn = r.CharacterUpdateCount;
            for (int i = 0; i < cn; i++)
            {
                CharacterState cs = _charBuf[i];
                JoltCharacter a;
                lock (_avatars)
                    _avatars.TryGetValue(cs.Character.Value, out a);
                a?.ApplyCharacterState(in cs);
            }

            DispatchContacts(_contactBuf, contactCount, contactsOverflowed, mergeSubsteps);

            // Grow any buffer this frame filled, now that it has been read. Overflow does not lose
            // updates (the backend carries them over), so the warning is rate-limited like the capacity one.
            bool charFull = r.CharacterUpdateCount >= _charBuf.Length;
            if (r.BodyBufferOverflowed || charFull || contactsOverflowed)
            {
                string grew = "";
                if (r.BodyBufferOverflowed) grew += GrowBuffer(ref _bodyBuf, _bodyBufMax, "body");
                if (charFull) grew += GrowBuffer(ref _charBuf, _charBufMax, "character");
                if (contactsOverflowed) grew += GrowBuffer(ref _contactBuf, _contactBufMax, "contact");
                long now = System.DateTime.UtcNow.Ticks;
                if (_overflowLastWarnTicks == 0 || now - _overflowLastWarnTicks >= _capacityLogIntervalTicks)
                {
                    _overflowLastWarnTicks = now;
                    m_log.LogWarning($"{LogHeader} {RegionName}: step buffer overflow (bodies {r.BodyUpdateCount}/{r.ActiveBodyCount} active, contacts overflowed={contactsOverflowed});{grew} updates carry over to the next step.");
                }
            }
        }

        // [Jolt] PhysicsStepRate on: one heartbeat runs several backend steps of exactly 1 / rate seconds (as many
        // as SubstepAccumulator hands out) inside this call, on the heartbeat thread.
        //   Once per heartbeat, first: the linkset and activation drains (as on the single-step path).
        //   Every step: the vehicle controllers (with that step's dt), then the backend step, which steps every
        //   avatar before the solver; queued body changes and forces from other threads land before the next step.
        //   Once per heartbeat, last: the body and avatar reports (from the last step) and the collision dispatch.
        // The steps before the last hand the backend empty body and avatar buffers, so it reports nothing for them:
        // a body that sleeps in an earlier step keeps its settle state queued in the backend (it never drops one that
        // does not fit) and the last step reports it, unless the body woke again. Contacts from every step of the
        // heartbeat are drained into one buffer and dispatched once, a touching pair counted once (see
        // DispatchContacts). Each step takes and releases the job pool's gate and _simLock itself, as one step does.
        private float SimulateSubsteps(float timeStep)
        {
            DrainDirtyLinksets();
            DrainPendingActivation();

            int n = _substeps.Advance(timeStep);
            float dt = _substeps.StepSeconds;
            LastTimeStep = dt;   // AddForce: a non-push force acts for one step, so its impulse stays the force given
            PushForceScale = timeStep / dt;
            if (n == 0)
                return 1f;   // nothing stepped: no reports, and no collision_end for contacts that were not re-checked

            // Held for the whole heartbeat: a teardown that lands between two steps disposes the backend (whose steps
            // then return nothing) but cannot null it out from under this loop. A teardown that landed after Simulate
            // checked it and before this line has already nulled it: nothing is stepped.
            IPhysicsBackend backend = _backend;
            if (backend == null)
                return 1f;
            if (Interlocked.Read(ref _physicsClockTicks) == 0)
                Interlocked.Exchange(ref _physicsClockTicks, (VehicleClock?.Invoke() ?? DateTime.Now).Ticks);
            long stepTicks = (long)Math.Round(dt * (double)TimeSpan.TicksPerSecond);

            StepResult r = default;
            int contactCount = 0;
            bool contactsOverflowed = false;
            float physicsMs = 0f;
            var timing = new HeartbeatTiming();
            for (int k = 0; k < n; k++)
            {
                bool last = k == n - 1;
                Interlocked.Add(ref _physicsClockTicks, stepTicks);
                StepVehicles(dt);
                r = backend.Step(dt,
                    last ? _bodyBuf : Span<BodyState>.Empty,
                    last ? _charBuf : Span<CharacterState>.Empty,
                    _contactBuf.AsSpan(contactCount));
                if (k == 0)
                    _stepCount++;   // counts heartbeats, as on the single-step path
                contactCount += r.ContactCount;
                contactsOverflowed |= r.ContactBufferOverflowed;
                physicsMs += r.PhysicsMilliseconds;
                timing.Add(in r);
            }
            ReportStep(backend, in r, timeStep, contactCount, contactsOverflowed, physicsMs, mergeSubsteps: true, in timing);
            TraceCharFrame(backend);
            return 1f;
        }

        // Double a step buffer up to its cap. Returns a note for the overflow warning.
        private static string GrowBuffer<T>(ref T[] buf, int max, string what)
        {
            if (buf.Length >= max)
                return $" {what} buffer at its max {buf.Length};";
            int size = (int)System.Math.Min((long)max, buf.Length * 2L);
            buf = new T[size];
            return $" {what} buffer grown to {size};";
        }

        // After each Step, warn (at most once per [Jolt] CapacityLogIntervalSeconds per region) when the update
        // reported a capacity error or CreateBody was refused since the last warning, naming the [Jolt] key to raise.
        private void CheckCapacity(IPhysicsBackend backend, float timeStep)
        {
            PhysicsCapacityStats s = backend.GetCapacityStats();
            long now = System.DateTime.UtcNow.Ticks;
            CheckGateWait(in s, now, timeStep);
            if (!_capBaseSet)
            {
                _capBase = s;
                _capBaseTicks = now;
                _capBaseSet = true;
                return;
            }
            bool worse = s.BodyCreateFailures > _capBase.BodyCreateFailures
                || s.BodyPairCacheFullSteps > _capBase.BodyPairCacheFullSteps
                || s.ManifoldCacheFullSteps > _capBase.ManifoldCacheFullSteps
                || s.ContactConstraintsFullSteps > _capBase.ContactConstraintsFullSteps;
            if (!worse)
            {
                _capBase = s;          // nothing to report: keep the window anchored at "now"
                _capBaseTicks = now;
                return;
            }
            if (_capLastWarnTicks != 0 && now - _capLastWarnTicks < _capacityLogIntervalTicks)
                return;                // quiet period: let the counts accumulate into the next line
            string msg = CapacityReport.Warning(RegionName,
                _capBase, s, System.Math.Max(1.0, (now - _capBaseTicks) / (double)System.TimeSpan.TicksPerSecond));
            if (msg != null)
                m_log.LogWarning(msg);
            _capLastWarnTicks = now;
            _capBase = s;
            _capBaseTicks = now;
        }
        private long _capLastWarnTicks;

        // Once per capacity-log interval, compare this region's time waiting at its job pool's gate with
        // its frame time over that interval; more than 20% means too few pools for this many busy regions.
        private long _gateWindowStartTicks;
        private double _gateWindowStartWaitMs;
        private double _gateWindowFrameMs;
        private void CheckGateWait(in PhysicsCapacityStats s, long now, float timeStep)
        {
            _gateWindowFrameMs += timeStep * 1000.0;
            if (_gateWindowStartTicks == 0)
            {
                _gateWindowStartTicks = now;
                _gateWindowStartWaitMs = s.UpdateGateWaitMsTotal;
                _gateWindowFrameMs = 0;
                return;
            }
            if (now - _gateWindowStartTicks < _capacityLogIntervalTicks)
                return;
            string msg = CapacityReport.GateWarning(RegionName, s.UpdateGateWaitMsTotal - _gateWindowStartWaitMs, _gateWindowFrameMs, s.PoolIndex, s.JobPools);
            if (msg != null)
                m_log.LogWarning(msg);
            _gateWindowStartTicks = now;
            _gateWindowStartWaitMs = s.UpdateGateWaitMsTotal;
            _gateWindowFrameMs = 0;
        }

        private void JoltCapacity()
        {
            PhysicsCapacityStats s = CapacityStats();   // default after teardown
            MainConsole.Instance.Output(CapacityReport.Render(RegionName, s,
                _bodyBuf.Length, _bodyOverflowFrames, _charBuf.Length, _charFullFrames, _contactBuf.Length, _contactOverflowFrames, _substeps,
                JoltMetrics.LastIntervalOf(RegionName)));
        }

        // Collision dispatch: turn this frame's ContactReports into OpenSim collision events. Each
        // subscribed prim gets ONE CollisionEventUpdate listing the LocalIDs it is touching this frame
        // (terrain = 0); OpenSim's SceneObjectPart.PhysicsCollision diffs that against last frame to fire
        // collision_start / collision / collision_end, and llDetected* off the collider list. Runs on the
        // heartbeat thread right after the drain (same thread SOP.PhysicsCollision expects).
        //
        // Contacts carry Begin (first touch) + Persist (each frame while touching, gated on a subscribed
        // body) + End (separation). The "currently touching" set OpenSim wants = Begin|Persist this frame;
        // End is implicit (a pair that drops out of the set). A prim that touched last frame but not now
        // still needs one (empty) update so collision_end can fire - _collidedLastFrame drives that flush.
        // Per-child: each contact names the STRUCK part on each side (ChildUserData - the
        // compound child hit, resolved from the contact sub-shape), so a linkset reports against the specific
        // child and llDetectedLinkNumber returns that child's link (see the AddCollider block below).
        //
        // With several backend steps in one heartbeat (mergeSubsteps), the buffer holds every step's reports. A
        // touching pair reports in each step, so only its first Begin/Persist report of the heartbeat counts: the
        // collision score and the collider set are what one step per heartbeat gives. A contact that begins and
        // ends inside one heartbeat has a Begin report, so it is touching for that heartbeat (collision_start) and
        // absent from the next (collision_end); one that spans heartbeats reports in each.
        internal void DispatchContacts(ContactReport[] contacts, int contactCount, bool contactsOverflowed, bool mergeSubsteps)
        {
            // Resolve every prim this frame can touch - both sides of each contact, plus last frame's colliders
            // (the collision_end candidates) - under ONE lock(_prims), into a reused per-frame map.
            _frameIds.Clear();
            for (int i = 0; i < contactCount; i++)
            {
                ref ContactReport c = ref contacts[i];
                if (c.Phase == ContactPhase.End)
                    continue;
                _frameIds.Add(c.ChildUserDataA);
                _frameIds.Add(c.ChildUserDataB);
            }
            foreach (uint id in _collisions.CollidedLastFrame)
                _frameIds.Add(id);
            foreach (uint id in _collisions.Scores.Keys)   // last frame's scored prims (zeroed if gone)
                _frameIds.Add(id);
            _framePrims.Clear();
            lock (_prims)
            {
                foreach (uint id in _frameIds)
                    if (_prims.TryGetValue(id, out JoltPrim p))
                        _framePrims[id] = p;
            }

            _collisions.BeginFrame();
            if (mergeSubsteps)
                _framePairs.Clear();
            for (int i = 0; i < contactCount; i++)
            {
                ref ContactReport c = ref contacts[i];
                if (c.Phase == ContactPhase.End)
                    continue;   // OpenSim derives "ended" from absence in the current set
                if (mergeSubsteps && !_framePairs.Add(PairKey(c.ChildUserDataA, c.ChildUserDataB)))
                    continue;   // this pair already reported in an earlier step of this heartbeat

                // Every Begin/Persist report scores both struck parts, subscribed or not.
                _collisions.CountContact(c.ChildUserDataA);
                _collisions.CountContact(c.ChildUserDataB);

                // Per-child identity: dispatch to the STRUCK part on each side
                // (ChildUserData - the compound child hit, or the body itself for a single prim), and name
                // the OTHER side's struck part as the collider. Delivering to child N's PhysicsActor makes
                // OpenSim run child N's PhysicsCollision, so llDetectedLinkNumber == N (and it propagates to
                // the root script - every linkset part is subscribed via the root's aggregated events).
                // Jolt's normal points A -> B; give each side the surface normal pointing back at it.
                // ContactReport carries System.Numerics vectors (SVector3); OpenSim's ContactPoint is OMV.
                Vector3 pt = new Vector3(c.Point.X, c.Point.Y, c.Point.Z);
                if (IsSubscribedPrim(c.ChildUserDataA))
                    _collisions.AddCollider(c.ChildUserDataA, c.ChildUserDataB, new ContactPoint(pt, new Vector3(c.Normal.X, c.Normal.Y, c.Normal.Z), 0f));
                if (IsSubscribedPrim(c.ChildUserDataB))
                    _collisions.AddCollider(c.ChildUserDataB, c.ChildUserDataA, new ContactPoint(pt, new Vector3(-c.Normal.X, -c.Normal.Y, -c.Normal.Z), 0f));
            }

            // Deliver this frame's sets (outside the lock).
            foreach (KeyValuePair<uint, CollisionEventUpdate> kv in _collisions.Current)
                if (_framePrims.TryGetValue(kv.Key, out JoltPrim p))
                    p.SendCollisionUpdate(kv.Value);

            // Flush an EMPTY update to prims that collided last frame but not now (fires collision_end). On a
            // frame whose contact buffer overflowed the tracker ends nobody: absence is not proof.
            foreach (uint id in _collisions.EndFrame(contactsOverflowed))
                if (_framePrims.TryGetValue(id, out JoltPrim p) && p.SubscribedEvents())
                    p.SendCollisionUpdate(new CollisionEventUpdate());

            // Publish this frame's scores; a prim scored last frame and not now drops back to 0.
            foreach (uint id in _collisions.PreviouslyScored)
                if (!_collisions.Scores.ContainsKey(id) && _framePrims.TryGetValue(id, out JoltPrim p))
                    p.CollisionScore = 0f;
            foreach (KeyValuePair<uint, int> kv in _collisions.Scores)
                if (_framePrims.TryGetValue(kv.Key, out JoltPrim p))
                    p.CollisionScore = kv.Value;
        }

        private readonly HashSet<ulong> _framePairs = new HashSet<ulong>();
        private static ulong PairKey(uint a, uint b)
            => a <= b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

        // A LocalID resolves (this frame) to a prim that currently has a collision-script subscription (dispatch
        // is prim-scoped; ScenePresence collisions with an avatar as the subscriber are not dispatched here).
        private bool IsSubscribedPrim(uint localID)
            => _framePrims.TryGetValue(localID, out JoltPrim p) && p.SubscribedEvents();

        public override void SetTerrain(float[] heightMap)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null || heightMap == null)
                return;
            int sx = _regionSizeX, sy = _regionSizeY;
            if (sx <= 0 || sy <= 0 || heightMap.Length < sx * sy)
            {
                m_log.LogWarning($"{LogHeader} SetTerrain: heightMap length {heightMap?.Length ?? 0} < {sx}x{sy}; ignoring.");
                return;
            }

            // Build the (N+1)-square sample field (also for var regions). A region of N metres ->
            // N+1 samples at 1 m spacing spans exactly [0, N] metres, so the far EDGE is covered (a
            // heightfield spans (samples - 1) * spacing, so an N-sample field would fall 1 m short). The extra row/column
            // duplicate the last real sample (fetching the neighbour region's row 0 is the later
            // refinement). Non-square regions pad to max(sx,sy) square by edge replication.
            // OpenSim serialises heightMap[y*sx + x] = height at (x,y) - the SAME convention as
            // CreateHeightFieldShape, so it feeds through with no transpose (the Z-up wrapper + row-mirror
            // fix inside the backend do the rest).
            int m = Math.Max(sx, sy) + 1;
            float[] field = new float[m * m];
            for (int y = 0; y < m; y++)
            {
                int srcRow = Math.Min(y, sy - 1) * sx;
                int dstRow = y * m;
                for (int x = 0; x < m; x++)
                    field[dstRow + x] = heightMap[srcRow + Math.Min(x, sx - 1)];
            }

            // 1 m sample spacing, heights already in metres (unit height scale), origin at the region
            // corner (physics runs in region-local coords).
            ShapeId newShape = backend.CreateHeightFieldShape(field, m, m, new SVector3(1f, 1f, 1f));
            backend.SetTerrain(newShape, SVector3.Zero);
            // At MaxBodies the engine refuses the terrain body - a region with no terrain collision is broken.
            if (backend.GetCapacityStats().TerrainBodyMissing)
                m_log.LogError($"{LogHeader} region '{RegionName}': the physics engine refused the terrain body (MaxBodies reached); " +
                               "this region has NO terrain collision - raise [Jolt] MaxBodies.");

            // Retain the cooked samples for TerrainHeightAt (vehicle hover/ground inputs) - the
            // exact field the collision surface was built from, so heights agree with contacts.
            _terrainField = field;
            _terrainFieldM = m;

            // Release the previous terrain shape: SetTerrain already replaced its body (dropping that
            // native ref), so releasing our handle frees it.
            if (_terrainShape.IsValid)
                backend.ReleaseShape(_terrainShape);
            _terrainShape = newShape;

            // Un-bury any avatar the raise left below the new surface (the terrain body was swapped out
            // from under its CharacterVirtual, which keeps its old Z). Runs only here, on a real terrain
            // edit (~5 s tainted cadence), and only lifts avatars now below the surface - a lowered terrain
            // leaves them above, to settle by normal gravity.
            ReGroundAvatarsOnTerrainChange();

            // Step-stamp + a couple of height samples so a [charframe] session can see whether SetTerrain
            // is re-firing during a walk (it should NOT - TerrainModule only ticks it every ~5 s when the
            // heightmap is tainted) and whether the heights it re-cooks are drifting downward.
            m_log.LogInformation($"{LogHeader} terrain set: step={_stepCount} {sx}x{sy} region -> {m}x{m} heightfield " +
                                 $"(spans {m - 1} m/side; sample[centre]={heightMap[(sy / 2) * sx + (sx / 2)]:0.000} sample[0]={heightMap[0]:0.000}).");
        }

        // Distance (m) a capsule centre must be below its seat before we treat it as buried and lift it.
        // Small enough that any real raise lifts, large enough to ignore float noise / a normal grounded
        // avatar sitting exactly at seatZ. Only ever lifts UP, so a modest value is safe either way.
        private const float TerrainUnburyEps = 0.05f;

        /// <summary>
        /// Pure un-bury decision (no physics state), isolated so it is unit-testable and used by the
        /// live pass (ReGroundAvatarsOnTerrainChange). Given a capsule centre Z, the new terrain
        /// surface at its XY, and its seat geometry, returns true + the seat Z it should snap to when the
        /// avatar is below the new surface (buried); false (leave it) when it is at or above the surface -
        /// so a LOWERED terrain never triggers a snap (avatar settles by gravity), and a prim-stander high
        /// above ground is never yanked down.
        /// </summary>
        internal static bool TryComputeUnbury(float currentCentreZ, float terrainZ, float standHalf, float feetOffset, float buriedEps, out float seatZ)
        {
            seatZ = terrainZ + standHalf + feetOffset;   // capsule centre that seats the feet ON the surface
            return currentCentreZ < seatZ - buriedEps;
        }

        /// <summary>
        /// Load-time position sanity (prim analog of <see cref="TryComputeUnbury"/>). A PHYSICAL prim
        /// whose centre is BELOW where it would rest on the terrain surface (terrainZ + halfHeightZ) is buried;
        /// return true + the rest Z to snap it to. A prim resting on the surface, or FLOATING above it (a boat
        /// on water), is at/above restZ, so this returns false and leaves it exactly where it is - the land
        /// box (control) and a floating boat are never touched. Pure (no physics state) so it is unit-testable.
        /// </summary>
        internal static bool TryComputeUnburyPrim(float currentCentreZ, float terrainZ, float halfHeightZ, float buriedEps, out float restZ)
        {
            restZ = terrainZ + halfHeightZ;   // prim centre resting ON the surface
            return currentCentreZ < restZ - buriedEps;
        }

        /// <summary>
        /// Load-time entry used by JoltPrim just before a physical body goes active: if <paramref name="pos"/> is
        /// below the terrain surface for a prim of <paramref name="size"/>, hand back the lifted position so the
        /// body is created RESTING on terrain instead of penetrating it. Applying this BEFORE the body is created
        /// stops (1) the bad position ever draining back to the SOP + persisting, and (2) the native solver
        /// churning on a deep-penetration load (a multi-second stall that can trip the watchdog on reload). No terrain yet -> no lift.
        /// </summary>
        internal bool TryUnburyPhysicalLoad(Vector3 pos, Vector3 size, out Vector3 lifted)
        {
            lifted = pos;
            if (_terrainField == null || _terrainFieldM < 2)
                return false;
            float terrainZ = TerrainHeightAt(pos.X, pos.Y);
            if (!TryComputeUnburyPrim(pos.Z, terrainZ, size.Z * 0.5f, TerrainUnburyEps, out float restZ))
                return false;
            lifted = new Vector3(pos.X, pos.Y, restZ);
            return true;
        }

        // After a live terrain edit, lift any avatar now below the new surface onto it (see SetTerrain).
        // Flying avatars are lifted too - a buried flyer can't rise through the solid heightfield above it.
        // Uses the same seat formula as spawn (groundZ + StandHalf + FeetOffset) and
        // the just-cooked _terrainField (via TerrainHeightAt), so the avatar lands exactly on the contact
        // surface. The reposition is gated in the backend, so it cannot race the per-step character update.
        private void ReGroundAvatarsOnTerrainChange()
        {
            List<JoltCharacter> avs;
            lock (_avatars)
                avs = new List<JoltCharacter>(_avatars.Values);

            foreach (JoltCharacter a in avs)
            {
                Vector3 p = a.Position;
                float terrainZ = TerrainHeightAt(p.X, p.Y);
                if (TryComputeUnbury(p.Z, terrainZ, a.StandHalf, a.FeetOffset, TerrainUnburyEps, out float seatZ))
                {
                    a.ReGround(new Vector3(p.X, p.Y, seatZ));
                    m_log.LogInformation($"{LogHeader} terrain-unbury: avatar {a.LocalID} lifted z={p.Z:0.000} -> seatZ={seatZ:0.000} " +
                                         $"(terrain now {terrainZ:0.000}, flying={a.Flying}).");
                }
            }
        }

        public override void SetWaterLevel(float baseheight)
        {
            WaterLevel = baseheight;   // vehicle hover (HoverWaterOnly) reads this
            _backend?.SetWaterHeight(baseheight);
        }

        public override void DeleteTerrain()
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend != null && _terrainShape.IsValid)
                backend.ReleaseShape(_terrainShape);
            _terrainShape = ShapeId.Invalid;
        }

        // Up to 25 prims with a non-zero CollisionScore (this frame's contact count), highest first,
        // keyed by LocalID - ubODE's model. Note: Persist contacts are only generated for subscribed pairs, so a
        // resting pile of unscripted objects scores only on its Begin frames.
        public override Dictionary<uint, float> GetTopColliders()
        {
            var scored = new List<KeyValuePair<uint, float>>();
            lock (_prims)
                foreach (KeyValuePair<uint, JoltPrim> kv in _prims)
                    if (kv.Value.CollisionScore > 0f)
                        scored.Add(new KeyValuePair<uint, float>(kv.Key, kv.Value.CollisionScore));
            var top = new Dictionary<uint, float>();
            foreach (KeyValuePair<uint, float> kv in CollisionFrameTracker.TopColliders(scored, 25))
                top[kv.Key] = kv.Value;
            return top;
        }

        public override void Dispose()
        {
            _backend?.Dispose();   // backend teardown -> Foundation.Shutdown
            _backend = null;
        }
    }
}
