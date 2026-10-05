/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The limits on what scripts can make a region's physics do: the cost of a script ray cast ([Jolt] RayCastBudgetMs,
/// RayCastMaxTestedHits, RayCastMaxHits) and pushes on avatars ([Jolt] AvatarPushMaxSpeed, AvatarPushRecovery).
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RayCastAndPushLimitTests
{
    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float Gravity = 9.80665f;

    private const RayFilterFlags AllTypes =
        RayFilterFlags.land | RayFilterFlags.agent | RayFilterFlags.physical | RayFilterFlags.nonphysical | RayFilterFlags.BackFaceCull;

    private static JoltScene NewScene(float physicsRate, params (string key, string value)[] keys)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        if (physicsRate > 0f)
            jolt.Set("PhysicsStepRate", physicsRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach ((string key, string value) in keys)
            jolt.Set(key, value);
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    private static PhysicsActor Box(JoltScene scene, uint localId, Vector3 position, Vector3 size, bool physical, PrimitiveBaseShape shape = null)
    {
        PhysicsActor pa = scene.AddPrimShape("test box", shape ?? PrimitiveBaseShape.CreateBox(), position, size, Quaternion.Identity, physical, localId);
        if (physical)
            pa.Density = 1000f;
        return pa;
    }

    // A row of small boxes along y 128 from x 20, the kind of pile a long ray crosses.
    private static void Row(JoltScene scene, int boxes, uint firstId = 5000)
    {
        for (int i = 0; i < boxes; i++)
            Box(scene, firstId + (uint)i, new Vector3(20f + i * 0.5f, 128f, Ground + 1f), new Vector3(0.25f, 0.5f, 0.5f), false);
    }

    private static List<ContactResult> Cast(JoltScene scene, Vector3 from, Vector3 dir, float length, int count)
        => (List<ContactResult>)scene.RaycastWorld(from, dir, length, count, AllTypes);

    // ------------------------------------------------------------------ keys

    [Fact]
    public void The_new_keys_default_to_their_safe_values_parse_and_fall_back()
    {
        var d = JoltConfig.FromConfig(new IniConfigSource(), null);
        Assert.Equal(5f, d.RayCastBudgetMs);
        Assert.Equal(1024, d.RayCastMaxTestedHits);
        Assert.Equal(256, d.RayCastMaxHits);
        Assert.Equal(10f, d.AvatarPushMaxSpeed);
        Assert.Equal(5f, d.AvatarPushRecovery);
        PhysicsBackendSettings ds = d.ToBackendSettings(256, 256);
        Assert.Equal(5f, ds.RayCastBudgetMs);
        Assert.Equal(1024, ds.RayCastMaxTestedHits);
        Assert.Equal(10f, ds.AvatarPushMaxSpeed);
        Assert.Equal(5f, ds.AvatarPushRecovery);
        Assert.Equal(PhysicsBackendSettings.Default.RayCastBudgetMs, ds.RayCastBudgetMs);
        Assert.Equal(PhysicsBackendSettings.Default.AvatarPushMaxSpeed, ds.AvatarPushMaxSpeed);

        var src = new IniConfigSource();
        IConfig j = src.AddConfig("Jolt");
        j.Set("RayCastBudgetMs", "2.5");
        j.Set("RayCastMaxTestedHits", "300");
        j.Set("RayCastMaxHits", "32");
        j.Set("AvatarPushMaxSpeed", "0");
        j.Set("AvatarPushRecovery", "2");
        var warnings = new List<string>();
        var c = JoltConfig.FromConfig(src, warnings);
        Assert.Empty(warnings);
        PhysicsBackendSettings cs = c.ToBackendSettings(256, 256);
        Assert.Equal(2.5f, cs.RayCastBudgetMs);
        Assert.Equal(300, cs.RayCastMaxTestedHits);
        Assert.Equal(32, c.RayCastMaxHits);
        Assert.Equal(0f, cs.AvatarPushMaxSpeed);
        Assert.Equal(2f, cs.AvatarPushRecovery);

        foreach ((string key, string value) in new[]
                 {
                     ("RayCastBudgetMs", "0"), ("RayCastBudgetMs", "NaN"), ("RayCastMaxTestedHits", "0"), ("RayCastMaxHits", "257"),
                     ("RayCastMaxHits", "0"), ("AvatarPushMaxSpeed", "-1"), ("AvatarPushRecovery", "Infinity"),
                 })
        {
            var bad = new IniConfigSource();
            bad.AddConfig("Jolt").Set(key, value);
            var w = new List<string>();
            var b = JoltConfig.FromConfig(bad, w);
            Assert.Single(w);
            Assert.Contains(key, w[0]);
            Assert.Equal(d.ToBackendSettings(256, 256), b.ToBackendSettings(256, 256));
            Assert.Equal(d.RayCastMaxHits, b.RayCastMaxHits);
        }
    }

    // ------------------------------------------------------------------ ray casts

    // Ordinary casts through a scene of terrain, boxes, spheres, physical bodies and an avatar give, hit for hit and in
    // order, what the unbounded RayCastAll gives (the path llCastRay took before).
    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Ordinary_casts_return_what_they_returned_before_in_order(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            var rnd = new Random(20261005);
            uint id = 100;
            for (int i = 0; i < 40; i++)
            {
                var p = new Vector3(40f + (float)rnd.NextDouble() * 176f, 40f + (float)rnd.NextDouble() * 176f, Ground + 0.5f + (float)rnd.NextDouble() * 6f);
                var size = new Vector3(0.3f + (float)rnd.NextDouble() * 3f, 0.3f + (float)rnd.NextDouble() * 3f, 0.3f + (float)rnd.NextDouble() * 3f);
                bool sphere = i % 4 == 0;
                Box(scene, id++, p, size, physical: i % 3 == 0, sphere ? PrimitiveBaseShape.CreateSphere() : null);
            }
            Row(scene, 60);
            scene.AddAvatar(99u, "Test User", new Vector3(128f, 128f, Ground + 2f), Run.AvatarSize, 0f, false);
            for (int k = 0; k < 22; k++)
                scene.Simulate(Heartbeat);

            IPhysicsBackend backend = scene.Backend;
            int compared = 0, withHits = 0, multi = 0;
            for (int i = 0; i < 400; i++)
            {
                var from = new Vector3(30f + (float)rnd.NextDouble() * 196f, 30f + (float)rnd.NextDouble() * 196f, Ground + 0.2f + (float)rnd.NextDouble() * 10f);
                var dir = new Vector3((float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 0.6f - 0.4f);
                if (i % 5 == 0)
                {
                    from = new Vector3(5f + (float)rnd.NextDouble() * 20f, 128f, Ground + 1f);   // along the row: many hits
                    dir = Vector3.UnitX;
                }
                dir.Normalize();
                float length = 1f + (float)rnd.NextDouble() * 120f;
                // Keep the whole ray inside the region, where the clip changes nothing.
                Vector3 end = from + dir * length;
                if (end.X < 0f || end.X > Size || end.Y < 0f || end.Y > Size)
                    continue;
                int count = new[] { 1, 4, 16 }[i % 3];

                scene.Simulate(Heartbeat);   // a fresh heartbeat: the budget never interferes here
                List<ContactResult> now = Cast(scene, from, dir, length, count);
                var before = new RayHit[count];
                int n = backend.RayCastAll(new SVector3(from.X, from.Y, from.Z), new SVector3(dir.X, dir.Y, dir.Z), length, QueryFilter.Default, before);

                Assert.Equal(n, now.Count);
                for (int h = 0; h < n; h++)
                {
                    Assert.Equal(before[h].UserData, now[h].ConsumerID);
                    Assert.True(Vector3.Distance(new Vector3(before[h].Point.X, before[h].Point.Y, before[h].Point.Z), now[h].Pos) < 1e-4f, $"ray {i} hit {h}: point");
                    Assert.True(Vector3.Distance(new Vector3(before[h].Normal.X, before[h].Normal.Y, before[h].Normal.Z), now[h].Normal) < 1e-4f, $"ray {i} hit {h}: normal");
                    Assert.True(MathF.Abs(before[h].Distance - now[h].Depth) < 1e-4f, $"ray {i} hit {h}: distance");
                    if (h > 0)
                        Assert.True(now[h].Depth >= now[h - 1].Depth, $"ray {i}: hits out of order");
                }
                compared++;
                if (n > 0) withHits++;
                if (n > 1) multi++;
            }
            Assert.True(compared > 150, $"only {compared} rays compared");
            Assert.True(withHits > 60, $"only {withHits} rays hit anything");
            Assert.True(multi > 20, $"only {multi} rays had more than one hit");
            Assert.Equal(0, scene.CapacityStats().RayCastsRefused);
            Assert.Equal(0, scene.CapacityStats().RayCastsCutShort);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_cast_along_a_row_returns_its_closest_boxes_and_a_ray_is_cut_to_the_region()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);

            List<ContactResult> hits = Cast(scene, from, Vector3.UnitX, 300f, 16);
            Assert.Equal(16, hits.Count);
            for (int i = 0; i < 16; i++)
                Assert.Equal(5000u + (uint)i, hits[i].ConsumerID);

            // A ray far longer than the region: the same hits, distances from the caller's start.
            List<ContactResult> longRay = Cast(scene, from, Vector3.UnitX, 1e30f, 16);
            Assert.Equal(hits.Select(h => h.ConsumerID), longRay.Select(h => h.ConsumerID));
            for (int i = 0; i < 16; i++)
                Assert.True(MathF.Abs(hits[i].Depth - longRay[i].Depth) < 1e-3f);

            // Starting far outside the region and coming in: clipped to where it enters, depth still from its start.
            List<ContactResult> fromAfar = Cast(scene, new Vector3(-5000f, 128f, Ground + 1f), Vector3.UnitX, 6000f, 1);
            Assert.Single(fromAfar);
            Assert.Equal(5000u, fromAfar[0].ConsumerID);
            Assert.True(MathF.Abs(fromAfar[0].Depth - (5000f + hits[0].Depth + 10f)) < 1e-2f, $"depth {fromAfar[0].Depth}");

            long castsBefore = scene.CapacityStats().RayCasts;
            Assert.Empty(Cast(scene, new Vector3(-100f, 128f, Ground + 1f), -Vector3.UnitX, 1000f, 4));   // never in the region
            Assert.Empty(Cast(scene, from, Vector3.UnitX, float.NaN, 4));
            Assert.Empty(Cast(scene, from, Vector3.UnitX, float.PositiveInfinity, 4));
            Assert.Empty(Cast(scene, from, Vector3.UnitX, -5f, 4));
            Assert.Empty(Cast(scene, new Vector3(float.NaN, 128f, 30f), Vector3.UnitX, 10f, 4));
            Assert.Empty(Cast(scene, from, new Vector3(float.NaN, 0f, 0f), 10f, 4));
            Assert.Equal(castsBefore, scene.CapacityStats().RayCasts);   // none reached the engine

            // More hits than RayCastMaxHits are never returned.
            Assert.Equal(256, Cast(scene, from, Vector3.UnitX, 300f, 100000).Count);
        }
        finally { scene.Dispose(); }
    }

    // Second Life: "RCERR_CAST_TIME_EXCEEDED -3: The raycast failed because the parcel or agent has exceeded the maximum
    // time allowed for raycasting. This resource pool is continually replenished, so waiting a few frames and retrying
    // is likely to succeed." The 4-argument caller (Phlox llCastRay) reports an exception from RaycastWorld as that
    // code; the 5-argument caller gets no hits.
    [Fact]
    public void A_refused_or_cut_short_cast_is_reported_as_cast_time_exceeded_and_the_next_heartbeat_casts_again()
    {
        JoltScene scene = NewScene(0f, ("RayCastBudgetMs", "1"));
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);

            // Spend this heartbeat's 1 ms; the casts after that are refused.
            var sw = Stopwatch.StartNew();
            RayCastTimeExceededException refused = null;
            while (refused == null && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                // The cast that runs out of the time is cut short; the ones after it are refused.
                try { scene.RaycastWorld(from, Vector3.UnitX, 300f, 16); }
                catch (RayCastTimeExceededException e) { if (e.Status == RayCastStatus.Refused) refused = e; }
            }
            Assert.NotNull(refused);
            Assert.Empty(Cast(scene, from, Vector3.UnitX, 300f, 16));   // the 5-argument caller: no hits
            Assert.True(scene.CapacityStats().RayCastsRefused >= 2);

            scene.Simulate(Heartbeat);   // a new heartbeat: the time is there again
            Assert.Equal(4, scene.RaycastWorld(from, Vector3.UnitX, 300f, 4).Count);
        }
        finally { scene.Dispose(); }

        // Cut short: a cast that tests more hits than one cast may.
        scene = NewScene(0f, ("RayCastMaxTestedHits", "10"));
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);
            var e = Assert.Throws<RayCastTimeExceededException>(() => scene.RaycastWorld(from, Vector3.UnitX, 300f, 16));
            Assert.Equal(RayCastStatus.CutShort, e.Status);
            Assert.Empty(Cast(scene, from, Vector3.UnitX, 300f, 16));
            Assert.Equal(2, scene.CapacityStats().RayCastsCutShort);
            Assert.Equal(3, scene.RaycastWorld(from, Vector3.UnitX, 11f, 16).Count);   // a ray crossing three boxes: fine
        }
        finally { scene.Dispose(); }
    }

    // Script threads casting as fast as they can, long rays through a row of 2000 boxes asking for 256 hits each: a
    // cast holds the region's physics lock, and the heartbeat's step waits for it. The region's ray cast time per
    // heartbeat is bounded by RayCastBudgetMs (5 ms) plus the overrun of the one cast in flight when it ran out, so the
    // heartbeat is delayed by at most that.
    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_flood_of_casts_keeps_the_heartbeat_under_its_bound(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            Row(scene, 2000);
            for (int i = 0; i < 100; i++)
                Box(scene, 9000u + (uint)i, new Vector3(60f + (i % 10) * 3f, 60f + (i / 10) * 3f, Ground + 3f), new Vector3(1f, 1f, 1f), true);
            for (int k = 0; k < 11; k++)
                scene.Simulate(Heartbeat);

            double baseline = MaxHeartbeatMs(scene, 33);

            using var stop = new CancellationTokenSource();
            long casts = 0;
            var threads = new Thread[4];
            for (int t = 0; t < threads.Length; t++)
            {
                float y = 128f;
                threads[t] = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        Cast(scene, new Vector3(10f, y, Ground + 1f), Vector3.UnitX, 1000f, 256);
                        Interlocked.Increment(ref casts);
                    }
                }) { IsBackground = true };
                threads[t].Start();
            }
            double flooded;
            try
            {
                while (Interlocked.Read(ref casts) < 20)
                    Thread.Yield();
                flooded = MaxHeartbeatMs(scene, 33);
            }
            finally
            {
                stop.Cancel();
                foreach (Thread t in threads)
                    t.Join();
            }

            PhysicsCapacityStats s = scene.CapacityStats();
            Assert.True(s.RayCastsRefused > 0, "the flood never ran out of time: it was not a flood");
            Assert.True(s.RayCastMsMaxHeartbeat <= 5.0 + 3.0, $"one heartbeat spent {s.RayCastMsMaxHeartbeat:0.00} ms on ray casts");
            Assert.True(flooded <= baseline + 5.0 + 25.0, $"heartbeat {flooded:0.0} ms with the flood, {baseline:0.0} ms without");
        }
        finally { scene.Dispose(); }
    }

    // Heartbeats paced as a region runs them (one every 1/11 s; the budget is per heartbeat), the longest Simulate.
    private static double MaxHeartbeatMs(JoltScene scene, int heartbeats)
    {
        double max = 0;
        long period = (long)(Heartbeat * Stopwatch.Frequency), next = Stopwatch.GetTimestamp();
        for (int k = 0; k < heartbeats; k++)
        {
            long t0 = Stopwatch.GetTimestamp();
            scene.Simulate(Heartbeat);
            max = Math.Max(max, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            next += period;
            int wait = (int)((next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (wait > 0)
                Thread.Sleep(wait);
        }
        return max;
    }

    // The harness's raycast-cost scenario: what one cast through the row costs, read from the region's counters.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_harness_measures_what_a_cast_costs(double physicsRate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("raycast-cost"), new HarnessOptions { PhysicsRateHz = physicsRate });
        Summary m = r.Summary;
        Assert.Equal((long)Harness.Harness.RayCastsPerHeartbeat * m.Steps, m.RayCasts);
        Assert.Equal(0, m.RayCastsRefused);
        Assert.Equal(0, m.RayCastsCutShort);
        Assert.True(m.RayCastMicrosPerCast > 0 && m.RayCastMicrosPerCast < 2000, $"{m.RayCastMicrosPerCast} us per cast");
    }

    // ------------------------------------------------------------------ pushes

    // llPushObject on an avatar: its velocity changes by impulse / mass (the push's distance attenuation is applied
    // by the script engine before it reaches the actor). Pushed straight up with 6 m/s it flies as a jump of 6 m/s
    // does: up v^2 / 2g.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_single_push_moves_an_avatar_as_documented(double physicsRate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("push-avatar"), new HarnessOptions { PhysicsRateHz = physicsRate });
        float v = Harness.Harness.PushAvatarSpeed;
        float peak = v * v / (2f * Gravity);   // 1.835 m
        float slack = (float)(Gravity * Heartbeat * Heartbeat / 8) + 0.002f;
        Assert.Equal(0, r.Summary.NonFinite);
        Assert.InRange(r.Summary.PeakRise, peak - slack, peak + 0.002f);
        Assert.True(r.Samples.Max(s => s.Velocity.Z) <= v + 1e-3f);
    }

    // A push of 1e9 every heartbeat for six seconds. Each push spends the avatar's push allowance (AvatarPushMaxSpeed,
    // 10 m/s, refilled at AvatarPushRecovery, 5 m/s per second), and pushes never take its speed past 10 m/s. Since the
    // allowance refills slower than gravity takes speed away, the most it can rise is 10^2 / (2 (g - 5)) = 10.4 m.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_flood_of_pushes_cannot_take_an_avatar_past_the_cap(double physicsRate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("push-flood"), new HarnessOptions { PhysicsRateHz = physicsRate });
        const float cap = PhysicsBackendSettings.DefaultAvatarPushMaxSpeed;
        const float recovery = PhysicsBackendSettings.DefaultAvatarPushRecovery;
        Assert.Equal(0, r.Summary.NonFinite);
        // What pushes add: the horizontal speed, the upward speed, and the speed while going up. Falling back, gravity
        // adds its own.
        List<Sample> pushed = r.Samples.Where(s => s.T <= 7.0).ToList();
        float topHorizontal = pushed.Max(s => s.HorizontalSpeed);
        float topUp = pushed.Max(s => s.Velocity.Z);
        float topRising = pushed.Where(s => s.Velocity.Z >= 0f).Max(s => s.Speed);
        Assert.True(topHorizontal <= cap + 1e-3f, $"{topHorizontal:0.000} m/s horizontal while pushed");
        Assert.True(topUp <= cap + 1e-3f, $"{topUp:0.000} m/s up while pushed");
        Assert.True(topRising <= cap + 1e-3f, $"{topRising:0.000} m/s while rising");
        Assert.True(topHorizontal >= cap * 0.9f, $"{topHorizontal:0.000} m/s: the pushes did not move it");
        float bound = cap * cap / (2f * (Gravity - recovery));
        Assert.True(r.Summary.PeakRise <= bound + 0.05f, $"rose {r.Summary.PeakRise:0.000} m, the bound is {bound:0.000}");
        Assert.True(double.IsNaN(r.Summary.LeftRegionT), "pushed out of the region");
    }

    // A push on a parked car (asleep under the rest rule) and on a box that has settled and slept: it wakes the body
    // and moves it, about the 2 m/s the impulse gives its mass, and nothing becomes non-finite.
    [Theory]
    [InlineData("push-car", 0.0)]
    [InlineData("push-car", 45.0)]
    [InlineData("push-box", 0.0)]
    [InlineData("push-box", 45.0)]
    public void A_push_wakes_a_sleeping_body_or_parked_vehicle_and_moves_it(string scenario, double physicsRate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find(scenario), new HarnessOptions { PhysicsRateHz = physicsRate });
        Assert.Equal(0, r.Summary.NonFinite);
        Sample asleep = r.Samples.Last(s => s.T <= 5.0);
        Assert.Equal(0, asleep.Active);
        List<Sample> after = r.Samples.Where(s => s.T > 5.0 && s.T <= 6.0).ToList();
        Assert.Contains(after, s => s.Active > 0);
        float top = after.Max(s => s.HorizontalSpeed);
        Assert.InRange(top, 0.5f, 2.05f);
        Assert.True(r.Samples[^1].Position.X - asleep.Position.X > 0.05f, $"moved {r.Samples[^1].Position.X - asleep.Position.X:0.000} m");
    }

    [Fact]
    public void Pushes_of_any_size_leave_an_avatar_finite_and_AvatarPushMaxSpeed_0_turns_them_off()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            PhysicsActor av = scene.AddAvatar(99u, "Test User", new Vector3(128f, 128f, Ground + 2f), Run.AvatarSize, 0f, false);
            for (int k = 0; k < 11; k++)
                scene.Simulate(Heartbeat);
            foreach (Vector3 push in new[]
                     {
                         new Vector3(float.MaxValue, 0f, 0f), new Vector3(0f, 0f, float.PositiveInfinity), new Vector3(float.NaN, 1f, 1f),
                         new Vector3(-float.MaxValue, float.MaxValue, float.MaxValue), new Vector3(1e-30f, 0f, 0f),
                     })
            {
                av.AddForce(push, true);
                scene.Simulate(Heartbeat);
                Assert.True(av.Position.IsFinite() && av.Velocity.IsFinite(), $"after {push}: {av.Position} {av.Velocity}");
                Assert.True(av.Velocity.Length() <= PhysicsBackendSettings.DefaultAvatarPushMaxSpeed + Gravity * Heartbeat, $"after {push}: {av.Velocity}");
            }
        }
        finally { scene.Dispose(); }

        scene = NewScene(0f, ("AvatarPushMaxSpeed", "0"));
        try
        {
            PhysicsActor av = scene.AddAvatar(99u, "Test User", new Vector3(128f, 128f, Ground + 2f), Run.AvatarSize, 0f, false);
            for (int k = 0; k < 11; k++)
                scene.Simulate(Heartbeat);
            Vector3 at = av.Position;
            for (int k = 0; k < 11; k++)
            {
                av.AddForce(new Vector3(800f, 0f, 800f), true);
                scene.Simulate(Heartbeat);
            }
            Assert.True(Vector3.Distance(at, av.Position) < 0.01f, $"moved from {at} to {av.Position}");
        }
        finally { scene.Dispose(); }
    }
}
