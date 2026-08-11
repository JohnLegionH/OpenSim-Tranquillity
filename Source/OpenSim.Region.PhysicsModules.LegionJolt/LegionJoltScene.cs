/*
 * Legion Grid — Jolt physics as an OpenSim region module (PhysicsScene).
 *
 * ============================ SLICE 1 SKELETON ONLY ============================
 * This proves ONE thing against the Tranquillity (NGC) tree: the module is DISCOVERED via
 * DotNetCorePlugins (see PluginRegistration.cs), self-selects when [Startup] physics == "Jolt",
 * attaches to a region as its PhysicsScene, steps an empty world, and shuts down cleanly.
 *
 * It has ZERO physics behaviour by design:
 *   - AddAvatar / AddPrimShape return PhysicsActor.Null (accept-and-ignore, so a region still boots).
 *   - SetTerrain / SetWaterLevel accept-and-ignore.
 *   - Simulate is a no-op that returns 0.
 *
 * The engine-agnostic backend (Legion.Physics, with the per-instance _simLock that REQUIRES the
 * patched joltc), the vehicle controller (Legion.Vehicles), and the real prim/character/terrain
 * bodies (JoltPrim/JoltCharacter/JoltVehicleBody, ~10,900 LOC total) arrive in later slices,
 * bottom-up. This file is deliberately the thin seam that validates registration + native packaging
 * on the RC before any of that volume lands.
 *
 * Registration mirrors BulletSim's BSScene: this type is BOTH the INonSharedRegionModule and the
 * PhysicsScene. It self-selects — it never edits [Startup]; the operator picks `physics = Jolt`.
 * ==============================================================================
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using Legion.Physics;
using LegionJoltBackend = Legion.Physics.Jolt.JoltPhysicsBackend;
// The backend speaks System.Numerics.Vector3; OpenSim's PhysicsScene contract speaks
// OpenMetaverse.Vector3 (the unqualified Vector3 here). Alias the numerics one so backend calls
// are unambiguous (mirrors Legion's LegionJoltScene).
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.LegionJolt;

public sealed class LegionJoltScene : PhysicsScene, INonSharedRegionModule
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    internal const string LogHeader = "[LEGION JOLT]";

    private bool m_Enabled = false;
    private IConfigSource m_Config;

    // SLICE 2: the engine-agnostic Jolt backend (per-instance _simLock; REQUIRES the patched joltc).
    private ILegionPhysicsBackend _backend;
    private string _regionName;
    private long _stepCount;
    // Caller-owned step buffers (the backend allocates nothing per frame). The skeleton drains little;
    // sized modestly and revisited when real actors arrive (slice 4).
    private BodyState[] _bodyBuf = new BodyState[1024];
    private CharacterState[] _charBuf = new CharacterState[256];
    private ContactReport[] _contactBuf = new ContactReport[2048];

    // ---------------------------------------------------------------------
    // INonSharedRegionModule
    // ---------------------------------------------------------------------

    public string Name => "Jolt";

    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        // Self-selection only: enable when the operator chose us. We never hard-enable and never
        // touch [Startup] ourselves (mirrors BSScene / ubODEModule). Physics selection is process-
        // global in this tree, so a single [Startup] physics = Jolt applies to every region this
        // process hosts.
        IConfig config = source.Configs["Startup"];
        if (config == null)
            return;

        string physics = config.GetString("physics", string.Empty);
        if (physics != Name)                       // case-sensitive, matching ubODE/BulletSim
            return;

        m_Config = source;
        m_Enabled = true;

        // The real backend enforces meshing == "Meshmerizer"; the skeleton only notes a mismatch
        // (it has no prims to cook) so the load proof is robust regardless of the meshing setting.
        string mesher = config.GetString("meshing", string.Empty);
        if (!string.Equals(mesher, "Meshmerizer", StringComparison.Ordinal))
            m_log.WarnFormat("{0} [Startup] meshing = \"{1}\" (the real Jolt backend will require \"Meshmerizer\"; harmless for the skeleton).", LogHeader, mesher);

        m_log.InfoFormat("{0} skeleton enabled (physics = {1}) — discovered via DotNetCorePlugins.", LogHeader, Name);
    }

    public void Close() { }

    public void AddRegion(Scene scene)
    {
        if (!m_Enabled)
            return;

        _regionName = scene.RegionInfo.RegionName;
        PhysicsSceneName = Name + "/" + _regionName;

        // This is how the Scene acquires its PhysicsScene (same call BSScene/Legion make).
        scene.RegisterModuleInterface<PhysicsScene>(this);

        // SLICE 2: stand up the real Jolt backend — creates a JPH PhysicsSystem with its OWN patched
        // per-system TempAllocator + a JobSystemThreadPool. Ported as-is; the shared-thread-pool fix
        // (design item #1) is deliberately NOT applied here.
        var settings = PhysicsBackendSettings.Default;
        _backend = new LegionJoltBackend();
        _backend.Initialize(settings);

        EngineType = Name;
        EngineName = $"{_backend.Name} {_backend.Version}";

        // SLICE-2 CONCURRENCY-TEST SCAFFOLDING (removed when real prims arrive in slice 4): a few
        // dynamic boxes so the solver + the per-system TempAllocator run every step. An empty world
        // barely touches the allocator; these make the two-region concurrent-step test actually
        // exercise the _simLock <-> patched-native coupling.
        SpawnConcurrencyTestBodies();

        m_log.InfoFormat("{0} attached to region \"{1}\" — Jolt backend up ({2}).", LogHeader, _regionName, EngineName);
    }

    public void RemoveRegion(Scene scene) { }

    public void RegionLoaded(Scene scene)
    {
        if (!m_Enabled)
            return;

        m_log.InfoFormat("{0} region \"{1}\" loaded on the Jolt skeleton.", LogHeader, scene.RegionInfo.RegionName);
    }

    // SLICE-2 test scaffolding only (removed when real prim bodies arrive in slice 4).
    private void SpawnConcurrencyTestBodies()
    {
        ShapeId shape = _backend.CreateBoxShape(new SVector3(0.5f, 0.5f, 0.5f));
        for (int i = 0; i < 8; i++)
        {
            BodyDesc d = BodyDesc.Default;
            d.Shape = shape;
            d.Layer = PhysicsLayer.Dynamic;
            d.MotionType = BodyMotionType.Dynamic;
            d.Mass = 10f;
            // No terrain in the skeleton, so these fall forever and never sleep -> the solver and the
            // per-system TempAllocator stay busy every step (exactly the stress we want).
            d.Position = new SVector3(128f + (i % 4), 128f + (i / 4), 40f + i * 1.5f);
            d.StartActive = true;
            _backend.CreateBody(in d);
        }
        m_log.InfoFormat("{0} region \"{1}\": 8 dynamic test boxes created (slice-2 concurrency scaffolding).", LogHeader, _regionName);
    }

    // ---------------------------------------------------------------------
    // PhysicsScene — skeleton overrides (empty world; accept-and-ignore)
    // ---------------------------------------------------------------------

    public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
        => PhysicsActor.Null;

    public override void RemoveAvatar(PhysicsActor actor) { }

    public override void RemovePrim(PhysicsActor prim) { }

    public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                              Vector3 size, Quaternion rotation, bool isPhysical, uint localid)
        => PhysicsActor.Null;

    public override float Simulate(float timeStep)
    {
        if (_backend == null || timeStep <= 0f)
            return 0f;

        _stepCount++;
        // The real per-frame native step: JPH_PhysicsSystem_Update on THIS region's system, under the
        // backend's per-instance _simLock. Two regions in one process run this on two threads at once —
        // the exact condition the patched per-system TempAllocator exists to make safe.
        _backend.Step(timeStep, _bodyBuf, _charBuf, _contactBuf);
        return 1f;
    }

    public override void SetTerrain(float[] heightMap) { }

    public override void SetWaterLevel(float baseheight) { }

    public override void DeleteTerrain() { }

    public override void Dispose()
    {
        // Disposes the backend under its own _simLock + ref-counts Foundation down (only the last
        // region out calls Foundation.Shutdown).
        var b = _backend;
        _backend = null;
        b?.Dispose();
    }

    public override Dictionary<uint, float> GetTopColliders() => new Dictionary<uint, float>();
}
