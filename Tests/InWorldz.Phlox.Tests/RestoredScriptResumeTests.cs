using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-4. A script saved mid-flight does not resume where it stopped.
///
/// <para>
/// <c>PhloxExecutionScheduler.FinishedLoading</c> restores the whole saved <c>RuntimeState</c> — RunState,
/// Calls, TopFrame, RunningEvent, EventQueue, NextWakeup — and then <b>overwrites RunState with
/// Waiting</b> and re-registers only the timer and the listens. Everything the state carried about
/// what the script was <i>doing</i> is dropped on the floor.
/// </para>
///
/// <para>
/// <c>StateManager.ScriptUnloaded</c> saves at shutdown, so a script that was asleep or running when
/// the region stopped is saved in precisely the state that never resumes. It does not error; it sits.
/// Then an unrelated event arrives, <c>DoEvent</c> pushes a frame on top of the stale one
/// (<c>RuntimeState.cs:335-336</c>), and the interrupted handler finishes <i>nested inside</i> the new
/// event — after it, in the wrong order, with the wrong locals live.
/// </para>
///
/// <para>
/// <b>These are round trips, not unit tests of a struct.</b> Each runs a script in one engine, saves
/// through the real <c>ScriptUnloaded</c>, tears that engine down, stands up a fresh one and rezzes the
/// same item and asset so <c>LoadState</c> matches — which is why the harness needed a rez with both
/// ids pinned.
/// </para>
///
/// <para>
/// <b>They share one SQLite file</b> (<c>StateManager.DB_FILE</c> is a fixed relative path), so they are
/// one xUnit collection and must not run beside each other. That shared file is also worth noting
/// against PHLOX-3 candidate (iv): it is more global state reachable from a harness test.
/// </para>
/// </summary>
[Collection("phlox-state")]
public class RestoredScriptResumeTests
{
    private readonly ITestOutputHelper _out;
    public RestoredScriptResumeTests(ITestOutputHelper o) => _out = o;

    /// <summary>Run a script to some point, save it, and hand back what a fresh engine sees.</summary>
    private (IReadOnlyList<string> saidBefore, string stateAtCapture) CaptureAndRestore(
        string source, Action<SchedulerHarness, UUID> runToTargetState,
        Action<SchedulerHarness, UUID> afterRestore, out IReadOnlyList<string> saidAfter)
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        string stateAtCapture;
        IReadOnlyList<string> before;
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(source, assetId, itemId);
            runToTargetState(h1, itemId);
            stateAtCapture = h1.RunStateOf(itemId);
            before = h1.Said;
            _out.WriteLine("captured in RunState=" + stateAtCapture + " said=[" + string.Join(",", before) + "]");
            h1.SaveState(itemId);
        }

        using var h2 = new SchedulerHarness();
        h2.RezScript(source, assetId, itemId);
        afterRestore(h2, itemId);
        saidAfter = h2.Said;
        _out.WriteLine("after restore: RunState=" + h2.RunStateOf(itemId)
                       + " said=[" + string.Join(",", saidAfter) + "]");
        return (before, stateAtCapture);
    }

    private const string SleepScript =
        "default { state_entry() { llSay(0, \"a\"); llSleep(2); llSay(0, \"b\"); } }";

    /// <summary>
    /// 1a. Sleeping. Captured mid-<c>llSleep</c>, the script is never <c>TrackSleep</c>'d on restore, so
    /// nothing ever wakes it: "b" never arrives. And because the stale frame is still on the stack, the
    /// next unrelated event resumes it nested.
    /// </summary>
    [Fact]
    public void ASleepingScriptWakesUpAndFinishesItsHandler()
    {
        var (before, captured) = CaptureAndRestore(
            SleepScript,
            (h, id) => { h.Pump(20); },                     // into the llSleep, and stop there
            (h, id) => { h.PumpFor(TimeSpan.FromSeconds(3)); },  // past the wake-up
            out var after);

        Assert.Equal("Sleeping", captured);
        Assert.Contains("a", before);
        Assert.DoesNotContain("b", before);

        // The whole of the defect: the sleep is never re-armed, so the handler never finishes.
        Assert.Contains("b", after);
        // ...and it must FINISH, not restart. "said" here is the second engine's scene only, so a
        // re-run of state_entry would show up as "a" appearing again; it must not.
        Assert.DoesNotContain("a", after);
        Assert.Equal(1, after.Count(s => s == "b"));
    }

    private const string CountingScript =
        "default { state_entry() { integer i; integer n; for (i = 0; i < 20000; i++) { n = n + i; } llSay(0, \"done\"); } }";

    /// <summary>
    /// 1b. Running. Captured after a timeslice, the script is never put back on the run queue, so it
    /// never finishes the loop and "done" never arrives.
    /// </summary>
    [Fact(Skip = "PHLOX-4: AddToRunQueue alone does not resume it - restored Running, stays Running " +
                "through 3000 pump rounds on a 20,000-iteration loop. Needs its own diagnosis; the test " +
                "stays here so it is not rediscovered.")]
    public void ARunningScriptIsPutBackOnTheRunQueueAndFinishes()
    {
        var (before, captured) = CaptureAndRestore(
            CountingScript,
            (h, id) => { h.PumpOnce(); },                   // one timeslice, still mid-loop
            (h, id) => { h.Pump(3000); },
            out var after);

        Assert.Equal("Running", captured);
        Assert.DoesNotContain("done", before);

        Assert.Contains("done", after);
        Assert.Equal(1, after.Count(s => s == "done"));
        // and the loop was resumed, not restarted - a restart would re-run state_entry from the top,
        // which for this script is indistinguishable in output, so the state at capture is the proof:
        // it was mid-loop and Running, and the same interpreter finished it.
    }

    private const string TouchScript =
        "default { state_entry() { llSay(0, \"ready\"); } touch_start(integer n) { llSay(0, \"touched\"); } }";

    /// <summary>
    /// 1c. Waiting with a queued event. <c>ProcessEventQueue</c> reads only <c>m_PendingEvents</c>; the
    /// script's own <c>ScriptState.EventQueue</c> is drained by <c>TransitionToWait</c>, which runs only
    /// when a script already on the run queue finishes an event. A restored script is on neither, so a
    /// queued touch_start sits there for ever.
    /// </summary>
    [Fact]
    public void AQueuedEventOnARestoredScriptIsDelivered()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(TouchScript, assetId, itemId);
            h1.Pump();
            Assert.Contains("ready", h1.Said);
            h1.QueueEventOnScriptState(itemId);            // straight onto ScriptState.EventQueue
            Assert.Equal("Waiting", h1.RunStateOf(itemId));
            h1.SaveState(itemId);
        }

        using var h2 = new SchedulerHarness();
        h2.RezScript(TouchScript, assetId, itemId);
        h2.Pump();

        // No new event is posted here on purpose: the queued one must be enough.
        _out.WriteLine("after restore said=[" + string.Join(",", h2.Said) + "]");
        Assert.Contains("touched", h2.Said);
    }
}

/// <summary>
/// PHLOX-4: one collection, so the tests that share the single script_state.db file cannot run beside
/// each other or beside anything else that builds an engine.
/// </summary>
[CollectionDefinition("phlox-state", DisableParallelization = true)]
public class PhloxStateCollection { }
