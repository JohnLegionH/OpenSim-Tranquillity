// Legion Grid - the [Jolt] config section (JOLT-5, audit J-8, J-9, S-4c).
//
// Every knob the module used to hardcode, read once in Initialise. The rule: every default reproduces the
// pre-JOLT-5 behaviour exactly (PhysicsBackendSettings.Default + CollisionSteps 6 + the MaxBodies area rule +
// today's step-buffer caps). Scaling the pair/contact caps with region area is opt-in (ScaleCapsWithArea).
// Invalid values (unparsable, non-finite, out of range) warn and fall back to the default - a typo in the INI
// must never take a region down or silently change physics.

using System;
using System.Collections.Generic;
using System.Globalization;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Nini.Config;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class JoltConfig
    {
        internal const string Section = "Jolt";

        // Decision #3: 65536 bodies per standard 256 x 256 m region, scaled by AREA for varregions.
        internal const int BaseMaxBodies = 65536;
        internal const long BaseArea = 256L * 256L;

        public float Gravity = -9.80665f;          // world Z gravity, m/s^2
        public int CollisionSteps = 6;             // M6.5 finding #3: solver sub-steps inside Update
        public int PositionIterations = 2;
        public int VelocityIterations = 10;
        public int ThreadCount = 0;                // 0 = ProcessorCount - 1 (the shared pool; first region wins)
        public bool DeterministicMode = false;
        public int MaxBodies = 0;                  // 0 = BaseMaxBodies scaled by area
        public int MaxBodyPairs = 65536;
        public int MaxContactConstraints = 16384;
        public bool ScaleCapsWithArea = false;     // true: scale MaxBodyPairs / MaxContactConstraints by area too
        public int BodyUpdateBufferMax = 65536;
        public int CharacterUpdateBufferMax = 1024;
        public int ContactBufferMax = 0;           // 0 = the backend's contact ring capacity
        public float AvatarJumpSpeed = 4.0f;       // CharacterDesc.JumpSpeed, m/s
        public float CapacityLogIntervalSeconds = 10f;
        public int JobPools = 1;                   // JOLT-7: job pools, one physics update at a time each; splits ThreadCount

        /// <summary>Parse [Jolt]. Missing keys keep their defaults; each invalid one adds a line to <paramref name="warnings"/>.</summary>
        internal static JoltConfig FromConfig(IConfigSource source, List<string> warnings)
        {
            var c = new JoltConfig();
            IConfig cfg = source?.Configs[Section];
            if (cfg == null)
                return c;

            c.Gravity = F(cfg, "Gravity", c.Gravity, -1000f, 1000f, warnings);
            c.CollisionSteps = I(cfg, "CollisionSteps", c.CollisionSteps, 1, 64, warnings);
            c.PositionIterations = I(cfg, "PositionIterations", c.PositionIterations, 1, 255, warnings);
            c.VelocityIterations = I(cfg, "VelocityIterations", c.VelocityIterations, 1, 255, warnings);
            c.ThreadCount = I(cfg, "ThreadCount", c.ThreadCount, 0, 256, warnings);
            c.DeterministicMode = B(cfg, "DeterministicMode", c.DeterministicMode, warnings);
            c.MaxBodies = I(cfg, "MaxBodies", c.MaxBodies, 0, 8_388_607, warnings);   // Jolt's BodyID index limit
            c.MaxBodyPairs = I(cfg, "MaxBodyPairs", c.MaxBodyPairs, 1, 8_388_607, warnings);
            c.MaxContactConstraints = I(cfg, "MaxContactConstraints", c.MaxContactConstraints, 1, 8_388_607, warnings);
            c.ScaleCapsWithArea = B(cfg, "ScaleCapsWithArea", c.ScaleCapsWithArea, warnings);
            c.BodyUpdateBufferMax = I(cfg, "BodyUpdateBufferMax", c.BodyUpdateBufferMax, 1, 8_388_607, warnings);
            c.CharacterUpdateBufferMax = I(cfg, "CharacterUpdateBufferMax", c.CharacterUpdateBufferMax, 1, 65536, warnings);
            c.ContactBufferMax = I(cfg, "ContactBufferMax", c.ContactBufferMax, 0, 16_777_216, warnings);
            c.AvatarJumpSpeed = F(cfg, "AvatarJumpSpeed", c.AvatarJumpSpeed, 0f, 100f, warnings);
            c.CapacityLogIntervalSeconds = F(cfg, "CapacityLogIntervalSeconds", c.CapacityLogIntervalSeconds, 0.1f, 86400f, warnings);
            c.JobPools = I(cfg, "JobPools", c.JobPools, 1, JoltPhysicsBackend.MaxJobPools, warnings);
            return c;
        }

        /// <summary>Area multiplier against a standard region: 1 for 256 x 256 (and anything smaller), 16 for 1024 x 1024.</summary>
        internal static long AreaFactorNumerator(uint sizeX, uint sizeY) => Math.Max((long)sizeX * sizeY, BaseArea);

        private static int ScaleByArea(int value, uint sizeX, uint sizeY)
            => (int)Math.Min((long)value * AreaFactorNumerator(sizeX, sizeY) / BaseArea, int.MaxValue);

        /// <summary>The backend settings for a region of this size - what AddRegion hands Initialize.</summary>
        internal PhysicsBackendSettings ToBackendSettings(uint sizeX, uint sizeY)
        {
            PhysicsBackendSettings s = PhysicsBackendSettings.Default;
            s.Gravity = new SVector3(0f, 0f, Gravity);
            s.MaxBodies = MaxBodies > 0 ? MaxBodies : ScaleByArea(BaseMaxBodies, sizeX, sizeY);
            s.MaxBodyPairs = ScaleCapsWithArea ? ScaleByArea(MaxBodyPairs, sizeX, sizeY) : MaxBodyPairs;
            s.MaxContactConstraints = ScaleCapsWithArea ? ScaleByArea(MaxContactConstraints, sizeX, sizeY) : MaxContactConstraints;
            s.ThreadCount = ThreadCount;
            s.PositionIterations = PositionIterations;
            s.VelocityIterations = VelocityIterations;
            s.CollisionSteps = CollisionSteps;
            s.DeterministicMode = DeterministicMode;
            s.JobPools = JobPools;
            return s;
        }

        /// <summary>How many worker threads these settings ask the shared pool for (the backend's own rule).</summary>
        internal int RequestedThreadCount => JoltPhysicsBackend.ResolveThreadCount(ThreadCount, DeterministicMode);

        /// <summary>JOLT-7: the workers each job pool gets when RequestedThreadCount is split across JobPools.</summary>
        internal int RequestedThreadsPerPool => JoltPhysicsBackend.ResolveThreadsPerPool(RequestedThreadCount, JobPools);

        // ---------------------------------------------------------------- parsing (invariant culture, never throws)

        private static float F(IConfig cfg, string key, float def, float min, float max, List<string> warnings)
        {
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                && float.IsFinite(v) && v >= min && v <= max)
                return v;
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected a finite number in [{min}, {max}]); using the default {def.ToString(CultureInfo.InvariantCulture)}.");
            return def;
        }

        private static int I(IConfig cfg, string key, int def, int min, int max, List<string> warnings)
        {
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= min && v <= max)
                return v;
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected an integer in [{min}, {max}]); using the default {def}.");
            return def;
        }

        private static bool B(IConfig cfg, string key, bool def, List<string> warnings)
        {
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            if (bool.TryParse(raw.Trim(), out bool v))
                return v;
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected true or false); using the default {def}.");
            return def;
        }
    }
}
