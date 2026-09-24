using System.Numerics;
using System.Runtime.CompilerServices;
using Legion.Physics;
using Nini.Config;
using Xunit;

namespace OpenSim.Region.PhysicsModules.LegionJolt.Tests;

/// <summary>
/// JOLT-5 (audit J-8, J-9, S-4c). The [Jolt] section: an empty config reproduces the pre-JOLT-5 constants and
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

    /// <summary>What AddRegion built before JOLT-5: Default, MaxBodies by the area rule, CollisionSteps 6.</summary>
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
            "Source", "OpenSim.Region.PhysicsModules.LegionJolt", "LegionJoltScene.cs"));

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
            "Source", "OpenSim.Region.PhysicsModules.LegionJolt", "LegionJoltScene.cs"));
}
