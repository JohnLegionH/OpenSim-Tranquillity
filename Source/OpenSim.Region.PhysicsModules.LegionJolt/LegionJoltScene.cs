// Legion Grid - Jolt physics as an OpenSim region module (PhysicsScene).
//
// ============================ READ THIS FIRST ============================
// M6.1 SKELETON ONLY. This is the seam between OpenSim's PhysicsScene contract and the
// engine-agnostic ILegionPhysicsBackend (whose Jolt implementation we proved across M1-M4.5 in a
// clean-room harness). This slice proves ONE thing: the module registers, boots under
// `physics = Jolt`, steps an empty world, and shuts down cleanly. It has ZERO physics behaviour:
//   - AddPrimShape / AddAvatar return PhysicsActor.Null (accept-and-ignore, so a region with
//     content still boots).
//   - SetTerrain accepts-and-ignores (real terrain is M6.2).
//   - Simulate steps the backend over an empty active set and returns.
// The batched-buffer drain (StepResult -> per-actor RequestPhysicsterseUpdate / collision dispatch)
// is M6.4/M6.6 and is deliberately NOT here.
//
// Registration mirrors BSScene: a Mono.Addins region module that self-selects when [Startup]
// physics == Name. No [Startup] edit - the operator picks `physics = Jolt`; this module recognises
// its own name.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using Nini.Config;
using log4net;
using OpenMetaverse;

using Legion.Physics;
using LegionJoltBackend = Legion.Physics.Jolt.JoltPhysicsBackend;
// The backend speaks System.Numerics.Vector3; OpenSim speaks OpenMetaverse.Vector3 (the unqualified
// Vector3 here). Alias the numerics one so backend calls are unambiguous.
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    // Registration via PluginRegistration.cs (IPluginRegistryProvider); NGC develop dropped Mono.Addins.
    public sealed class LegionJoltScene : PhysicsScene, INonSharedRegionModule
    {
        internal static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        internal const string LogHeader = "[LEGION JOLT]";

        // Gate for JoltCharacter's [charjump] path trace; toggled by `jolt charframe` and kept in sync with
        // the [charframe] window by Simulate. Static so the per-avatar actor can read it without a back-ref.
        internal static bool CharJumpTrace;

        private bool m_Enabled = false;
        private IConfigSource m_Config;

        // The engine-agnostic backend (the deliverable proven in the clean-room harness).
        private ILegionPhysicsBackend _backend;

        // Held for M6.3 shape cooking; NOT used this slice.
        private IMesher m_mesher;

        public string RegionName { get; private set; }

        // Terrain (M6.2): the current cooked heightfield ShapeId (released + replaced on each SetTerrain),
        // and the region dimensions needed to interpret the flat float[] heightmap OpenSim hands us.
        private ShapeId _terrainShape = ShapeId.Invalid;
        private int _regionSizeX;
        private int _regionSizeY;
        private Scene _scene;

        // Vehicle-controller world inputs (M8): the region water plane, the last cooked terrain
        // sample field (for height-at-XY without a per-frame raycast), the world gravity handed to
        // the backend, and the last Simulate dt (BulletSim's LastTimeStep, used by AddForce).
        internal float WaterLevel { get; private set; }
        internal SVector3 DefaultGravity { get; private set; } = new SVector3(0f, 0f, -9.80665f);
        internal float LastTimeStep = 0.0909f;
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

        // M6.2 Task 2: radial-cone hill parameters (set by `jolt terrainhill`) so `jolt hilltest` can
        // print hand-computable expected Z. z = base + amp*max(0, 1 - dist((x,y),(cx,cy))/R).
        private float _hillCx, _hillCy, _hillBase, _hillAmp, _hillR;
        private bool _hillSet;

        private float HillZ(float x, float y)
        {
            float dx = x - _hillCx, dy = y - _hillCy;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            return _hillBase + _hillAmp * Math.Max(0f, 1f - d / _hillR);
        }

        // M6.3: live prims by SceneObjectPart.LocalId. RemovePrim looks up here; also the future
        // Step-drain target for physical (M6.4) actors. Guarded because Add/RemovePrim can arrive off
        // the heartbeat thread (the backend permits concurrent Create/Remove with Step).
        private readonly Dictionary<uint, JoltPrim> _prims = new Dictionary<uint, JoltPrim>();

        // True only before the first Simulate (the region-reload window). Used by JoltPrim to drop a restored
        // physical prim's horizontal velocity on load (so a reloaded body doesn't inherit a stale coast).
        internal bool IsRegionLoading => _stepCount == 0;

        // STRUCTURAL PORT of BulletSim's taint-deferred body creation: physical bodies are created INERT and
        // their activation is deferred to the top of the next Simulate (step thread), so a body is never
        // stepped by the engine before all its load-time state (incl. the vehicle's gravity-cancellation) is
        // applied - it cannot free-fall during the load or the reload stall. Drained in Simulate.
        private readonly List<JoltPrim> _pendingActivation = new List<JoltPrim>();
        internal void RegisterPendingActivation(JoltPrim p)
        {
            lock (_pendingActivation)
                if (!_pendingActivation.Contains(p)) _pendingActivation.Add(p);
        }

        // M6.5: the logged-in avatars, keyed by their CharacterId handle (the value the character drain
        // echoes back). Keyed by handle rather than LocalID so the drain mapping is independent of when
        // ScenePresence assigns LocalID after AddAvatar returns.
        private readonly Dictionary<uint, JoltCharacter> _avatars = new Dictionary<uint, JoltCharacter>();
        private uint _sitPrimId;   // M6.6: the prim `jolt sittest` rezzed to sit on, so `jolt unsit` can clean it up

        // M6.3 Task 2 proof bookkeeping: the console-rezzed test prims (so `jolt rayprims` can state
        // expected hits and `jolt clearprims` can delete them through the real scene-delete path).
        private struct TestPrim { public uint LocalId; public UUID Sog; public string Kind; public Vector3 Pos; public Vector3 Size; }
        private readonly List<TestPrim> _testPrims = new List<TestPrim>();

        // M6.3 Task 2: collision-mesh LOD (matches BulletSim's BSParam.MeshLOD default), and the
        // characterization of the last RAW mesher output cooked (verts/tris/degenerate/duplicate/AABB).
        private const float MeshLod = 32f;
        private struct MeshStats
        {
            public int Verts, Tris, DegenerateTris, DuplicateVerts, OutOfRangeIndices;
            public SVector3 Min, Max;
            public float Volume;   // enclosed volume of the (closed) mesh; == convex-hull volume for a convex prim
        }
        private MeshStats _lastMeshStats;

        // M6.4 dynamics: per-frame step counter + latest active-body count (drop asserts read these), and
        // the tracked physical drops for `jolt droptest`/`dropmesh`/`dropstatus`. _lastBoxRestZ/_lastMeshRestZ
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

        // Caller-owned step buffers (M1 contract: nothing allocates per frame). Empty world drains
        // nothing; sized modestly for the skeleton and revisited when real actors arrive (M6.4).
        private BodyState[] _bodyBuf = new BodyState[1024];
        private CharacterState[] _charBuf = new CharacterState[256];
        private ContactReport[] _contactBuf = new ContactReport[2048];

        // Collision dispatch (M7 Task 3, base): per-frame accumulation of colliders per subscribed prim,
        // and the set of prims that reported collisions LAST frame - so a prim that stops touching gets one
        // empty CollisionEventUpdate this frame, which is how OpenSim's SOP.PhysicsCollision fires collision_end.
        private readonly Dictionary<uint, CollisionEventUpdate> _collisionAccum = new Dictionary<uint, CollisionEventUpdate>();
        private readonly HashSet<uint> _collidedLastFrame = new HashSet<uint>();

        // ---------------------------------------------------------------------
        // INonSharedRegionModule
        // ---------------------------------------------------------------------

        public string Name => "Jolt";

        public System.Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            // Self-selection: only enable when the operator chose us. Mirrors BSScene - we do NOT
            // hard-enable, and we never touch [Startup] ourselves.
            IConfig config = source.Configs["Startup"];
            if (config != null)
            {
                string physics = config.GetString("physics", string.Empty);
                if (physics == Name)
                {
                    string mesher = config.GetString("meshing", string.Empty);
                    if (string.IsNullOrEmpty(mesher) || !mesher.Equals("Meshmerizer"))
                    {
                        m_log.Error($"{LogHeader} [Startup] meshing must be set to \"Meshmerizer\" for the Jolt physics module.");
                        throw new System.Exception("Invalid physics meshing option for Jolt");
                    }

                    m_Enabled = true;
                    m_Config = source;
                    m_log.Info($"{LogHeader} enabled (physics = {Name}).");
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

            uint sizeX = scene.RegionInfo.RegionSizeX;
            uint sizeY = scene.RegionInfo.RegionSizeY;

            // Stored BEFORE base.Initialise, because that calls SetTerrain(heightMap) - which needs the
            // region dims to interpret the flat float[] and build the (N+1) field.
            _scene = scene;
            _regionSizeX = (int)sizeX;
            _regionSizeY = (int)sizeY;

            var settings = PhysicsBackendSettings.Default;
            settings.MaxBodies = ComputeMaxBodies(sizeX, sizeY);   // decision #3: 65536 / 256 m, scaled by area

            // Sub-step the RIGID-BODY solver 6x inside _system.Update (M6.5 finding #3). At OpenSim's ~11 fps
            // (0.0908 s/frame) a single integration lets a fast prim move ~1.5 m and tunnel through the terrain
            // heightfield (discrete narrowphase; per-body LinearCast/CCD does NOT catch the heightfield - see
            // the harness). CollisionSteps slices the SOLVER only, NOT the character step (which runs once per
            // Step, before Update), so dropped prims rest WITHOUT disturbing the avatar's known-good 1-step path.
            settings.CollisionSteps = 6;

            _backend = new LegionJoltBackend();
            _backend.Initialize(settings);
            DefaultGravity = settings.Gravity;   // the vehicle controller applies this manually

            EngineType = Name;                              // osGetPhysicsEngineType
            EngineName = $"{_backend.Name} {_backend.Version}"; // osGetPhysicsEngineName

            // Terrain/water are accepted-and-ignored this slice (real terrain is M6.2). The base
            // Initialise wires the request-asset delegate and calls our (stub) SetTerrain/SetWaterLevel.
            base.Initialise(scene.PhysicsRequestAsset,
                (scene.Heightmap != null ? scene.Heightmap.GetFloatsSerialised() : new float[sizeX * sizeY]),
                (float)scene.RegionInfo.RegionSettings.WaterHeight);

            m_log.Info($"{LogHeader} region '{RegionName}' {sizeX}x{sizeY}m: backend initialised, MaxBodies={settings.MaxBodies}. {EngineName}");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            // M6.8 parity harness: an engine-agnostic A/B driver registered under ANY physics engine, so
            // the SAME console command runs under BulletSim and Jolt for a clean comparison. It MUST be set
            // up BEFORE the m_Enabled gate (under physics = BulletSim this module is loaded/scanned but is
            // NOT the physics engine, so m_Enabled is false and the rest of RegionLoaded early-returns). The
            // harness drives ONLY the standard Scene/SceneObjectGroup/PhysicsActor surface - no Jolt backend.
            RegisterParityConsole(scene);

            if (!m_Enabled)
                return;

            // Held for M6.3 shape cooking; unused this slice.
            m_mesher = scene.RequestModuleInterface<IMesher>();
            if (m_mesher == null)
                m_log.Warn($"{LogHeader} no IMesher available - shape cooking (M6.3) will need it.");

            scene.PhysicsEnabled = true;

            // M6.2 proof hook: a console command that raycasts straight down onto the cooked terrain and
            // reports the hit Z - the rigorous, viewer-free gate. Registered once (global console).
            if (MainConsole.Instance != null && !_consoleRegistered)
            {
                _consoleRegistered = true;
                MainConsole.Instance.Commands.AddCommand("Physics", false, "jolt",
                    "jolt linktest | unlinktest | collidetest | boattest [linear|hover|attract|steer] | cartest [linear|steer|attract] | sledtest [slide|nosteer|grip] | planetest [thrust|bank|climb] | balloontest [hover|lift|drift] | terraintest | terrainslope | terrainhill | hilltest | probe <x> <y> | rezprims | rayprims | rezmesh | rezmeshn <count> | raymesh | droptest | dropmesh | dropstatus | avatarstatus | charframe [secs] | sitstatus | sittest | unsit | sittarget | sensortest | raytest | heights <x> <y> | reloadcheck | vehiclestatus | clearprims",
                    "Legion Jolt proofs (M6.2 terrain / M6.3 prims): raycast the cooked collision surfaces and report hits.",
                    HandleJoltConsole);
            }
        }

        private static bool _consoleRegistered;

        // Raycast straight down at XY (from well above the region) and report the hit Z - proves the
        // heightfield's ACTUAL collision surface, not "it booted". `jolt terraintest` sweeps the extent
        // probes (interior + the far edge that the (N+1) field must now cover); `jolt probe x y` is ad hoc.
        private void HandleJoltConsole(string module, string[] cmd)
        {
            if (_backend == null) { MainConsole.Instance.Output($"{LogHeader} no backend."); return; }

            if (cmd.Length >= 2 && cmd[1] == "linktest") { JoltLinkTest(); return; }
            if (cmd.Length >= 2 && cmd[1] == "unlinktest") { JoltUnlinkTest(); return; }
            if (cmd.Length >= 2 && cmd[1] == "collidetest") { JoltCollideTest(); return; }
            if (cmd.Length >= 2 && cmd[1] == "collidelinktest") { JoltCollideLinkTest(); return; }
            if (cmd.Length >= 2 && cmd[1] == "boattest") { JoltBoatTest(cmd.Length >= 3 ? cmd[2] : "linear"); return; }
            if (cmd.Length >= 2 && cmd[1] == "cartest") { JoltCarTest(cmd.Length >= 3 ? cmd[2] : "linear"); return; }
            if (cmd.Length >= 2 && cmd[1] == "sledtest") { JoltSledTest(cmd.Length >= 3 ? cmd[2] : "slide"); return; }
            if (cmd.Length >= 2 && cmd[1] == "planetest") { JoltPlaneTest(cmd.Length >= 3 ? cmd[2] : "thrust"); return; }
            if (cmd.Length >= 2 && cmd[1] == "balloontest") { JoltBalloonTest(cmd.Length >= 3 ? cmd[2] : "hover"); return; }

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

            if (cmd.Length >= 2 && cmd[1] == "terrainslope")
            {
                // Push a KNOWN X-gradient (z rises with X, independent of Y) through the real SetTerrain
                // path to prove orientation + the row-mirror fix on real-shaped data: a raycast at (x,y)
                // must read z = base + x*slope. A transpose would make z depend on Y; a mirror would
                // invert it. base+slope chosen so probes are unambiguous.
                const float baseZ = 10f, slope = 0.1f;
                var hm = new float[_regionSizeX * _regionSizeY];
                for (int gy = 0; gy < _regionSizeY; gy++)
                    for (int gx = 0; gx < _regionSizeX; gx++)
                        hm[gy * _regionSizeX + gx] = baseZ + gx * slope;
                SetTerrain(hm);
                MainConsole.Instance.Output($"{LogHeader} set X-gradient terrain: z = {baseZ} + x*{slope} (independent of y).");
                MainConsole.Instance.Output($"  confirm orientation: jolt probe 50 200 -> z~15 ; jolt probe 200 50 -> z~30 (z tracks X, not Y).");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "terrainhill")
            {
                // A KNOWN radial cone: elevation varies in BOTH axes (a transpose/single-axis bug shows),
                // exact closed form, and equal-distance symmetry for the 2D-orientation check. R is large
                // enough that the slope reaches the region edges (no flat base to hide behind).
                _hillCx = _regionSizeX / 2f; _hillCy = _regionSizeY / 2f;
                _hillBase = 20f; _hillAmp = 40f; _hillR = 200f; _hillSet = true;

                // Write into the SCENE heightmap (not just physics), so the taint propagates to the
                // VIEWER (patch send) as well; then push to physics immediately so `jolt hilltest`
                // works this instant instead of waiting for the ~5 s terrain tick.
                for (int gy = 0; gy < _regionSizeY; gy++)
                    for (int gx = 0; gx < _regionSizeX; gx++)
                        _scene.Heightmap[gx, gy] = HillZ(gx, gy);
                SetTerrain(_scene.Heightmap.GetFloatsSerialised());

                MainConsole.Instance.Output($"{LogHeader} radial cone set (scene + physics): z = {_hillBase} + {_hillAmp}*max(0, 1 - dist((x,y),({_hillCx},{_hillCy}))/{_hillR})");
                MainConsole.Instance.Output($"  peak ({_hillCx},{_hillCy}) z={_hillBase + _hillAmp:0.00}; hand-check any XY with that formula. Run: jolt hilltest");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "hilltest")
            {
                if (!_hillSet) { MainConsole.Instance.Output($"{LogHeader} run `jolt terrainhill` first."); return; }
                float cx = _hillCx, cy = _hillCy;
                // (peak; two equal-distance points at +X vs +Y - MUST match; a mid-slope; the non-flat
                // EDGE at (255.5,255.5); the last real edge sample; a low corner).
                var pts = new (float x, float y)[]
                { (cx, cy), (cx + 50f, cy), (cx, cy + 50f), (cx + 72f, cy + 72f),
                  (_regionSizeX - 0.5f, _regionSizeY - 0.5f), (_regionSizeX - 1f, _regionSizeY - 1f), (10f, 10f) };
                MainConsole.Instance.Output($"{LogHeader} hill raycast probes (expected = cone formula; small interp/edge-strip deltas OK):");
                MainConsole.Instance.Output($"     x       y   |  expected |  actual  |  delta   |  dist");
                foreach (var (px, py) in pts)
                {
                    float exp = HillZ(px, py);
                    float dd = (float)Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    bool hit = _backend.RayCast(new SVector3(px, py, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.All, out RayHit h);
                    string act = hit ? $"{h.Point.Z,8:0.000}" : "  miss  ";
                    string del = hit ? $"{h.Point.Z - exp,8:0.000}" : "   -    ";
                    MainConsole.Instance.Output($"  ({px,6:0.0},{py,6:0.0}) | {exp,8:0.000} | {act} | {del} | {dd,6:0.0}");
                }
                MainConsole.Instance.Output($"  ({cx + 50f:0},{cy}) and ({cx},{cy + 50f:0}) are equal-distance -> MUST read the same Z (2D orientation).");
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

            if (cmd.Length >= 2 && cmd[1] == "rezprims")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                ClearTestPrims();   // idempotent: re-rez from a clean slate

                // Three basic shapes at z=100 (above any terrain/hill), spread 8 m in X so they don't
                // overlap. Sizes chosen so the raycast proofs are unambiguous: the cylinder is tall+thin
                // (halfHeight 2, radius 0.5) so a Z-axis (correct) top-cap hit at 102 is nowhere near a
                // Y-axis (wrong) curved-side hit at 100.5.
                RezTestPrim("box", new Vector3(120f, 128f, 100f), new Vector3(2f, 3f, 4f));
                RezTestPrim("sphere", new Vector3(128f, 128f, 100f), new Vector3(2f, 2f, 2f));
                RezTestPrim("cylinder", new Vector3(136f, 128f, 100f), new Vector3(1f, 1f, 4f));

                MainConsole.Instance.Output($"{LogHeader} rezzed {_testPrims.Count} test prims via the real AddNewSceneObject -> ApplyPhysics -> AddPrimShape path:");
                foreach (var tp in _testPrims)
                {
                    string via = "?";
                    lock (_prims)
                        if (_prims.TryGetValue(tp.LocalId, out JoltPrim jp)) via = jp.ShapeKind;
                    MainConsole.Instance.Output($"  id={tp.LocalId,-6} {tp.Kind,-9} pos=({tp.Pos.X:0.0},{tp.Pos.Y:0.0},{tp.Pos.Z:0.0}) size=({tp.Size.X:0.0},{tp.Size.Y:0.0},{tp.Size.Z:0.0}) -> jolt shape: {via}");
                }
                MainConsole.Instance.Output($"  now run: jolt rayprims  (casts through Scene.RayCastFiltered - the exact llCastRay pipeline).");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "rayprims")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                RayPrims();   // runs with prims (expect hits) OR after clearprims (expect all miss)
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "rezmesh")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                ClearTestPrims();

                // A triangular PRISM forces the mesher (not a fast-path shape) and needs NO asset (unlike
                // a sculpt, which can't mesh synchronously headless). size (4,4,3): a flat triangular top
                // at z=101.5, inscribed in a 4x4 bbox - the bbox corners are EMPTY (the tetra-vs-bbox test).
                var pos = new Vector3(120f, 128f, 100f);
                var size = new Vector3(4f, 4f, 3f);
                RezTestPrim("prism", pos, size);

                uint id = _testPrims.Count > 0 ? _testPrims[0].LocalId : 0u;
                string kind = "?";
                lock (_prims)
                    if (_prims.TryGetValue(id, out JoltPrim jp)) kind = jp.ShapeKind;
                MainConsole.Instance.Output($"{LogHeader} rezzed prism id={id} via the real AddPrimShape path -> jolt shape: {kind}  (expect 'mesh(mesher)', NOT basic/bbox).");

                MeshStats s = _lastMeshStats;
                MainConsole.Instance.Output($"  REAL mesher geometry: verts={s.Verts} tris={s.Tris} degenerate={s.DegenerateTris} duplicateVerts={s.DuplicateVerts} outOfRangeIdx={s.OutOfRangeIndices}");
                MainConsole.Instance.Output($"    local AABB min=({s.Min.X:0.00},{s.Min.Y:0.00},{s.Min.Z:0.00}) max=({s.Max.X:0.00},{s.Max.Y:0.00},{s.Max.Z:0.00})");

                // Decision-point check (physical -> convex hull, delta #31): cook the SAME prism physical,
                // inline, purely to confirm routing (cook+release, no body). Real physical dynamics is M6.4.
                ShapeId hull = CookPrimShape(GetPrismPbs(), size, true, out _, out string hullKind);
                MainConsole.Instance.Output($"  decision-point: physical prism cooks to '{hullKind}' (expect 'hull(mesher)' - a mesh's Volume=0 would rez a physical prim mass-0; hull avoids it).");
                if (hull.IsValid) _backend.ReleaseShape(hull);

                MainConsole.Instance.Output($"  now run: jolt raymesh  (grid cast - triangle top HITs ~101.5, empty bbox corners MISS; a box would hit all).");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "raymesh")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                RayMesh();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "rezmeshn")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                int count = 4;
                if (cmd.Length >= 3 && int.TryParse(cmd[2], out int c)) count = c;
                count = Math.Max(1, Math.Min(12, count));
                RezMeshN(count);
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "droptest")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                ClearTestPrims();
                DropOne("box", new Vector3(2f, 2f, 2f), 120f, 128f);
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "dropmesh")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                ClearTestPrims();
                DropOne("prism", new Vector3(2f, 2f, 2f), 136f, 128f);
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "dropstatus")
            {
                DropStatus();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "avatarstatus")
            {
                AvatarStatus();
                return;
            }

            // Console proof tool (`jolt reloadcheck`): snapshot every physical prim's
            // saved (birth) pos vs where it is NOW, plus terrain/water under it, and classify. Run a few
            // seconds after a region reload to SEE which physical objects were displaced (SANK/FLUNG) and
            // by how much - the "before" evidence.
            if (cmd.Length >= 2 && cmd[1] == "reloadcheck")
            {
                ReloadCheck();
                return;
            }

            // Console proof tool (`jolt vehiclestatus`): dump the LIVE vehicle state of every prim so you can CONFIRM a
            // boat is actually TYPE_BOAT + buoyancy=1 + active BEFORE testing reload (the missing confirmation).
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

            if (cmd.Length >= 2 && cmd[1] == "sittest")
            {
                SitTest();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "unsit")
            {
                Unsit();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "sittarget")
            {
                SitTarget();
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

            if (cmd.Length >= 2 && cmd[1] == "clearprims")
            {
                int n = ClearTestPrims();
                MainConsole.Instance.Output($"{LogHeader} deleted {n} test prims (scene delete -> RemovePrim). `jolt rayprims` should now miss.");
                return;
            }

            MainConsole.Instance.Output("Usage: jolt linktest | unlinktest | collidetest | boattest [linear|hover|attract|steer] | cartest [linear|steer|attract] | sledtest [slide|nosteer|grip] | planetest [thrust|bank|climb] | balloontest [hover|lift|drift] | terraintest | terrainslope | terrainhill | hilltest | probe <x> <y> | rezprims | rayprims | rezmesh | rezmeshn <count> | raymesh | droptest | dropmesh | dropstatus | avatarstatus | charframe [secs] | sitstatus | sittest | unsit | sittarget | sensortest | raytest | heights <x> <y> | reloadcheck | vehiclestatus | clearprims");
        }

        // M7 Task 1 proof: rez a root + 2 children at offsets, make the root physical, then run the OpenSim
        // handoff (child.PhysActor.link(root.PhysActor)) so the children WELD into the root's compound body.
        // Assert the compound's mass jumps to ~3x a single prim (sum of parts) and the whole thing falls as
        // ONE body. (Children are separate SOGs here, so this proves the PHYSICS - the visual linkset is the
        // viewer Ctrl+L test.)
        private void JoltLinkTest()
        {
            float tz = 25f;
            try { tz = (float)_scene.Heightmap[128, 128]; } catch { }
            Vector3 rootPos = new Vector3(128f, 128f, tz + 12f);
            var size = new Vector3(0.5f, 0.5f, 0.5f);
            SceneObjectGroup root = RezTestPrim("box", rootPos, size);
            SceneObjectGroup c1 = RezTestPrim("box", rootPos + new Vector3(0.6f, 0f, 0f), size);
            SceneObjectGroup c2 = RezTestPrim("box", rootPos + new Vector3(0f, 0.6f, 0f), size);

            root.ScriptSetPhysicsStatus(true);
            PhysicsActor rpa = root.RootPart.PhysActor;
            if (rpa == null) { MainConsole.Instance.Output($"{LogHeader} linktest: root has no PhysActor."); return; }
            float singleMass = rpa.Mass;
            c1.RootPart.PhysActor?.link(rpa);   // the OpenSim child.link(root) handoff
            c2.RootPart.PhysActor?.link(rpa);
            System.Threading.Thread.Sleep(500);   // the compound rebuild is coalesced to the next Simulate
            float compoundMass = rpa.Mass;
            float startZ = rpa.Position.Z;

            System.Threading.Thread.Sleep(3000);   // let the compound fall on the heartbeat

            float endZ = rpa.Position.Z;
            float childBodyZ = c1.RootPart.PhysActor != null ? c1.RootPart.PhysActor.Position.Z : float.NaN;
            MainConsole.Instance.Output($"{LogHeader} [linktest] singleMass={singleMass:0.0}  compoundMass={compoundMass:0.0}  (expect ~3x = {singleMass * 3f:0.0})");
            MainConsole.Instance.Output($"{LogHeader} [linktest] root Z {startZ:0.00} -> {endZ:0.00}  ({(endZ < startZ - 1f ? "FELL as one compound" : "did NOT fall")});  welded child's own body Z stayed {childBodyZ:0.00} (no independent sim = welded)");
            bool massOk = singleMass > 0f && System.Math.Abs(compoundMass - singleMass * 3f) < singleMass * 0.1f;
            MainConsole.Instance.Output($"{LogHeader} [linktest] {((massOk && endZ < startZ - 1f) ? "PASS" : "CHECK")}: compound mass=sum AND fell as one. (Viewer: Ctrl+L a real linkset for the visual.)");

            _scene.DeleteSceneObject(root, false);
            _scene.DeleteSceneObject(c1, false);
            _scene.DeleteSceneObject(c2, false);
        }

        // M7 Task 3 (base collision dispatch) proof: drop a SUBSCRIBED dynamic box onto a static platform +
        // terrain, hook its PhysicsActor.OnCollisionUpdate (exactly what a script's collision handler wires),
        // and confirm the module delivers CollisionEventUpdates - a non-empty collider set while touching
        // (start + ongoing), the struck OBJECT's LocalID in that set (-> llDetected* / link number), and an
        // empty set after the box is removed (-> collision_end). Console stand-in for the viewer script.
        private void JoltCollideTest()
        {
            float tz = 25f;
            try { tz = (float)_scene.Heightmap[128, 128]; } catch { }
            SceneObjectGroup plat = RezTestPrim("box", new Vector3(128f, 128f, tz + 2f), new Vector3(3f, 3f, 0.5f));
            SceneObjectGroup box = RezTestPrim("box", new Vector3(128f, 128f, tz + 6f), new Vector3(0.5f, 0.5f, 0.5f));
            uint platLink = plat.RootPart.LocalId;

            box.ScriptSetPhysicsStatus(true);
            PhysicsActor bpa = box.RootPart.PhysActor;
            if (bpa == null) { MainConsole.Instance.Output($"{LogHeader} collidetest: box has no PhysActor."); return; }

            int events = 0, nonEmpty = 0, emptyAfter = 0;
            var seen = new HashSet<uint>();
            bool anyReported = false;
            PhysicsActor.CollisionUpdate handler = (EventArgs e) =>
            {
                var u = (CollisionEventUpdate)e;
                System.Threading.Interlocked.Increment(ref events);
                lock (seen)
                {
                    if (u.m_objCollisionList.Count > 0) { nonEmpty++; anyReported = true; foreach (uint k in u.m_objCollisionList.Keys) seen.Add(k); }
                    else if (anyReported) emptyAfter++;
                }
            };
            bpa.OnCollisionUpdate += handler;
            bpa.SubscribeEvents(50);   // exactly what OpenSim does when a collision-handler script is present

            System.Threading.Thread.Sleep(4000);   // fall onto the platform, rest -> start + ongoing contacts

            bool hitPlatform, hitLand; int seenCount; string seenList;
            lock (seen) { hitPlatform = seen.Contains(platLink); hitLand = seen.Contains(0u); seenCount = seen.Count; seenList = string.Join(",", seen); }

            // Remove the box's body -> next frame the platform-side set drops it; but we test the box side:
            // stop touching by deleting the platform out from under it, then let it fall to terrain and settle,
            // which also exercises the collider-set CHANGING. Then unsubscribe and read the end-flush counter.
            System.Threading.Thread.Sleep(500);
            bpa.UnSubscribeEvents();
            bpa.OnCollisionUpdate -= handler;

            MainConsole.Instance.Output($"{LogHeader} [collidetest] updates={events} (nonEmpty={nonEmpty}, emptyAfter={emptyAfter})  collidedWith={{{seenList}}}  platformLink={platLink}");
            MainConsole.Instance.Output($"{LogHeader} [collidetest] hitPlatform(object)={hitPlatform}  hitLand(id0)={hitLand}");
            bool pass = nonEmpty > 0 && (hitPlatform || hitLand);
            MainConsole.Instance.Output($"{LogHeader} [collidetest] {(pass ? "PASS" : "CHECK")}: subscribed prim received collision dispatch (start+ongoing){(hitPlatform ? " incl. the OBJECT it rests on" : "")}. (Viewer: a Phlox collision(n) script for the full path.)");

            _scene.DeleteSceneObject(box, false);
            _scene.DeleteSceneObject(plat, false);
        }

        // M8 Task 2 boat proofs. `jolt boattest [linear|hover|attract|steer]` (default linear).
        // Each cooks a physics-only water basin if the region has no open water, rezzes a physical
        // VEHICLE_TYPE_BOAT, drives ONE aspect of the extracted Halcyon controller and asserts it.
        //   linear  (slice a): held linear motor -> forward speed ramps to target
        //   hover   (slice b): settle from above, rise from below, hold at rest
        //   attract (slice c): tilt -> self-rights; yaw stays free
        //   steer   (slice d): angular motor -> yaws, friction stops it, stays upright
        private void JoltBoatTest(string scenario)
        {
            if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
            switch (scenario)
            {
                case "hover":   BoatHoverTest();   break;
                case "attract": BoatAttractTest(); break;
                case "steer":   BoatSteerTest();   break;
                default:        BoatLinearTest();  break;
            }
        }

        // M8 CAR proofs. `jolt cartest [linear|steer|attract]` (default linear). Rezzes a physical
        // VEHICLE_TYPE_CAR on the terrain (no water needed - a car rides the ground) and drives ONE
        // aspect of the extracted controller, asserting it:
        //   linear  : held linear motor -> forward speed ramps; car stays on the ground (no sink, no hover)
        //   steer   : angular motor -> heading yaws; car stays upright
        //   attract : born tilted -> vertical attractor self-rights it while it sits on the ground
        private void JoltCarTest(string scenario)
        {
            if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
            switch (scenario)
            {
                case "steer":   CarSteerTest();   break;
                case "attract": CarAttractTest(); break;
                default:        CarLinearTest();  break;
            }
        }

        // Rez a physical VEHICLE_TYPE_CAR box at (x,y,z). Returns the SOG + PhysActor + JoltPrim + id,
        // or (null,...) on failure (caller checks pa).
        private (SceneObjectGroup sog, PhysicsActor pa, JoltPrim jp, uint id) RezCar(float x, float y, float z, Quaternion rot)
        {
            SceneObjectGroup car = RezTestPrim("box", new Vector3(x, y, z), new Vector3(2f, 1f, 0.5f));
            if (rot != Quaternion.Identity)
                car.UpdateGroupRotationR(rot);   // born tilted: ApplyPhysics below cooks the body at this rot
            car.ScriptSetPhysicsStatus(true);
            PhysicsActor pa = car.RootPart.PhysActor;
            if (pa == null) { _scene.DeleteSceneObject(car, false); return (null, null, null, 0); }
            if (rot != Quaternion.Identity) pa.Orientation = rot;
            uint id = car.RootPart.LocalId;
            pa.VehicleType = (int)Vehicle.TYPE_CAR;
            JoltPrim jp;
            lock (_prims) _prims.TryGetValue(id, out jp);
            return (car, pa, jp, id);
        }

        // Full live body state for a car (position incl. X/Y so we can measure height above the terrain
        // it is driving over). Mirrors BoatState but returns the whole position vector.
        private bool CarState(JoltPrim jp, out float tiltDeg, out SVector3 pos, out SVector3 linVel, out SQuaternion orient)
        {
            tiltDeg = 0f; pos = default; linVel = default; orient = SQuaternion.Identity;
            if (jp == null || !_backend.TryGetBodyState(jp.BodyHandle, out BodyState st))
                return false;
            var up = SVector3.Transform(new SVector3(0f, 0f, 1f), st.Orientation);
            tiltDeg = (float)(Math.Acos(Math.Clamp(up.Z, -1f, 1f)) * 180.0 / Math.PI);
            pos = st.Position; linVel = st.LinearVelocity; orient = st.Orientation;
            return true;
        }

        // Heading (deg) of the body's local +X (nose) about world Z.
        private static float HeadingDeg(SQuaternion o)
        {
            var fwd = SVector3.Transform(new SVector3(1f, 0f, 0f), o);
            return (float)(Math.Atan2(fwd.Y, fwd.X) * 180.0 / Math.PI);
        }

        // ---- car (linear): held motor -> forward ramp, stays on the ground -------------------------
        private void CarLinearTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            var (car, pa, jp, id) = RezCar(cx, cy, ground + 1.0f, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} cartest: car has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [cartest:linear] car id={id} at ({cx:0},{cy:0}) ground={ground:0.00} mass={pa.Mass:0.00} type={pa.VehicleType} (expect 2)");
            System.Threading.Thread.Sleep(1000);   // let it drop and settle on the ground first
            var motor = new Vector3(6f, 0f, 0f);   // hold forward at 6 m/s (local +X)
            MainConsole.Instance.Output($"{LogHeader} [cartest:linear] holding LINEAR_MOTOR_DIRECTION={motor} (re-set every 0.5 s)");
            MainConsole.Instance.Output($"     t   |  fwdSpeed |   speedXY | z-ground |  tiltDeg");

            float first = float.NaN, last = float.NaN, maxClear = float.MinValue, minClear = float.MaxValue;
            for (int i = 0; i <= 8; i++)
            {
                pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, motor);
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out SVector3 lv, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                var fwd = SVector3.Transform(new SVector3(1f, 0f, 0f), o);
                float fwdSpeed = lv.X * fwd.X + lv.Y * fwd.Y + lv.Z * fwd.Z;
                float speedXY = (float)Math.Sqrt(lv.X * lv.X + lv.Y * lv.Y);
                float clear = p.Z - TerrainHeightAt(p.X, p.Y);
                if (i >= 1) { if (clear > maxClear) maxClear = clear; if (clear < minClear) minClear = clear; }
                if (i == 1) first = fwdSpeed;
                last = fwdSpeed;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {fwdSpeed,9:0.000} | {speedXY,9:0.000} | {clear,8:0.000} | {tilt,8:0.0}");
            }

            // moved forward, and stayed on the ground the whole time (didn't sink through, didn't hover up).
            bool ramped = last > 2f && (float.IsNaN(first) || last >= first - 0.25f);
            bool onGround = minClear > -0.5f && maxClear < 1.5f;
            bool pass = ramped && onGround;
            MainConsole.Instance.Output($"{LogHeader} [cartest:linear] {(pass ? "PASS" : "FAIL")}: reached {last:0.00} m/s forward (target 6), ground clearance {minClear:0.00}..{maxClear:0.00} m (stayed grounded={onGround}).");
            _scene.DeleteSceneObject(car, false);
        }

        // ---- car (steer): angular motor -> heading yaws, stays upright -----------------------------
        private void CarSteerTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            var (car, pa, jp, id) = RezCar(cx, cy, ground + 1.0f, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} cartest: car has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [cartest:steer] car id={id} type={pa.VehicleType} (expect 2)");
            System.Threading.Thread.Sleep(1000);
            pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(4f, 0f, 0f));   // rolling forward
            var yaw = new Vector3(0f, 0f, 0.6f);   // hold a left yaw
            MainConsole.Instance.Output($"{LogHeader} [cartest:steer] holding ANGULAR_MOTOR_DIRECTION={yaw}");
            MainConsole.Instance.Output($"     t   |  heading  |  tiltDeg");

            float startHeading = float.NaN, lastHeading = float.NaN, maxTilt = 0f;
            for (int i = 0; i <= 8; i++)
            {
                pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(4f, 0f, 0f));
                pa.VehicleVectorParam((int)Vehicle.ANGULAR_MOTOR_DIRECTION, yaw);
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out _, out _, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                float h = HeadingDeg(o);
                if (float.IsNaN(startHeading)) startHeading = h;
                lastHeading = h;
                if (tilt > maxTilt) maxTilt = tilt;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {h,8:0.0} | {tilt,8:0.0}");
            }

            float netYaw = Math.Abs(lastHeading - startHeading);
            if (netYaw > 180f) netYaw = 360f - netYaw;
            bool pass = netYaw > 20f && maxTilt < 30f;
            MainConsole.Instance.Output($"{LogHeader} [cartest:steer] {(pass ? "PASS" : "FAIL")}: turned {netYaw:0.0} deg, stayed upright (maxTilt {maxTilt:0.0}).");
            _scene.DeleteSceneObject(car, false);
        }

        // ---- car (attract): born tilted -> self-rights on the ground -------------------------------
        private void CarAttractTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 30f * (float)(Math.PI / 180.0));
            var (car, pa, jp, id) = RezCar(cx, cy, ground + 1.5f, roll);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} cartest: car has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [cartest:attract] car id={id} born rolled ~30 deg, type={pa.VehicleType} (expect 2)");
            MainConsole.Instance.Output($"     t   |  tiltDeg | z-ground");

            float startTilt = float.NaN, lastTilt = float.NaN;
            for (int i = 0; i <= 10; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out _, out _))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                if (float.IsNaN(startTilt)) startTilt = tilt;
                lastTilt = tilt;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {tilt,8:0.0} | {p.Z - TerrainHeightAt(p.X, p.Y),8:0.000}");
            }

            bool pass = lastTilt < startTilt - 10f && lastTilt < 15f;
            MainConsole.Instance.Output($"{LogHeader} [cartest:attract] {(pass ? "PASS" : "FAIL")}: righted from {startTilt:0.0} to {lastTilt:0.0} deg.");
            _scene.DeleteSceneObject(car, false);
        }

        // M8 SLED proofs. `jolt sledtest [slide|nosteer|grip]` (default slide). Rezzes a physical
        // VEHICLE_TYPE_SLED on the terrain and drives ONE aspect of the extracted controller:
        //   slide   : born nose-down -> glides forward down its nose (SimulateSledMovement gravity engine)
        //   nosteer : angular motor -> the sled does NOT turn (motor TS 1000 inert) - the car/sled contrast
        //   grip    : nose-down glide -> forward speed builds while lateral slip stays gripped
        private void JoltSledTest(string scenario)
        {
            if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
            switch (scenario)
            {
                case "nosteer": SledNoSteerTest(); break;
                case "grip":    SledGripTest();    break;
                default:        SledSlideTest();   break;
            }
        }

        // Rez a physical VEHICLE_TYPE_SLED box at (x,y,z). Returns the SOG + PhysActor + JoltPrim + id.
        private (SceneObjectGroup sog, PhysicsActor pa, JoltPrim jp, uint id) RezSled(float x, float y, float z, Quaternion rot)
        {
            SceneObjectGroup sled = RezTestPrim("box", new Vector3(x, y, z), new Vector3(2f, 1f, 0.5f));
            if (rot != Quaternion.Identity)
                sled.UpdateGroupRotationR(rot);
            sled.ScriptSetPhysicsStatus(true);
            PhysicsActor pa = sled.RootPart.PhysActor;
            if (pa == null) { _scene.DeleteSceneObject(sled, false); return (null, null, null, 0); }
            if (rot != Quaternion.Identity) pa.Orientation = rot;
            uint id = sled.RootPart.LocalId;
            pa.VehicleType = (int)Vehicle.TYPE_SLED;
            JoltPrim jp;
            lock (_prims) _prims.TryGetValue(id, out jp);
            return (sled, pa, jp, id);
        }

        // ---- sled (slide): born nose-down -> glides forward down its nose --------------------------
        private void SledSlideTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 20f * (float)(Math.PI / 180.0)); // nose down
            var (sled, pa, jp, id) = RezSled(cx, cy, ground + 1.0f, pitch);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} sledtest: sled has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [sledtest:slide] sled id={id} born nose-down ~20deg, type={pa.VehicleType} (expect 1)");
            MainConsole.Instance.Output($"     t   |  fwdSpeed |   speedXY | z-ground |  tiltDeg");

            float maxSpeed = 0f;
            for (int i = 0; i <= 8; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out SVector3 lv, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                var fwd = SVector3.Transform(new SVector3(1f, 0f, 0f), o);
                float fwdSpeed = lv.X * fwd.X + lv.Y * fwd.Y + lv.Z * fwd.Z;
                float speedXY = (float)Math.Sqrt(lv.X * lv.X + lv.Y * lv.Y);
                if (speedXY > maxSpeed) maxSpeed = speedXY;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {fwdSpeed,9:0.000} | {speedXY,9:0.000} | {p.Z - TerrainHeightAt(p.X, p.Y),8:0.000} | {tilt,8:0.0}");
            }

            bool pass = maxSpeed > 0.5f;
            MainConsole.Instance.Output($"{LogHeader} [sledtest:slide] {(pass ? "PASS" : "FAIL")}: sled glided down its nose (max XY speed {maxSpeed:0.00} m/s).");
            _scene.DeleteSceneObject(sled, false);
        }

        // ---- sled (nosteer): angular motor -> the sled does NOT turn (the car/sled contrast) -------
        private void SledNoSteerTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            var (sled, pa, jp, id) = RezSled(cx, cy, ground + 1.0f, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} sledtest: sled has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [sledtest:nosteer] sled id={id} type={pa.VehicleType} (expect 1)");
            System.Threading.Thread.Sleep(1000);
            var yaw = new Vector3(0f, 0f, 0.6f);   // the same yaw command a car steers hard under
            MainConsole.Instance.Output($"{LogHeader} [sledtest:nosteer] holding ANGULAR_MOTOR_DIRECTION={yaw} - a sled should NOT turn");
            MainConsole.Instance.Output($"     t   |  heading  |  tiltDeg");

            float startHeading = float.NaN, lastHeading = float.NaN;
            for (int i = 0; i <= 8; i++)
            {
                pa.VehicleVectorParam((int)Vehicle.ANGULAR_MOTOR_DIRECTION, yaw);
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out _, out _, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                float h = HeadingDeg(o);
                if (float.IsNaN(startHeading)) startHeading = h;
                lastHeading = h;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {h,8:0.0} | {tilt,8:0.0}");
            }

            float netYaw = Math.Abs(lastHeading - startHeading);
            if (netYaw > 180f) netYaw = 360f - netYaw;
            bool pass = netYaw < 15f;   // a car turns >20 deg under the same command; a sled must not
            MainConsole.Instance.Output($"{LogHeader} [sledtest:nosteer] {(pass ? "PASS" : "FAIL")}: sled turned only {netYaw:0.0} deg (a car would turn hard).");
            _scene.DeleteSceneObject(sled, false);
        }

        // ---- sled (grip): nose-down glide, forward builds while lateral slip stays gripped ---------
        private void SledGripTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 15f * (float)(Math.PI / 180.0)); // slight nose-down to glide
            var (sled, pa, jp, id) = RezSled(cx, cy, ground + 1.0f, pitch);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} sledtest: sled has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [sledtest:grip] sled id={id} type={pa.VehicleType} (expect 1) - glides forward, lateral slip gripped");
            MainConsole.Instance.Output($"     t   |  fwdSpeed | sideSpeed | z-ground");

            float lastFwd = 0f, maxSide = 0f;
            for (int i = 0; i <= 8; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out SVector3 lv, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                var fwd = SVector3.Transform(new SVector3(1f, 0f, 0f), o);
                var side = SVector3.Transform(new SVector3(0f, 1f, 0f), o);
                float fwdSpeed = lv.X * fwd.X + lv.Y * fwd.Y + lv.Z * fwd.Z;
                float sideSpeed = Math.Abs(lv.X * side.X + lv.Y * side.Y + lv.Z * side.Z);
                if (sideSpeed > maxSide) maxSide = sideSpeed;
                lastFwd = fwdSpeed;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {fwdSpeed,9:0.000} | {sideSpeed,9:0.000} | {p.Z - TerrainHeightAt(p.X, p.Y),8:0.000}");
            }

            bool pass = lastFwd > 0.5f && maxSide < 1.0f;
            MainConsole.Instance.Output($"{LogHeader} [sledtest:grip] {(pass ? "PASS" : "FAIL")}: forward glide {lastFwd:0.00} m/s, max lateral slip {maxSide:0.00} m/s.");
            _scene.DeleteSceneObject(sled, false);
        }

        // M8 AIRPLANE proofs. `jolt planetest [thrust|bank|climb]` (default thrust). Rezzes a physical
        // VEHICLE_TYPE_AIRPLANE HIGH in the air and holds thrust each tick (a plane has buoyancy 0 - it
        // FALLS without continuous thrust, which is correct). Drives ONE aspect of the controller:
        //   thrust : held forward motor -> airspeed builds
        //   climb  : nose-up + thrust -> gains altitude (lift from linear deflection)
        //   bank   : rolled + thrust -> heading turns (banking->yaw; a plane turns by banking)
        private void JoltPlaneTest(string scenario)
        {
            if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
            switch (scenario)
            {
                case "bank":  PlaneBankTest();  break;
                case "climb": PlaneClimbTest(); break;
                default:      PlaneThrustTest(); break;
            }
        }

        // Rez a physical VEHICLE_TYPE_AIRPLANE box at (x,y,z). Returns the SOG + PhysActor + JoltPrim + id.
        private (SceneObjectGroup sog, PhysicsActor pa, JoltPrim jp, uint id) RezPlane(float x, float y, float z, Quaternion rot)
        {
            SceneObjectGroup plane = RezTestPrim("box", new Vector3(x, y, z), new Vector3(3f, 2f, 0.5f));
            if (rot != Quaternion.Identity)
                plane.UpdateGroupRotationR(rot);
            plane.ScriptSetPhysicsStatus(true);
            PhysicsActor pa = plane.RootPart.PhysActor;
            if (pa == null) { _scene.DeleteSceneObject(plane, false); return (null, null, null, 0); }
            if (rot != Quaternion.Identity) pa.Orientation = rot;
            uint id = plane.RootPart.LocalId;
            pa.VehicleType = (int)Vehicle.TYPE_AIRPLANE;
            JoltPrim jp;
            lock (_prims) _prims.TryGetValue(id, out jp);
            return (plane, pa, jp, id);
        }

        // ---- plane (thrust): held forward motor -> airspeed builds ---------------------------------
        private void PlaneThrustTest()
        {
            float cx = 128f, cy = 128f;
            float z0 = TerrainHeightAt(cx, cy) + 100f;   // high in the air
            var (plane, pa, jp, id) = RezPlane(cx, cy, z0, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} planetest: plane has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [planetest:thrust] plane id={id} at z={z0:0.0} (high), type={pa.VehicleType} (expect 4)");
            var motor = new Vector3(15f, 0f, 0f);   // hold forward thrust (a plane needs continuous thrust)
            MainConsole.Instance.Output($"{LogHeader} [planetest:thrust] holding LINEAR_MOTOR_DIRECTION={motor}");
            MainConsole.Instance.Output($"     t   |  fwdSpeed |   speedXY | z-drop");

            float last = 0f;
            for (int i = 0; i <= 8; i++)
            {
                pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, motor);
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out SVector3 lv, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                var fwd = SVector3.Transform(new SVector3(1f, 0f, 0f), o);
                float fwdSpeed = lv.X * fwd.X + lv.Y * fwd.Y + lv.Z * fwd.Z;
                float speedXY = (float)Math.Sqrt(lv.X * lv.X + lv.Y * lv.Y);
                last = fwdSpeed;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {fwdSpeed,9:0.000} | {speedXY,9:0.000} | {z0 - p.Z,7:0.00}");
            }

            bool pass = last > 3f;
            MainConsole.Instance.Output($"{LogHeader} [planetest:thrust] {(pass ? "PASS" : "FAIL")}: plane accelerated to {last:0.00} m/s forward under thrust.");
            _scene.DeleteSceneObject(plane, false);
        }

        // ---- plane (climb): nose-up + thrust -> gains altitude (lift) ------------------------------
        private void PlaneClimbTest()
        {
            // Start back from the +X edge so the fast climb-run (~40 m/s forward) stays inside the region.
            float cx = 64f, cy = 128f;
            float zRez = TerrainHeightAt(cx, cy) + 100f;
            // Pitch >= 23deg: the lift model is a velocity-preserving deflection, so per frame lift ~
            // airspeed * blend * sin(pitch); to beat gravity you need airspeed*sin(pitch) > 9.81. At 25deg
            // the stall airspeed is 9.81/sin(25) = 23.2 m/s, so cruising at 40 clears it with margin.
            Quaternion noseUp = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -25f * (float)(Math.PI / 180.0));
            var (plane, pa, jp, id) = RezPlane(cx, cy, zRez, noseUp);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} planetest: plane has no PhysActor."); return; }

            // The body is created INERT (deferred activation); a velocity set BEFORE it wakes is dropped -
            // Jolt keeps the creation-time velocity (0), so the earlier harness launched at ~0 airspeed
            // (below stall) and sank (fwdSpeed ramped 0->29 instead of starting at 40). Let one Simulate wake
            // the body (DrainPendingActivation), THEN inject cruise airspeed on the now-ACTIVE body so it is
            // present at t=0. The linear motor keeps a body already moving faster than its ramping target (it
            // never drags it down, see the adjvel guard), so the injected 40 persists and lift beats gravity.
            System.Threading.Thread.Sleep(600);              // wake the deferred body
            pa.Velocity = new Vector3(40f, 0f, 0f);          // inject cruise airspeed on the ACTIVE body
            var motor = new Vector3(40f, 0f, 0f);            // sustain ~40 m/s along the nose
            // Re-baseline altitude at cruise-injection (ignore the tiny fall during the ~0.6 s spin-up).
            float z0 = zRez;
            if (CarState(jp, out _, out SVector3 pz0, out _, out _)) z0 = pz0.Z;
            MainConsole.Instance.Output($"{LogHeader} [planetest:climb] plane id={id} nose-up ~25deg, cruise 40 m/s at z={z0:0.0}, type={pa.VehicleType} (expect 4)");
            MainConsole.Instance.Output($"{LogHeader} [planetest:climb] holding thrust={motor} (cruise airspeed injected on active body; fwdSpeed should read ~40 at t=0)");
            MainConsole.Instance.Output($"     t   |  altitude | z-gain | fwdSpeed");

            float maxGain = float.MinValue;
            for (int i = 0; i <= 7; i++)
            {
                pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, motor);
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out SVector3 lv, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                var fwd = SVector3.Transform(new SVector3(1f, 0f, 0f), o);
                float fwdSpeed = lv.X * fwd.X + lv.Y * fwd.Y + lv.Z * fwd.Z;
                float gain = p.Z - z0;
                if (gain > maxGain) maxGain = gain;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {p.Z,9:0.00} | {gain,6:0.00} | {fwdSpeed,8:0.00}");
            }

            bool pass = maxGain > 1f;   // climbed clearly above the start altitude (lift beat gravity)
            MainConsole.Instance.Output($"{LogHeader} [planetest:climb] {(pass ? "PASS" : "FAIL")}: max altitude gain {maxGain:0.00} m above cruise-start (lift).");
            _scene.DeleteSceneObject(plane, false);
        }

        // ---- plane (bank): rolled + thrust -> heading turns (banks to turn) ------------------------
        private void PlaneBankTest()
        {
            float cx = 128f, cy = 128f;
            float z0 = TerrainHeightAt(cx, cy) + 100f;
            Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 25f * (float)(Math.PI / 180.0));
            var (plane, pa, jp, id) = RezPlane(cx, cy, z0, roll);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} planetest: plane has no PhysActor."); return; }

            // A plane turns by BANKING (roll->yaw), and the bank must be HELD or the vertical attractor
            // levels the wings and the turn washes out (the old harness applied only thrust, so a born-25deg
            // bank self-leveled and it turned just 11.6deg). Hold a roll input each tick (ANGULAR_MOTOR.X) so
            // the bank is sustained; the weak airplane attractor lets a modest held roll settle at a steady
            // bank -> a steady banking turn. Forward airspeed feeds the dynamic half of banking->yaw.
            pa.Velocity = new Vector3(15f, 0f, 0f);      // forward airspeed (nose is +X; roll is about the nose)
            var thrust = new Vector3(20f, 0f, 0f);
            var heldRoll = new Vector3(0.15f, 0f, 0f);   // sustain the bank at a gentler steady angle (roll rate, body X)
            MainConsole.Instance.Output($"{LogHeader} [planetest:bank] plane id={id} rolled ~25deg at z={z0:0.0}, type={pa.VehicleType} (expect 4)");
            MainConsole.Instance.Output($"{LogHeader} [planetest:bank] holding thrust + HELD roll {heldRoll} (sustain the bank -> banking turn)");
            MainConsole.Instance.Output($"     t   |  heading  | tiltDeg");

            float startHeading = float.NaN, lastHeading = float.NaN;
            for (int i = 0; i <= 10; i++)
            {
                pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, thrust);
                pa.VehicleVectorParam((int)Vehicle.ANGULAR_MOTOR_DIRECTION, heldRoll);
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out _, out _, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                float h = HeadingDeg(o);
                if (float.IsNaN(startHeading)) startHeading = h;
                lastHeading = h;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {h,8:0.0} | {tilt,8:0.0}");
            }

            float netYaw = Math.Abs(lastHeading - startHeading);
            if (netYaw > 180f) netYaw = 360f - netYaw;
            bool pass = netYaw > 15f;
            MainConsole.Instance.Output($"{LogHeader} [planetest:bank] {(pass ? "PASS" : "FAIL")}: banked plane turned {netYaw:0.0} deg (banks to turn).");
            _scene.DeleteSceneObject(plane, false);
        }

        // M8 BALLOON proofs. `jolt balloontest [hover|lift|drift]` (default hover). Rezzes a physical
        // VEHICLE_TYPE_BALLOON in the air; buoyancy 1.0 cancels gravity so it HANGS (hover trims to ~5 m above
        // ground). The 5th and final SL type:
        //   hover : no input -> hangs in mid-air (doesn't fall to the ground like a car)
        //   lift  : Z-up motor -> climbs (live vertical motor, no airspeed needed - hover clamps most of it)
        //   drift : horizontal motor -> drifts gently
        private void JoltBalloonTest(string scenario)
        {
            if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
            switch (scenario)
            {
                case "lift":  BalloonLiftTest();  break;
                case "drift": BalloonDriftTest(); break;
                default:      BalloonHoverTest(); break;
            }
        }

        // Rez a physical VEHICLE_TYPE_BALLOON box at (x,y,z). Returns the SOG + PhysActor + JoltPrim + id.
        private (SceneObjectGroup sog, PhysicsActor pa, JoltPrim jp, uint id) RezBalloon(float x, float y, float z, Quaternion rot)
        {
            SceneObjectGroup balloon = RezTestPrim("box", new Vector3(x, y, z), new Vector3(2f, 2f, 2f));
            if (rot != Quaternion.Identity)
                balloon.UpdateGroupRotationR(rot);
            balloon.ScriptSetPhysicsStatus(true);
            PhysicsActor pa = balloon.RootPart.PhysActor;
            if (pa == null) { _scene.DeleteSceneObject(balloon, false); return (null, null, null, 0); }
            if (rot != Quaternion.Identity) pa.Orientation = rot;
            uint id = balloon.RootPart.LocalId;
            pa.VehicleType = (int)Vehicle.TYPE_BALLOON;
            JoltPrim jp;
            lock (_prims) _prims.TryGetValue(id, out jp);
            return (balloon, pa, jp, id);
        }

        // ---- balloon (hover): no input -> hangs in mid-air (buoyancy 1.0 cancels gravity) ----------
        private void BalloonHoverTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            float z0 = ground + 10f;   // rez in the air
            var (balloon, pa, jp, id) = RezBalloon(cx, cy, z0, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} balloontest: balloon has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [balloontest:hover] balloon id={id} at z-ground={z0 - ground:0.0}, type={pa.VehicleType} (expect 5), NO input");
            MainConsole.Instance.Output($"     t   |  z-ground |    vZ");

            float minClear = float.MaxValue;
            for (int i = 0; i <= 10; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out SVector3 lv, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                float clear = p.Z - TerrainHeightAt(p.X, p.Y);
                if (i >= 1 && clear < minClear) minClear = clear;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {clear,8:0.00} | {lv.Z,7:0.000}");
            }

            bool pass = minClear > 2f;   // stayed airborne (hung) - a car would fall to the ground (0)
            MainConsole.Instance.Output($"{LogHeader} [balloontest:hover] {(pass ? "PASS" : "FAIL")}: balloon HUNG in mid-air (min clearance {minClear:0.00} m; a car falls to 0).");
            _scene.DeleteSceneObject(balloon, false);
        }

        // ---- balloon (lift): Z-up motor -> climbs (live vertical motor, no airspeed) ---------------
        private void BalloonLiftTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            // Rez AT the hover height (~5 m) so there is almost no descent to drain - the old harness rezzed
            // at +10 m and measured the baseline mid-descent (hover TS 10 is slow), so the slow Z-motor spent
            // seconds fighting the ongoing sink and showed no net gain. Settle first, THEN measure the climb
            // from the settled hover point (the same measure-from-equilibrium the unit test uses).
            var (balloon, pa, jp, id) = RezBalloon(cx, cy, ground + 5f, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} balloontest: balloon has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [balloontest:lift] balloon id={id} type={pa.VehicleType} (expect 5)");
            System.Threading.Thread.Sleep(3500);   // settle to a steady hover (drain the spawn transient) FIRST
            float baseClear = 0f;
            if (CarState(jp, out _, out SVector3 pb, out _, out _)) baseClear = pb.Z - TerrainHeightAt(pb.X, pb.Y);
            var up = new Vector3(0f, 0f, 15f);   // Z linear motor - straight up, no airspeed
            MainConsole.Instance.Output($"{LogHeader} [balloontest:lift] settled at z-ground={baseClear:0.00}; holding Z-up motor {up} (slow climb - give it time)");
            MainConsole.Instance.Output($"     t   |  z-ground |    vZ");

            float maxClear = baseClear;
            for (int i = 0; i <= 16; i++)   // longer window: the Z motor is deliberately slow (TS 5, decay 60)
            {
                pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, up);
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out SVector3 lv, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                float clear = p.Z - TerrainHeightAt(p.X, p.Y);
                if (clear > maxClear) maxClear = clear;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {clear,8:0.00} | {lv.Z,7:0.000}");
            }

            bool pass = maxClear > baseClear + 0.5f;   // the live Z-motor lifted it above its settled hover hold
            MainConsole.Instance.Output($"{LogHeader} [balloontest:lift] {(pass ? "PASS" : "FAIL")}: Z-motor lifted balloon {maxClear - baseClear:0.00} m above settled hover (live vertical motor).");
            _scene.DeleteSceneObject(balloon, false);
        }

        // ---- balloon (drift): horizontal motor -> gentle drift -------------------------------------
        private void BalloonDriftTest()
        {
            float cx = 128f, cy = 128f;
            float ground = TerrainHeightAt(cx, cy);
            var (balloon, pa, jp, id) = RezBalloon(cx, cy, ground + 10f, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} balloontest: balloon has no PhysActor."); return; }

            MainConsole.Instance.Output($"{LogHeader} [balloontest:drift] balloon id={id} type={pa.VehicleType} (expect 5)");
            System.Threading.Thread.Sleep(1000);   // settle to hover height
            var motor = new Vector3(5f, 0f, 0f);   // gentle horizontal motor
            MainConsole.Instance.Output($"{LogHeader} [balloontest:drift] holding horizontal motor {motor} (gentle drift)");
            MainConsole.Instance.Output($"     t   |  speedXY | z-ground");

            float maxSpeed = 0f;
            for (int i = 0; i <= 8; i++)
            {
                pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, motor);
                System.Threading.Thread.Sleep(500);
                if (!CarState(jp, out float tilt, out SVector3 p, out SVector3 lv, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                float speedXY = (float)Math.Sqrt(lv.X * lv.X + lv.Y * lv.Y);
                if (speedXY > maxSpeed) maxSpeed = speedXY;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {speedXY,8:0.000} | {p.Z - TerrainHeightAt(p.X, p.Y),8:0.00}");
            }

            bool pass = maxSpeed > 0.3f;   // drifted horizontally under the gentle motor
            MainConsole.Instance.Output($"{LogHeader} [balloontest:drift] {(pass ? "PASS" : "FAIL")}: balloon drifted (max XY speed {maxSpeed:0.00} m/s).");
            _scene.DeleteSceneObject(balloon, false);
        }

        // Ensure there is deep water at the test spot. Grid-scans the terrain field for the deepest
        // spot; if the region is a plateau above the water plane (no open water), cooks a PHYSICS-ONLY
        // 48 m basin (scene heightmap untouched -> no taint -> the terrain tick won't re-push it mid
        // test). Returns the restore heightmap (null if real water was found); caller SetTerrain()s it
        // back at teardown. bx/by/water are the chosen spot + water plane.
        private float[] EnsureBoatWater(out float bx, out float by, out float water)
        {
            water = WaterLevel;
            bx = 128f; by = 128f;
            float bestDepth = float.MinValue;
            for (int gy = 24; gy <= _regionSizeY - 24; gy += 8)
                for (int gx = 24; gx <= _regionSizeX - 24; gx += 8)
                {
                    float depth = water - TerrainHeightAt(gx, gy);
                    if (depth > bestDepth) { bestDepth = depth; bx = gx; by = gy; }
                }

            if (bestDepth >= 2f)
                return null;

            bx = 128f; by = 128f;
            float[] hm = _scene.Heightmap.GetFloatsSerialised();
            float[] restoreHm = (float[])hm.Clone();
            for (int gy = (int)by - 24; gy <= (int)by + 24; gy++)
                for (int gx = (int)bx - 24; gx <= (int)bx + 24; gx++)
                    if (gx >= 0 && gx < _regionSizeX && gy >= 0 && gy < _regionSizeY)
                        hm[gy * _regionSizeX + gx] = water - 6f;
            SetTerrain(hm);
            MainConsole.Instance.Output($"{LogHeader} [boattest] no open water in this region (deepest spot {bestDepth:0.0} m) - cooked a physics-only 48 m basin at ({bx:0},{by:0}), terrain there now {TerrainHeightAt(bx, by):0.00}, restored after the test.");
            return restoreHm;
        }

        // Rez a physical VEHICLE_TYPE_BOAT box at (x,y,z). Returns the SOG + its PhysActor + JoltPrim +
        // id, or (null,...) on failure (caller checks pa).
        private (SceneObjectGroup sog, PhysicsActor pa, JoltPrim jp, uint id) RezBoat(float x, float y, float z, Quaternion rot)
        {
            SceneObjectGroup boat = RezTestPrim("box", new Vector3(x, y, z), new Vector3(2f, 1f, 0.5f));
            if (rot != Quaternion.Identity)
                boat.UpdateGroupRotationR(rot);   // born tilted: ApplyPhysics below cooks the body at this rot
            boat.ScriptSetPhysicsStatus(true);
            PhysicsActor pa = boat.RootPart.PhysActor;
            if (pa == null) { _scene.DeleteSceneObject(boat, false); return (null, null, null, 0); }
            if (rot != Quaternion.Identity) pa.Orientation = rot;   // belt-and-braces: ensure the body carries it
            uint id = boat.RootPart.LocalId;
            pa.VehicleType = (int)Vehicle.TYPE_BOAT;
            JoltPrim jp;
            lock (_prims) _prims.TryGetValue(id, out jp);
            return (boat, pa, jp, id);
        }

        // Tilt (deg) of the body's local +Z from world up, and world Z-position, from the live body.
        private bool BoatState(JoltPrim jp, out float tiltDeg, out float posZ, out SVector3 linVel, out SVector3 angVel, out SQuaternion orient)
        {
            tiltDeg = 0f; posZ = float.NaN; linVel = default; angVel = default; orient = SQuaternion.Identity;
            if (jp == null || !_backend.TryGetBodyState(jp.BodyHandle, out BodyState st))
                return false;
            var up = SVector3.Transform(new SVector3(0f, 0f, 1f), st.Orientation);
            tiltDeg = (float)(Math.Acos(Math.Clamp(up.Z, -1f, 1f)) * 180.0 / Math.PI);
            posZ = st.Position.Z; linVel = st.LinearVelocity; angVel = st.AngularVelocity; orient = st.Orientation;
            return true;
        }

        // ---- slice (a): linear motor ----------------------------------------------------------
        private void BoatLinearTest()
        {
            float[] restoreHm = EnsureBoatWater(out float bx, out float by, out float water);
            var (boat, pa, jp, id) = RezBoat(bx, by, water + 0.4f, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} boattest: boat has no PhysActor."); if (restoreHm != null) SetTerrain(restoreHm); return; }

            MainConsole.Instance.Output($"{LogHeader} [boattest:linear] boat id={id} at ({bx:0},{by:0}) water={water:0.00} mass={pa.Mass:0.00} type={pa.VehicleType} (expect 3)");
            var motor = new Vector3(4f, 0f, 0f);   // hold forward at 4 m/s (local +X)
            MainConsole.Instance.Output($"{LogHeader} [boattest:linear] holding LINEAR_MOTOR_DIRECTION={motor} (re-set every 0.5 s)");
            MainConsole.Instance.Output($"     t   |  fwdSpeed |   speedXY |  z-water  |  tiltDeg");

            float first = float.NaN, last = float.NaN;
            for (int i = 0; i <= 8; i++)
            {
                pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, motor);
                System.Threading.Thread.Sleep(500);
                if (!BoatState(jp, out float tilt, out float posZ, out SVector3 lv, out _, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | (no body state)"); continue; }
                var fwd = SVector3.Transform(new SVector3(1f, 0f, 0f), o);
                float fwdSpeed = lv.X * fwd.X + lv.Y * fwd.Y + lv.Z * fwd.Z;
                float speedXY = (float)Math.Sqrt(lv.X * lv.X + lv.Y * lv.Y);
                if (i == 1) first = fwdSpeed;
                last = fwdSpeed;
                MainConsole.Instance.Output($"  {i * 0.5f,4:0.0}s | {fwdSpeed,9:0.000} | {speedXY,9:0.000} | {posZ - water,9:0.000} | {tilt,8:0.0}");
            }

            bool pass = last > 1.5f && (float.IsNaN(first) || last >= first - 0.25f);
            MainConsole.Instance.Output($"{LogHeader} [boattest:linear] {(pass ? "PASS" : "FAIL")}: reached {last:0.00} m/s forward (target 4).");
            _scene.DeleteSceneObject(boat, false);
            if (restoreHm != null) SetTerrain(restoreHm);
        }

        // ---- slice (b): hover -----------------------------------------------------------------
        // Boat preset: HOVER_HEIGHT 0.5, HOVER_EFFICIENCY 0.8, HOVER_TIMESCALE 0.2, HoverWaterOnly.
        // Target rest position Z = water + 0.5. Three sub-scenarios, fresh boat each: settle from
        // above, rise from below, hold at rest. No motor - pure Z-balance.
        private void BoatHoverTest()
        {
            float[] restoreHm = EnsureBoatWater(out float bx, out float by, out float water);
            const float target = 0.5f;   // HOVER_HEIGHT for the boat preset
            MainConsole.Instance.Output($"{LogHeader} [boattest:hover] target rest z-water = +{target:0.00} (HoverWaterOnly, water={water:0.00})");

            bool allPass = true;
            // (start z-water offset, label, expected trend)
            var cases = new (float z0, string label)[] { (3.0f, "settle-from-above"), (-3.0f, "rise-from-below"), (0.5f, "hold-at-rest") };
            foreach (var (z0, label) in cases)
            {
                var (boat, pa, jp, id) = RezBoat(bx, by, water + z0, Quaternion.Identity);
                if (pa == null) { MainConsole.Instance.Output($"{LogHeader} [boattest:hover] {label}: no PhysActor."); allPass = false; continue; }

                MainConsole.Instance.Output($"{LogHeader} [boattest:hover] {label}: start z-water={z0:+0.00;-0.00}, id={id}");
                MainConsole.Instance.Output($"     t   |  z-water  |    vZ     |  tiltDeg");
                float lastZ = float.NaN, minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i <= 10; i++)
                {
                    System.Threading.Thread.Sleep(400);
                    if (!BoatState(jp, out float tilt, out float posZ, out SVector3 lv, out _, out _))
                    { MainConsole.Instance.Output($"  {i * 0.4f,4:0.0}s | (no body state)"); continue; }
                    lastZ = posZ - water;
                    if (i >= 5) { minZ = Math.Min(minZ, lastZ); maxZ = Math.Max(maxZ, lastZ); }   // steady-state band (last ~2 s)
                    MainConsole.Instance.Output($"  {i * 0.4f,4:0.0}s | {lastZ,9:0.000} | {lv.Z,9:0.000} | {tilt,8:0.0}");
                }
                // settled near target and steady-state band tight (not oscillating/drifting)
                bool settled = Math.Abs(lastZ - target) < 0.25f;
                bool steady = (maxZ - minZ) < 0.30f;
                bool pass = settled && steady;
                allPass &= pass;
                MainConsole.Instance.Output($"{LogHeader} [boattest:hover] {label}: {(pass ? "PASS" : "FAIL")} (final z-water={lastZ:0.000}, target+{target:0.00}; steady-band={(maxZ - minZ):0.000})");
                _scene.DeleteSceneObject(boat, false);
            }

            MainConsole.Instance.Output($"{LogHeader} [boattest:hover] {(allPass ? "ALL PASS" : "CHECK")}: boat settles to water+{target:0.00} from above & below and holds (HoverWaterOnly Z-balance).");
            if (restoreHm != null) SetTerrain(restoreHm);
        }

        // ---- slice (c): vertical attractor ----------------------------------------------------
        // Rez the boat rolled 40 deg; with the vertical attractor (eff 0.5, TS 0.2) it must roll back
        // upright over a couple of seconds (not instant-snap, not never). Then a YAW check: spin it in
        // yaw and confirm the attractor lets the heading change freely while still holding it level.
        private void BoatAttractTest()
        {
            float[] restoreHm = EnsureBoatWater(out float bx, out float by, out float water);

            // -- self-right from a 30 deg roll (a realistic wave/wake tilt, not a capsize) --
            // Sampled at 0.25 s so a fast recovery isn't aliased; 24 samples = 6 s. The boat preset's
            // VERTICAL_ATTRACTION_TIMESCALE is a stiff 0.2 s, so recovery is snappy and can ring a
            // little on a hard tilt - we assert it RECOVERS and HOLDS near level (mean of the last 2 s),
            // not that it's critically damped. A/B vs BulletSim (same math) characterizes any ringing.
            float roll0 = 30f;
            Quaternion tilt = Quaternion.CreateFromEulers((float)(roll0 * Math.PI / 180.0), 0f, 0f);
            var (boat, pa, jp, id) = RezBoat(bx, by, water + 0.5f, tilt);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} [boattest:attract] no PhysActor."); if (restoreHm != null) SetTerrain(restoreHm); return; }

            MainConsole.Instance.Output($"{LogHeader} [boattest:attract] self-right: rezzed rolled {roll0:0}deg, id={id} (sample 0.25s x 24)");
            MainConsole.Instance.Output($"     t   |  tiltDeg  |  z-water  | rollRate(deg/s)");
            float firstPeak = 0f;          // worst tilt in the first 1 s (the initial swing)
            float lateSum = 0f, lateMax = 0f; int lateN = 0;   // last 2 s (settled band)
            float timeToLevel = -1f;
            for (int i = 0; i <= 24; i++)
            {
                System.Threading.Thread.Sleep(250);
                if (!BoatState(jp, out float td, out float posZ, out _, out SVector3 av, out _))
                { MainConsole.Instance.Output($"  {i * 0.25f,4:0.0}s | (no body state)"); continue; }
                float t = i * 0.25f;
                if (t <= 1.0f) firstPeak = Math.Max(firstPeak, td);
                if (timeToLevel < 0 && td < 8f) timeToLevel = t;
                if (t >= 4.0f) { lateSum += td; lateMax = Math.Max(lateMax, td); lateN++; }
                float rollRateDeg = (float)(av.X * 180.0 / Math.PI);
                MainConsole.Instance.Output($"  {t,4:0.0}s | {td,8:0.0} | {posZ - water,9:0.000} | {rollRateDeg,8:0.0}");
            }
            float lateMean = lateN > 0 ? lateSum / lateN : float.NaN;
            // Recovered = reached near-level at least once; holds = last-2s mean stays near level even
            // if it rings a bit; started genuinely tilted.
            bool recovered = timeToLevel >= 0f;
            bool holdsLevel = lateMean < 12f && lateMax < 22f;
            bool startedTilted = firstPeak > 15f;
            bool selfRightPass = recovered && holdsLevel && startedTilted;
            MainConsole.Instance.Output($"{LogHeader} [boattest:attract] self-right: {(selfRightPass ? "PASS" : "CHECK")} (peak {firstPeak:0}deg -> reached <8deg at {(timeToLevel < 0 ? "never" : timeToLevel.ToString("0.0") + "s")}; last-2s mean {lateMean:0.0}deg max {lateMax:0.0}deg)");
            _scene.DeleteSceneObject(boat, false);

            // -- yaw is free: attractor must NOT cancel a heading change. Drive yaw with a repeated
            // angular-velocity nudge; the boat's heavy Z angular friction (preset TS 0.1) fights an
            // imposed spin down, so the SIGNAL is "heading still moves AND tilt stays ~0", not a big
            // yaw rate. If the attractor were fighting yaw, tilt would spike or heading would freeze.
            var (boat2, pa2, jp2, id2) = RezBoat(bx, by, water + 0.5f, Quaternion.Identity);
            bool yawPass = false;
            if (pa2 != null)
            {
                MainConsole.Instance.Output($"{LogHeader} [boattest:attract] yaw-free: nudging yaw (1 rad/s each 0.4s), id={id2} (heading must change, tilt must stay ~0)");
                MainConsole.Instance.Output($"     t   |  yawDeg   |  tiltDeg");
                float yaw0 = float.NaN, yawLast = 0f, maxTilt = 0f;
                for (int i = 0; i <= 8; i++)
                {
                    pa2.RotationalVelocity = new Vector3(0f, 0f, 1.0f);   // nudge a yaw spin (friction fights it down)
                    System.Threading.Thread.Sleep(400);
                    if (!BoatState(jp2, out float td, out _, out _, out _, out SQuaternion o)) continue;
                    var e = QuatYawDeg(o);
                    if (float.IsNaN(yaw0)) yaw0 = e;
                    yawLast = e; maxTilt = Math.Max(maxTilt, td);
                    MainConsole.Instance.Output($"  {i * 0.4f,4:0.0}s | {e,8:0.0} | {td,8:0.0}");
                }
                float yawChange = Math.Abs(YawDelta(yaw0, yawLast));
                yawPass = yawChange > 12f && maxTilt < 8f;   // heading moved despite friction, boat stayed level
                MainConsole.Instance.Output($"{LogHeader} [boattest:attract] yaw-free: {(yawPass ? "PASS" : "CHECK")} (yaw moved {yawChange:0}deg, max tilt {maxTilt:0.0}deg - attractor holds level without fighting yaw)");
                _scene.DeleteSceneObject(boat2, false);
            }

            MainConsole.Instance.Output($"{LogHeader} [boattest:attract] {(selfRightPass && yawPass ? "ALL PASS" : "CHECK")}: boat self-rights from tilt and turns freely in yaw.");
            if (restoreHm != null) SetTerrain(restoreHm);
        }

        // ---- slice (d): angular motor (steering) ----------------------------------------------
        private void BoatSteerTest()
        {
            float[] restoreHm = EnsureBoatWater(out float bx, out float by, out float water);
            var (boat, pa, jp, id) = RezBoat(bx, by, water + 0.5f, Quaternion.Identity);
            if (pa == null) { MainConsole.Instance.Output($"{LogHeader} [boattest:steer] no PhysActor."); if (restoreHm != null) SetTerrain(restoreHm); return; }

            MainConsole.Instance.Output($"{LogHeader} [boattest:steer] boat id={id}. Phase 1: hold ANGULAR_MOTOR yaw=1.0 (turn); Phase 2: release (friction must stop the spin).");
            MainConsole.Instance.Output($"     t   |  yawDeg   | yawRate(deg/s) |  tiltDeg  | phase");

            var steer = new Vector3(0f, 0f, 1.0f);   // yaw motor
            float yaw0 = float.NaN, yawAtRelease = 0f, yawLast = 0f, maxTilt = 0f, rateAtRelease = 0f, rateAtEnd = 0f;
            for (int i = 0; i <= 14; i++)
            {
                bool turning = i < 7;               // first ~2.8 s: hold the turn; then release
                if (turning) pa.VehicleVectorParam((int)Vehicle.ANGULAR_MOTOR_DIRECTION, steer);
                System.Threading.Thread.Sleep(400);
                if (!BoatState(jp, out float td, out _, out _, out SVector3 av, out SQuaternion o))
                { MainConsole.Instance.Output($"  {i * 0.4f,4:0.0}s | (no body state)"); continue; }
                float yaw = QuatYawDeg(o);
                if (float.IsNaN(yaw0)) yaw0 = yaw;
                float yawRate = (float)(av.Z * 180.0 / Math.PI);
                maxTilt = Math.Max(maxTilt, td);
                yawLast = yaw;
                if (i == 6) { yawAtRelease = yaw; rateAtRelease = yawRate; }
                if (i == 14) rateAtEnd = yawRate;
                MainConsole.Instance.Output($"  {i * 0.4f,4:0.0}s | {yaw,8:0.0} | {yawRate,8:0.0}       | {td,8:0.0} | {(turning ? "TURN" : "coast")}");
            }

            float turnedWhileDriven = Math.Abs(YawDelta(yaw0, yawAtRelease));
            bool turns = turnedWhileDriven > 30f;                       // motor produced real yaw
            bool stops = Math.Abs(rateAtEnd) < Math.Abs(rateAtRelease) * 0.35f + 5f;   // friction damped the spin
            bool upright = maxTilt < 12f;                              // stayed level through the turn
            bool pass = turns && stops && upright;
            MainConsole.Instance.Output($"{LogHeader} [boattest:steer] {(pass ? "PASS" : "FAIL")}: turned {turnedWhileDriven:0}deg under motor (rate {rateAtRelease:0}deg/s), coasted to {rateAtEnd:0}deg/s after release (friction), max tilt {maxTilt:0.0}deg.");
            MainConsole.Instance.Output($"{LogHeader} [boattest:steer] (turns={turns} frictionStops={stops} stayedUpright={upright})");
            _scene.DeleteSceneObject(boat, false);
            if (restoreHm != null) SetTerrain(restoreHm);
        }

        // Yaw (deg) of a body orientation about world Z.
        private static float QuatYawDeg(SQuaternion q)
        {
            double siny = 2.0 * (q.W * q.Z + q.X * q.Y);
            double cosy = 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z);
            return (float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI);
        }

        // Signed shortest-arc delta between two yaw angles (deg), in [-180,180].
        private static float YawDelta(float a, float b)
        {
            float d = b - a;
            while (d > 180f) d -= 360f;
            while (d < -180f) d += 360f;
            return d;
        }

        // M7 Task 3 (landing 2) proof: per-child collision identity. Build a REAL scene linkset (root=link1 +
        // two children link2/link3), make it physical (-> Jolt compound), subscribe + hook each link's actor
        // (what a root collision script triggers via UpdatePhysicsSubscribedEvents), drop a box onto LINK 3,
        // and confirm the module delivers that collision to LINK 3's actor (not the root). Because OpenSim's
        // PhysicsCollision runs on the receiving part, llDetectedLinkNumber for that collision == 3.
        private void JoltCollideLinkTest()
        {
            float tz = 25f;
            try { tz = (float)_scene.Heightmap[128, 128]; } catch { }
            var size = new Vector3(0.5f, 0.5f, 0.5f);
            Vector3 rootPos = new Vector3(128f, 128f, tz + 0.30f);
            SceneObjectGroup ls = RezTestPrim("box", rootPos, size);
            SceneObjectGroup a = RezTestPrim("box", rootPos + new Vector3(0f, 0.7f, 0f), size);
            SceneObjectGroup b = RezTestPrim("box", rootPos + new Vector3(0f, 1.4f, 0f), size);
            ls.LinkToGroup(a);              // real scene link: root=1, a=link2, b=link3
            ls.LinkToGroup(b);
            ls.ScriptSetPhysicsStatus(true);   // physical linkset -> compound welded via child.link(root)
            System.Threading.Thread.Sleep(1000);

            SceneObjectPart link3 = ls.GetLinkNumPart(3);
            if (ls.RootPart.PhysActor == null || link3 == null || ls.PrimCount < 3)
            {
                MainConsole.Instance.Output($"{LogHeader} collidelinktest: linkset setup failed (parts={ls.PrimCount}).");
                try { _scene.DeleteSceneObject(ls, false); } catch { }
                return;
            }

            UUID dropUUID = UUID.Zero;

            // (a) EventManager capture: the DetectedObject.linkNumber OpenSim hands the script engine (what
            //     Phlox's collision handler consumes via DetectParams.Populate). Register real collision flags
            //     on the ROOT so OpenSim subscribes every linkset part + delivers via OnScriptColliderStart.
            UUID fakeItem = UUID.Random();
            ls.RootPart.SetScriptEvents(fakeItem, (ulong)(scriptEvents.collision_start | scriptEvents.collision | scriptEvents.collision_end));
            var emLinks = new List<int>();
            EventManager.ScriptColliding emHandler = (uint localID, ColliderArgs col) =>
            {
                lock (emLinks)
                    foreach (DetectedObject d in col.Colliders)
                        if (dropUUID != UUID.Zero && d.keyUUID == dropUUID) emLinks.Add(d.linkNumber);
            };
            _scene.EventManager.OnScriptColliderStart += emHandler;
            _scene.EventManager.OnScriptColliding += emHandler;

            // (b) REAL Phlox script on the ROOT: capture what llDetectedLinkNumber ACTUALLY returns (the full
            //     Phlox VM path - the exact thing John's viewer script sees), via llSay -> OnChatFromWorld.
            var scriptLinks = new List<int>();
            EventManager.ChatFromWorldEvent chatHandler = (object sender, OSChatMessage m) =>
            {
                if (m?.Message != null && m.Message.StartsWith("COLLIDELINK="))
                    if (int.TryParse(m.Message.Substring("COLLIDELINK=".Length), out int L)) lock (scriptLinks) scriptLinks.Add(L);
            };
            _scene.EventManager.OnChatFromWorld += chatHandler;

            UUID owner = ls.RootPart.OwnerID;
            string lsl = "default { collision_start(integer n) { llSay(0, \"COLLIDELINK=\" + (string)llDetectedLinkNumber(0)); } }";
            bool scriptRezzed = false;
            try
            {
                // Manual rez (bypass the CanCreateObjectInventory gate - no avatar is logged in headless).
                var asset = new AssetBase(UUID.Random(), "collidelink-probe", (sbyte)AssetType.LSLText, owner.ToString())
                { Data = System.Text.Encoding.ASCII.GetBytes(lsl) };
                _scene.AssetService.Store(asset);
                var taskItem = new TaskInventoryItem
                {
                    ItemID = UUID.Random(), AssetID = asset.FullID,
                    ParentPartID = ls.RootPart.UUID, ParentID = ls.RootPart.UUID,
                    Name = "collidelink-probe", Description = "",
                    Type = (int)AssetType.LSLText, InvType = (int)InventoryType.LSL,
                    OwnerID = owner, CreatorID = owner,
                    BasePermissions = (uint)OpenMetaverse.PermissionMask.All, CurrentPermissions = (uint)OpenMetaverse.PermissionMask.All,
                    EveryonePermissions = 0, NextPermissions = (uint)OpenMetaverse.PermissionMask.All, GroupPermissions = 0,
                    GroupID = UUID.Zero, Flags = 0, CreationDate = 0, PermsGranter = UUID.Zero, PermsMask = 0,
                };
                ls.RootPart.Inventory.AddInventoryItem(taskItem, false);
                scriptRezzed = ls.RootPart.Inventory.CreateScriptInstance(taskItem, 0, false, _scene.DefaultScriptEngine, 1);
            }
            catch (System.Exception ex) { MainConsole.Instance.Output($"{LogHeader} collidelinktest: manual script rez failed: {ex.Message}"); }
            System.Threading.Thread.Sleep(2500);   // compile + start + settle

            Vector3 c3 = link3.GetWorldPosition();
            SceneObjectGroup drop = RezTestPrim("box", new Vector3(c3.X, c3.Y, c3.Z + 3.5f), size);
            dropUUID = drop.RootPart.UUID;
            drop.ScriptSetPhysicsStatus(true);
            System.Threading.Thread.Sleep(4500);   // fall + strike link 3

            _scene.EventManager.OnScriptColliderStart -= emHandler;
            _scene.EventManager.OnScriptColliding -= emHandler;
            _scene.EventManager.OnChatFromWorld -= chatHandler;
            try { ls.RootPart.RemoveScriptEvents(fakeItem); } catch { }

            string emVals, scVals;
            lock (emLinks) emVals = string.Join(",", emLinks);
            lock (scriptLinks) scVals = string.Join(",", scriptLinks);
            MainConsole.Instance.Output($"{LogHeader} [collidelinktest] box struck link 3 ({link3.LocalId}). scriptRezzed={scriptRezzed}");
            MainConsole.Instance.Output($"{LogHeader} [collidelinktest] EventManager DetectedObject.linkNumber=[{emVals}]  |  REAL script llDetectedLinkNumber=[{scVals}]  (want 3)");
            bool emOk; lock (emLinks) emOk = emLinks.Contains(3);
            bool scOk; lock (scriptLinks) scOk = scriptLinks.Contains(3);
            MainConsole.Instance.Output($"{LogHeader} [collidelinktest] {((emOk && scOk) ? "PASS" : "CHECK")}: EventManager={(emOk ? "3" : "not-3")}, script={(scOk ? "3" : "not-3")}. {(emOk && !scOk ? "-> BREAK IS IN THE PHLOX VM (EventManager correct, script wrong)." : "")}");

            _scene.DeleteSceneObject(drop, false);
            _scene.DeleteSceneObject(ls, false);
        }

        // M7 Task 2 proof: build a physical linkset (root + 2 children), then UNLINK the way OpenSim does
        // (PhysicsScene.RemovePrim(childPa) -> JoltPrim.Destroy -> detach + rebuild) and watch the compound
        // mass track membership: 3x -> 2x -> 1x (down-to-one reverts to a plain single body, NOT a degenerate
        // 1-child compound). Then repeated link/unlink cycles: mass must return to exactly `single` each time
        // (a leaked/stale/double body would drift it up). Console proof of the live rebuild + no-leak.
        private void JoltUnlinkTest()
        {
            float tz = 25f;
            try { tz = (float)_scene.Heightmap[128, 128]; } catch { }
            Vector3 rootPos = new Vector3(128f, 128f, tz + 12f);
            var size = new Vector3(0.5f, 0.5f, 0.5f);
            SceneObjectGroup root = RezTestPrim("box", rootPos, size);
            root.ScriptSetPhysicsStatus(true);
            PhysicsActor rpa = root.RootPart.PhysActor;
            if (rpa == null) { MainConsole.Instance.Output($"{LogHeader} unlinktest: no root PhysActor."); return; }
            float single = rpa.Mass;

            // Multi-child + down-to-one: link 2 (3x), unlink each back to the single root body.
            SceneObjectGroup c1 = RezTestPrim("box", rootPos + new Vector3(0.6f, 0f, 0f), size); c1.ScriptSetPhysicsStatus(true);
            SceneObjectGroup c2 = RezTestPrim("box", rootPos + new Vector3(0f, 0.6f, 0f), size); c2.ScriptSetPhysicsStatus(true);
            c1.RootPart.PhysActor?.link(rpa);
            c2.RootPart.PhysActor?.link(rpa);
            System.Threading.Thread.Sleep(500);   // rebuild coalesced to the next Simulate
            float m3 = rpa.Mass;
            RemovePrim(c1.RootPart.PhysActor); c1.RootPart.PhysActor = null;   // OpenSim's unlink handoff
            System.Threading.Thread.Sleep(500);
            float m2 = rpa.Mass;
            RemovePrim(c2.RootPart.PhysActor); c2.RootPart.PhysActor = null;
            System.Threading.Thread.Sleep(500);
            float m1 = rpa.Mass;
            MainConsole.Instance.Output($"{LogHeader} [unlinktest] single={single:0.0}  linked3={m3:0.0}(~{single * 3f:0.0})  unlink->{m2:0.0}(~{single * 2f:0.0})  unlink->{m1:0.0}(~{single:0.0}=single, down-to-one clean)");
            _scene.DeleteSceneObject(c1, false); _scene.DeleteSceneObject(c2, false);

            // Repeated link/unlink cycles (fresh child each time): mass returns to `single` every cycle.
            bool cyclesOk = true; string cyc = "";
            for (int i = 0; i < 5; i++)
            {
                SceneObjectGroup ch = RezTestPrim("box", rootPos + new Vector3(0.6f, 0f, 0f), size);
                ch.ScriptSetPhysicsStatus(true);
                ch.RootPart.PhysActor?.link(rpa);
                System.Threading.Thread.Sleep(350);
                float up = rpa.Mass;
                RemovePrim(ch.RootPart.PhysActor); ch.RootPart.PhysActor = null;
                System.Threading.Thread.Sleep(350);
                float down = rpa.Mass;
                _scene.DeleteSceneObject(ch, false);
                cyc += $" [{up:0.0}/{down:0.0}]";
                if (System.Math.Abs(up - single * 2f) > single * 0.1f || System.Math.Abs(down - single) > single * 0.1f) cyclesOk = false;
            }
            MainConsole.Instance.Output($"{LogHeader} [unlinktest] 5x link/unlink up/down:{cyc}");
            bool ok = System.Math.Abs(m3 - single * 3f) < single * 0.15f && System.Math.Abs(m2 - single * 2f) < single * 0.15f
                      && System.Math.Abs(m1 - single) < single * 0.1f && cyclesOk;
            MainConsole.Instance.Output($"{LogHeader} [unlinktest] {(ok ? "PASS" : "CHECK")}: unlink rebuilds 3x->2x->1x, down-to-one=single body, 5x cycles return to single (no leak/stale/double).");

            _scene.DeleteSceneObject(root, false);
        }

        // Build one basic prim with a CANONICAL PrimitiveBaseShape (a real viewer/OAR prim's values,
        // not the quirky CreateCylinder factory) and rez it through the genuine scene path so OpenSim -
        // not us - calls AddPrimShape. Non-physical, non-phantom by default => a static Jolt body.
        private SceneObjectGroup RezTestPrim(string kind, Vector3 pos, Vector3 size)
        {
            PrimitiveBaseShape pbs;
            switch (kind)
            {
                case "sphere":   pbs = PrimitiveBaseShape.CreateSphere(); break;              // HalfCircle + Curve1
                case "cylinder": pbs = PrimitiveBaseShape.CreateBox();                        // start from Square+Straight (no-cut, scale 100)
                                 pbs.ProfileShape = ProfileShape.Circle; break;               // -> canonical cylinder: Circle + Straight
                case "prism":    pbs = PrimitiveBaseShape.CreateBox();                        // triangular section -> forces the mesher
                                 pbs.ProfileShape = ProfileShape.EquilateralTriangle; break;  // EquilateralTriangle + Straight, no asset
                default:         pbs = PrimitiveBaseShape.CreateBox(); break;                 // Square + Straight
            }

            UUID owner = _scene.RegionInfo.EstateSettings.EstateOwner;
            var sog = new SceneObjectGroup(owner, pos, Quaternion.Identity, pbs);
            sog.RootPart.Scale = size;   // AddPrimShape receives this as `size` (== SceneObjectPart.Scale)

            // attachToBackup:false -> ephemeral (no region-DB residue), but still physics-wired and
            // viewer-visible this session. AttachToScene calls ApplyPhysics synchronously here.
            _scene.AddNewSceneObject(sog, false);

            _testPrims.Add(new TestPrim
            {
                LocalId = sog.RootPart.LocalId,
                Sog = sog.UUID,
                Kind = kind,
                Pos = pos,
                Size = size,
            });
            return sog;
        }

        // Delete every console-rezzed test prim through the real scene-delete path (-> RemovePrim ->
        // backend RemoveBody/ReleaseShape). Returns how many were removed.
        private int ClearTestPrims()
        {
            int n = 0;
            foreach (var tp in _testPrims)
            {
                SceneObjectGroup sog = _scene?.GetSceneObjectGroup(tp.Sog);
                if (sog != null)
                {
                    _scene.DeleteSceneObject(sog, false);
                    n++;
                }
            }
            _testPrims.Clear();
            _drops.Clear();
            return n;
        }

        // Cast the proof rays through Scene.RayCastFiltered - the SAME call llCastRay makes - so this
        // is Jolt answering a real SL-facing raycast, just triggered from the console (no viewer/chat
        // dependency). Each row prints expected-vs-actual-vs-delta and which prim id was struck.
        private void RayPrims()
        {
            // Resolve the three ids for readability.
            uint boxId = 0, sphId = 0, cylId = 0;
            foreach (var tp in _testPrims)
            {
                if (tp.Kind == "box") boxId = tp.LocalId;
                else if (tp.Kind == "sphere") sphId = tp.LocalId;
                else if (tp.Kind == "cylinder") cylId = tp.LocalId;
            }

            const float sq75 = 0.8660254f;   // sqrt(1 - 0.5^2), the sphere offset-surface height
            const float cy = 128f;
            bool haveP = _testPrims.Count > 0;   // false after clearprims -> every row should MISS

            // label, origin, expectedZ (NaN = expect a MISS), expected prim id (0 = n/a), filter
            var rays = new (string label, Vector3 origin, float expZ, uint expId, RayFilterFlags filter)[]
            {
                ("box   top (face)",     new Vector3(120f,  cy, 107f), 102f,      boxId, RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("sphere top (centre)",  new Vector3(128f,  cy, 106f), 101f,      sphId, RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("sphere +0.5 (CURVE)",  new Vector3(128.5f,cy, 106f), 100f+sq75, sphId, RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("cyl top cap (AXIS)",   new Vector3(136f,   cy,    107f), 102f,      cylId, RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("cyl diag .4,.4 ROUND", new Vector3(136.4f, cy+0.4f,107f), float.NaN, 0u,    RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("box, STATIC excluded", new Vector3(120f,  cy, 107f), float.NaN, 0u,    RayFilterFlags.land),
            };

            MainConsole.Instance.Output($"{LogHeader} rayprims via Scene.RayCastFiltered (the llCastRay pipeline) - {_testPrims.Count} test prim(s) live{(haveP ? "" : " -> EVERY row should MISS")}.");
            MainConsole.Instance.Output($"  CURVE proves sphere-surface-not-bbox; AXIS proves the cylinder Z-height correction. (NaN exp = expect miss.)");
            MainConsole.Instance.Output($"     label            |   exp z  |  act z   |  delta  | hit id | note");
            foreach (var r in rays)
            {
                var dir = new Vector3(0f, 0f, -1f);
                var hits = _scene.RayCastFiltered(r.origin, dir, 10f, 4, r.filter) as List<ContactResult>;
                ContactResult? best = null;
                if (hits != null)
                    foreach (var h in hits)
                        if (best == null || h.Depth < best.Value.Depth) best = h;

                // After clearprims there are no prims, so a hit-expecting row should now miss.
                bool expMiss = float.IsNaN(r.expZ) || !haveP;
                if (best == null)
                {
                    string ok = expMiss ? "OK (miss)" : "MISS (expected hit!)";
                    MainConsole.Instance.Output($"  {r.label,-20} | {(float.IsNaN(r.expZ) ? "  miss  " : r.expZ.ToString("0.000")),8} |   miss   |    -    |   -    | {ok}");
                }
                else
                {
                    float az = best.Value.Pos.Z;
                    string del = float.IsNaN(r.expZ) ? "   -    " : $"{az - r.expZ,7:0.000}";
                    string note = expMiss ? "hit (expected MISS!)"
                                 : (best.Value.ConsumerID == r.expId ? "OK" : $"WRONG id (want {r.expId})");
                    MainConsole.Instance.Output($"  {r.label,-20} | {(float.IsNaN(r.expZ) ? "  miss  " : r.expZ.ToString("0.000")),8} | {az,8:0.000} | {del} | {best.Value.ConsumerID,6} | {note}");
                }
            }
            MainConsole.Instance.Output($"  CURVE row exp {100f + sq75:0.000} (bbox would read 101.000); AXIS row exp 102.000 (wrong Y-axis cylinder -> 100.500);");
            MainConsole.Instance.Output($"  ROUND row exp miss (offset 0.566 > radius 0.5; a bbox fallback would instead HIT ~102.000, proving the cross-section is circular).");
        }

        // Closest hit of a single downward-ish cast through the real llCastRay pipeline. null = miss.
        private ContactResult? CastOne(Vector3 origin, Vector3 dir, float length, RayFilterFlags filter)
        {
            var res = _scene.RayCastFiltered(origin, dir, length, 4, filter) as List<ContactResult>;
            ContactResult? best = null;
            if (res != null)
                foreach (var h in res)
                    if (best == null || h.Depth < best.Value.Depth) best = h;
            return best;
        }

        // PERMANENT REGRESSION GUARD for the mesher cache-poisoning bug (delta #38). Rezzes N SEPARATE
        // identical prisms back-to-back: same size/lod -> same Meshmerizer cache key -> the exact
        // repeated-content path that a region with N copies of one mesh asset hits at M6.5. Pre-fix,
        // prim 2..N would get the poisoned shared Mesh and cook to bbox(fallback) (the NotSupportedException
        // now caught by the guard); post-fix, every prim cooks to mesh(mesher). Each is also cast-verified
        // as a real triangle (centre HIT, corner MISS) - not a bbox.
        private void RezMeshN(int count)
        {
            ClearTestPrims();
            var size = new Vector3(4f, 4f, 3f);
            for (int k = 0; k < count; k++)
                RezTestPrim("prism", new Vector3(120f + k * 8f, 128f, 100f), size);

            MainConsole.Instance.Output($"{LogHeader} rezzed {count} IDENTICAL prisms (size {size.X}x{size.Y}x{size.Z} -> same Meshmerizer cache key). Per-prim cook + cast:");
            var filter = RayFilterFlags.land | RayFilterFlags.nonphysical;
            var dir = new Vector3(0f, 0f, -1f);
            int clean = 0, realMesh = 0;
            for (int k = 0; k < _testPrims.Count; k++)
            {
                TestPrim tp = _testPrims[k];
                string kind = "?";
                lock (_prims)
                    if (_prims.TryGetValue(tp.LocalId, out JoltPrim jp)) kind = jp.ShapeKind;
                if (kind == "mesh(mesher)") clean++;

                ContactResult? cHit = CastOne(new Vector3(tp.Pos.X, tp.Pos.Y, 106f), dir, 10f, filter);
                ContactResult? kMiss = CastOne(new Vector3(tp.Pos.X + 1.8f, tp.Pos.Y + 1.8f, 106f), dir, 10f, filter);
                bool triProven = cHit.HasValue && !kMiss.HasValue;   // centre hit + corner miss => real triangle
                if (triProven) realMesh++;

                string centre = cHit.HasValue ? $"HIT@{cHit.Value.Pos.Z:0.00}" : "miss";
                string corner = kMiss.HasValue ? $"HIT@{kMiss.Value.Pos.Z:0.00}" : "miss";
                MainConsole.Instance.Output($"  prim {k + 1,-2} id={tp.LocalId,-6} kind={kind,-13} centre={centre,-11} corner={corner,-11} {(triProven ? "real-triangle" : "NOT-triangle")}");
            }

            bool pass = clean == count && realMesh == count;
            MainConsole.Instance.Output($"  {clean}/{count} cooked clean (mesh(mesher), cache NOT poisoned); {realMesh}/{count} cast-verified real triangle (centre hit + corner miss).");
            MainConsole.Instance.Output(pass
                ? $"  PASS: {count}/{count} repeated identical mesh cooks are clean - delta #38 (cache poisoning) stays fixed."
                : $"  FAIL: a prim fell to bbox/failed - cache poisoning or cook regression. Investigate before shipping.");
            MainConsole.Instance.Output($"  (jolt clearprims then jolt raymesh -> all miss.)");
        }

        // M6.4: rez a prim NON-physical via the real path, then flip it physical through OpenSim's own
        // ScriptSetPhysicsStatus (-> the actor's IsPhysical setter -> recreate Dynamic, delta #15) so it
        // FALLS. Probes terrain at the drop XY to pick a modest drop height (no tunnelling) and the
        // expected rest Z. A physical MESH (prism) recreates to a convex HULL - the load-bearing case.
        private void DropOne(string kind, Vector3 size, float x, float y)
        {
            float terrainZ = 20f;
            if (_backend.RayCast(new SVector3(x, y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit th))
                terrainZ = th.Point.Z;
            float dropZ = terrainZ + 15f;

            SceneObjectGroup sog = RezTestPrim(kind, new Vector3(x, y, dropZ), size);
            if (sog == null) { MainConsole.Instance.Output($"{LogHeader} rez failed."); return; }

            sog.ScriptSetPhysicsStatus(true);   // OpenSim's real physics toggle -> IsPhysical setter -> Dynamic

            uint id = sog.RootPart.LocalId;
            string shapeNow = "?";
            lock (_prims)
                if (_prims.TryGetValue(id, out JoltPrim jp)) shapeNow = jp.ShapeKind;

            bool isBox = kind == "box";
            // Mass basis: box = exact box volume; mesh hull = enclosed mesh volume (== convex-hull volume
            // for the convex prism), captured from the cook. Both x density 1000 (BodyDesc.Default).
            float volume = isBox ? size.X * size.Y * size.Z : _lastMeshStats.Volume;
            float expMass = volume * 1000f;

            _drops.Add(new DropTrack
            {
                LocalId = id,
                Kind = isBox ? "box" : "mesh",
                StartZ = dropZ,
                StartStep = _stepCount,
                ExpectedMass = expMass,
                ExpectedRestZ = terrainZ + size.Z * 0.5f,
            });

            _logStepsUntil = _stepCount + 25;   // log the next ~25 Simulate frames (dt / active / liveZ)

            MainConsole.Instance.Output($"{LogHeader} dropped physical {kind} id={id} shape={shapeNow} from z={dropZ:0.00} (terrain {terrainZ:0.00}) at ({x:0},{y:0}).");
            MainConsole.Instance.Output($"  expected: mass~={expMass:0} kg (volume {volume:0.000} x 1000), rest z~={terrainZ + size.Z * 0.5f:0.00}. WATCH the viewer, then: jolt dropstatus");
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

        // Report each tracked drop: fell / rested / JustDeactivated-exactly-once / steps-to-rest / rest Z
        // vs expected / mass / determinism vs the previous same-kind drop. This is the rigorous console gate
        // behind the viewer watch.
        private void DropStatus()
        {
            if (_drops.Count == 0) { MainConsole.Instance.Output($"{LogHeader} no active drops - run jolt droptest / jolt dropmesh first."); return; }

            // dropstatus is a SINGLE-INSTANT snapshot. Read once, right after `droptest`, it can catch the
            // body still spawning/mid-air and (M6.4 delta) mislabel a healthy fall as a stall. The sim thread
            // keeps stepping and updating each DropTrack while this console-thread handler blocks, so auto-wait
            // until every tracked drop has rested (JustDeactivated -> RestZ set) or a hard timeout elapses,
            // BEFORE printing PASS/FAIL. [dropframe] remains the honest continuous per-frame trace.
            const int settleTimeoutMs = 4000, pollMs = 100;
            int waitedMs = 0;
            while (waitedMs < settleTimeoutMs && _drops.Exists(d => float.IsNaN(d.RestZ)))
            {
                System.Threading.Thread.Sleep(pollMs);
                waitedMs += pollMs;
            }
            if (waitedMs > 0)
                MainConsole.Instance.Output($"{LogHeader} waited {waitedMs} ms for drops to settle before reading.");

            MainConsole.Instance.Output($"{LogHeader} drop status (step {_stepCount}, ActiveBodyCount now={_lastActiveBodyCount}):");
            foreach (DropTrack t in _drops)
            {
                string shapeNow = "?";
                string live = "no body";
                bool haveLive = false;
                float liveZ = float.NaN, liveVz = float.NaN;
                lock (_prims)
                    if (_prims.TryGetValue(t.LocalId, out JoltPrim jp))
                    {
                        shapeNow = jp.ShapeKind;
                        // Ground truth from Jolt: is the body active, and where is it NOW? Distinguishes
                        // Static/asleep (active=N, liveZ==startZ) from active-falling (active=Y, liveZ<startZ)
                        // from fell-through-terrain (liveZ << terrain).
                        if (_backend.TryGetBodyState(jp.BodyHandle, out BodyState st))
                        {
                            haveLive = true;
                            liveZ = st.Position.Z;
                            liveVz = st.LinearVelocity.Z;
                            live = $"joltActive={(((st.Flags & BodyStateFlags.Active) != 0) ? "Y" : "N")} liveZ={liveZ:0.000}";
                        }
                    }

                bool fell = (t.StartZ - t.MinZ) > 0.5f;
                bool rested = t.JustDeactivatedCount >= 1;
                long steps = t.RestStep >= 0 ? t.RestStep - t.StartStep : -1;
                float restErr = float.IsNaN(t.RestZ) ? float.NaN : t.RestZ - t.ExpectedRestZ;

                float prevRest = t.Kind == "box" ? _lastBoxRestZ : _lastMeshRestZ;
                // Not yet rested after the settle wait is NOT a failure - it means the body is still in
                // motion. Report it as such (live Z + vertical velocity) so an early/incomplete read can
                // never be misread as "hung". Only a truly rested drop feeds the determinism compare.
                string det = float.IsNaN(t.RestZ)
                    ? (haveLive ? $"still falling (liveZ={liveZ:0.000}, vZ={liveVz:0.000})" : "still falling (no live body)")
                    : (float.IsNaN(prevRest) ? "first drop (re-run to compare)" : $"det dZ={t.RestZ - prevRest:0.0000} vs previous {t.Kind}");

                MainConsole.Instance.Output($"  {t.Kind,-4} id={t.LocalId,-6} shape={shapeNow,-12} startZ={t.StartZ:0.00} [{live}]");
                MainConsole.Instance.Output($"        fell={(fell ? "Y" : "N")} rested={(rested ? "Y" : "N")} JustDeactivated={t.JustDeactivatedCount}(want 1) steps-to-rest={steps}");
                MainConsole.Instance.Output($"        restZ={t.RestZ:0.000} exp={t.ExpectedRestZ:0.000} dErr={restErr:0.000} speed={t.LastSpeed:0.000} mass~={t.ExpectedMass:0} kg  [{det}]");
            }
            // Record rest Z for the next-run determinism compare.
            foreach (DropTrack t in _drops)
                if (!float.IsNaN(t.RestZ))
                {
                    if (t.Kind == "box") _lastBoxRestZ = t.RestZ;
                    else _lastMeshRestZ = t.RestZ;
                }
            MainConsole.Instance.Output($"  PASS/row: fell=Y, rested=Y, JustDeactivated=1 (exactly once), dErr~0, mass>0. Re-run droptest+dropstatus -> det dZ ~ 0 (determinism).");
        }

        // Report each logged-in avatar's CharacterVirtual state - position, IsSupported, ground normal/body,
        // capsule dims - and assert it spawned ON the terrain (supported, not sinking, capsule centre ~
        // terrainZ + StandHalf at the spawn XY), not at NaN or underground. The console gate behind John's
        // walk: run it right after login, and again after he walks somewhere to confirm position tracks and
        // IsSupported stays true on the flat.
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
        // and a verdict (OK / SANK / SANK-BELOW-TERRAIN / FLUNG). This is the "before" evidence: a physical
        // object that reads e.g. saved z=25.0 -> now z=-40.2 SANK-BELOW-TERRAIN is the silent loss - that
        // now-position is what OpenSim persists back, so it is invisible on the next reload.
        // `jolt vehiclestatus`: live vehicle-state dump - confirms a
        // boat is a working vehicle (TYPE_BOAT, buoyancy=1, active) LIVE, before we ever test reload.
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
        // M6.6 sit / unsit. The physics core is the CHARACTER LIFECYCLE: OpenSim SITS by REMOVING the
        // physics actor (ScenePresence.RemoveFromPhysicalScene -> RemoveAvatar -> the CharacterVirtual +
        // its M4.5 marker are destroyed) and STANDS by re-adding it (AddToPhysicalScene -> AddAvatar -> a
        // fresh character at the release position). So "suspend" == the character is GONE (no gravity, no
        // ground-detection, no movement integration), and "re-engage" == the 6.5 walking model rebuilt.
        // A moving seat is ridden via OpenSim scene-graph parenting (the seated avatar's world position
        // tracks the prim), independent of physics. These consoles OBSERVE and DRIVE that transition.
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
                    : (hasChar ? "PASS: WALKING -> character present (6.5 model live)"
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

        // Drive the REAL sit path: rez a static prim in front of the logged-in avatar and sit it there via
        // ScenePresence.HandleAgentRequestSit (the same entry the viewer uses). Then report sitstatus so the
        // SEATED -> character-removed transition is visible. `jolt unsit` stands back up + cleans the prim.
        private void SitTest()
        {
            ScenePresence sp = FirstRootAvatar();
            if (sp == null) { MainConsole.Instance.Output($"{LogHeader} no logged-in avatar - log in first."); return; }
            if (sp.IsSatOnObject) { MainConsole.Instance.Output($"{LogHeader} '{sp.Name}' is already seated - `jolt unsit` first."); return; }

            Vector3 pos = sp.AbsolutePosition + new Vector3(1.5f, 0f, 0f);   // 1.5 m to the avatar's +X
            if (_backend.RayCast(new SVector3(pos.X, pos.Y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit th))
                pos.Z = th.Point.Z + 0.5f;   // box half-height 0.5 -> resting on terrain

            SceneObjectGroup seat = RezTestPrim("box", pos, new Vector3(1f, 1f, 1f));
            if (seat == null) { MainConsole.Instance.Output($"{LogHeader} failed to rez the sit prim."); return; }
            _sitPrimId = seat.RootPart.LocalId;
            MainConsole.Instance.Output($"{LogHeader} rezzed sit prim id={seat.RootPart.LocalId} at ({pos.X:0},{pos.Y:0},{pos.Z:0.0}); sitting '{sp.Name}' on it via the real sit path...");

            sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, seat.UUID, Vector3.Zero);
            SitStatus();
            MainConsole.Instance.Output($"  -> expect SEATED=Y, hasCharacter=N. Then `jolt unsit` to re-engage (repeat sittest/unsit to check for a state leak).");
        }

        // Stand the logged-in avatar up (real StandUp path) and clean up the sittest prim.
        private void Unsit()
        {
            ScenePresence sp = FirstRootAvatar();
            if (sp == null) { MainConsole.Instance.Output($"{LogHeader} no logged-in avatar."); return; }
            if (!sp.IsSatOnObject)
                MainConsole.Instance.Output($"{LogHeader} '{sp.Name}' is not seated.");
            else
            {
                sp.StandUp();
                MainConsole.Instance.Output($"{LogHeader} stood '{sp.Name}' up (real StandUp path).");
            }

            if (_sitPrimId != 0)
            {
                foreach (var tp in _testPrims)
                    if (tp.LocalId == _sitPrimId)
                    {
                        SceneObjectGroup sog = _scene?.GetSceneObjectGroup(tp.Sog);
                        if (sog != null) _scene.DeleteSceneObject(sog, false);
                        break;
                    }
                _sitPrimId = 0;
            }
            SitStatus();
            MainConsole.Instance.Output($"  -> expect SEATED=N, hasCharacter=Y (re-engaged). `jolt avatarstatus` to confirm supported + not sliding.");
        }

        // M6.6 Task 2 - llSitTarget offset/rotation. This is OpenSim's placement math (SendSitResponse/
        // HandleAgentSit read the prim's SitTargetPosition/Orientation and seat the avatar at that offset in
        // the PRIM's frame); physics stays out of the way (the character is removed on sit). This console
        // proves it: rez a prim rotated 90deg yaw, set a sit-target offset, sit, and check the seated avatar
        // lands at the offset IN THE PRIM'S LOCAL FRAME (so the offset composes with the prim rotation).
        private void SitTarget()
        {
            ScenePresence sp = FirstRootAvatar();
            if (sp == null) { MainConsole.Instance.Output($"{LogHeader} no logged-in avatar - log in first."); return; }
            if (sp.IsSatOnObject) { MainConsole.Instance.Output($"{LogHeader} '{sp.Name}' is already seated - `jolt unsit` first."); return; }

            // sit-target offset in the prim's LOCAL frame. Z chosen as 0.30 (NOT 0.60) on purpose: the old
            // 0.60 composed to 0.95, which coincidentally equals the avatar's standHalf and hid whether the
            // vertical term was the SL offset or a capsule leak. 0.30 composes to 0.65 != standHalf, so the
            // gate below distinguishes them.
            Vector3 offset = new Vector3(1.5f, 0f, 0.30f);
            Quaternion primRot = Quaternion.CreateFromEulers(0f, 0f, (float)(Math.PI / 2.0));  // 90deg yaw (Z)

            // OpenSim's HandleAgentSit (LegacySitOffsets) composes the seated LOCAL position as
            //   sitTargetPos - up*0.05 + SIT_TARGET_ADJUSTMENT   (SIT_TARGET_ADJUSTMENT = (0,0,0.4)).
            // For an identity SitTargetOrientation the up vector is (0,0,1), so the vertical term is
            // (0.4 - 0.05) = +0.35. This is the STANDARD SL sit offset (furniture creators expect it) and it
            // lives in ScenePresence - physics-independent, identical for Jolt / BulletSim / ubODE (the
            // character is removed on sit, so no capsule term is involved).
            const float SlSitZ = 0.40f - 0.05f;   // SIT_TARGET_ADJUSTMENT.Z - up*0.05, identity orientation
            Vector3 expectedLocal = offset + new Vector3(0f, 0f, SlSitZ);

            Vector3 pos = sp.AbsolutePosition + new Vector3(2f, 0f, 0f);
            if (_backend.RayCast(new SVector3(pos.X, pos.Y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit th))
                pos.Z = th.Point.Z + 0.5f;

            SceneObjectGroup seat = RezTestPrim("box", pos, new Vector3(1f, 1f, 1f));
            if (seat == null) { MainConsole.Instance.Output($"{LogHeader} failed to rez the sit-target prim."); return; }
            seat.UpdateGroupRotationR(primRot);
            seat.RootPart.SitTargetPosition = offset;
            seat.RootPart.SitTargetOrientation = Quaternion.Identity;
            _sitPrimId = seat.RootPart.LocalId;

            Vector3 primWorld = seat.AbsolutePosition;
            Quaternion primWorldRot = seat.RootPart.GetWorldRotation();
            Vector3 expectedWorld = primWorld + expectedLocal * primWorldRot;   // composed local, rotated into world

            MainConsole.Instance.Output($"{LogHeader} sit-target test: prim id={seat.RootPart.LocalId} world=({primWorld.X:0.00},{primWorld.Y:0.00},{primWorld.Z:0.00}) yaw=90deg SitTargetPosition(local)=({offset.X:0.00},{offset.Y:0.00},{offset.Z:0.00})");
            MainConsole.Instance.Output($"  expected LOCAL = sitTarget + SL sit offset (0,0,{SlSitZ:0.00}) = ({expectedLocal.X:0.00},{expectedLocal.Y:0.00},{expectedLocal.Z:0.00}); expected world = ({expectedWorld.X:0.00},{expectedWorld.Y:0.00},{expectedWorld.Z:0.00})");

            sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, seat.UUID, Vector3.Zero);

            Vector3 seated = sp.AbsolutePosition;
            Vector3 localSeated = (seated - primWorld) * Quaternion.Inverse(primWorldRot);   // back to the prim frame
            // Gate ALL THREE axes against the SL-composed expected local position (X/Y prove offset+rotation
            // composition; Z proves the vertical term is exactly OpenSim's SL sit offset, not a capsule leak).
            bool offsetOk = sp.IsSatOnObject
                && Math.Abs(localSeated.X - expectedLocal.X) < 0.10f
                && Math.Abs(localSeated.Y - expectedLocal.Y) < 0.10f
                && Math.Abs(localSeated.Z - expectedLocal.Z) < 0.10f;

            MainConsole.Instance.Output($"  seated world=({seated.X:0.00},{seated.Y:0.00},{seated.Z:0.00}) -> prim-local=({localSeated.X:0.000},{localSeated.Y:0.000},{localSeated.Z:0.000}) vs expected-local=({expectedLocal.X:0.000},{expectedLocal.Y:0.000},{expectedLocal.Z:0.000})");
            MainConsole.Instance.Output($"  [{(offsetOk ? "PASS: seated at SitTargetPosition + SL sit offset, in the prim's LOCAL frame (X/Y offset+rotation composed; Z = OpenSim's standard sit offset, not a capsule leak)" : "CHECK: seated local pos does not match the SL-composed expected - see numbers above")}]");
            SitStatus();
            MainConsole.Instance.Output($"  -> `jolt unsit` to re-engage (character recreates at the release pos). A MOVING prim carries this offset via parenting.");
        }

        // M6.7 Task 1 - the M4.5 marker payoff. IMPORTANT REFRAME: OpenSim's llSensor is SCENE-GRAPH, not
        // physics - SensorRepeat.doAgentSensor/doObjectSensor iterate the ScenePresence / Entities lists and
        // compute distance/arc directly (and explicitly handle SEATED avatars), so llSensor never touches the
        // engine and finds avatars with or without a marker. The M4.5 kinematic query-marker matters for the
        // PHYSICS query path (llCastRay / OverlapSphere with the Avatar filter). This console is the first
        // LIVE test of that: a physics agent-overlap must find the logged-in avatar's marker, by UserData
        // (#30: avatar presence carries UserData, not a solver BodyId), and be range-correct.
        private void SensorTest()
        {
            ScenePresence sp = FirstRootAvatar();
            if (sp == null) { MainConsole.Instance.Output($"{LogHeader} no logged-in avatar - log in first."); return; }
            Vector3 p = sp.AbsolutePosition;

            MainConsole.Instance.Output($"{LogHeader} M4.5 marker payoff - physics agent-query vs the live avatar '{sp.Name}' (LocalId={sp.LocalId}):");
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
            MainConsole.Instance.Output($"  avatar seated = {(seated ? "Y" : "N")}  (SEATED => the M4.5 marker is destroyed with the character, so a PHYSICS agent-query cannot find it; OpenSim's scene-graph llSensor still finds seated avatars.)");

            string verdict = (nearFound && !farFound)
                ? "PASS: M4.5 marker is query-visible LIVE via the physics Avatar filter - avatar found in range, identity by UserData, not found out of range."
                : seated ? "note: avatar is SEATED -> no marker -> physics agent-query can't find it (expected). `jolt unsit` and re-run to see the marker."
                : "FAIL: the physics agent-query did NOT find the avatar marker in range - the M4.5 marker is not query-visible live (regression from the clean-room proof).";
            MainConsole.Instance.Output($"  [{verdict}]");
            MainConsole.Instance.Output($"  llSensor(AGENT) itself: works out-of-box (scene-graph) for WALKING and SEATED avatars - no physics wiring needed. This test validates the marker for the llCastRay/overlap path (Task 2).");
        }

        // M6.7 Task 2 - llCastRay through the real ray path. Casts a ray straight DOWN through the logged-in
        // avatar with the Avatar|Terrain filter and expects, in DISTANCE order: [0] the avatar's M4.5 marker
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
                ? "PASS: the physics ray HITS the walking avatar via the M4.5 marker (identity by UserData), terrain hit, multi-hit sorted by distance."
                : seated ? "note: SEATED -> the M4.5 marker is gone, so this RAW physics ray misses the avatar. That is CORRECT and SL-exact: llCastRay itself STILL hits a seated avatar via OpenSim's AvatarIntersection(skipPhys) fallback (it handles agents WITHOUT a physics body). `jolt unsit` + re-run to see the marker hit."
                : "FAIL: the agent ray did not hit the walking avatar marker.";
            MainConsole.Instance.Output($"  terrainHit={(terrainHit ? "Y" : "N")} avatarHit={(avatarHit ? "Y" : "N")} distanceOrdered={(ordered ? "Y" : "N")}");
            MainConsole.Instance.Output($"  [{verdict}]");
            MainConsole.Instance.Output($"  llCastRay is SL-exact: WALKING avatars come from this physics marker (agent->Avatar filter); SEATED avatars are added by OpenSim's AvatarIntersection(skipPhys) - so each avatar is detected exactly ONCE (no duplicate). RayFilterFlags map: agent->Avatar, physical->Dynamic, nonphysical->Static, land->Terrain, LSLPhantom(phantom|volumedtc)->Sensor.");
        }

        // The canonical triangular-prism PrimitiveBaseShape (EquilateralTriangle + Straight) used by the
        // mesh proof - shared by the real rez and the inline decision-point check.
        private static PrimitiveBaseShape GetPrismPbs()
        {
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            pbs.ProfileShape = ProfileShape.EquilateralTriangle;
            return pbs;
        }

        // Grid-cast the meshed prism at (120,128,100) size (4,4,3): a triangular top face at z=101.5 that
        // does NOT fill its 4x4 bbox. Centre is inside the triangle (HIT ~101.5); at least one bbox corner
        // is empty (MISS). A bounding box (or basic fallback) would HIT all five - so a corner miss with a
        // centre hit proves Jolt is colliding the ACTUAL triangle surface, not the bbox.
        private void RayMesh()
        {
            const float cx = 120f, cy = 128f;
            bool haveP = _testPrims.Count > 0;
            var pts = new (string label, float x, float y)[]
            {
                ("centre",       cx,        cy       ),
                ("corner +X+Y",  cx + 1.8f, cy + 1.8f),
                ("corner -X+Y",  cx - 1.8f, cy + 1.8f),
                ("corner +X-Y",  cx + 1.8f, cy - 1.8f),
                ("corner -X-Y",  cx - 1.8f, cy - 1.8f),
            };
            MainConsole.Instance.Output($"{LogHeader} raymesh grid on the prism (via Scene.RayCastFiltered) - {_testPrims.Count} test prim(s) live{(haveP ? "" : " -> expect all MISS")}. bbox top would be z=101.5 everywhere:");
            MainConsole.Instance.Output($"     point        |  act z   | hit id | note");
            int hits = 0, misses = 0;
            var dir = new Vector3(0f, 0f, -1f);
            var filter = RayFilterFlags.land | RayFilterFlags.nonphysical;
            foreach (var p in pts)
            {
                var origin = new Vector3(p.x, p.y, 106f);
                var res = _scene.RayCastFiltered(origin, dir, 10f, 4, filter) as List<ContactResult>;
                ContactResult? best = null;
                if (res != null)
                    foreach (var h in res)
                        if (best == null || h.Depth < best.Value.Depth) best = h;
                if (best == null)
                {
                    misses++;
                    MainConsole.Instance.Output($"  {p.label,-12} |   miss   |   -    | empty here (bbox would HIT 101.5)");
                }
                else
                {
                    hits++;
                    MainConsole.Instance.Output($"  {p.label,-12} | {best.Value.Pos.Z,8:0.000} | {best.Value.ConsumerID,6} | hit real surface");
                }
            }
            MainConsole.Instance.Output($"  -> {hits} hit / {misses} miss. PASS = centre HITs ~101.5 AND corner(s) MISS. A triangle cannot cover all 4 bbox corners");
            MainConsole.Instance.Output($"     (>=2 always empty, orientation-independent), so a box/bbox fallback would hit all 5 - corner misses prove the real triangle surface.");
        }

        // Decision #3: MaxBodies ceiling tracks TOTAL prim count (every prim is a body), default
        // 65536 for a standard 256 m region, scaling with region AREA for varregions.
        private static int ComputeMaxBodies(uint sizeX, uint sizeY)
        {
            const long baseBodies = 65536;
            const long baseArea = 256 * 256;
            long area = (long)sizeX * sizeY;
            long scaled = baseBodies * System.Math.Max(area, baseArea) / baseArea;
            return (int)System.Math.Min(scaled, int.MaxValue);
        }

        // ---------------------------------------------------------------------
        // PhysicsScene - M6.1 stubs (accept-and-ignore so a populated region still boots)
        // ---------------------------------------------------------------------

        // M6.5: the avatar finally gets a physics body - a Jolt CharacterVirtual. ScenePresence calls the
        // localID overload (via the feetOffset one); overriding it here means we have the avatar's LocalID
        // up front, so the CharacterVirtual + its M4.5 query marker carry the right identity. The abstract
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
            if (_backend == null)
                return PhysicsActor.Null;

            // Spawn ON the terrain. Raycast straight down at the login XY against the heightfield and seat
            // the capsule so its FEET rest on the surface. This is the fix for the historical "avatar spawns
            // underground" symptom on every prior boot: it happened because the avatar had NO physics body
            // to place it; now it does. If the ray misses (e.g. login off-region), fall back to the incoming Z.
            //
            // Seat Z (avatar root = capsule centre) = groundZ + StandHalf + feetOffset: OpenSim's avatar
            // root is the body centre, and the visual feet sit StandHalf + feetOffset below it. Omitting
            // feetOffset (M6.5 Task 1) sank the avatar by exactly that gap, so the feet clipped INTO terrain.
            float standHalf = JoltCharacter.StandHalfFor(size);
            float groundZ = position.Z - standHalf - feetOffset;
            if (_backend.RayCast(new SVector3(position.X, position.Y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit th))
                groundZ = th.Point.Z;
            // +1 cm so StickToFloor settles from just above rather than starting in penetration (which would
            // resolve as a shove on frame 1).
            var spawn = new Vector3(position.X, position.Y, groundZ + standHalf + feetOffset + 0.01f);

            var jc = new JoltCharacter(this, _backend, localID, avName, spawn, size, feetOffset, isFlying);
            if (velocity != Vector3.Zero)
                jc.SetMomentum(velocity);

            lock (_avatars)
                _avatars[jc.CharacterHandle.Value] = jc;

            m_log.Info($"{LogHeader} avatar '{avName}' id={localID} spawned at ({position.X:0},{position.Y:0}) terrainZ={groundZ:0.00} centreZ={spawn.Z:0.000} standHalf={standHalf:0.000} feetOffset={feetOffset:0.000} flying={isFlying}.");
            return jc;
        }

        public override void RemoveAvatar(PhysicsActor actor)
        {
            if (actor is not JoltCharacter jc)
                return;
            lock (_avatars)
                _avatars.Remove(jc.CharacterHandle.Value);
            jc.Destroy();   // RemoveCharacter also tears down the M4.5 query marker
            m_log.Info($"{LogHeader} avatar '{jc.Name}' id={jc.LocalID} removed.");
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
        // Jolt body. (Pure phantoms never reach here - ApplyPhysics skips them; physical dynamics is M6.4.)
        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                                  Vector3 size, Quaternion rotation, bool isPhysical, uint localid)
        {
            if (_backend == null || pbs == null)
                return PhysicsActor.Null;

            // Defence in depth: the cook path is throw-free (CookPrimShape always returns a valid shape -
            // fast-path, mesh/hull, or bbox fallback), but if body creation ever throws we accept-and-ignore
            // so one bad prim can never abort a whole region load. Returns PhysicsActor.Null on failure.
            JoltPrim prim;
            try
            {
                prim = new JoltPrim(this, _backend, localid, primName, pbs, position, size, rotation, isPhysical);
            }
            catch (Exception e)
            {
                m_log.Warn($"{LogHeader} AddPrimShape failed for '{primName}' (localid {localid}): {e.GetType().Name}: {e.Message}; prim has no physics.");
                return PhysicsActor.Null;
            }
            lock (_prims)
                _prims[localid] = prim;
            return prim;
        }

        // Fixed-shape fast path (M6.3 Task 1): an UN-CUT box / sphere / cylinder cooks straight to a
        // Jolt primitive with NO meshmerizer. Classification matches what a real viewer/OAR prim
        // carries (canonical ProfileShape+Extrusion), NOT PrimitiveBaseShape.CreateCylinder() - whose
        // factory emits Square+Curve1 (an SL "tube"), a known OpenSim quirk. Anything else (cut/hollow/
        // twisted, sculpt/mesh, non-uniform sphere/cylinder) falls back to a bounding box for now; the
        // real IMesher path is M6.3 Task 2. `axisCorrection` (System.Numerics) is folded into the body
        // orientation by JoltPrim; `kind` is for the proof read-out.
        internal ShapeId CookPrimShape(PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out SQuaternion axisCorrection, out string kind)
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
                    return _backend.CreateBoxShape(new SVector3(hx, hy, hz));
                }

                // SPHERE: half-circle profile, curve1 extrusion. Native sphere only when uniform - a
                // non-uniform "sphere" is an ellipsoid and must go through the mesher (Task 2).
                if (profile == ProfileShape.HalfCircle && path == (byte)Extrusion.Curve1
                    && Approx(size.X, size.Y) && Approx(size.Y, size.Z))
                {
                    kind = "sphere";
                    return _backend.CreateSphereShape(hx);
                }

                // CYLINDER: circle profile, straight extrusion. SL cylinders are Z-height; Jolt's
                // CylinderShape axis is Y, so correct +90 deg about X (local Y -> local Z) before the
                // prim's own rotation. Circular cross-section only (X==Y); elliptical -> mesher.
                if (profile == ProfileShape.Circle && path == (byte)Extrusion.Straight
                    && Approx(size.X, size.Y))
                {
                    kind = "cylinder";
                    axisCorrection = SQuaternion.CreateFromAxisAngle(SVector3.UnitX, MathF.PI * 0.5f);
                    return _backend.CreateCylinderShape(hz, hx);   // halfHeight=Z/2, radius=X/2
                }
            }

            // Not a basic fast-path shape (cut/hollow/twisted, prism, torus, sculpt, mesh): go through
            // the meshmerizer (M6.3 Task 2). The convex-vs-mesh decision lives HERE - our equivalent of
            // BulletSim's BSShapeCollection.CreateGeomMeshOrHull (physical && ShouldUseHulls -> hull;
            // else mesh). Contract (delta #31): a triangle MeshShape has Volume 0, so a PHYSICAL prim
            // MUST use the convex hull or it would rez with mass 0 at M6.4 - hence physical -> hull here.
            ShapeId cooked = CookMeshShape(pbs, size, isPhysical, out kind);
            if (cooked.IsValid)
                return cooked;

            // Mesher unavailable / returned nothing usable / cook threw: conservative solid bounding box.
            kind = "bbox(fallback)";
            return _backend.CreateBoxShape(new SVector3(hx, hy, hz));
        }

        // The IMesher path: PrimitiveBaseShape -> IMesher.CreateMesh -> getVertexListAsFloat /
        // getIndexListAsInt -> CreateMeshShape (non-physical triangle mesh) or CreateConvexHullShape
        // (physical hull). Returns ShapeId.Invalid on any failure so the caller can fall back. Also
        // stashes a characterization of the RAW mesher output (_lastMeshStats) for the proof read-out.
        private ShapeId CookMeshShape(PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out string kind)
        {
            kind = "bbox(fallback)";
            if (m_mesher == null)
            {
                m_log.Warn($"{LogHeader} no IMesher - cannot cook mesh; bounding-box fallback.");
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
            // never a propagating exception that could abort a prim rez or (at 6.5) a whole region load.
            SVector3[] points;
            int[] indices;
            try
            {
                // isPhysical:false to the mesher = "do not substitute a bounding box for tiny prims" -
                // we always want the real triangle soup (BulletSim passes false here for the same reason).
                IMesh mesh = m_mesher.CreateMesh("legionjolt-prim", pbs, size, MeshLod, false, false, false);
                if (mesh == null)
                {
                    // A sculpt whose asset (texture) has not been fetched meshes to null - it needs the
                    // async asset path (M6 request-asset delegate) first. Bounding box for now.
                    m_log.Debug($"{LogHeader} IMesher returned null (unfetched sculpt asset or empty geometry); bounding-box fallback.");
                    return ShapeId.Invalid;
                }

                indices = mesh.getIndexListAsInt();          // fresh copy - do NOT release the shared mesh
                float[] verts = mesh.getVertexListAsFloat(); // fresh copy (flattened x,y,z,...)
                if (verts == null || indices == null || verts.Length < 12 || indices.Length < 3 || (indices.Length % 3) != 0)
                {
                    m_log.Warn($"{LogHeader} mesher geometry unusable (verts={verts?.Length ?? 0}, indices={indices?.Length ?? 0}); bounding-box fallback.");
                    return ShapeId.Invalid;
                }

                points = new SVector3[verts.Length / 3];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new SVector3(verts[3 * i], verts[3 * i + 1], verts[3 * i + 2]);
            }
            catch (Exception e)
            {
                m_log.Warn($"{LogHeader} mesher geometry extraction threw ({e.GetType().Name}: {e.Message}); bounding-box fallback.");
                return ShapeId.Invalid;
            }

            _lastMeshStats = CharacterizeMesh(points, indices);   // honest read-out of REAL mesher output

            // Cook the Jolt shape. No shape/body exists until one of these RETURNS a handle, so a throw
            // here creates nothing to leak - caller falls back to a full bbox.
            try
            {
                ShapeId shape = isPhysical
                    ? _backend.CreateConvexHullShape(points)   // physical: hull (mesh Volume=0 -> mass 0; delta #31)
                    : _backend.CreateMeshShape(points, indices); // non-physical: real triangle mesh
                kind = isPhysical ? "hull(mesher)" : "mesh(mesher)";
                return shape;
            }
            catch (Exception e)
            {
                m_log.Warn($"{LogHeader} backend cook of mesher output threw ({e.GetType().Name}: {e.Message}); bounding-box fallback.");
                return ShapeId.Invalid;   // kind stays "bbox(fallback)"
            }
        }

        // Characterize RAW mesher output: what real geometry looks like vs the clean-room synthetic
        // tetra. Duplicate-vertex count uses mm-quantized coords (O(n)); degenerate = topological
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
        // Query wiring pulled forward for the M6.3 proof: this is the path a SCRIPT llCastRay takes.
        // llCastRay -> Scene.RayCastFiltered -> PhysicsScene.RaycastWorld (here) -> backend.RayCast.
        // Returning true from SupportsRaycastWorldFiltered flips llCastRay onto the physics engine
        // instead of OpenSim's own geometry intersection, so a script ray genuinely tests Jolt's
        // shapes. (Full query family - RaycastActor, Sphere/BoxProbe for llSensor - remains M6.7.)
        // ---------------------------------------------------------------------

        public override bool SupportsRaycastWorldFiltered() => true;

        // TWO llCastRay entry points route here, and BOTH must be overridden or llCastRay returns 0:
        //  - the 5-arg (RayFilterFlags) overload is what OpenSim's XEngine/YEngine LSL_Api.llCastRay calls
        //    (it maps RC_* -> RayFilterFlags, then we -> QueryFilter);
        //  - the 4-arg (no filter) overload is what PHLOX's own llCastRay calls (Halcyon port) - it does the
        //    reject-physical/agent/land TYPE filtering on the returned list itself, so we hand it ALL solid
        //    layers + avatars (QueryFilter.Default = Terrain|Static|Dynamic|Avatar; phantom/Sensor excluded,
        //    which Phlox neither requests nor filters). M6.7 regression: only the 5-arg was overridden, so
        //    under Phlox every cast fell through to the base (empty list) = 0 hits.
        public override List<ContactResult> RaycastWorld(Vector3 position, Vector3 direction, float length, int Count)
            => CastAll(position, direction, length, Count, QueryFilter.Default);

        public override object RaycastWorld(Vector3 position, Vector3 direction, float length, int Count, RayFilterFlags filter)
            => CastAll(position, direction, length, Count, ToQueryFilter(filter));

        private List<ContactResult> CastAll(Vector3 position, Vector3 direction, float length, int Count, QueryFilter qf)
        {
            var results = new List<ContactResult>();
            if (_backend == null || qf == QueryFilter.None || length <= 0f)
                return results;

            Vector3 dn = direction;
            dn.Normalize();
            var origin = new SVector3(position.X, position.Y, position.Z);
            var dir = new SVector3(dn.X, dn.Y, dn.Z);

            int want = Count > 0 ? Count : 1;
            var hits = new RayHit[want];
            int n = _backend.RayCastAll(origin, dir, length, qf, hits);
            for (int i = 0; i < n; i++)
            {
                var cr = new ContactResult
                {
                    ConsumerID = hits[i].UserData,           // SceneObjectPart.LocalId (0 = terrain)
                    Pos = new Vector3(hits[i].Point.X, hits[i].Point.Y, hits[i].Point.Z),
                    Normal = new Vector3(hits[i].Normal.X, hits[i].Normal.Y, hits[i].Normal.Z),
                    Depth = hits[i].Distance,
                };
                results.Add(cr);
            }
            return results;
        }

        // llCastRay's reject-type flags -> our layer filter. water has no body; phantom/volumedetect
        // map to the Sensor layer (M6.6).
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

        // ===================================================================================
        // M6.8 A/B PARITY HARNESS - engine-agnostic. Registered under BOTH BulletSim and Jolt (see
        // RegionLoaded), drives ONLY the standard OpenSim Scene/SceneObjectGroup/PhysicsActor surface
        // (the SAME rez path as `jolt rezprims`: EstateOwner -> new SceneObjectGroup -> AddNewSceneObject
        // -> ScriptSetPhysicsStatus -> DeleteSceneObject). Identical code runs on either engine by only
        // changing [Startup] physics=, which is what makes the A/B comparison valid. `parity core` writes
        // a capture file parity-<EngineType>.txt so the two boots can be diffed into a delta table.
        // Hosted in this module (rather than a new assembly) to stay within the module+backend guardrail;
        // it uses no Jolt-specific state, so it is valid while BulletSim is the physics engine.
        // ===================================================================================
        private static bool s_parityRegistered;
        private static Scene s_parityScene;

        private void RegisterParityConsole(Scene scene)
        {
            if (scene != null) s_parityScene = scene;   // one region in the scratch standalone; last-wins
            if (MainConsole.Instance == null || s_parityRegistered) return;
            s_parityRegistered = true;
            MainConsole.Instance.Commands.AddCommand("Physics", false, "parity",
                "parity drop | core | boat",
                "M6.8 engine-agnostic A/B parity harness. Drops a box + a mesher-forced prism through the STANDARD "
                + "physics surface and reports rest position / settle frames / mass, so BulletSim and Jolt can be "
                + "compared by booting each with physics= and running the same command. 'core' also writes parity-<engine>.txt.",
                HandleParityConsole);
        }

        private void HandleParityConsole(string module, string[] cmd)
        {
            Scene scene = s_parityScene;
            if (scene == null) { MainConsole.Instance.Output("[parity] no scene loaded yet."); return; }
            string sub = cmd.Length >= 2 ? cmd[1].ToLowerInvariant() : "core";
            switch (sub)
            {
                case "terrain": ParityTerrainLog(scene); break;
                case "ramp": ParityRampTest(scene); break;
                case "drop": ParityRunCore(scene, false); break;
                case "core": ParityRunCore(scene, true); break;
                case "boat": ParityBoat(scene, true); break;
                default: MainConsole.Instance.Output("Usage: parity terrain (gradient proof) | ramp (steep-slope slide test) | drop | core (writes parity-<engine>.txt) | boat (M8 boat A/B, writes parity-boat-<engine>.txt)"); break;
            }
        }

        // Scenarios 1 + 2 (+ 8 mass): drop identical shapes from a controlled height (terrain + 15 m, so
        // the FALL is identical on both engines regardless of terrain). Dropped at BOTH the slope point
        // (128,128 - the cone from avatar testing) AND the flattest terrain we can find, so a slope-friction
        // divergence is separated from a general drop bug. Each row reports rest pos, settle frames/ms,
        // engine-assigned mass, the OpenSim-facing friction, and the local terrain slope at the drop XY.
        private void ParityRunCore(Scene scene, bool toFile)
        {
            string engine = scene.PhysicsScene != null ? scene.PhysicsScene.EngineType : "unknown";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# M6.8 parity CORE  engine={engine}  region={scene.RegionInfo.RegionName}");

            // Data-driven drop points: SLOPE drop on the steepest flank found (NOT the cone apex, whose
            // centred gradient is ~0 - a peak, not a slope), FLAT drop on the genuinely flattest cell
            // (min max-neighbour-delta - a flat has all neighbours equal; a peak's are all lower).
            FindTerrainExtremes(scene, out float fx, out float fy, out float fSlope, out float sx, out float sy, out float sSlope);
            sb.AppendLine($"# steepest <{sx:0},{sy:0}> slope={sSlope:0.00}deg   flattest <{fx:0},{fy:0}> slope={fSlope:0.00}deg");
            sb.AppendLine("# scenario\trest\tsettleFrames\tsettleMs\tmass\tfriction\tslopeDeg\tdrop");

            ParityDropOne(scene, sb, "slope.box",   "box",   sx, sy);   // steepest flank - real slope test
            ParityDropOne(scene, sb, "slope.prism", "prism", sx, sy);
            ParityDropOne(scene, sb, "flat.box",    "box",   fx, fy);   // flattest - isolating baseline (~0 drift expected)
            ParityDropOne(scene, sb, "flat.prism",  "prism", fx, fy);

            string outText = sb.ToString();
            MainConsole.Instance.Output(outText);
            if (toFile)
            {
                string path = $"parity-{engine}.txt";
                try { System.IO.File.WriteAllText(path, outText); MainConsole.Instance.Output($"[parity] wrote {System.IO.Path.GetFullPath(path)}"); }
                catch (Exception e) { MainConsole.Instance.Output($"[parity] file write FAILED: {e.Message}"); }
            }
        }

        private void ParityDropOne(Scene scene, System.Text.StringBuilder sb, string label, string kind, float dropX, float dropY)
        {
            SceneObjectGroup sog = null;
            try
            {
                float terrainZ = TerrainH(scene, dropX, dropY);
                float slopeDeg = SlopeDegAt(scene, dropX, dropY);
                var dropPos = new Vector3(dropX, dropY, terrainZ + 15f);   // controlled 15 m fall on both engines
                var size = new Vector3(0.5f, 0.5f, 0.5f);

                PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
                if (kind == "prism") pbs.ProfileShape = ProfileShape.EquilateralTriangle;   // forces the mesher (no mesh asset)

                UUID owner = scene.RegionInfo.EstateSettings.EstateOwner;
                sog = new SceneObjectGroup(owner, dropPos, Quaternion.Identity, pbs);
                sog.RootPart.Scale = size;
                scene.AddNewSceneObject(sog, false);          // ephemeral (attachToBackup:false), physics-wired
                sog.ScriptSetPhysicsStatus(true);             // OpenSim's real physics toggle -> dynamic body

                // Physics is applied on the heartbeat; wait for the actor to exist.
                PhysicsActor pa = null;
                for (int i = 0; i < 40 && pa == null; i++) { pa = sog.RootPart.PhysActor; if (pa == null) System.Threading.Thread.Sleep(50); }
                if (pa == null) { sb.AppendLine($"{label}\tERROR: no PhysicsActor (physics not applied)"); return; }

                float mass = pa.Mass;
                float friction = pa.Friction;
                uint startFrame = scene.Frame;
                var wall = System.Diagnostics.Stopwatch.StartNew();

                // Rest = linear speed below threshold for 8 consecutive samples (~0.4 s), or timeout (~30 s,
                // long enough for BulletSim's 23 s slope slide).
                int stable = 0; Vector3 restPos = pa.Position;
                for (int i = 0; i < 600; i++)
                {
                    System.Threading.Thread.Sleep(50);
                    restPos = pa.Position;
                    if (pa.Velocity.Length() < 0.02f) { if (++stable >= 8) break; } else stable = 0;
                }
                wall.Stop();
                uint frames = scene.Frame - startFrame;

                sb.AppendLine($"{label}\t<{restPos.X:0.000},{restPos.Y:0.000},{restPos.Z:0.000}>\t{frames}\t"
                    + $"{wall.ElapsedMilliseconds}\t{mass:0.0000}\t{friction:0.000}\t{slopeDeg:0.00}\t"
                    + $"<{dropPos.X:0.0},{dropPos.Y:0.0},{dropPos.Z:0.0}>");
            }
            catch (Exception e)
            {
                sb.AppendLine($"{label}\tEXCEPTION: {e.Message}");
            }
            finally
            {
                if (sog != null) { try { scene.DeleteSceneObject(sog, false); } catch { } }
            }
        }

        // ===================================================================================
        // M8 boat A/B parity. ENGINE-AGNOSTIC: drives the boat through the STANDARD PhysicsActor
        // vehicle surface (VehicleType / VehicleVectorParam) and reads state through the standard
        // getters (Position / Orientation / Velocity / RotationalVelocity / Mass), so the SAME code
        // runs under physics=BulletSim (-> LegionVehicleDynamics) and physics=Jolt (-> the extracted
        // Legion.Vehicles controller). Both run the SAME Halcyon math, so the numbers should match;
        // any difference localizes to force APPLICATION (Bullet vs Jolt), not the math. Writes
        // parity-boat-<engine>.txt for a two-boot diff. Uses scene.PhysicsScene.SetTerrain to cook a
        // PHYSICS-ONLY water basin (this region is a plateau above the water plane) - the scene
        // heightmap is untouched, so nothing taints the viewer and the terrain tick won't re-push.
        // ===================================================================================
        private void ParityBoat(Scene scene, bool toFile)
        {
            string engine = scene.PhysicsScene != null ? scene.PhysicsScene.EngineType : "unknown";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# M8 boat parity  engine={engine}  region={scene.RegionInfo.RegionName}");

            float[] restoreHm = ParityEnsureBoatWater(scene, out float bx, out float by, out float water);
            sb.AppendLine($"# spot <{bx:0},{by:0}> water={water:0.00}  (all z are z-water; tilt/yaw in deg)");
            try
            {
                ParityBoatLinear(scene, sb, bx, by, water);
                ParityBoatHover(scene, sb, bx, by, water);
                ParityBoatAttract(scene, sb, bx, by, water);
                ParityBoatSteer(scene, sb, bx, by, water);
            }
            catch (Exception e) { sb.AppendLine($"EXCEPTION: {e}"); }
            finally { if (restoreHm != null) scene.PhysicsScene.SetTerrain(restoreHm); }

            string outText = sb.ToString();
            MainConsole.Instance.Output(outText);
            if (toFile)
            {
                string path = $"parity-boat-{engine}.txt";
                try { System.IO.File.WriteAllText(path, outText); MainConsole.Instance.Output($"[parity] wrote {System.IO.Path.GetFullPath(path)}"); }
                catch (Exception e) { MainConsole.Instance.Output($"[parity] file write FAILED: {e.Message}"); }
            }
        }

        // Deepest spot; if none deep enough, cook a physics-only basin at region centre. Returns the
        // restore heightmap (null if real water existed). Agnostic: scene heightmap + PhysicsScene.SetTerrain.
        private float[] ParityEnsureBoatWater(Scene scene, out float bx, out float by, out float water)
        {
            water = (float)scene.RegionInfo.RegionSettings.WaterHeight;
            int rx = (int)scene.RegionInfo.RegionSizeX, ry = (int)scene.RegionInfo.RegionSizeY;
            bx = rx / 2f; by = ry / 2f;
            float bestDepth = float.MinValue;
            for (int gy = 24; gy <= ry - 24; gy += 8)
                for (int gx = 24; gx <= rx - 24; gx += 8)
                {
                    float depth = water - TerrainH(scene, gx, gy);
                    if (depth > bestDepth) { bestDepth = depth; bx = gx; by = gy; }
                }
            if (bestDepth >= 2f)
                return null;

            bx = rx / 2f; by = ry / 2f;
            float[] hm = scene.Heightmap.GetFloatsSerialised();
            float[] restoreHm = (float[])hm.Clone();
            for (int gy = (int)by - 24; gy <= (int)by + 24; gy++)
                for (int gx = (int)bx - 24; gx <= (int)bx + 24; gx++)
                    if (gx >= 0 && gx < rx && gy >= 0 && gy < ry)
                        hm[gy * rx + gx] = water - 6f;
            scene.PhysicsScene.SetTerrain(hm);
            System.Threading.Thread.Sleep(600);   // let a taint-queued SetTerrain (BulletSim) take effect
            MainConsole.Instance.Output($"[parity:boat] no open water (deepest {bestDepth:0.0} m) - cooked a physics-only basin at ({bx:0},{by:0}); restored after.");
            return restoreHm;
        }

        // Rez a physical VEHICLE_TYPE_BOAT box (2x1x0.5) at (x,y,z) with rotation rot, agnostically.
        private SceneObjectGroup ParityRezBoat(Scene scene, float x, float y, float z, Quaternion rot, out PhysicsActor pa)
        {
            pa = null;
            var pbs = PrimitiveBaseShape.CreateBox();
            UUID owner = scene.RegionInfo.EstateSettings.EstateOwner;
            var sog = new SceneObjectGroup(owner, new Vector3(x, y, z), rot, pbs);
            sog.RootPart.Scale = new Vector3(2f, 1f, 0.5f);
            scene.AddNewSceneObject(sog, false);
            sog.ScriptSetPhysicsStatus(true);
            for (int i = 0; i < 40 && pa == null; i++) { pa = sog.RootPart.PhysActor; if (pa == null) System.Threading.Thread.Sleep(50); }
            if (pa != null)
            {
                if (rot != Quaternion.Identity) pa.Orientation = rot;
                pa.VehicleType = (int)Vehicle.TYPE_BOAT;
            }
            return sog;
        }

        // Tilt of local +Z from world up, and yaw about world Z (deg), from a live PhysicsActor.
        private static void BoatTiltYaw(PhysicsActor pa, out float tiltDeg, out float yawDeg)
        {
            Quaternion q = pa.Orientation;
            Vector3 up = Vector3.UnitZ * q;
            tiltDeg = (float)(Math.Acos(Math.Clamp(up.Z, -1f, 1f)) * 180.0 / Math.PI);
            double siny = 2.0 * (q.W * q.Z + q.X * q.Y);
            double cosy = 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z);
            yawDeg = (float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI);
        }

        private void ParityBoatLinear(Scene scene, System.Text.StringBuilder sb, float bx, float by, float water)
        {
            SceneObjectGroup boat = ParityRezBoat(scene, bx, by, water + 0.4f, Quaternion.Identity, out PhysicsActor pa);
            try
            {
                if (pa == null) { sb.AppendLine("linear\tERROR: no PhysActor"); return; }
                sb.AppendLine($"# linear: hold LINEAR_MOTOR <4,0,0>, re-set each 0.5s (mass={pa.Mass:0.00})");
                sb.AppendLine("# linear\tt\tfwdSpeed\tzWater\ttilt");
                var motor = new Vector3(4f, 0f, 0f);
                for (int i = 0; i <= 8; i++)
                {
                    pa.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, motor);
                    System.Threading.Thread.Sleep(500);
                    Vector3 v = pa.Velocity; Quaternion o = pa.Orientation;
                    Vector3 fwd = Vector3.UnitX * o;
                    float fwdSpeed = v.X * fwd.X + v.Y * fwd.Y + v.Z * fwd.Z;
                    BoatTiltYaw(pa, out float tilt, out _);
                    sb.AppendLine($"linear\t{i * 0.5f:0.0}\t{fwdSpeed:0.000}\t{pa.Position.Z - water:0.000}\t{tilt:0.0}");
                }
            }
            finally { try { scene.DeleteSceneObject(boat, false); } catch { } }
        }

        private void ParityBoatHover(Scene scene, System.Text.StringBuilder sb, float bx, float by, float water)
        {
            sb.AppendLine("# hover: no motor; target z-water = +0.50 (HoverWaterOnly)");
            sb.AppendLine("# hover\tcase\tfinalZWater\tsteadyBand");
            var cases = new (float z0, string label)[] { (3.0f, "settle-above"), (-3.0f, "rise-below"), (0.5f, "hold-rest") };
            foreach (var (z0, label) in cases)
            {
                SceneObjectGroup boat = ParityRezBoat(scene, bx, by, water + z0, Quaternion.Identity, out PhysicsActor pa);
                try
                {
                    if (pa == null) { sb.AppendLine($"hover\t{label}\tERROR"); continue; }
                    float lastZ = float.NaN, minZ = float.MaxValue, maxZ = float.MinValue;
                    for (int i = 0; i <= 10; i++)
                    {
                        System.Threading.Thread.Sleep(400);
                        lastZ = pa.Position.Z - water;
                        if (i >= 5) { minZ = Math.Min(minZ, lastZ); maxZ = Math.Max(maxZ, lastZ); }
                    }
                    sb.AppendLine($"hover\t{label}\t{lastZ:0.000}\t{(maxZ - minZ):0.000}");
                }
                finally { try { scene.DeleteSceneObject(boat, false); } catch { } }
            }
        }

        private void ParityBoatAttract(Scene scene, System.Text.StringBuilder sb, float bx, float by, float water)
        {
            // self-right from 30 deg roll; sample 0.25s x 24 (6s)
            Quaternion tilt0 = Quaternion.CreateFromEulers((float)(30.0 * Math.PI / 180.0), 0f, 0f);
            SceneObjectGroup boat = ParityRezBoat(scene, bx, by, water + 0.5f, tilt0, out PhysicsActor pa);
            try
            {
                if (pa == null) { sb.AppendLine("attract\tERROR"); return; }
                sb.AppendLine("# attract self-right from 30deg roll; sample 0.25s x24");
                sb.AppendLine("# attract\tt\ttilt\tzWater\trollRate");
                float firstPeak = 0f, lateSum = 0f, lateMax = 0f; int lateN = 0; float timeToLevel = -1f;
                for (int i = 0; i <= 24; i++)
                {
                    System.Threading.Thread.Sleep(250);
                    BoatTiltYaw(pa, out float td, out _);
                    float t = i * 0.25f;
                    if (t <= 1.0f) firstPeak = Math.Max(firstPeak, td);
                    if (timeToLevel < 0 && td < 8f) timeToLevel = t;
                    if (t >= 4.0f) { lateSum += td; lateMax = Math.Max(lateMax, td); lateN++; }
                    float rollRate = (float)(pa.RotationalVelocity.X * 180.0 / Math.PI);
                    sb.AppendLine($"attract\t{t:0.00}\t{td:0.0}\t{pa.Position.Z - water:0.000}\t{rollRate:0.0}");
                }
                float lateMean = lateN > 0 ? lateSum / lateN : float.NaN;
                sb.AppendLine($"# attract.summary\tpeak={firstPeak:0.0}\ttimeToLevel={(timeToLevel < 0 ? "never" : timeToLevel.ToString("0.0"))}\tlate2sMean={lateMean:0.0}\tlate2sMax={lateMax:0.0}");
            }
            finally { try { scene.DeleteSceneObject(boat, false); } catch { } }

            // yaw-free: nudge a yaw spin; heading must move, tilt must stay ~0
            SceneObjectGroup boat2 = ParityRezBoat(scene, bx, by, water + 0.5f, Quaternion.Identity, out PhysicsActor pa2);
            try
            {
                if (pa2 == null) { sb.AppendLine("attract.yawfree\tERROR"); return; }
                float yaw0 = float.NaN, yawLast = 0f, maxTilt = 0f;
                for (int i = 0; i <= 8; i++)
                {
                    pa2.RotationalVelocity = new Vector3(0f, 0f, 1.0f);
                    System.Threading.Thread.Sleep(400);
                    BoatTiltYaw(pa2, out float td, out float yaw);
                    if (float.IsNaN(yaw0)) yaw0 = yaw;
                    yawLast = yaw; maxTilt = Math.Max(maxTilt, td);
                }
                sb.AppendLine($"# attract.yawfree\tyawMoved={Math.Abs(YawDelta(yaw0, yawLast)):0.0}\tmaxTilt={maxTilt:0.0}");
            }
            finally { try { scene.DeleteSceneObject(boat2, false); } catch { } }
        }

        private void ParityBoatSteer(Scene scene, System.Text.StringBuilder sb, float bx, float by, float water)
        {
            SceneObjectGroup boat = ParityRezBoat(scene, bx, by, water + 0.5f, Quaternion.Identity, out PhysicsActor pa);
            try
            {
                if (pa == null) { sb.AppendLine("steer\tERROR"); return; }
                sb.AppendLine("# steer: hold ANGULAR_MOTOR yaw=1.0 for ~2.8s then release (friction must stop it)");
                sb.AppendLine("# steer\tt\tyaw\tyawRate\ttilt\tphase");
                var steer = new Vector3(0f, 0f, 1.0f);
                float yaw0 = float.NaN, yawAtRelease = 0f, maxTilt = 0f, rateAtRelease = 0f, rateAtEnd = 0f;
                for (int i = 0; i <= 14; i++)
                {
                    bool turning = i < 7;
                    if (turning) pa.VehicleVectorParam((int)Vehicle.ANGULAR_MOTOR_DIRECTION, steer);
                    System.Threading.Thread.Sleep(400);
                    BoatTiltYaw(pa, out float td, out float yaw);
                    if (float.IsNaN(yaw0)) yaw0 = yaw;
                    float yawRate = (float)(pa.RotationalVelocity.Z * 180.0 / Math.PI);
                    maxTilt = Math.Max(maxTilt, td);
                    if (i == 6) { yawAtRelease = yaw; rateAtRelease = yawRate; }
                    if (i == 14) rateAtEnd = yawRate;
                    sb.AppendLine($"steer\t{i * 0.4f:0.0}\t{yaw:0.0}\t{yawRate:0.0}\t{td:0.0}\t{(turning ? "TURN" : "coast")}");
                }
                sb.AppendLine($"# steer.summary\tturnedUnderMotor={Math.Abs(YawDelta(yaw0, yawAtRelease)):0.0}\trateAtRelease={rateAtRelease:0.0}\trateAtEnd={rateAtEnd:0.0}\tmaxTilt={maxTilt:0.0}");
            }
            finally { try { scene.DeleteSceneObject(boat, false); } catch { } }
        }

        // Engine-agnostic terrain height (ITerrainChannel), clamped to region bounds.
        private static float TerrainH(Scene scene, float x, float y)
        {
            int rx = (int)scene.RegionInfo.RegionSizeX, ry = (int)scene.RegionInfo.RegionSizeY;
            int ix = Math.Clamp((int)x, 0, rx - 1), iy = Math.Clamp((int)y, 0, ry - 1);
            return (float)scene.Heightmap[ix, iy];
        }

        // Local terrain slope in degrees from the height gradient over a 4 m span at (x,y).
        private static float SlopeDegAt(Scene scene, float x, float y)
        {
            float dx = TerrainH(scene, x + 2, y) - TerrainH(scene, x - 2, y);
            float dy = TerrainH(scene, x, y + 2) - TerrainH(scene, x, y - 2);
            float grad = (float)Math.Sqrt(dx * dx + dy * dy) / 4f;
            return (float)(Math.Atan(grad) * 180.0 / Math.PI);
        }

        // Max absolute height difference to the 4 neighbours at +/-2 m. This is the FLATNESS metric that
        // the centred-gradient slope cannot give: at a symmetric peak (the cone apex) the centred gradient
        // is ~0 (opposite neighbours cancel) yet the point is NOT flat - its neighbours are all LOWER, so
        // maxNbDelta > 0. A genuinely flat cell has maxNbDelta ~ 0. Use this to find flat, slope to find steep.
        private static float MaxNbDeltaAt(Scene scene, float x, float y)
        {
            float h = TerrainH(scene, x, y);
            float d = 0f;
            d = Math.Max(d, Math.Abs(TerrainH(scene, x + 2, y) - h));
            d = Math.Max(d, Math.Abs(TerrainH(scene, x - 2, y) - h));
            d = Math.Max(d, Math.Abs(TerrainH(scene, x, y + 2) - h));
            d = Math.Max(d, Math.Abs(TerrainH(scene, x, y - 2) - h));
            return d;
        }

        // Scan a coarse grid for the FLATTEST cell (min maxNbDelta - genuinely level, not a peak) and the
        // STEEPEST cell (max slope - a cone flank). The two are different XY, giving a real slope-vs-flat pair.
        private void FindTerrainExtremes(Scene scene, out float fx, out float fy, out float fSlope,
                                         out float sx, out float sy, out float sSlope)
        {
            fx = fy = sx = sy = 0f; fSlope = 0f; sSlope = 0f;
            float bestFlat = float.MaxValue, bestSteep = -1f;
            int rx = (int)scene.RegionInfo.RegionSizeX, ry = (int)scene.RegionInfo.RegionSizeY;
            for (int x = 16; x < rx - 16; x += 8)
                for (int y = 16; y < ry - 16; y += 8)
                {
                    float flat = MaxNbDeltaAt(scene, x, y);
                    float s = SlopeDegAt(scene, x, y);
                    if (flat < bestFlat) { bestFlat = flat; fx = x; fy = y; fSlope = s; }
                    if (s > bestSteep) { bestSteep = s; sx = x; sy = y; sSlope = s; }
                }
        }

        // Gradient PROOF: log height / slope / maxNbDelta at fixed test points plus the scanned extremes,
        // so we can verify the slope calc reports non-zero angles on the flanks and ~0 at a genuinely flat
        // spot BEFORE trusting any drop data. The apex (128,128) is expected to read ~0 slope (it is a peak).
        private void ParityTerrainLog(Scene scene)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[parity terrain] gradient proof - H / slopeDeg / maxNbDelta(m):");
            var pts = new (float x, float y)[] { (128,128),(128,140),(140,128),(128,160),(160,128),(100,100),(64,64),(200,200),(32,32),(224,224) };
            foreach (var (x, y) in pts)
                sb.AppendLine($"  <{x:0},{y:0}>\tH={TerrainH(scene,x,y):0.00}\tslope={SlopeDegAt(scene,x,y):0.00}deg\tmaxNbDelta={MaxNbDeltaAt(scene,x,y):0.000}");
            FindTerrainExtremes(scene, out float fx, out float fy, out float fSlope, out float sx, out float sy, out float sSlope);
            sb.AppendLine($"  => STEEPEST <{sx:0},{sy:0}> slope={sSlope:0.00}deg   FLATTEST <{fx:0},{fy:0}> slope={fSlope:0.00}deg maxNbDelta={MaxNbDeltaAt(scene,fx,fy):0.000}");
            sb.AppendLine("  (apex 128,128 reads ~0 slope because it is a PEAK, not flat - the flank points must read non-zero)");
            MainConsole.Instance.Output(sb.ToString());
        }

        // Steep-slope SANITY (finding 2): the region's steepest terrain is only ~11 deg, below the ~31 deg
        // (atan 0.6) friction threshold, so we cannot test slide-when-it-should on terrain. Instead drop a
        // box onto a STATIC ramp prim tilted to several angles (no terrain modification). A friction-0.6 box
        // should STAY at <=30 deg and SLIDE above ~31 deg. If Jolt does that, it is friction-modelling
        // correctly (not pinning boxes), which locks the "Jolt is more correct than BulletSim on slopes" call.
        private void ParityRampTest(Scene scene)
        {
            string engine = scene.PhysicsScene != null ? scene.PhysicsScene.EngineType : "unknown";
            FindTerrainExtremes(scene, out float fx, out float fy, out _, out _, out _, out _);
            float groundZ = TerrainH(scene, fx, fy);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[parity ramp] engine={engine}  at flat <{fx:0},{fy:0}> groundZ={groundZ:0.0}");
            sb.AppendLine("# box placed AT REST (flush, tilted to match, zero initial velocity) on a static tilted ramp prim.");
            sb.AppendLine("# Isolates friction from drop-impact. All surfaces friction 0.6 -> textbook: STAY/settle at 20/30");
            sb.AppendLine("# (tan<0.6), SLIDE (never rests) at 35/45 (tan>0.6). CREEPING at <=30 would be a Jolt friction bug.");
            sb.AppendLine("# angleDeg\tslide\tpeakVel\tfinalVel\tcameToRest\tverdict\tvelTrace(1s steps)");
            foreach (float ang in new float[] { 20f, 30f, 35f, 45f })
                ParityRampAtRest(scene, sb, ang, fx, fy, groundZ);
            MainConsole.Instance.Output(sb.ToString());
        }

        // Place a box AT REST (bottom face flush on the tilted ramp, matching tilt, zero velocity) and trace
        // its velocity. A friction-correct box holds (or briefly settles then rests) below ~31 deg and slides
        // continuously above it. Continuous motion below 31 deg = a real friction bug; a brief settle that
        // reaches v~0 = fine. This isolates the impact-slide seen when DROPPING onto the ramp.
        private void ParityRampAtRest(Scene scene, System.Text.StringBuilder sb, float angleDeg, float fx, float fy, float groundZ)
        {
            SceneObjectGroup ramp = null, box = null;
            try
            {
                float th = (float)(angleDeg * Math.PI / 180.0);
                float sinT = (float)Math.Sin(th), cosT = (float)Math.Cos(th);
                UUID owner = scene.RegionInfo.EstateSettings.EstateOwner;
                var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, th);   // tilt about Y -> downhill along X
                var rampPos = new Vector3(fx, fy, groundZ + 4f);

                ramp = new SceneObjectGroup(owner, rampPos, rot, PrimitiveBaseShape.CreateBox());
                ramp.RootPart.Scale = new Vector3(8f, 4f, 0.4f);      // static plate (never physical)
                scene.AddNewSceneObject(ramp, false);

                // Box flush on the ramp top face: ramp centre + faceNormal*(0.2 half-plate + 0.25 half-box).
                var normal = new Vector3(sinT, 0f, cosT);
                box = new SceneObjectGroup(owner, rampPos + normal * 0.45f, rot, PrimitiveBaseShape.CreateBox());
                box.RootPart.Scale = new Vector3(0.5f, 0.5f, 0.5f);
                scene.AddNewSceneObject(box, false);
                box.ScriptSetPhysicsStatus(true);

                PhysicsActor pa = null;
                for (int i = 0; i < 40 && pa == null; i++) { pa = box.RootPart.PhysActor; if (pa == null) System.Threading.Thread.Sleep(50); }
                if (pa == null) { sb.AppendLine($"{angleDeg:0}\tERROR: no PhysActor"); return; }
                pa.Velocity = Vector3.Zero;   // at-rest start - no drop impact

                Vector3 start = pa.Position;
                float peak = 0f;
                var trace = new System.Text.StringBuilder();
                for (int i = 0; i < 200; i++)   // ~10 s
                {
                    System.Threading.Thread.Sleep(50);
                    float v = pa.Velocity.Length();
                    if (v > peak) peak = v;
                    if (i % 20 == 0) trace.Append($" {i / 20}s:{v:0.00}");
                }
                float finalV = pa.Velocity.Length();
                Vector3 end = pa.Position;
                float slide = (float)Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y) + (end.Z - start.Z) * (end.Z - start.Z));
                bool cameToRest = finalV < 0.02f;
                string verdict = !cameToRest ? "SLIDING" : (slide < 0.3f ? "STAYED" : "settled-then-REST");
                sb.AppendLine($"{angleDeg:0}\t{slide:0.00}m\t{peak:0.00}\t{finalV:0.000}\t{cameToRest}\t{verdict}\t(tan={Math.Tan(th):0.00}){trace}");
            }
            catch (Exception e) { sb.AppendLine($"{angleDeg:0}\tEXCEPTION: {e.Message}"); }
            finally
            {
                if (box != null) { try { scene.DeleteSceneObject(box, false); } catch { } }
                if (ramp != null) { try { scene.DeleteSceneObject(ramp, false); } catch { } }
            }
        }

        // M7 Task 2: linkset roots whose compound needs a (re)build, coalesced and applied once per frame in
        // Simulate. link()/unlink() add to this instead of rebuilding inline (which hung the boot-load).
        private readonly HashSet<JoltPrim> _dirtyLinksets = new HashSet<JoltPrim>();

        // ---------------------------------------------------------------------
        // Vehicles (M8): active vehicle prims, driven per-frame from Simulate BEFORE the physics
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
                    m_log.Error($"{LogHeader} vehicle step EXCEPTION for prim {v.LocalID}: {e}");
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
                catch (Exception e) { m_log.Error($"{LogHeader} pending-activation EXCEPTION for prim {p.LocalID}: {e}"); }
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
            if (_backend == null)
                return 1f;

            // M7 Task 2: (re)build changed linkset compounds ONCE per frame, here on the step thread before
            // the step. link()/unlink() only mark the root dirty (they no longer rebuild inline); this
            // coalesces a whole linkset's worth of child-links into a single rebuild - the boot-load of a
            // persisted physical linkset used to hang because every child's link() churned the live root.
            DrainDirtyLinksets();

            // STRUCTURAL PORT of BulletSim's taint-deferred creation: activate physical bodies that were
            // created INERT (asleep) now, AFTER the linkset weld and any load-time property/vehicle setup have
            // completed, and BEFORE StepVehicles/StepOnce. So a body enters the engine step already fully
            // configured - a reloaded vehicle wakes with its gravity already cancelled (StepVehicles asserts it
            // just below, before StepOnce), so it can NEVER free-fall during load or the reload stall. Mirrors
            // BulletSim draining ALL taints before PE.PhysicsStep().
            DrainPendingActivation();

            // M8: run each active vehicle's Halcyon controller BEFORE the physics step, so its
            // velocity changes/forces/torques are consumed by THIS step (BulletSim's BeforeStep model).
            LastTimeStep = timeStep;
            StepVehicles(timeStep);

            // ONE backend Step per frame at OpenSim's ~11 fps cadence (Scene.FrameTime 0.0909 s). The
            // character is stepped exactly once per frame - the known-good path (M6.5 Task 1: stood + ran
            // smooth). Fast-body tunnelling through the terrain (M6.5 finding #3) is handled NOT by sub-
            // stepping the whole Simulate (that 6x'd the character/drain/terse pipeline and was a live
            // PERFORMANCE regression - bounce/jitter), but by CollisionSteps=6 set at Initialize: Jolt sub-
            // steps the RIGID-BODY solver INSIDE _system.Update without re-running the character step, so a
            // dropped prim integrates in solver sub-slices and rests, while the avatar stays at 1 step/frame.
            StepOnce(timeStep);

            // [charframe] live trace (toggle: `jolt charframe`): per-frame avatar Z / support / vertical
            // velocity so a re-walk PROVES the bounce is gone (or shows it in the numbers if it is not).
            if (_stepCount <= _charFrameUntil)
            {
                List<JoltCharacter> avs;
                lock (_avatars) avs = new List<JoltCharacter>(_avatars.Values);
                foreach (JoltCharacter a in avs)
                {
                    // Identify the ground body by its UserData: 0 = TERRAIN (expected), == the avatar's own
                    // LocalID = its M4.5 query marker (a bug), any other id = a prim/box. The terrain body
                    // IS a registered body, so "has a ground body" alone does NOT mean the marker.
                    string ground = "none";
                    if (a.GroundBody.IsValid && _backend.TryGetBodyState(a.GroundBody, out BodyState gb))
                        ground = gb.UserData == 0 ? "TERRAIN"
                               : gb.UserData == a.LocalID ? $"OWN-MARKER({gb.UserData})"
                               : $"prim({gb.UserData})";
                    // Terrain surface directly under the avatar (raycast) vs where the feet actually are -
                    // negative & shrinking feetAboveTerrain = sinking THROUGH the collision surface.
                    Vector3 p = a.Position;
                    float terrZ = float.NaN;
                    if (_backend.RayCast(new SVector3(p.X, p.Y, p.Z + 50f), new SVector3(0f, 0f, -1f), 300f, QueryFilter.Terrain, out RayHit th))
                        terrZ = th.Point.Z;
                    // FIXED-POINT terrain probe at the region centre - INDEPENDENT of the avatar's position.
                    // If this descends while the avatar stands still, the terrain surface is genuinely moving
                    // (terrain bug). If it holds constant but the avatar's own terrainZ descends, the avatar is
                    // drifting horizontally onto lower ground (a slide, not a sinking terrain).
                    float fixZ = float.NaN;
                    float cx = _regionSizeX * 0.5f, cy = _regionSizeY * 0.5f;
                    if (_backend.RayCast(new SVector3(cx, cy, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit fh))
                        fixZ = fh.Point.Z;
                    m_log.Debug($"{LogHeader} [charframe] step={_stepCount} id={a.LocalID} XY=({p.X:0.00},{p.Y:0.00}) Z={p.Z:0.000} " +
                                $"sup={(a.IsSupported ? "Y" : "N")} sliding={(a.IsSliding ? "Y" : "N")} vZ={a.Velocity.Z:0.000} flying={(a.Flying ? "Y" : "N")} ground={ground} " +
                                $"terrainZ@avatar={terrZ:0.000} terrainZ@centre({cx:0},{cy:0})={fixZ:0.000} feetAboveTerrain={p.Z - a.StandHalf - a.FeetOffset - terrZ:0.000}");
                }
            }
            else if (CharJumpTrace)
                CharJumpTrace = false;   // window elapsed -> stop the [charjump] trace too
            return 1f;
        }

        // One backend Step + drain (bodies -> prims, characters -> avatars) + the [dropframe] diagnostic.
        private void StepOnce(float timeStep)
        {
            // Step, then DRAIN: the backend fills _bodyBuf with a BodyState per ACTIVE body (moving prims)
            // plus one final JustDeactivated state per body that slept this step. For each, push the new
            // transform/velocity into the matching actor (by UserData = LocalID) and fire its terse update
            // so the viewer sees motion; the JustDeactivated state is the settle update that stops a rested
            // object drifting. Sleeping bodies aren't reported, so idle prims cost nothing.
            StepResult r = _backend.Step(timeStep, _bodyBuf, _charBuf, _contactBuf);
            _stepCount++;
            _lastActiveBodyCount = r.ActiveBodyCount;

            // Windowed per-frame diagnostic (set by a drop): is Step advancing with a REAL dt, is the
            // just-dropped body in our active set, and is its Z actually changing? This is the definitive
            // read on the "1st drop works, 2nd hangs" pattern - dt=0 => idle-step stall; active=1 but
            // liveZ frozen => body active-but-not-integrated (deeper); active=0 => activation lost.
            if (_stepCount <= _logStepsUntil && _drops.Count > 0)
            {
                DropTrack td = _drops[_drops.Count - 1];
                float lz = float.NaN, vz = float.NaN; bool ja = false;
                lock (_prims)
                    if (_prims.TryGetValue(td.LocalId, out JoltPrim jd) && _backend.TryGetBodyState(jd.BodyHandle, out BodyState sd))
                    { lz = sd.Position.Z; vz = sd.LinearVelocity.Z; ja = (sd.Flags & BodyStateFlags.Active) != 0; }
                m_log.Debug($"{LogHeader} [dropframe] step={_stepCount} dt={timeStep:0.0000} active={r.ActiveBodyCount} updates={r.BodyUpdateCount} box(id={td.LocalId}) liveZ={lz:0.000} vZ={vz:0.000} joltActive={ja}");
            }

            if (r.BodyBufferOverflowed)
                m_log.Warn($"{LogHeader} body update buffer overflowed ({_bodyBuf.Length}); some terse updates dropped this step.");

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

            // Character drain (M6.5): the avatar equivalent of the body drain above. The backend stepped
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

            DispatchContacts(r.ContactCount);
        }

        // M7 Task 3 (base dispatch): turn this frame's ContactReports into OpenSim collision events. Each
        // subscribed prim gets ONE CollisionEventUpdate listing the LocalIDs it is touching this frame
        // (terrain = 0); OpenSim's SceneObjectPart.PhysicsCollision diffs that against last frame to fire
        // collision_start / collision / collision_end, and llDetected* off the collider list. Runs on the
        // heartbeat thread right after the drain (same thread SOP.PhysicsCollision expects).
        //
        // Contacts carry Begin (first touch) + Persist (each frame while touching, gated on a subscribed
        // body) + End (separation). The "currently touching" set OpenSim wants = Begin|Persist this frame;
        // End is implicit (a pair that drops out of the set). A prim that touched last frame but not now
        // still needs one (empty) update so collision_end can fire - _collidedLastFrame drives that flush.
        // Per-child (landing 2): each contact names the STRUCK part on each side (ChildUserData - the
        // compound child hit, resolved from the contact sub-shape), so a linkset reports against the specific
        // child and llDetectedLinkNumber returns that child's link (see the AddCollider block below).
        private void DispatchContacts(int contactCount)
        {
            _collisionAccum.Clear();

            for (int i = 0; i < contactCount; i++)
            {
                ref ContactReport c = ref _contactBuf[i];
                if (c.Phase == ContactPhase.End)
                    continue;   // OpenSim derives "ended" from absence in the current set

                // Per-child identity (M7 Task 3 landing 2): dispatch to the STRUCK part on each side
                // (ChildUserData - the compound child hit, or the body itself for a single prim), and name
                // the OTHER side's struck part as the collider. Delivering to child N's PhysicsActor makes
                // OpenSim run child N's PhysicsCollision, so llDetectedLinkNumber == N (and it propagates to
                // the root script - every linkset part is subscribed via the root's aggregated events).
                // Jolt's normal points A -> B; give each side the surface normal pointing back at it.
                // ContactReport carries System.Numerics vectors (SVector3); OpenSim's ContactPoint is OMV.
                Vector3 pt = new Vector3(c.Point.X, c.Point.Y, c.Point.Z);
                if (IsSubscribedPrim(c.ChildUserDataA))
                    AccumFor(c.ChildUserDataA).AddCollider(c.ChildUserDataB, new ContactPoint(pt, new Vector3(c.Normal.X, c.Normal.Y, c.Normal.Z), 0f));
                if (IsSubscribedPrim(c.ChildUserDataB))
                    AccumFor(c.ChildUserDataB).AddCollider(c.ChildUserDataA, new ContactPoint(pt, new Vector3(-c.Normal.X, -c.Normal.Y, -c.Normal.Z), 0f));
            }

            // Deliver this frame's sets.
            foreach (KeyValuePair<uint, CollisionEventUpdate> kv in _collisionAccum)
            {
                JoltPrim p;
                lock (_prims) _prims.TryGetValue(kv.Key, out p);
                p?.SendCollisionUpdate(kv.Value);
            }

            // Flush an EMPTY update to prims that collided last frame but not now (fires collision_end),
            // then roll the "collided last frame" set forward to this frame's colliders.
            foreach (uint id in _collidedLastFrame)
            {
                if (_collisionAccum.ContainsKey(id))
                    continue;
                JoltPrim p;
                lock (_prims) _prims.TryGetValue(id, out p);
                if (p != null && p.SubscribedEvents())
                    p.SendCollisionUpdate(new CollisionEventUpdate());
            }
            _collidedLastFrame.Clear();
            foreach (uint id in _collisionAccum.Keys)
                _collidedLastFrame.Add(id);
        }

        // A LocalID resolves to a prim that currently has a collision-script subscription (M7 Task 3 base
        // is prim-scoped; avatar-as-subscriber ScenePresence collisions are a noted follow-up).
        private bool IsSubscribedPrim(uint localID)
        {
            JoltPrim p;
            lock (_prims) _prims.TryGetValue(localID, out p);
            return p != null && p.SubscribedEvents();
        }

        private CollisionEventUpdate AccumFor(uint localID)
        {
            if (!_collisionAccum.TryGetValue(localID, out CollisionEventUpdate u))
            {
                u = new CollisionEventUpdate();
                _collisionAccum[localID] = u;
            }
            return u;
        }

        public override void SetTerrain(float[] heightMap)
        {
            if (_backend == null || heightMap == null)
                return;
            int sx = _regionSizeX, sy = _regionSizeY;
            if (sx <= 0 || sy <= 0 || heightMap.Length < sx * sy)
            {
                m_log.Warn($"{LogHeader} SetTerrain: heightMap length {heightMap?.Length ?? 0} < {sx}x{sy}; ignoring.");
                return;
            }

            // Build the (N+1)-square sample field (resolved varregion decision). A region of N metres ->
            // N+1 samples at 1 m spacing spans exactly [0, N] metres, so the far EDGE is covered (the
            // clean-room (N-1)*s finding: an N-sample field would fall 1 m short). The extra row/column
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
            // corner (physics runs in region-local coords - decision #2).
            ShapeId newShape = _backend.CreateHeightFieldShape(field, m, m, new SVector3(1f, 1f, 1f));
            _backend.SetTerrain(newShape, SVector3.Zero);

            // Retain the cooked samples for TerrainHeightAt (vehicle hover/ground inputs) - the
            // exact field the collision surface was built from, so heights agree with contacts.
            _terrainField = field;
            _terrainFieldM = m;

            // Release the previous terrain shape: SetTerrain already replaced its body (dropping that
            // native ref), so releasing our handle frees it.
            if (_terrainShape.IsValid)
                _backend.ReleaseShape(_terrainShape);
            _terrainShape = newShape;

            // Un-bury any avatar the raise left below the new surface (the terrain body was swapped out
            // from under its CharacterVirtual, which keeps its old Z). Runs only here, on a real terrain
            // edit (~5 s tainted cadence), and only lifts avatars now below the surface - a lowered terrain
            // leaves them above, to settle by normal gravity.
            ReGroundAvatarsOnTerrainChange();

            // Step-stamp + a couple of height samples so a [charframe] session can see whether SetTerrain
            // is re-firing during a walk (it should NOT - TerrainModule only ticks it every ~5 s when the
            // heightmap is tainted) and whether the heights it re-cooks are drifting downward.
            m_log.Info($"{LogHeader} terrain set: step={_stepCount} {sx}x{sy} region -> {m}x{m} heightfield " +
                       $"(spans {m - 1} m/side; sample[centre]={heightMap[(sy / 2) * sx + (sx / 2)]:0.000} sample[0]={heightMap[0]:0.000}).");
        }

        // Distance (m) a capsule centre must be below its seat before we treat it as buried and lift it.
        // Small enough that any real raise lifts, large enough to ignore float noise / a normal grounded
        // avatar sitting exactly at seatZ. Only ever lifts UP, so a modest value is safe either way.
        private const float TerrainUnburyEps = 0.05f;

        /// <summary>
        /// Pure un-bury decision (no physics state), isolated so it is unit-testable and shared by the
        /// live pass and the `jolt terrain-unbury` console assert. Given a capsule centre Z, the new terrain
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
        /// Cause-A load-time position sanity (prim analog of <see cref="TryComputeUnbury"/>). A PHYSICAL prim
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
        /// Cause-A entry used by JoltPrim just before a physical body goes active: if <paramref name="pos"/> is
        /// below the terrain surface for a prim of <paramref name="size"/>, hand back the lifted position so the
        /// body is created RESTING on terrain instead of penetrating it. Applying this BEFORE the body is created
        /// stops (1) the bad position ever draining back to the SOP + persisting, and (2) the native solver
        /// churning on a deep-penetration load (the ~5.8s reload watchdog stall). No terrain yet -> no lift.
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
        // Flying avatars are lifted too - a buried flyer can't rise through the solid heightfield above it
        // (John's exact case). Uses the same seat formula as spawn (groundZ + StandHalf + FeetOffset) and
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
                    m_log.Info($"{LogHeader} terrain-unbury: avatar {a.LocalID} lifted z={p.Z:0.000} -> seatZ={seatZ:0.000} " +
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
            if (_backend != null && _terrainShape.IsValid)
                _backend.ReleaseShape(_terrainShape);
            _terrainShape = ShapeId.Invalid;
        }

        public override Dictionary<uint, float> GetTopColliders() => new Dictionary<uint, float>();

        public override void Dispose()
        {
            _backend?.Dispose();   // backend teardown -> Foundation.Shutdown
            _backend = null;
        }
    }
}
