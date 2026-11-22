/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// Drifting wind volumes (WV_DRIFT): the centre is worked out from the time whenever the wind is sampled, so it needs no
/// update step and no lock; a drift faster than the cap is refused; a drifting volume stops once it is wholly outside
/// the region; and one object can create and remove only so many volumes a minute.
/// </summary>
// Each test owns its store, its test clock and, where it needs one, its scene and module; the module tests run one
// at a time with the rest of the assembly (AssemblyInfo.cs). No process-wide state is touched.
public class WindVolumeMotionTests
{
    private class Clock
    {
        public long Ms = 1000;
        public long Now() => Interlocked.Read(ref Ms);
        public void Advance(double seconds) => Interlocked.Add(ref Ms, (long)Math.Round(seconds * 1000.0));
    }

    private static readonly UUID ObjA = UUID.Random();
    private static readonly UUID ObjB = UUID.Random();
    private static readonly UUID Owner = UUID.Random();

    private static WindVolumeStore Store(Clock clock, WindVolumeLimits? limits = null, float regionX = 256f, float regionY = 256f)
        => new WindVolumeStore(true, limits, clock.Now) { RegionSize = new Vector2(regionX, regionY) };

    private static WindVolumeSpec Sphere(Vector3 center, Vector3 drift, float radius = 10f, float lifetime = 600f, Vector3? wind = null)
        => new WindVolumeSpec
        {
            Shape = WindVolumeShape.Sphere, Center = center, Radius = radius, FalloffStart = 1f,
            LifetimeSeconds = lifetime, Velocity = wind ?? new Vector3(10, 0, 0), Drift = drift,
        };

    private static WindVolumeSpec Cylinder(Vector3 center, Vector3 drift, float radius = 10f, float height = 20f)
        => new WindVolumeSpec
        {
            Shape = WindVolumeShape.Cylinder, Center = center, Radius = radius, Height = height, FalloffStart = 1f,
            LifetimeSeconds = 600f, Velocity = new Vector3(10, 0, 0), Drift = drift,
        };

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static WindVolume Only(WindVolumeStore store) => Assert.Single(store.List(UUID.Zero));

    // ---- the centre by formula ----

    [Fact]
    public void AVolumeWithNoDriftStaysWhereItWasMade()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)) > 0);

        clock.Advance(120);
        Near(new Vector3(100, 100, 50), Only(store).CenterAt(clock.Now()));
        Near(new Vector3(10, 0, 0), store.Sample(new Vector3(100, 100, 50)));
    }

    [Fact]
    public void TheCentreMovesByTheDriftTimesItsAge()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(4, -2, 0.5f))) > 0);
        WindVolume v = Only(store);

        Near(new Vector3(100, 100, 50), v.CenterAt(v.CreatedMs));
        Near(new Vector3(140, 80, 55), v.CenterAt(v.CreatedMs + 10_000));
        Near(new Vector3(202, 49, 62.75f), v.CenterAt(v.CreatedMs + 25_500));
    }

    [Fact]
    public void TheWindIsWhereTheCentreIsNowAndGoneFromWhereItWas()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(5, 0, 0), radius: 10f)) > 0);

        clock.Advance(10);
        Near(new Vector3(10, 0, 0), store.Sample(new Vector3(150, 100, 50)));
        Near(Vector3.Zero, store.Sample(new Vector3(100, 100, 50)));
        // Half a radius behind the moving centre, still inside it at full strength (falloff 1).
        Near(new Vector3(10, 0, 0), store.Sample(new Vector3(145, 100, 50)));
    }

    [Fact]
    public void AnyTimeCanBeSampledWithoutTheTimesBetween()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, regionX: 4096f, regionY: 4096f);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(3, 0, 0))) > 0);
        long t0 = Only(store).CreatedMs;

        // A late time first, then an earlier one: the result depends only on the time asked for.
        Near(new Vector3(10, 0, 0), store.Sample(new Vector3(400, 100, 50), t0 + 100_000));
        Near(Vector3.Zero, store.Sample(new Vector3(100, 100, 50), t0 + 100_000));
        Near(new Vector3(10, 0, 0), store.Sample(new Vector3(250, 100, 50), t0 + 50_000));
        Near(new Vector3(10, 0, 0), store.Sample(new Vector3(100, 100, 50), t0));
    }

    [Fact]
    public void TheWeightOfAVolumeFollowsItsCentre()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(0, 2, 0))) > 0);
        WindVolume v = Only(store);

        Assert.Equal(1f, v.Weight(new Vector3(100, 120, 50), v.CreatedMs + 10_000));
        Assert.Equal(0f, v.Weight(new Vector3(100, 100, 50), v.CreatedMs + 10_000));
    }

    // ---- refusals ----

    [Fact]
    public void ADriftAtTheCapIsAcceptedAndOneAboveItIsRefusedAndNothingIsStored()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, new WindVolumeLimits { MaxMoveSpeed = 40f });

        Assert.Equal(WindVolumeError.DriftTooFast, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(40.01f, 0, 0))));
        Assert.Equal(WindVolumeError.DriftTooFast, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(30, 30, 0))));
        Assert.Equal(WindVolumeError.DriftTooFast, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(0, 0, -41))));
        Assert.Equal(0, store.Count);

        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(40, 0, 0))) > 0);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(24, 32, 0))) > 0);
        Assert.Equal(2, store.Count);
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void ADriftThatIsNotFiniteIsRefused(float x, float y, float z)
    {
        WindVolumeStore store = Store(new Clock());
        Assert.Equal(WindVolumeError.InvalidParameter, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(x, y, z))));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ADriftCapOfZeroRefusesEveryDriftButNotAVolumeThatStaysStill()
    {
        WindVolumeStore store = Store(new Clock(), new WindVolumeLimits { MaxMoveSpeed = 0f });
        Assert.Equal(WindVolumeError.DriftTooFast, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(0.1f, 0, 0))));
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)) > 0);
    }

    // ---- the region's edge ----

    public static IEnumerable<object[]> Edges() => new[]
    {
        // region size, start, drift, where it stops
        new object[] { 256f, new Vector3(200, 128, 60), new Vector3(10, 0, 0), new Vector3(266, 128, 60) },
        new object[] { 256f, new Vector3(50, 128, 60), new Vector3(-10, 0, 0), new Vector3(-10, 128, 60) },
        new object[] { 256f, new Vector3(128, 200, 60), new Vector3(0, 10, 0), new Vector3(128, 266, 60) },
        new object[] { 256f, new Vector3(128, 50, 60), new Vector3(0, -10, 0), new Vector3(128, -10, 60) },
        // A larger region: the edge is where the region ends, not at 256.
        new object[] { 512f, new Vector3(450, 128, 60), new Vector3(10, 0, 0), new Vector3(522, 128, 60) },
        new object[] { 512f, new Vector3(128, 450, 60), new Vector3(0, 10, 0), new Vector3(128, 522, 60) },
    };

    [Theory]
    [MemberData(nameof(Edges))]
    public void ADriftingVolumeStopsOneRadiusOutsideTheEdgeAndAddsNothingInsideTheRegion(float size, Vector3 start, Vector3 drift, Vector3 stop)
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, regionX: size, regionY: size);
        Assert.True(store.Create(ObjA, Owner, Sphere(start, drift, radius: 10f)) > 0);

        clock.Advance(60);
        WindVolume v = Only(store);
        Near(stop, v.CenterAt(clock.Now()));
        clock.Advance(240);
        Near(stop, v.CenterAt(clock.Now()));

        // Every point on the region's edge nearest the volume, and inside the region, gets nothing from it.
        Vector3 edge = new Vector3(Math.Clamp(stop.X, 0f, size), Math.Clamp(stop.Y, 0f, size), stop.Z);
        Near(Vector3.Zero, store.Sample(edge));
        // The volume is still there until its lifetime runs out: the script still owns it and can remove it.
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void ACylinderDriftingUpOrDownStopsHalfItsHeightOutsideTheRegionsHeight()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, new WindVolumeLimits { MaxVolumesObject = 8 });
        Assert.True(store.Create(ObjA, Owner, Cylinder(new Vector3(128, 128, 4000), new Vector3(0, 0, 20), height: 20f)) > 0);
        Assert.True(store.Create(ObjB, Owner, Cylinder(new Vector3(128, 128, 30), new Vector3(0, 0, -20), height: 20f)) > 0);

        clock.Advance(100);
        Near(new Vector3(128, 128, 4106), store.List(ObjA)[0].CenterAt(clock.Now()));
        Near(new Vector3(128, 128, -10), store.List(ObjB)[0].CenterAt(clock.Now()));
    }

    [Fact]
    public void ADiagonalDriftStopsEachAxisAtItsOwnEdge()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(250, 128, 60), new Vector3(10, 10, 0))) > 0);

        clock.Advance(3);
        Near(new Vector3(266, 158, 60), Only(store).CenterAt(clock.Now()));
    }

    [Fact]
    public void AVolumeMadeOutsideTheRegionDriftsInFromWhereItWasMade()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        // Partly inside: its centre 25 m outside the west edge, its radius 30 m.
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(-25, 128, 60), new Vector3(6, 0, 0), radius: 30f)) > 0);
        WindVolume v = Only(store);

        Near(new Vector3(-25, 128, 60), v.CenterAt(v.CreatedMs));
        Near(new Vector3(35, 128, 60), v.CenterAt(v.CreatedMs + 10_000));
        Near(new Vector3(10, 0, 0), store.Sample(new Vector3(35, 128, 60), v.CreatedMs + 10_000));
    }

    [Theory]
    [InlineData(266.01f, 128f, 60f)]
    [InlineData(-10.01f, 128f, 60f)]
    [InlineData(128f, 266.01f, 60f)]
    [InlineData(128f, -10.01f, 60f)]
    [InlineData(128f, 128f, -10.01f)]
    [InlineData(128f, 128f, 4106.01f)]
    public void AVolumeCannotBeMadeWhollyOutsideTheRegion(float x, float y, float z)
    {
        WindVolumeStore store = Store(new Clock());
        Assert.Equal(WindVolumeError.InvalidParameter, store.Create(ObjA, Owner, Sphere(new Vector3(x, y, z), Vector3.Zero)));
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData(266f, 128f, 60f)]
    [InlineData(-10f, 128f, 60f)]
    [InlineData(128f, 128f, -10f)]
    [InlineData(128f, 128f, 4106f)]
    public void AVolumeMayBeMadeJustTouchingTheRegion(float x, float y, float z)
    {
        WindVolumeStore store = Store(new Clock());
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(x, y, z), Vector3.Zero)) > 0);
    }

    [Fact]
    public void ANonSquareRegionStopsEachAxisAtItsOwnSize()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, new WindVolumeLimits { MaxVolumesObject = 8 }, regionX: 512f, regionY: 256f);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(400, 128, 60), new Vector3(10, 0, 0))) > 0);
        Assert.True(store.Create(ObjB, Owner, Sphere(new Vector3(128, 200, 60), new Vector3(0, 10, 0))) > 0);

        clock.Advance(60);
        Near(new Vector3(522, 128, 60), store.List(ObjA)[0].CenterAt(clock.Now()));
        Near(new Vector3(128, 266, 60), store.List(ObjB)[0].CenterAt(clock.Now()));
    }

    [Fact]
    public void ATimeBeforeTheVolumeWasMadeGivesTheCentreWhereItWasMade()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(5, 0, 0))) > 0);
        WindVolume v = Only(store);

        Near(new Vector3(100, 100, 50), v.CenterAt(v.CreatedMs - 5_000));
        Near(new Vector3(10, 0, 0), store.Sample(new Vector3(100, 100, 50), v.CreatedMs - 5_000));
    }

    [Fact]
    public void AStoppedVolumeStillEndsWithItsLifetimeAndFreesItsPlace()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, new WindVolumeLimits { MaxVolumesRegion = 1 });
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(200, 128, 60), new Vector3(20, 0, 0), lifetime: 30f)) > 0);

        clock.Advance(20);
        Assert.Equal(WindVolumeError.RegionFull, store.Create(ObjB, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)));
        clock.Advance(10);
        Assert.Equal(0, store.Count);
        Assert.True(store.Create(ObjB, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)) > 0);
    }

    [Fact]
    public void TheConsoleShowsADriftingVolumesCentreNowAndItsDrift()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(4, 0, 0))) > 0);
        Assert.True(store.Create(ObjB, Owner, Sphere(new Vector3(50, 50, 50), Vector3.Zero)) > 0);
        clock.Advance(10);

        var lines = new List<string>();
        WindVolumeConsole.List(store, "Test Region", clock.Now(), lines.Add);

        Assert.Contains(lines, l => l.Contains("centre <140, 100, 50>") && l.Contains("drift <4, 0, 0>"));
        Assert.Contains(lines, l => l.Contains("centre <50, 50, 50>") && !l.Contains("drift"));
    }

    // ---- sampling: no allocation, no lock ----

    [Fact]
    public void SamplingDriftingVolumesAllocatesNothing()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, new WindVolumeLimits { MaxVolumesObject = 8 });
        for (int i = 0; i < 4; i++)
            Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100 + i, 100, 50), new Vector3(3, -1, 0), radius: 20f)) > 0);

        float sink = 0f;
        for (int i = 0; i < 2000; i++)
            sink += store.Sample(new Vector3(100 + (i % 7), 100, 50), clock.Ms + i).X;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200_000; i++)
            sink += store.Sample(new Vector3(100 + (i % 13), 100, 50), clock.Ms + i).X;
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        Assert.False(float.IsNaN(sink));
    }

    [Fact]
    public void SamplingADriftingVolumeDoesNotWaitForAWriterHoldingTheLock()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(5, 0, 0))) > 0);
        clock.Advance(10);

        object writeLock = typeof(WindVolumeStore).GetField("m_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        Vector3 seen = default;
        lock (writeLock)
        {
            // A plain thread, joined with a time limit: the test holds a lock, so it cannot await.
            var reader = new Thread(() => seen = store.Sample(new Vector3(150, 100, 50)));
            reader.Start();
            Assert.True(reader.Join(TimeSpan.FromSeconds(10)), "a sample waited for the store's write lock");
        }
        Near(new Vector3(10, 0, 0), seen);
    }

    // ---- the create and remove throttle ----

    private static WindVolumeLimits Throttle(int perMinute)
        => new WindVolumeLimits { CreateThrottlePerMinute = perMinute, MaxVolumesObject = 4, MaxVolumesRegion = 16 };

    // Creates and removes one volume in turn: the churn a script would make without drift.
    private static int Churn(WindVolumeStore store, UUID obj, int calls)
    {
        int last = 0, handle = 0;
        for (int i = 0; i < calls; i++)
        {
            last = i % 2 == 0
                ? handle = store.Create(obj, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero))
                : store.Remove(handle, obj);
            if (last < 0) return last;
        }
        return last;
    }

    [Fact]
    public void TheEleventhCreateOrRemoveInAMinuteIsRefusedAndChangesNothing()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, Throttle(10));

        Assert.Equal(WindVolumeError.Ok, Churn(store, ObjA, 10));   // 5 creates and 5 removes
        Assert.Equal(0, store.Count);

        Assert.Equal(WindVolumeError.Throttled, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ARemoveIsThrottledTooAndTheVolumeStays()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, Throttle(10));
        Assert.Equal(WindVolumeError.Ok, Churn(store, ObjA, 8));
        int kept = store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero));   // the 9th
        Assert.True(kept > 0);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(120, 100, 50), Vector3.Zero)) > 0);   // the 10th

        Assert.Equal(WindVolumeError.Throttled, store.Remove(kept, ObjA));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void SixtySecondsAfterTheOldestCallTheObjectMayCallAgainEvenIfItKeptTrying()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, Throttle(10));
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(WindVolumeError.Ok, Churn(store, ObjA, 1) < 0 ? -100 : WindVolumeError.Ok);
            store.Clear();
            clock.Advance(1);
        }

        // The oldest call was 10 s ago; keep trying until just before it is 60 s old.
        clock.Advance(49.9);
        for (int i = 0; i < 20; i++)
            Assert.Equal(WindVolumeError.Throttled, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)));

        clock.Advance(0.1);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)) > 0);
    }

    [Fact]
    public void RefusedCallsCountButThrottledCallsDoNot()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, Throttle(3));
        Assert.Equal(WindVolumeError.NotFound, store.Remove(99, ObjA));
        Assert.Equal(WindVolumeError.TooLarge, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero, radius: 1000f)));
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)) > 0);

        // Throttled tries half way through the window: if they counted, the window would not clear at 60 s.
        clock.Advance(30);
        for (int i = 0; i < 5; i++)
            Assert.Equal(WindVolumeError.Throttled, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)));

        clock.Advance(30);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(120, 100, 50), Vector3.Zero)) > 0);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(140, 100, 50), Vector3.Zero)) > 0);
    }

    [Fact]
    public void AThrottledCallThatIsAlsoInvalidIsThrottledAndDoesNotCount()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, Throttle(2));
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)) > 0);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(120, 100, 50), Vector3.Zero)) > 0);

        Assert.Equal(WindVolumeError.Throttled, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(41, 0, 0))));
        Assert.Equal(WindVolumeError.Throttled, store.Remove(424242, ObjA));
        Assert.Equal(2, store.Count);

        clock.Advance(60);
        Assert.Equal(WindVolumeError.DriftTooFast, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), new Vector3(41, 0, 0))));
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(140, 100, 50), Vector3.Zero)) > 0);
    }

    [Fact]
    public void ARemoveOfAnotherObjectsVolumeCountsAgainstTheCallerAndNotTheOwner()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, Throttle(3));
        int theirs = store.Create(ObjB, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero));
        Assert.True(theirs > 0);

        for (int i = 0; i < 3; i++)
            Assert.Equal(WindVolumeError.NotAllowed, store.Remove(theirs, ObjA));
        Assert.Equal(WindVolumeError.Throttled, store.Remove(theirs, ObjA));

        // The owner's allowance is untouched: it has used one call of three.
        Assert.Equal(WindVolumeError.Ok, store.Remove(theirs, ObjB));
        Assert.True(store.Create(ObjB, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)) > 0);
        Assert.Equal(WindVolumeError.Throttled, store.Create(ObjB, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)));
    }

    [Fact]
    public void OneObjectsThrottleLeavesAnotherObjectAlone()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, Throttle(10));
        Assert.Equal(WindVolumeError.Ok, Churn(store, ObjA, 10));
        Assert.Equal(WindVolumeError.Throttled, store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)));

        Assert.True(store.Create(ObjB, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero)) > 0);
    }

    [Fact]
    public void ThrottleZeroSetsNoLimit()
    {
        WindVolumeStore store = Store(new Clock(), Throttle(0));
        Assert.Equal(WindVolumeError.Ok, Churn(store, ObjA, 200));
    }

    [Fact]
    public void TheConsoleAndAnObjectLeavingTheRegionAreNeverThrottled()
    {
        var clock = new Clock();
        WindVolumeStore store = Store(clock, Throttle(4));
        int first = store.Create(ObjA, Owner, Sphere(new Vector3(100, 100, 50), Vector3.Zero));
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(120, 100, 50), Vector3.Zero)) > 0);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(140, 100, 50), Vector3.Zero)) > 0);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(160, 100, 50), Vector3.Zero)) > 0);
        Assert.Equal(WindVolumeError.Throttled, store.Remove(first, ObjA));

        Assert.Equal(WindVolumeError.Ok, store.Remove(first, UUID.Zero));
        Assert.Equal(3, store.RemoveByCreator(ObjA));
        Assert.Equal(0, store.Count);
    }

    // ---- the rule and the config ----

    [Fact]
    public void TheDriftRuleIsReadAsAVectorAndDefaultsToZero()
    {
        object[] baseRules = { WindVolumeRules.WV_CENTER, new Vector3(100, 100, 50), WindVolumeRules.WV_RADIUS, 10f,
            WindVolumeRules.WV_VELOCITY, new Vector3(5, 0, 0), WindVolumeRules.WV_LIFETIME, 60f };

        Assert.Equal(WindVolumeError.Ok, WindVolumeRules.TryParse(baseRules, out WindVolumeSpec still));
        Assert.Equal(Vector3.Zero, still.Drift);

        object[] drifting = baseRules.Concat(new object[] { WindVolumeRules.WV_DRIFT, new Vector3(3, -4, 0) }).ToArray();
        Assert.Equal(WindVolumeError.Ok, WindVolumeRules.TryParse(drifting, out WindVolumeSpec moving));
        Assert.Equal(new Vector3(3, -4, 0), moving.Drift);
        // The wind is still WV_VELOCITY.
        Assert.Equal(new Vector3(5, 0, 0), moving.Velocity);

        object[] notAVector = baseRules.Concat(new object[] { WindVolumeRules.WV_DRIFT, 3f }).ToArray();
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(notAVector, out _));
    }

    [Fact]
    public void TheRuleNumberIsSeven()
    {
        Assert.Equal(7, WindVolumeRules.WV_DRIFT);
        Assert.Equal(-10, WindVolumeError.DriftTooFast);
        Assert.Equal(-11, WindVolumeError.Throttled);
    }

    private static IConfig Weather(params (string Key, string Value)[] values)
    {
        IConfig weather = new IniConfigSource().AddConfig("Weather");
        weather.Set("enabled", "true");
        foreach (var (key, value) in values)
            weather.Set(key, value);
        return weather;
    }

    [Fact]
    public void TheDriftCapAndTheThrottleComeFromTheWeatherSection()
    {
        IWindVolumeStore defaults = WindModule.CreateWindVolumes(Weather());
        Assert.Equal(40f, defaults.Limits.MaxMoveSpeed);
        Assert.Equal(10, defaults.Limits.CreateThrottlePerMinute);

        IWindVolumeStore set = WindModule.CreateWindVolumes(Weather(("max_move_speed", "12.5"), ("create_throttle", "3")));
        Assert.Equal(12.5f, set.Limits.MaxMoveSpeed);
        Assert.Equal(3, set.Limits.CreateThrottlePerMinute);
    }

    [Fact]
    public void ANegativeDriftCapIsHeldAtZero()
    {
        IWindVolumeStore store = WindModule.CreateWindVolumes(Weather(("max_move_speed", "-5")));
        Assert.Equal(0f, store.Limits.MaxMoveSpeed);
    }

    [Theory]
    [InlineData("fast", "many")]
    [InlineData("NaN", "2.5")]
    [InlineData("Infinity", "-1")]
    public void ADriftCapOrThrottleThatIsNotUsableTakesItsDefault(string speed, string throttle)
    {
        IWindVolumeStore store = null!;
        Exception? thrown = Record.Exception(() => store = WindModule.CreateWindVolumes(Weather(("max_move_speed", speed), ("create_throttle", throttle))));

        Assert.Null(thrown);
        Assert.Equal(40f, store.Limits.MaxMoveSpeed);
        Assert.Equal(10, store.Limits.CreateThrottlePerMinute);
    }

    // ---- through the wind module ----

    [Fact]
    public void TheWindModuleGivesTheStoreTheRegionsSize()
    {
        TestScene scene = new SceneHelpers().SetupScene("Wind drift region", UUID.Random(), 1000, 1000, 512, 256, new IniConfigSource());
        IniConfigSource config = new IniConfigSource();
        config.AddConfig("Wind").Set("wind_plugin", "ConfigurableWind");
        config.AddConfig("Weather").Set("enabled", "true");
        var module = new WindModule();
        module.Initialise(config);
        module.AddRegion(scene);

        Assert.Equal(new Vector2(512f, 256f), ((WindVolumeStore)module.WindVolumes).RegionSize);
    }

    [Fact]
    public void TheWindAtFixedPointsFollowsADriftingVolumeThroughTheWindModule()
    {
        TestScene scene = new SceneHelpers().SetupScene("Wind drift region", UUID.Random(), 1000, 1000, 256, 256, new IniConfigSource());
        scene.RegionInfo.RegionSettings.WaterHeight = 0f;
        IniConfigSource config = new IniConfigSource();
        config.AddConfig("Wind").Set("wind_plugin", "ConfigurableWind");
        config.AddConfig("Weather").Set("enabled", "true");
        var module = new WindModule();
        module.Initialise(config);
        module.AddRegion(scene);

        var clock = new Clock();
        var store = new WindVolumeStore(true, null, clock.Now) { RegionSize = new Vector2(256f, 256f) };
        typeof(WindModule).GetField("m_windVolumes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(module, store);
        Assert.True(store.Create(ObjA, Owner, Sphere(new Vector3(40, 128, 60), new Vector3(8, 0, 0), radius: 15f, wind: new Vector3(0, 0, 12))) > 0);

        // The model's wind has no vertical part, so the vertical part read at a point is the volume's alone.
        var points = new[] { new Vector3(40, 128, 60), new Vector3(120, 128, 60), new Vector3(200, 128, 60) };
        float[] At() => points.Select(p => module.WindSpeedAt(p).Z).ToArray();

        Assert.Equal(new[] { 12f, 0f, 0f }, At());
        clock.Advance(10);
        Assert.Equal(new[] { 0f, 12f, 0f }, At());
        clock.Advance(10);
        Assert.Equal(new[] { 0f, 0f, 12f }, At());
        clock.Advance(60);   // past the east edge: stopped at 271, nothing inside the region
        Assert.Equal(new[] { 0f, 0f, 0f }, At());
        Assert.Equal(0f, module.WindSpeedAt(new Vector3(255.9f, 128, 60)).Z);
    }
}
