using System.Numerics;
using Legion.Physics;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.LegionJolt.Tests;

/// <summary>
/// JOLT-3 (audit S-4a, S-4b). Capacity failures are surfaced, not silent: CreateBody at MaxBodies returns
/// BodyId.Invalid and counts it (it used to record Jolt's invalid id as a live body), and every non-None
/// PhysicsUpdateError from the step is counted per flag and kept as the last value.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class CapacityTests
{
    private readonly ITestOutputHelper _out;
    public CapacityTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void Bodies_past_MaxBodies_are_refused_and_counted()
    {
        using var t = new JoltTestBackend(JoltTestBackend.Settings(maxBodies: 16));
        var box = t.B.CreateBoxShape(new Vector3(0.5f));
        var ids = new List<BodyId>();
        for (var i = 0; i < 20; i++)
        {
            var d = BodyDesc.Default;
            d.Shape = box;
            d.Position = new Vector3(10f + i * 2f, 10f, 10f);
            d.UserData = (uint)(100 + i);
            ids.Add(t.B.CreateBody(d));
        }

        Assert.Equal(4, ids.Count(id => !id.IsValid));
        Assert.Equal(4, t.B.GetCapacityStats().BodyCreateFailures);
        for (var i = 16; i < 20; i++)
        {
            Assert.False(ids[i].IsValid);
            Assert.False(t.B.IsBodyValid(ids[i]));
        }
        for (var i = 0; i < 16; i++)
        {
            Assert.True(t.B.IsBodyValid(ids[i]));
            Assert.True(t.B.TryGetBodyState(ids[i], out var s));
            Assert.Equal((uint)(100 + i), s.UserData);
            Assert.Equal(10f + i * 2f, s.Position.X, 3);
        }
        var stats = t.B.GetCapacityStats();
        Assert.Equal(16, stats.LiveBodyCount);
        Assert.Equal(16, stats.MaxBodies);
    }

    /// <summary>The backend's Jolt-id -> record map: 0xFFFFFFFF must never be a key.</summary>
    private static System.Collections.IDictionary JoltToRecord(Legion.Physics.Jolt.JoltPhysicsBackend b)
        => (System.Collections.IDictionary)typeof(Legion.Physics.Jolt.JoltPhysicsBackend)
            .GetField("_joltToRecord", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(b)!;

    [Fact]
    public void Terrain_and_avatar_marker_past_MaxBodies_are_refused_and_counted()
    {
        // JOLT-7c: CreateBody's invalid-id policy at the terrain body and the avatar query marker.
        using var t = new JoltTestBackend(JoltTestBackend.Settings(maxBodies: 16));
        var box = t.B.CreateBoxShape(new Vector3(0.5f));
        for (var i = 0; i < 16; i++)
        {
            var d = BodyDesc.Default;
            d.Shape = box;
            d.Position = new Vector3(10f + i * 2f, 10f, 10f);
            Assert.True(t.B.CreateBody(d).IsValid);
        }
        Assert.Equal(0, t.B.GetCapacityStats().BodyCreateFailures);

        var heights = new float[17 * 17];
        var field = t.B.CreateHeightFieldShape(heights, 17, 17, Vector3.One);
        t.B.SetTerrain(field, Vector3.Zero);
        var s = t.B.GetCapacityStats();
        Assert.Equal(1, s.BodyCreateFailures);
        Assert.True(s.TerrainBodyMissing);

        var cd = CharacterDesc.Default;
        cd.Position = new Vector3(100f, 100f, 1.5f);
        var avatar = t.B.CreateCharacter(cd);
        Assert.True(avatar.Value != 0);   // the avatar itself still exists; only its query marker is refused
        s = t.B.GetCapacityStats();
        Assert.Equal(2, s.BodyCreateFailures);
        Assert.Equal(1, s.CharacterCount);
        Assert.Equal(16, s.LiveBodyCount);

        var map = JoltToRecord(t.B);
        Assert.False(map.Contains(0xFFFFFFFFu), "an invalid Jolt BodyID was recorded");
        Assert.Equal(16, map.Count);

        // Stepping, a second terrain attempt and removing the avatar must not touch the invalid id.
        t.Step();
        t.B.SetTerrain(field, Vector3.Zero);
        Assert.Equal(3, t.B.GetCapacityStats().BodyCreateFailures);
        t.B.RemoveCharacter(avatar);
        t.Step();
        Assert.False(JoltToRecord(t.B).Contains(0xFFFFFFFFu));
    }

    [Fact]
    public void Tiny_pair_and_contact_caps_record_the_update_error()
    {
        using var t = new JoltTestBackend(JoltTestBackend.Settings(maxBodies: 1024, maxBodyPairs: 4, maxContactConstraints: 4));
        t.Ground();
        var box = t.B.CreateBoxShape(new Vector3(0.4f));
        for (var i = 0; i < 40; i++)
            t.Dynamic(box, new Vector3(110f + (i % 8) * 1.5f, 110f + (i / 8) * 1.5f, 0.6f));

        PhysicsCapacityStats s = default;
        for (var step = 0; step < 60; step++)
        {
            t.Step();
            s = t.B.GetCapacityStats();
            if (s.LastUpdateError != PhysicsUpdateErrors.None)
                break;
        }
        _out.WriteLine($"last={s.LastUpdateError} pairs={s.BodyPairCacheFullSteps} manifold={s.ManifoldCacheFullSteps} constraints={s.ContactConstraintsFullSteps}");

        Assert.NotEqual(PhysicsUpdateErrors.None, s.LastUpdateError);
        Assert.True(s.BodyPairCacheFullSteps + s.ManifoldCacheFullSteps + s.ContactConstraintsFullSteps > 0);
        Assert.Equal(4, s.MaxBodyPairs);
        Assert.Equal(4, s.MaxContactConstraints);
    }

    [Fact]
    public void Warning_names_the_flags_and_the_key_to_raise()
    {
        var prev = new PhysicsCapacityStats { MaxBodies = 16 };
        Assert.Null(CapacityReport.Warning("Ebony", prev, prev, 10));

        var cur = prev;
        cur.ContactConstraintsFullSteps = 37;
        Assert.Equal("[LEGION JOLT] Ebony: collisions dropped (ContactConstraintsFull x37 in 10s) - raise [Jolt] MaxContactConstraints",
            CapacityReport.Warning("Ebony", prev, cur, 10));

        cur.BodyCreateFailures = 4;
        var both = CapacityReport.Warning("Ebony", prev, cur, 10)!;
        Assert.Contains("bodies refused (MaxBodies 16 reached x4 in 10s", both);
        Assert.Contains("raise [Jolt] MaxBodies", both);
    }

    [Fact]
    public void Gate_warning_fires_above_20_percent_of_frame_time()
    {
        Assert.Null(CapacityReport.GateWarning("Ebony", 1000, 10000, 0, 1));   // 10%
        Assert.Null(CapacityReport.GateWarning("Ebony", 2000, 10000, 0, 1));   // exactly 20%
        var w = CapacityReport.GateWarning("Ebony", 2500, 10000, 1, 2)!;
        Assert.Contains("raise [Jolt] JobPools", w);
        Assert.Contains("pool 1 of 2", w);
    }

    [Fact]
    public void Stats_report_the_configured_world()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        var cd = CharacterDesc.Default;
        cd.Position = new Vector3(100f, 100f, 1.5f);
        t.B.CreateCharacter(cd);
        var s = t.B.GetCapacityStats();
        Assert.Equal(1, s.CharacterCount);
        Assert.Equal(16384 * 2, s.ContactRingCapacity);
        Assert.True(s.JobThreadCount >= 1);
        Assert.Equal(0, s.DroppedContacts);

        // JOLT-7: the default is one pool, holding every thread - exactly the old single pool.
        Assert.Equal(1, s.JobPools);
        Assert.Equal(0, s.PoolIndex);
        Assert.Equal(s.JobThreadCount, s.JobThreadsPerPool);
        t.Step();
        s = t.B.GetCapacityStats();
        Assert.Equal(1, s.PoolPeakInside);
        Assert.Equal(0, s.UpdateGateWaits);
        var text = CapacityReport.Render("Ebony", s, 1, 0, 1, 0, 1, 0);
        Assert.Contains($"JobPools=1 threadsPerPool={s.JobThreadsPerPool}", text);
        Assert.Contains("pool=0 waits=0 waitMs", text);
        Assert.Contains("pool peakInside=1", text);
    }
}
