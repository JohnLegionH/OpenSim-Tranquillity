/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using OMVVector3 = OpenMetaverse.Vector3;
using OMVQuaternion = OpenMetaverse.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A region's own [&lt;Region Name&gt;] section may override [Jolt] MaxBodies, MaxBodyPairs and
/// MaxContactConstraints for that region only, with the keys Jolt_MaxBodies, Jolt_MaxBodyPairs and
/// Jolt_MaxContactConstraints (named for Jolt the way AIS_Enabled is): a region with no override is sized exactly
/// as before, an override in one region's section never reaches a neighbour in the same process, a region named
/// the same as an existing section gets no accidental override from that section's bare keys, and an invalid
/// region value warns and falls back instead of taking the region down.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class RegionLimitOverrideTests
{
    private static JoltConfig Defaults(IConfigSource source = null)
    {
        var warnings = new List<string>();
        JoltConfig c = JoltConfig.FromConfig(source ?? new IniConfigSource(), warnings);
        Assert.Empty(warnings);
        return c;
    }

    // ------------------------------------------------------------------ pure resolution (JoltConfig.ToBackendSettings)

    [Fact]
    public void A_region_with_no_section_is_sized_exactly_as_before()
    {
        var src = new IniConfigSource();
        var warnings = new List<string>();
        JoltRegionLimits none = JoltRegionLimits.FromConfig(src, "No Such Region", warnings);
        Assert.Empty(warnings);
        PhysicsBackendSettings baseline = Defaults().ToBackendSettings(256, 256, 0f, 15000, 100);
        PhysicsBackendSettings withNone = Defaults().ToBackendSettings(256, 256, 0f, 15000, 100, none);
        Assert.Equal(baseline, withNone);
    }

    [Fact]
    public void A_region_section_with_no_matching_keys_carries_no_override()
    {
        var src = new IniConfigSource();
        src.AddConfig("Region A").Set("MaxPrims", "15000");   // an ordinary region key, not one of ours
        var warnings = new List<string>();
        JoltRegionLimits limits = JoltRegionLimits.FromConfig(src, "Region A", warnings);
        Assert.Empty(warnings);
        Assert.Equal(0, limits.MaxBodies);
        Assert.Equal(0, limits.MaxBodyPairs);
        Assert.Equal(0, limits.MaxContactConstraints);
    }

    [Fact]
    public void A_region_named_the_same_as_an_existing_section_gets_no_accidental_override()
    {
        var src = new IniConfigSource();
        IConfig jolt = src.AddConfig("Jolt");
        jolt.Set("MaxBodies", "50000");   // the [Jolt] section's own key - unprefixed, process-wide, unaffected

        // A region literally named "Jolt" looks up the same IConfig object the process-wide [Jolt] setting
        // lives in (source.Configs["Jolt"]). The Jolt_ prefix on the region-override keys is what keeps that
        // bare MaxBodies from being misread as this region's own override.
        var warnings = new List<string>();
        JoltRegionLimits limits = JoltRegionLimits.FromConfig(src, "Jolt", warnings);
        Assert.Empty(warnings);
        Assert.Equal(0, limits.MaxBodies);
        Assert.Equal(0, limits.MaxBodyPairs);
        Assert.Equal(0, limits.MaxContactConstraints);
    }

    [Fact]
    public void A_region_override_is_used_exactly_whatever_the_process_setting_or_automatic_bound()
    {
        var src = new IniConfigSource();
        IConfig regionA = src.AddConfig("Region A");
        regionA.Set("Jolt_MaxBodies", "9000");
        regionA.Set("Jolt_MaxBodyPairs", "9500");
        regionA.Set("Jolt_MaxContactConstraints", "9600");
        var warnings = new List<string>();
        JoltRegionLimits limits = JoltRegionLimits.FromConfig(src, "Region A", warnings);
        Assert.Empty(warnings);

        // A region override wins over an automatically-sized 1024 m region (which would otherwise be 360448/65536/16384).
        PhysicsBackendSettings s = Defaults().ToBackendSettings(1024, 1024, 0f, 15000, 100, limits);
        Assert.Equal(9000, s.MaxBodies);
        Assert.Equal(9500, s.MaxBodyPairs);
        Assert.Equal(9600, s.MaxContactConstraints);
    }

    [Fact]
    public void A_region_override_wins_over_an_explicit_process_setting_too()
    {
        var jolt = new IniConfigSource();
        var j = jolt.AddConfig("Jolt");
        j.Set("MaxBodies", "50000");
        j.Set("MaxBodyPairs", "50000");
        j.Set("MaxContactConstraints", "5000");
        jolt.AddConfig("Region A").Set("Jolt_MaxBodies", "9000");

        var warnings = new List<string>();
        JoltConfig c = JoltConfig.FromConfig(jolt, warnings);
        Assert.Empty(warnings);
        JoltRegionLimits limits = JoltRegionLimits.FromConfig(jolt, "Region A", warnings);
        Assert.Empty(warnings);

        PhysicsBackendSettings s = c.ToBackendSettings(256, 256, 0f, 15000, 100, limits);
        Assert.Equal(9000, s.MaxBodies);          // region override wins
        Assert.Equal(50000, s.MaxBodyPairs);      // falls back to the process setting: no region override given
        Assert.Equal(5000, s.MaxContactConstraints);
    }

    [Fact]
    public void A_region_key_of_zero_carries_no_override_the_same_as_an_absent_key()
    {
        var src = new IniConfigSource();
        src.AddConfig("Region A").Set("Jolt_MaxBodies", "0");
        var warnings = new List<string>();
        JoltRegionLimits limits = JoltRegionLimits.FromConfig(src, "Region A", warnings);
        Assert.Empty(warnings);
        Assert.Equal(0, limits.MaxBodies);

        PhysicsBackendSettings baseline = Defaults().ToBackendSettings(256, 256, 0f, 15000, 100);
        PhysicsBackendSettings withZero = Defaults().ToBackendSettings(256, 256, 0f, 15000, 100, limits);
        Assert.Equal(baseline, withZero);
    }

    [Fact]
    public void An_invalid_region_value_warns_and_falls_back_to_the_process_setting()
    {
        var src = new IniConfigSource();
        src.AddConfig("Region A").Set("Jolt_MaxBodies", "not a number");
        var warnings = new List<string>();
        JoltRegionLimits limits = JoltRegionLimits.FromConfig(src, "Region A", warnings);
        Assert.Equal(0, limits.MaxBodies);
        Assert.Single(warnings);
        Assert.Contains("[Region A] Jolt_MaxBodies", warnings[0]);
        Assert.Contains("using [Jolt] MaxBodies", warnings[0]);

        PhysicsBackendSettings baseline = Defaults().ToBackendSettings(256, 256, 0f, 15000, 100);
        PhysicsBackendSettings withInvalid = Defaults().ToBackendSettings(256, 256, 0f, 15000, 100, limits);
        Assert.Equal(baseline, withInvalid);
    }

    [Fact]
    public void An_out_of_range_region_value_warns_and_falls_back()
    {
        var src = new IniConfigSource();
        src.AddConfig("Region A").Set("Jolt_MaxContactConstraints", "-5");
        var warnings = new List<string>();
        JoltRegionLimits limits = JoltRegionLimits.FromConfig(src, "Region A", warnings);
        Assert.Equal(0, limits.MaxContactConstraints);
        Assert.Single(warnings);
        Assert.Contains("[Region A] Jolt_MaxContactConstraints", warnings[0]);
    }

    // ------------------------------------------------------------------ source labels for the startup line

    [Fact]
    public void Source_names_the_region_override()
    {
        string s = JoltRegionLimits.Source(regionValue: 9000, processValue: 50000, regionName: "Region A", key: "MaxBodies");
        Assert.Contains("region override", s);
        Assert.Contains("[Region A] Jolt_MaxBodies = 9000", s);
    }

    [Fact]
    public void Source_names_the_process_setting_when_there_is_no_override()
    {
        string s = JoltRegionLimits.Source(regionValue: 0, processValue: 50000, regionName: "Region A", key: "MaxBodies");
        Assert.Contains("process setting", s);
        Assert.Contains("[Jolt] MaxBodies = 50000", s);
    }

    [Fact]
    public void Source_names_automatic_when_neither_is_set()
    {
        string s = JoltRegionLimits.Source(regionValue: 0, processValue: 0, regionName: "Region A", key: "MaxBodies");
        Assert.Equal("automatic", s);
    }

    // ------------------------------------------------------------------ two regions sharing one process (JoltScene)

    private const float Heartbeat = 1f / 11f;

    private static JoltScene NewScene(string regionName, uint size, int objectCapacity, int agentCapacity, IConfigSource config)
    {
        var scene = new JoltScene();
        scene.Initialise(config);
        float[] heights = new float[size * size];
        System.Array.Fill(heights, 25f);
        scene.InitialiseWithoutScene(regionName, size, size, heights, 20f, Heartbeat, objectCapacity, agentCapacity);
        var mesher = new OpenSim.Region.PhysicsModules.Meshing.Meshmerizer();
        mesher.Initialise(config);
        typeof(JoltScene).GetField("m_mesher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(scene, mesher);
        return scene;
    }

    private static IConfigSource TwoRegionConfig(int regionAMaxBodies)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        startup.Set("DecodedSculptMapPath", ScratchFolders.Create());
        config.AddConfig("Jolt");   // no process-wide override: automatic
        config.AddConfig("Region A").Set("Jolt_MaxBodies", regionAMaxBodies.ToString());
        config.AddConfig("Region B");   // no override: Region B keeps the automatic/process limit
        return config;
    }

    [Fact]
    public void An_override_applies_to_its_own_region_only()
    {
        IConfigSource config = TwoRegionConfig(regionAMaxBodies: 9000);
        JoltScene a = NewScene("Region A", 256, 15000, 100, config);
        JoltScene b = NewScene("Region B", 256, 15000, 100, config);
        try
        {
            Assert.Equal(9000, a.Backend.GetCapacityStats().MaxBodies);
            Assert.Equal(65536, b.Backend.GetCapacityStats().MaxBodies);   // the standard automatic value, untouched
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public void A_neighbour_in_the_same_process_keeps_its_own_limits_whichever_region_starts_first()
    {
        IConfigSource config = TwoRegionConfig(regionAMaxBodies: 9000);
        JoltScene b = NewScene("Region B", 256, 15000, 100, config);
        JoltScene a = NewScene("Region A", 256, 15000, 100, config);
        try
        {
            Assert.Equal(65536, b.Backend.GetCapacityStats().MaxBodies);
            Assert.Equal(9000, a.Backend.GetCapacityStats().MaxBodies);
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    // ------------------------------------------------------------------ the 80 % warning and the refusal at full
    // name the key for THIS region: the region's own override when it has one, [Jolt] otherwise.

    private static IConfigSource OneRegionConfig(string regionName, (string key, string value)[] regionOverrides,
        (string key, string value)[] joltOverrides = null)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        startup.Set("DecodedSculptMapPath", ScratchFolders.Create());
        IConfig jolt = config.AddConfig("Jolt");
        foreach (var (k, v) in joltOverrides ?? System.Array.Empty<(string, string)>())
            jolt.Set(k, v);
        IConfig region = config.AddConfig(regionName);
        foreach (var (k, v) in regionOverrides)
            region.Set(k, v);
        return config;
    }

    private static PhysicsActor Box(JoltScene scene, int i, uint id)
        => scene.AddPrimShape("box" + i, PrimitiveBaseShape.CreateBox(), new OMVVector3(20f + (i % 40) * 2f, 20f + (i / 40) * 2f, 25.5f),
            new OMVVector3(0.5f, 0.5f, 0.5f), OMVQuaternion.Identity, false, id);

    private static void Step(JoltScene scene, int frames)
    {
        for (int i = 0; i < frames; i++)
            scene.Simulate(Heartbeat);
    }

    [Fact]
    public void The_80_percent_warning_names_the_regions_own_override_key()
    {
        IConfigSource config = OneRegionConfig("Region A", new[] { ("Jolt_MaxBodies", "20"), ("Jolt_MaxBodyPairs", "64"), ("Jolt_MaxContactConstraints", "64") });
        JoltScene scene = NewScene("Region A", 256, 15000, 100, config);
        try
        {
            Step(scene, 1);
            uint id = 500;
            for (int i = 0; i < 14; i++)
                Box(scene, i, id++);
            Step(scene, 2);
            Assert.Equal(15, scene.Backend.GetCapacityStats().LiveBodyCount);
            Assert.Equal(0, scene.BodyLimitWarnings);

            Box(scene, 14, id++);
            Step(scene, 2);
            Assert.Equal(16, scene.Backend.GetCapacityStats().LiveBodyCount);   // 80 %
            Assert.Equal(1, scene.BodyLimitWarnings);
            Assert.Contains("16 of 20", scene.LastBodyLimitWarning);
            Assert.Contains("raise [Region A] Jolt_MaxBodies", scene.LastBodyLimitWarning);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_refusal_at_full_names_the_regions_own_override_key()
    {
        IConfigSource config = OneRegionConfig("Region A",
            new[] { ("Jolt_MaxBodies", "20"), ("Jolt_MaxBodyPairs", "64"), ("Jolt_MaxContactConstraints", "64") },
            new[] { ("CapacityLogIntervalSeconds", "3600") });
        JoltScene scene = NewScene("Region A", 256, 15000, 100, config);
        try
        {
            Step(scene, 1);
            uint id = 600;
            for (int i = 0; i < 19; i++)
                Box(scene, i, id++);
            Step(scene, 2);
            Assert.Equal(20, scene.Backend.GetCapacityStats().LiveBodyCount);   // full: terrain + 19 prims

            Box(scene, 19, id++);
            Step(scene, 2);
            Assert.Equal(1, scene.CapacityWarnings);
            Assert.Contains("bodies refused (MaxBodies 20 reached x1", scene.LastCapacityWarning);
            Assert.Contains("raise [Region A] Jolt_MaxBodies", scene.LastCapacityWarning);
        }
        finally { scene.Dispose(); }
    }
}
