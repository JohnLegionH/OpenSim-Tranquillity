/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The [Jolt] config section.
//
// Every knob the module used to hardcode, read once in Initialise. The rule: every default reproduces the
// module's built-in behaviour exactly (PhysicsBackendSettings.Default + CollisionSteps 6 + the MaxBodies area rule +
// today's step-buffer caps). Scaling the pair/contact caps with region area is opt-in (ScaleCapsWithArea).
// Invalid values (unparsable, non-finite, out of range) warn and fall back to the default - a typo in the INI
// must never take a region down or silently change physics.

using System;
using System.Collections.Generic;
using System.Globalization;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Nini.Config;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class JoltConfig
    {
        internal const string Section = "Jolt";

        // 65536 bodies per standard 256 x 256 m region, scaled by AREA for varregions.
        internal const int BaseMaxBodies = 65536;
        internal const long BaseArea = 256L * 256L;

        public float Gravity = -9.80665f;          // world Z gravity, m/s^2
        public int CollisionSteps = 6;             // solver sub-steps inside Update
        public int PositionIterations = 2;
        public int VelocityIterations = 10;
        public int ThreadCount = 0;                // all pools together; 0 = automatic: each pool's share of ProcessorCount - 1, at most 4 (first region wins)
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
        public int JobPools = 1;                   // job pools, one physics update at a time each; splits ThreadCount
        public bool JobPoolFairHandoff = false;    // true: a job pool is granted first come, first served (per physics step)
        public float PhysicsStepRate = DefaultPhysicsStepRate; // Hz; 0 = one physics step per heartbeat
        public int PhysicsStepCollisionSteps = 2;  // solver sub-steps per physics step, used only when PhysicsStepRate is on
        public float VehicleGroundGravityFactor = 1f;   // gravity on a car or sled touching something; 1 = whole
        public VehiclePresetSet VehiclePresets = VehiclePresetSet.Documented;   // the values llSetVehicleType gives each type

        // The limits a script's vehicle parameters are held to (VehicleSettings says what each one does).
        public float VehicleMaxLinearSpeed = VehicleLimits.MaxLinearVelocity;       // m/s
        public float VehicleReferenceSpeed = VehicleLimits.MaxLegacyLinearVelocity; // m/s
        public float VehicleMaxAngularSpeed = VehicleLimits.MaxAngularVelocity;     // rad/s
        public float VehicleMinTimescale = VehicleLimits.MinPhysicsTimestep;        // s
        public float VehicleMaxTimescale = VehicleLimits.MaxTimescale;              // s
        public float VehicleMaxDecayTimescale = VehicleLimits.MaxDecayTimescale;    // s
        public float VehicleMaxHoverTimescale = VehicleLimits.MaxHoverTimescale;    // s
        public float VehicleMaxAttractTimescale = VehicleLimits.MaxAttractTimescale;// s
        public float VehicleMaxMotorOffset = VehicleLimits.MaxLinearOffset;         // m
        public float VehicleMinHoverHeight = VehicleLimits.MinRegionHeight;         // m
        public float VehicleMaxHoverHeight = VehicleLimits.MaxRegionHeight;         // m
        public float VehicleSledAssist = VehicleSettings.DefaultSledAssist;         // share of gravity
        public float VehicleContactFriction = 0f;                                   // contact friction of a vehicle body
        public float VehicleRestSpeed = VehicleSettings.DefaultRestSpeed;           // m/s; 0 = no rest rule

        // The engine's own speed caps on every moving body (Jolt's BodyCreationSettings defaults).
        public float BodyMaxLinearSpeed = PhysicsBackendSettings.JoltMaxLinearSpeed;    // m/s
        public float BodyMaxAngularSpeed = PhysicsBackendSettings.JoltMaxAngularSpeed;  // rad/s

        public bool AllowUnrecordedNative = false; // true: load a joltc whose hash the module has no record of

        // Script ray casts (llCastRay) and pushes on avatars (llPushObject): limits on what scripts can make one region
        // do. They guard against abuse, so each defaults to its safe value.
        public float RayCastBudgetMs = PhysicsBackendSettings.DefaultRayCastBudgetMs;         // script casts, per region per heartbeat
        public float RayCastSimulatorBudgetMs = PhysicsBackendSettings.DefaultRayCastSimulatorBudgetMs; // every other cast, the same
        public int RayCastMaxTestedHits = PhysicsBackendSettings.DefaultRayCastMaxTestedHits; // per cast
        public int RayCastMaxHits = MaxRayCastHits;                                          // hits one cast returns
        public float AvatarPushMaxSpeed = PhysicsBackendSettings.DefaultAvatarPushMaxSpeed;   // m/s; 0 = pushes do not move avatars
        public float AvatarPushRecovery = PhysicsBackendSettings.DefaultAvatarPushRecovery;   // m/s per second

        // Second Life's documented maximum for llCastRay's RC_MAX_HITS ("Maximum value is 256").
        internal const int MaxRayCastHits = 256;

        // The highest PhysicsStepRate accepted. Each step costs a backend update, so a rate far above the heartbeat
        // mostly hits the per-heartbeat step cap (see SubstepAccumulator.MaxStepsPerFrame).
        internal const float MaxPhysicsStepRate = 1000f;

        // The default PhysicsStepRate: fixed 45 Hz physics steps, whatever the heartbeat.
        internal const float DefaultPhysicsStepRate = 45f;

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
            c.AllowUnrecordedNative = B(cfg, "AllowUnrecordedNative", c.AllowUnrecordedNative, warnings);
            c.BodyUpdateBufferMax = I(cfg, "BodyUpdateBufferMax", c.BodyUpdateBufferMax, 1, 8_388_607, warnings);
            c.CharacterUpdateBufferMax = I(cfg, "CharacterUpdateBufferMax", c.CharacterUpdateBufferMax, 1, 65536, warnings);
            c.ContactBufferMax = I(cfg, "ContactBufferMax", c.ContactBufferMax, 0, 16_777_216, warnings);
            c.AvatarJumpSpeed = F(cfg, "AvatarJumpSpeed", c.AvatarJumpSpeed, 0f, 100f, warnings);
            c.CapacityLogIntervalSeconds = F(cfg, "CapacityLogIntervalSeconds", c.CapacityLogIntervalSeconds, 0.1f, 86400f, warnings);
            c.JobPools = I(cfg, "JobPools", c.JobPools, 1, JoltPhysicsBackend.MaxJobPools, warnings);
            c.JobPoolFairHandoff = B(cfg, "JobPoolFairHandoff", c.JobPoolFairHandoff, warnings);
            // An invalid PhysicsStepRate gives one physics step per heartbeat, as it did while that was the default.
            c.PhysicsStepRate = F(cfg, "PhysicsStepRate", c.PhysicsStepRate, 0f, MaxPhysicsStepRate, warnings, onInvalid: 0f);
            c.PhysicsStepCollisionSteps = I(cfg, "PhysicsStepCollisionSteps", c.PhysicsStepCollisionSteps, 1, 64, warnings);
            c.VehicleGroundGravityFactor = F(cfg, "VehicleGroundGravityFactor", c.VehicleGroundGravityFactor, 0f, 1f, warnings);
            c.VehiclePresets = E(cfg, "VehiclePresets", c.VehiclePresets, warnings);
            c.VehicleMaxLinearSpeed = F(cfg, "VehicleMaxLinearSpeed", c.VehicleMaxLinearSpeed, 1f, 10000f, warnings);
            c.VehicleReferenceSpeed = F(cfg, "VehicleReferenceSpeed", c.VehicleReferenceSpeed, 0.1f, 10000f, warnings);
            c.VehicleMaxAngularSpeed = F(cfg, "VehicleMaxAngularSpeed", c.VehicleMaxAngularSpeed, 0.1f, 1000f, warnings);
            c.VehicleMinTimescale = F(cfg, "VehicleMinTimescale", c.VehicleMinTimescale, (float)VehicleMotorSolver.MinTimescale, 1f, warnings);
            c.VehicleMaxTimescale = F(cfg, "VehicleMaxTimescale", c.VehicleMaxTimescale, 1f, VehicleLimits.MaxTimescale, warnings);
            c.VehicleMaxDecayTimescale = F(cfg, "VehicleMaxDecayTimescale", c.VehicleMaxDecayTimescale, 1f, VehicleLimits.MaxTimescale, warnings);
            c.VehicleMaxHoverTimescale = F(cfg, "VehicleMaxHoverTimescale", c.VehicleMaxHoverTimescale, 1f, VehicleLimits.MaxTimescale, warnings);
            c.VehicleMaxAttractTimescale = F(cfg, "VehicleMaxAttractTimescale", c.VehicleMaxAttractTimescale, 1f, VehicleLimits.MaxTimescale, warnings);
            c.VehicleMaxMotorOffset = F(cfg, "VehicleMaxMotorOffset", c.VehicleMaxMotorOffset, 0f, 10000f, warnings);
            c.VehicleMinHoverHeight = F(cfg, "VehicleMinHoverHeight", c.VehicleMinHoverHeight, -100000f, 100000f, warnings);
            c.VehicleMaxHoverHeight = F(cfg, "VehicleMaxHoverHeight", c.VehicleMaxHoverHeight, -100000f, 100000f, warnings);
            if (c.VehicleMinHoverHeight > c.VehicleMaxHoverHeight)
            {
                warnings?.Add($"[{Section}] VehicleMinHoverHeight ({c.VehicleMinHoverHeight}) is above VehicleMaxHoverHeight ({c.VehicleMaxHoverHeight}); using the defaults for both.");
                c.VehicleMinHoverHeight = VehicleLimits.MinRegionHeight;
                c.VehicleMaxHoverHeight = VehicleLimits.MaxRegionHeight;
            }
            c.VehicleSledAssist = F(cfg, "VehicleSledAssist", c.VehicleSledAssist, 0f, 10f, warnings);
            c.VehicleContactFriction = F(cfg, "VehicleContactFriction", c.VehicleContactFriction, 0f, 10f, warnings);
            c.VehicleRestSpeed = F(cfg, "VehicleRestSpeed", c.VehicleRestSpeed, 0f, 1f, warnings);
            c.BodyMaxLinearSpeed = F(cfg, "BodyMaxLinearSpeed", c.BodyMaxLinearSpeed, 1f, 100000f, warnings);
            c.BodyMaxAngularSpeed = F(cfg, "BodyMaxAngularSpeed", c.BodyMaxAngularSpeed, 0.1f, 10000f, warnings);
            c.RayCastBudgetMs = F(cfg, "RayCastBudgetMs", c.RayCastBudgetMs, 0.1f, 1000f, warnings);
            c.RayCastSimulatorBudgetMs = F(cfg, "RayCastSimulatorBudgetMs", c.RayCastSimulatorBudgetMs, 0.1f, 1000f, warnings);
            c.RayCastMaxTestedHits = I(cfg, "RayCastMaxTestedHits", c.RayCastMaxTestedHits, 1, 1_000_000, warnings);
            c.RayCastMaxHits = I(cfg, "RayCastMaxHits", c.RayCastMaxHits, 1, MaxRayCastHits, warnings);
            c.AvatarPushMaxSpeed = F(cfg, "AvatarPushMaxSpeed", c.AvatarPushMaxSpeed, 0f, 1000f, warnings);
            c.AvatarPushRecovery = F(cfg, "AvatarPushRecovery", c.AvatarPushRecovery, 0f, 1000f, warnings);
            return c;
        }

        /// <summary>The vehicle settings every vehicle controller in a region with this configuration shares.</summary>
        internal VehicleSettings ToVehicleSettings() => new VehicleSettings
        {
            Presets = VehiclePresets,
            MaxLinearSpeed = VehicleMaxLinearSpeed,
            ReferenceSpeed = VehicleReferenceSpeed,
            MaxAngularSpeed = VehicleMaxAngularSpeed,
            MinTimescale = VehicleMinTimescale,
            MaxTimescale = VehicleMaxTimescale,
            MaxDecayTimescale = VehicleMaxDecayTimescale,
            MaxHoverTimescale = VehicleMaxHoverTimescale,
            MaxAttractTimescale = VehicleMaxAttractTimescale,
            MaxMotorOffset = VehicleMaxMotorOffset,
            MinHoverHeight = VehicleMinHoverHeight,
            MaxHoverHeight = VehicleMaxHoverHeight,
            SledAssist = VehicleSledAssist,
            ContactFriction = VehicleContactFriction,
            RestSpeed = VehicleRestSpeed,
        };

        /// <summary>
        /// The physics step rate a region with this heartbeat runs at: <see cref="PhysicsStepRate"/>, or 0 (one step
        /// per heartbeat) when it is off or below the heartbeat's own rate, which it cannot honour. A refused rate
        /// sets <paramref name="warning"/>.
        /// </summary>
        internal float EffectivePhysicsStepRate(float heartbeatSeconds, out string warning)
        {
            warning = null;
            if (PhysicsStepRate <= 0f)
                return 0f;
            if (!(heartbeatSeconds > 0f) || PhysicsStepRate * heartbeatSeconds < 1f - 1e-3f)
            {
                float heartbeatHz = heartbeatSeconds > 0f ? 1f / heartbeatSeconds : 0f;
                warning = $"[{Section}] PhysicsStepRate = {PhysicsStepRate.ToString(CultureInfo.InvariantCulture)} is below the heartbeat's own rate " +
                          $"({heartbeatHz.ToString("0.##", CultureInfo.InvariantCulture)} Hz, [Startup] FrameTime); using 0, one physics step per heartbeat.";
                return 0f;
            }
            return PhysicsStepRate;
        }

        /// <summary>Area multiplier against a standard region: 1 for 256 x 256 (and anything smaller), 16 for 1024 x 1024.</summary>
        internal static long AreaFactorNumerator(uint sizeX, uint sizeY) => Math.Max((long)sizeX * sizeY, BaseArea);

        private static int ScaleByArea(int value, uint sizeX, uint sizeY)
            => (int)Math.Min((long)value * AreaFactorNumerator(sizeX, sizeY) / BaseArea, int.MaxValue);

        /// <summary>The backend settings for a region of this size - what AddRegion hands Initialize.</summary>
        internal PhysicsBackendSettings ToBackendSettings(uint sizeX, uint sizeY) => ToBackendSettings(sizeX, sizeY, substepping: false);

        /// <summary>As above; with <paramref name="substepping"/> the solver takes PhysicsStepCollisionSteps per step.</summary>
        internal PhysicsBackendSettings ToBackendSettings(uint sizeX, uint sizeY, bool substepping)
        {
            PhysicsBackendSettings s = PhysicsBackendSettings.Default;
            s.Gravity = new SVector3(0f, 0f, Gravity);
            s.MaxBodies = MaxBodies > 0 ? MaxBodies : ScaleByArea(BaseMaxBodies, sizeX, sizeY);
            s.MaxBodyPairs = ScaleCapsWithArea ? ScaleByArea(MaxBodyPairs, sizeX, sizeY) : MaxBodyPairs;
            s.MaxContactConstraints = ScaleCapsWithArea ? ScaleByArea(MaxContactConstraints, sizeX, sizeY) : MaxContactConstraints;
            s.ThreadCount = ThreadCount;
            s.PositionIterations = PositionIterations;
            s.VelocityIterations = VelocityIterations;
            s.CollisionSteps = substepping ? PhysicsStepCollisionSteps : CollisionSteps;
            s.DeterministicMode = DeterministicMode;
            s.AllowUnrecordedNative = AllowUnrecordedNative;
            s.JobPools = JobPools;
            s.JobPoolFairHandoff = JobPoolFairHandoff;
            s.MaxBodyLinearSpeed = BodyMaxLinearSpeed;
            s.MaxBodyAngularSpeed = BodyMaxAngularSpeed;
            s.RayCastBudgetMs = RayCastBudgetMs;
            s.RayCastSimulatorBudgetMs = RayCastSimulatorBudgetMs;
            s.RayCastMaxTestedHits = RayCastMaxTestedHits;
            s.AvatarPushMaxSpeed = AvatarPushMaxSpeed;
            s.AvatarPushRecovery = AvatarPushRecovery;
            return s;
        }

        /// <summary>How many worker threads these settings ask the shared pool for (the backend's own rule).</summary>
        internal int RequestedThreadCount => JoltPhysicsBackend.ResolveThreadCount(ThreadCount, DeterministicMode, JobPools);

        /// <summary>The workers each job pool gets when RequestedThreadCount is split across JobPools.</summary>
        internal int RequestedThreadsPerPool => JoltPhysicsBackend.ResolveThreadsPerPool(RequestedThreadCount, JobPools);

        // ---------------------------------------------------------------- parsing (invariant culture, never throws)

        // onInvalid: the value an invalid entry gives, when that is not the default.
        private static float F(IConfig cfg, string key, float def, float min, float max, List<string> warnings, float? onInvalid = null)
        {
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                && float.IsFinite(v) && v >= min && v <= max)
                return v;
            string used = onInvalid is float fallback
                ? fallback.ToString(CultureInfo.InvariantCulture)
                : "the default " + def.ToString(CultureInfo.InvariantCulture);
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected a finite number in [{min}, {max}]); using {used}.");
            return onInvalid ?? def;
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

        // An enum value by its name, case ignored (numbers are not accepted).
        private static T E<T>(IConfig cfg, string key, T def, List<string> warnings) where T : struct, Enum
        {
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            string name = raw.Trim();
            if (name.Length > 0 && !char.IsDigit(name[0]) && name[0] != '-' && Enum.TryParse(name, true, out T v) && Enum.IsDefined(v))
                return v;
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected one of {string.Join(", ", Enum.GetNames<T>())}); using the default {def}.");
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
