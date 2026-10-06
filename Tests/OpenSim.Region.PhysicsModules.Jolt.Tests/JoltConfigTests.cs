/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Runtime.CompilerServices;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Nini.Config;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The [Jolt] section: an empty config reproduces the built-in constants and
/// AddRegion settings exactly, every key parses, invalid values fall back with a warning, and area scaling of the
/// pair/contact caps is opt-in.
/// </summary>
public class JoltConfigTests
{
    private static IConfigSource Source(params (string key, string value)[] kv)
    {
        var src = new IniConfigSource();
        var cfg = src.AddConfig("Jolt");
        foreach (var (k, v) in kv)
            cfg.Set(k, v);
        return src;
    }

    /// <summary>What AddRegion builds with no [Jolt] settings: Default, MaxBodies by the area rule, CollisionSteps 6.</summary>
    private static PhysicsBackendSettings Today(uint sx, uint sy)
    {
        var s = PhysicsBackendSettings.Default;
        s.MaxBodies = (int)(65536L * Math.Max((long)sx * sy, 256L * 256L) / (256L * 256L));
        s.CollisionSteps = 6;
        return s;
    }

    [Theory]
    [InlineData(256u, 256u, 65536)]
    [InlineData(1024u, 1024u, 1048576)]
    public void Empty_config_reproduces_todays_settings(uint sx, uint sy, int expectedMaxBodies)
    {
        foreach (var src in new[] { new IniConfigSource(), Source() })   // no section, and an empty section
        {
            var warnings = new List<string>();
            var c = JoltConfig.FromConfig(src, warnings);
            Assert.Empty(warnings);
            var s = c.ToBackendSettings(sx, sy);
            Assert.Equal(Today(sx, sy), s);
            Assert.Equal(expectedMaxBodies, s.MaxBodies);
            Assert.Equal(new Vector3(0f, 0f, -9.80665f), s.Gravity);
            Assert.Equal(4.0f, c.AvatarJumpSpeed);
            Assert.Equal(CharacterDesc.Default.JumpSpeed, c.AvatarJumpSpeed);
            Assert.Equal(65536, c.BodyUpdateBufferMax);
            Assert.Equal(1024, c.CharacterUpdateBufferMax);
            Assert.Equal(0, c.ContactBufferMax);
            Assert.Equal(10f, c.CapacityLogIntervalSeconds);
        }
    }

    [Fact]
    public void Every_key_parses()
    {
        var warnings = new List<string>();
        var c = JoltConfig.FromConfig(Source(
            ("Gravity", "-3.5"), ("CollisionSteps", "4"), ("PositionIterations", "3"), ("VelocityIterations", "12"),
            ("ThreadCount", "5"), ("DeterministicMode", "true"), ("MaxBodies", "1000"), ("MaxBodyPairs", "2000"),
            ("MaxContactConstraints", "3000"), ("ScaleCapsWithArea", "true"), ("BodyUpdateBufferMax", "4096"),
            ("CharacterUpdateBufferMax", "512"), ("ContactBufferMax", "9000"), ("AvatarJumpSpeed", "5.25"),
            ("CapacityLogIntervalSeconds", "30")), warnings);
        Assert.Empty(warnings);

        Assert.Equal(-3.5f, c.Gravity);
        Assert.Equal(4, c.CollisionSteps);
        Assert.Equal(3, c.PositionIterations);
        Assert.Equal(12, c.VelocityIterations);
        Assert.Equal(5, c.ThreadCount);
        Assert.True(c.DeterministicMode);
        Assert.Equal(1000, c.MaxBodies);
        Assert.Equal(2000, c.MaxBodyPairs);
        Assert.Equal(3000, c.MaxContactConstraints);
        Assert.True(c.ScaleCapsWithArea);
        Assert.Equal(4096, c.BodyUpdateBufferMax);
        Assert.Equal(512, c.CharacterUpdateBufferMax);
        Assert.Equal(9000, c.ContactBufferMax);
        Assert.Equal(5.25f, c.AvatarJumpSpeed);
        Assert.Equal(30f, c.CapacityLogIntervalSeconds);
        Assert.Equal(1, c.RequestedThreadCount);   // DeterministicMode forces one worker

        var s = c.ToBackendSettings(256, 256);
        Assert.Equal(new Vector3(0f, 0f, -3.5f), s.Gravity);
        Assert.Equal(1000, s.MaxBodies);            // explicit MaxBodies is taken as-is
        Assert.Equal(4, s.CollisionSteps);
        Assert.Equal(3, s.PositionIterations);
        Assert.Equal(12, s.VelocityIterations);
        Assert.Equal(5, s.ThreadCount);
        Assert.True(s.DeterministicMode);
    }

    [Theory]
    [InlineData("Gravity", "NaN")]
    [InlineData("Gravity", "Infinity")]
    [InlineData("Gravity", "downwards")]
    [InlineData("CollisionSteps", "0")]
    [InlineData("CollisionSteps", "1.5")]
    [InlineData("PositionIterations", "-1")]
    [InlineData("VelocityIterations", "0")]
    [InlineData("ThreadCount", "-2")]
    [InlineData("DeterministicMode", "yes please")]
    [InlineData("MaxBodies", "-1")]
    [InlineData("MaxBodies", "99999999999")]
    [InlineData("MaxBodyPairs", "0")]
    [InlineData("MaxContactConstraints", "")]
    [InlineData("ScaleCapsWithArea", "2")]
    [InlineData("BodyUpdateBufferMax", "0")]
    [InlineData("CharacterUpdateBufferMax", "-5")]
    [InlineData("ContactBufferMax", "-1")]
    [InlineData("AvatarJumpSpeed", "NaN")]
    [InlineData("AvatarJumpSpeed", "-1")]
    [InlineData("CapacityLogIntervalSeconds", "0")]
    public void Invalid_values_fall_back_with_a_warning(string key, string value)
    {
        var warnings = new List<string>();
        var c = JoltConfig.FromConfig(Source((key, value)), warnings);
        Assert.Single(warnings);
        Assert.Contains(key, warnings[0]);
        var d = JoltConfig.FromConfig(new IniConfigSource(), null);
        Assert.Equal(d.ToBackendSettings(256, 256), c.ToBackendSettings(256, 256));
        Assert.Equal(d.AvatarJumpSpeed, c.AvatarJumpSpeed);
        Assert.Equal(d.BodyUpdateBufferMax, c.BodyUpdateBufferMax);
        Assert.Equal(d.CharacterUpdateBufferMax, c.CharacterUpdateBufferMax);
        Assert.Equal(d.ContactBufferMax, c.ContactBufferMax);
        Assert.Equal(d.CapacityLogIntervalSeconds, c.CapacityLogIntervalSeconds);
    }

    [Fact]
    public void VehicleGroundGravityFactor_defaults_to_whole_gravity_parses_and_falls_back()
    {
        Assert.Equal(1f, JoltConfig.FromConfig(new IniConfigSource(), null).VehicleGroundGravityFactor);

        var warnings = new List<string>();
        Assert.Equal(0.2f, JoltConfig.FromConfig(Source(("VehicleGroundGravityFactor", "0.2")), warnings).VehicleGroundGravityFactor);
        Assert.Empty(warnings);

        foreach (string bad in new[] { "-0.1", "1.5", "NaN", "a fifth" })
        {
            warnings.Clear();
            Assert.Equal(1f, JoltConfig.FromConfig(Source(("VehicleGroundGravityFactor", bad)), warnings).VehicleGroundGravityFactor);
            Assert.Single(warnings);
        }
    }

    [Fact]
    public void VehiclePresets_defaults_to_the_documented_set_parses_and_falls_back()
    {
        JoltConfig d = JoltConfig.FromConfig(new IniConfigSource(), null);
        Assert.Equal(Vehicles.VehiclePresetSet.Documented, d.VehiclePresets);
        Assert.Equal(Vehicles.VehiclePresetSet.Documented, d.ToVehicleSettings().Presets);

        var warnings = new List<string>();
        foreach ((string text, Vehicles.VehiclePresetSet set) in new[] { ("legacy", Vehicles.VehiclePresetSet.Legacy), ("Legacy", Vehicles.VehiclePresetSet.Legacy), ("documented", Vehicles.VehiclePresetSet.Documented) })
        {
            JoltConfig c = JoltConfig.FromConfig(Source(("VehiclePresets", text)), warnings);
            Assert.Equal(set, c.VehiclePresets);
            Assert.Equal(set, c.ToVehicleSettings().Presets);
        }
        Assert.Empty(warnings);

        foreach (string bad in new[] { "1", "-1", "halcyon", "" })
        {
            warnings.Clear();
            Assert.Equal(Vehicles.VehiclePresetSet.Documented, JoltConfig.FromConfig(Source(("VehiclePresets", bad)), warnings).VehiclePresets);
            Assert.Single(warnings);
        }
    }

    [Fact]
    public void JobPools_defaults_parses_and_falls_back()
    {
        var d = JoltConfig.FromConfig(new IniConfigSource(), null);
        Assert.Equal(1, d.JobPools);
        Assert.Equal(1, d.ToBackendSettings(256, 256).JobPools);
        Assert.Equal(1, PhysicsBackendSettings.Default.JobPools);
        Assert.Equal(1, JoltPhysicsBackend.ResolveJobPools(0));   // an unset struct means one pool

        var w = new List<string>();
        var c = JoltConfig.FromConfig(Source(("JobPools", "7")), w);
        Assert.Empty(w);
        Assert.Equal(7, c.JobPools);
        Assert.Equal(7, c.ToBackendSettings(1024, 1024).JobPools);
        Assert.Equal(64, JoltConfig.FromConfig(Source(("JobPools", "64")), w).JobPools);
        Assert.Empty(w);

        foreach (var bad in new[] { "0", "65", "-1", "four", "" })
        {
            w.Clear();
            var b = JoltConfig.FromConfig(Source(("JobPools", bad)), w);
            Assert.Single(w);
            Assert.Contains("JobPools", w[0]);
            Assert.Equal(1, b.JobPools);
        }

        // MaxConcurrentUpdates is not a [Jolt] key and is not read.
        w.Clear();
        Assert.Equal(1, JoltConfig.FromConfig(Source(("MaxConcurrentUpdates", "4")), w).JobPools);
        Assert.Empty(w);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ThreadCount_is_split_across_the_pools(int pools)
    {
        // ThreadCount 0 = ProcessorCount - 1 in total, split evenly; the remainder is not started.
        int total = Math.Max(1, Environment.ProcessorCount - 1);
        var zero = JoltConfig.FromConfig(Source(("ThreadCount", "0"), ("JobPools", pools.ToString())), null);
        Assert.Equal(total, zero.RequestedThreadCount);
        Assert.Equal(Math.Max(1, total / pools), zero.RequestedThreadsPerPool);

        // An explicit ThreadCount = ProcessorCount.
        int cpus = Math.Min(Environment.ProcessorCount, 256);
        var all = JoltConfig.FromConfig(Source(("ThreadCount", cpus.ToString()), ("JobPools", pools.ToString())), null);
        Assert.Equal(cpus, all.RequestedThreadCount);
        Assert.Equal(Math.Max(1, cpus / pools), all.RequestedThreadsPerPool);
        Assert.True(all.RequestedThreadsPerPool * pools <= cpus || cpus < pools);

        // One pool holds every thread.
        if (pools == 1)
            Assert.Equal(all.RequestedThreadCount, all.RequestedThreadsPerPool);

        Assert.Equal(12 / pools, JoltPhysicsBackend.ResolveThreadsPerPool(12, pools));
        Assert.Equal(pools == 2 ? 3 : pools == 3 ? 2 : 7, JoltPhysicsBackend.ResolveThreadsPerPool(7, pools));
    }

    [Fact]
    public void ThreadCount_below_JobPools_still_gives_every_pool_a_thread()
    {
        Assert.Equal(1, JoltPhysicsBackend.ResolveThreadsPerPool(2, 3));
        Assert.Equal(1, JoltPhysicsBackend.ResolveThreadsPerPool(1, 64));
        var c = JoltConfig.FromConfig(Source(("ThreadCount", "2"), ("JobPools", "5")), null);
        Assert.Equal(1, c.RequestedThreadsPerPool);
        var det = JoltConfig.FromConfig(Source(("DeterministicMode", "true"), ("JobPools", "4")), null);
        Assert.Equal(1, det.RequestedThreadsPerPool);
    }

    [Fact]
    public void ScaleCapsWithArea_scales_pairs_and_contacts_by_16_for_1024()
    {
        var c = JoltConfig.FromConfig(Source(("ScaleCapsWithArea", "true")), null);
        var big = c.ToBackendSettings(1024, 1024);
        Assert.Equal(65536 * 16, big.MaxBodyPairs);
        Assert.Equal(16384 * 16, big.MaxContactConstraints);
        Assert.Equal(1048576, big.MaxBodies);

        var small = c.ToBackendSettings(256, 256);   // a standard region is unchanged
        Assert.Equal(65536, small.MaxBodyPairs);
        Assert.Equal(16384, small.MaxContactConstraints);

        var off = JoltConfig.FromConfig(new IniConfigSource(), null).ToBackendSettings(1024, 1024);
        Assert.Equal(65536, off.MaxBodyPairs);        // opt-in: off by default
        Assert.Equal(16384, off.MaxContactConstraints);
    }

    // ------------------------------------------------------------------ the module reads it

    private static string Scene([CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..",
            "Source", "OpenSim.Region.PhysicsModules.Jolt", "JoltScene.cs"));

    [Fact]
    public void AddRegion_builds_its_settings_from_JoltConfig()
    {
        var s = Scene();
        var at = s.IndexOf("public void AddRegion(Scene scene)", StringComparison.Ordinal);
        var end = s.IndexOf("public void RemoveRegion(", at, StringComparison.Ordinal);
        var body = s[at..end];
        Assert.Contains(".ToBackendSettings(", body);
        Assert.DoesNotContain("PhysicsBackendSettings.Default", body);
        Assert.DoesNotContain("settings.CollisionSteps = 6", body);
        Assert.Contains("JoltConfig.FromConfig(", s);
    }

    [Fact]
    public void AvatarJumpSpeed_reaches_the_CharacterDesc()
    {
        var c = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ScenePath())!, "JoltCharacter.cs"));
        var at = c.IndexOf("private void CreateCharacterInternal()", StringComparison.Ordinal);
        var body = c[at..c.IndexOf("_backend.CreateCharacter(desc)", at, StringComparison.Ordinal)];
        Assert.Contains("desc.JumpSpeed = _module.AvatarJumpSpeed", body);
    }

    private static string ScenePath([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..",
            "Source", "OpenSim.Region.PhysicsModules.Jolt", "JoltScene.cs"));
}
