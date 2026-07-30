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
// Registration mirrors BSScene: a region module that self-selects when [Startup] physics == Name.
// No [Startup] edit - the operator picks `physics = Jolt`; this module recognises its own name.
// NGC develop dropped Mono.Addins for the IPluginRegistryProvider host, so discovery is via the
// sibling PluginRegistration.cs (registers this class under /OpenSim/RegionModules) rather than a
// [Extension] attribute. The class itself is unchanged.
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
                    "jolt linktest | unlinktest | collidetest | terraintest | terrainslope | terrainhill | hilltest | probe <x> <y> | rezprims | rayprims | rezmesh | rezmeshn <count> | raymesh | droptest | dropmesh | dropstatus | avatarstatus | charframe [secs] | sitstatus | sittest | unsit | sittarget | sensortest | raytest | heights <x> <y> | clearprims",
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

            MainConsole.Instance.Output("Usage: jolt linktest | unlinktest | collidetest | terraintest | terrainslope | terrainhill | hilltest | probe <x> <y> | rezprims | rayprims | rezmesh | rezmeshn <count> | raymesh | droptest | dropmesh | dropstatus | avatarstatus | charframe [secs] | sitstatus | sittest | unsit | sittarget | sensortest | raytest | heights <x> <y> | clearprims");
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
                "parity drop | core",
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
                default: MainConsole.Instance.Output("Usage: parity terrain (gradient proof) | ramp (steep-slope slide test) | drop | core (writes parity-<engine>.txt)"); break;
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

        internal void MarkLinksetDirty(JoltPrim root)
        {
            lock (_dirtyLinksets) _dirtyLinksets.Add(root);
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

            // Release the previous terrain shape: SetTerrain already replaced its body (dropping that
            // native ref), so releasing our handle frees it.
            if (_terrainShape.IsValid)
                _backend.ReleaseShape(_terrainShape);
            _terrainShape = newShape;

            // Step-stamp + a couple of height samples so a [charframe] session can see whether SetTerrain
            // is re-firing during a walk (it should NOT - TerrainModule only ticks it every ~5 s when the
            // heightmap is tainted) and whether the heights it re-cooks are drifting downward.
            m_log.Info($"{LogHeader} terrain set: step={_stepCount} {sx}x{sy} region -> {m}x{m} heightfield " +
                       $"(spans {m - 1} m/side; sample[centre]={heightMap[(sy / 2) * sx + (sx / 2)]:0.000} sample[0]={heightMap[0]:0.000}).");
        }

        public override void SetWaterLevel(float baseheight)
        {
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
