using System.Collections.Concurrent;
using System.Numerics;
using Legion.Physics;
using Legion.Physics.Jolt;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.LegionJolt.Tests;

/// <summary>
/// PHYS-2. The harness PHYS-1 could not have had.
///
/// <para><b>Why it did not exist before.</b> The failure is <c>std::abort()</c> inside joltc when its LIFO
/// TempAllocator is freed out of order (<c>Jolt/Core/TempAllocator.h:83-84</c>). An abort takes the process
/// down: no managed exception, no stack, no failing assertion — PHYS-1 had to be diagnosed from a Windows
/// event id and two console lines, and the fix could only be argued from source. This harness replaces the
/// abort with a catchable exception by asking, at every managed call site that reaches the allocator, whether
/// the calling thread holds that backend's <c>_simLock</c>
/// (<c>JoltPhysicsBackend.AllocatorOwnerCheck</c> / <c>RequireSimLock</c>).</para>
///
/// <para><b>What it reproduces.</b> The class, not the instance: an inter-region crossing. Two backends step
/// on their own threads at ~11 Hz, as three regions in one simulator process do, while a driver repeatedly
/// does what a teleport arrival does — create a character in the arriving region and set its size immediately
/// after (<c>JoltCharacter.cs:274</c>), rez a handful of attachment bodies there, and remove the character
/// from the departing region. Every call is made from a thread that is neither region's step thread, which is
/// exactly where the scene/teleport thread sits.</para>
/// </summary>
public class TeleportCrossingHarnessTests
{
    private readonly ITestOutputHelper _out;
    public TeleportCrossingHarnessTests(ITestOutputHelper output) { _out = output; }

    private const int StepHz = 11;
    private const int AttachmentsPerArrival = 14;   // the 2026-09-05 crossing re-added ~14

    private static PhysicsBackendSettings Settings() => new()
    {
        Gravity = new Vector3(0f, 0f, -9.8f),
        MaxBodies = 4096,
        MaxBodyPairs = 4096,
        MaxContactConstraints = 2048,
        ThreadCount = 2,
        PositionIterations = 2,
        VelocityIterations = 10,
        CollisionSteps = 1,
    };

    private static CharacterDesc Avatar(Vector3 at) => new()
    {
        Position = at,
        Orientation = Quaternion.Identity,
        CapsuleHalfHeight = 0.45f,
        CapsuleRadius = 0.30f,
        Mass = 80f,
        Friction = 0.5f,
        MaxSlopeAngle = 1.0f,
        StepHeight = 0.45f,
        PushStrength = 1f,
    };

    /// <summary>One region: a backend and the heartbeat thread that steps it, as the simulator runs them.</summary>
    private sealed class Region : IDisposable
    {
        public readonly JoltPhysicsBackend Backend = new();
        public readonly ConcurrentQueue<Exception> Faults = new();
        private readonly Thread _thread;
        private volatile bool _run = true;
        public long Steps;

        public Region(string name)
        {
            Backend.Initialize(Settings());
            _thread = new Thread(Loop) { IsBackground = true, Name = $"step:{name}" };
            _thread.Start();
        }

        private void Loop()
        {
            var bodies = new BodyState[256];
            var chars = new CharacterState[16];
            var contacts = new ContactReport[256];
            // NOT a real 11 Hz heartbeat: this spins. A sleeping loop steps ~11 times a second and the
            // crossings, which take microseconds, simply never land inside an Update - the first version of
            // this harness ran 1000 crossings against ONE step per region and "passed" having overlapped
            // nothing. Spinning keeps each backend inside _system.Update as much of the time as possible,
            // which is the only way a crossing call can contend with one. The step size stays 1/11 s so the
            // simulation itself behaves like the live region.
            while (_run)
            {
                try
                {
                    Backend.Step(1f / StepHz, bodies, chars, contacts);
                    Interlocked.Increment(ref Steps);
                }
                catch (Exception ex) { Faults.Enqueue(ex); }
            }
        }

        public void Dispose()
        {
            _run = false;
            _thread.Join(TimeSpan.FromSeconds(5));
            Backend.Dispose();
        }
    }

    /// <summary>
    /// The crossing, from a thread that is neither step thread. Returns whatever it threw, or null.
    /// </summary>
    private static Exception Cross(Region arriving, Region departing, CharacterId leaving, out CharacterId arrived)
    {
        arrived = default;
        try
        {
            // 1. the arriving region creates the character...
            arrived = arriving.Backend.CreateCharacter(Avatar(new Vector3(128f, 128f, 25f)));

            // 2. ...and the scene thread sets its size immediately after, as JoltCharacter.Size does when the
            //    avatar's appearance is applied on arrival. This is the PHYS-1 call.
            arriving.Backend.SetCharacterShape(arrived, 0.48f, 0.31f);

            // 3. attachment rez: a handful of bodies into the arriving region's broadphase, then removed
            //    again so a thousand crossings do not exhaust MaxBodies. Both halves mutate the broadphase
            //    that Update is walking, which is the other half of the live sequence.
            var shape = arriving.Backend.CreateBoxShape(new Vector3(0.1f, 0.1f, 0.1f));
            var rezzed = new List<BodyId>(AttachmentsPerArrival);
            for (var i = 0; i < AttachmentsPerArrival; i++)
            {
                rezzed.Add(arriving.Backend.CreateBody(new BodyDesc
                {
                    Shape = shape,
                    Position = new Vector3(128f + i * 0.2f, 128f, 26f),
                    Orientation = Quaternion.Identity,
                    Layer = PhysicsLayer.Dynamic,
                    MotionType = BodyMotionType.Dynamic,
                    Density = 1000f,
                    GravityFactor = 1f,
                    StartActive = true,
                }));
            }
            foreach (var b in rezzed)
                arriving.Backend.RemoveBody(b);

            // 4. and the departing region drops its copy - "Making Truly Bazar a child agent".
            if (leaving.Value != 0)
                departing.Backend.RemoveCharacter(leaving);

            return null;
        }
        catch (Exception ex) { return ex; }
    }

    /// <summary>
    /// The harness proper. Runs the crossing repeatedly with both regions stepping, and fails naming the API
    /// and the region if any allocator-touching call is made without that backend's <c>_simLock</c>.
    /// </summary>
    [Theory]
    [InlineData(1000)]
    public void A_thousand_crossings_never_touch_a_TempAllocator_unlocked(int crossings)
    {
        Assert.True(JoltPhysicsBackend.AllocatorOwnerCheck,
            "the harness is meaningless without the owner check; tests build DEBUG, where it is on by default");

        using var ebony = new Region("Ebony");
        using var transylvania = new Region("Transylvania");

        var faults = new List<string>();
        CharacterId inEbony = default, inTransylvania = default;

        for (var i = 0; i < crossings && faults.Count == 0; i++)
        {
            // alternate direction, as a resident hopping back and forth does
            var toTrans = (i & 1) == 0;
            var arriving = toTrans ? transylvania : ebony;
            var departing = toTrans ? ebony : transylvania;
            var leaving = toTrans ? inEbony : inTransylvania;

            var ex = Cross(arriving, departing, leaving, out var arrived);
            if (ex is not null) faults.Add($"crossing {i}: {ex.GetType().Name}: {ex.Message}");

            if (toTrans) { inTransylvania = arrived; inEbony = default; }
            else { inEbony = arrived; inTransylvania = default; }
        }

        foreach (var r in new[] { ebony, transylvania })
            while (r.Faults.TryDequeue(out var ex))
                faults.Add($"step thread: {ex.GetType().Name}: {ex.Message}");

        _out.WriteLine($"crossings={crossings} steps: Ebony={ebony.Steps} Transylvania={transylvania.Steps}");
        Assert.True(faults.Count == 0, string.Join("\n", faults.Take(5)));

        // The result is worthless unless both regions really were stepping throughout. The first version of
        // this harness slept between steps and managed ONE step per region across a thousand crossings; it
        // passed, and it had overlapped nothing. Demand at least one step per crossing on each side.
        Assert.True(ebony.Steps >= crossings, $"Ebony stepped {ebony.Steps} times for {crossings} crossings - not contended");
        Assert.True(transylvania.Steps >= crossings, $"Transylvania stepped {transylvania.Steps} times for {crossings} crossings - not contended");
    }

    /// <summary>
    /// The harness's own control: with the check on, a deliberate unlocked call into an allocator-touching API
    /// must be caught. Without this, a harness that reproduces nothing proves nothing.
    /// </summary>
    [Fact]
    public void The_owner_check_catches_an_unlocked_allocator_call()
    {
        using var region = new Region("Control");
        var id = region.Backend.CreateCharacter(Avatar(new Vector3(128f, 128f, 25f)));

        var ex = Record.Exception(() => region.Backend.SetCharacterShapeUnlockedForTest(id, 0.5f, 0.3f));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("PHYS-2", ex.Message);
        Assert.Contains("SetShape", ex.Message);
    }
}
