/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A script's saved state survives a build that binds more host functions, is set aside with the name of the function when it holds
/// one that is gone, and a set-aside state can be tried again; an LLTimers timer goes on ticking after the script is restored.
/// A restart is a second scheduler over the same database file; each boot binds the host functions its test names.
/// </summary>
[Collection("native-library")]
public class StateSurvivalTests
{
    [Flags]
    private enum Functions { Mark = 1, Extra = 2, Gone = 4, All = Mark | Extra | Gone }

    private sealed class World : IDisposable
    {
        public readonly TempFolder Dir = new();
        public readonly UUID Asset = UUID.Random();
        public SqliteStateStore Store;
        public SchedulerHarness H;
        private ConcurrentQueue<string> m_marks = new();

        public World(Functions functions)
        {
            Boot(functions);
        }

        public string Db => System.IO.Path.Combine(Dir.Path, "state.db");

        public void Boot(Functions functions)
        {
            m_marks = new ConcurrentQueue<string>();   // script ids start again at 1 in every boot
            Store = new SqliteStateStore(Db, "SLua");
            H = new SchedulerHarness(configure: o =>
            {
                o.StateSink = Store;
                o.EngineName = "SLua";
                o.StateSaveIntervalMs = 2500;
                o.VmFactory = () =>
                {
                    var vm = new Vm(Watchdog.Default);
                    if (functions.HasFlag(Functions.Mark))
                        vm.Register("ossl", "Mark", (tag, a) => { m_marks.Enqueue($"{tag}:{a[0].S}:{(a.Length > 1 ? Text(a[1]) : "")}"); return null; });
                    vm.Register("ossl", "Timer", (tag, a) => { SchedulerScript.Current.SetTimer(a[0].Type == Bindings.ValueType.Number ? a[0].N : a[0].I); return null; });
                    if (functions.HasFlag(Functions.Extra))
                        vm.Register("ossl", "Extra", (tag, a) => null);
                    if (functions.HasFlag(Functions.Gone))
                        vm.Register("ossl", "Gone", (tag, a) => null);
                    return vm;
                };
            });
        }

        private static string Text(Value v) => v.Type is Bindings.ValueType.Number or Bindings.ValueType.Int
            ? (v.Type == Bindings.ValueType.Number ? v.N : v.I).ToString("G", System.Globalization.CultureInfo.InvariantCulture)
            : v.S;

        public string[] MarksOf(SchedulerScript s) =>
            m_marks.Where(m => m.StartsWith(s.Id + ":")).Select(m => m.Substring(m.IndexOf(':') + 1)).ToArray();

        public void Restart(Functions functions)
        {
            H.Dispose();
            Store.Dispose();
            Boot(functions);
        }

        public SchedulerScript Start(UUID item, string source)
        {
            SchedulerScript s = H.Add(source);
            s.ItemId = item;
            s.AssetId = Asset;
            byte[] saved = Store.Load(item, Asset);
            if (saved != null)
                s.RestoreRecord = ScriptStateRecord.Decode(saved, "SLua", out _);
            for (int i = 0; i < 1000 && s.State == ScriptRunState.Loading; i++)
                H.Scheduler.DoWork(H.Clock.Now);
            H.Scheduler.Start(s);
            H.Pump();
            return s;
        }

        public RetryOutcome Retry(SchedulerScript s, ScriptStateRecord record)
        {
            RetryOutcome? outcome = null;
            H.Scheduler.RetryState(s, record, o => outcome = o);
            for (int i = 0; i < 100 && outcome == null; i++)
                H.Pump();
            Assert.NotNull(outcome);
            return outcome.Value;
        }

        public void Dispose()
        {
            H.Dispose();
            Store.Dispose();
            Dir.Dispose();
        }
    }

    private static void Run(SchedulerHarness h, double seconds)
    {
        for (double done = 0; done < seconds - 1e-9; done += 0.5)
            h.Advance(0.5);
    }

    private const string Ticker = @"counter = 0
ossl.Mark('main', 'ran')
LLEvents:on('timer', function() counter = counter + 1; ossl.Mark('tick', counter) end)
ossl.Timer(1.0)
";

    // The ticker holds the Gone function in the closure of its timer handler, so its saved state holds it.
    private const string HoldsGone = @"counter = 0
local gone = ossl.Gone or function() end
ossl.Mark('main', 'ran')
LLEvents:on('timer', function() gone(); counter = counter + 1; ossl.Mark('tick', counter) end)
ossl.Timer(1.0)
";

    // ---- LLTimers after a restore ----

    // The runtime's LLTimers decides which timers are due by its own clock, which a test cannot move, so these tests look at what the
    // host does: the interval it keeps for the script and the timer events it wakes it with.
    private const string LlTimersScript = "LLTimers:every(1.0, function() ossl.Mark('tick') end)";

    [RequiresNativeFact]
    public void AnLlTimersTimerKeepsItsIntervalAndWakesTheScriptAfterTheScriptIsRestored()
    {
        using var w = new World(Functions.Mark);
        UUID item = UUID.Random();
        SchedulerScript s = w.Start(item, LlTimersScript);
        Assert.Equal(1.0, s.TimerInterval, 3);

        w.Restart(Functions.Mark);
        SchedulerScript back = w.Start(item, LlTimersScript);
        Assert.Equal(1.0, back.TimerInterval, 3);   // the restored script still has its timer
        long before = back.Dispatches;
        Run(w.H, 1.5);
        Assert.True(back.Dispatches > before, "the host did not wake the restored script's timer");
        Assert.True(back.TimerInterval > 0 && back.TimerInterval <= 1.0, "the timer was not set again after the wake: " + back.TimerInterval);   // the library gives the time to the next tick
    }

    // ---- host functions added or removed between builds ----

    [RequiresNativeFact]
    public void AStateSavedBeforeHostFunctionsWereAddedIsRestoredAndRunsOn()
    {
        using var w = new World(Functions.Mark);
        UUID item = UUID.Random();
        w.Start(item, Ticker);
        Run(w.H, 3.5);

        w.Restart(Functions.Mark | Functions.Extra | Functions.Gone);
        SchedulerScript back = w.Start(item, Ticker);
        Run(w.H, 2.0);
        Assert.Equal(new[] { "tick:4", "tick:5" }, w.MarksOf(back));
        Assert.True(w.Store.Flush());
        Assert.Null(w.Store.RejectedReason(item)); // nothing was set aside
    }

    [RequiresNativeFact]
    public void AStateThatHoldsAFunctionThatIsGoneIsSetAsideWithTheFunctionsName()
    {
        using var w = new World(Functions.Mark | Functions.Gone);
        UUID item = UUID.Random();
        w.Start(item, HoldsGone);
        Run(w.H, 2.5);

        w.Restart(Functions.Mark);
        SchedulerScript back = w.Start(item, HoldsGone);
        Assert.Equal(new[] { "main:ran" }, w.MarksOf(back)); // started fresh
        Assert.True(w.Store.Flush());
        string reason = w.Store.RejectedReason(item);
        Assert.Contains("ossl.Gone", reason);
        Assert.NotNull(w.Store.RejectedState(item)); // the old state is kept
    }

    [RequiresNativeFact]
    public void AStateThatDoesNotHoldTheFunctionThatIsGoneIsRestored()
    {
        using var w = new World(Functions.Mark | Functions.Gone);
        UUID item = UUID.Random();
        w.Start(item, Ticker);
        Run(w.H, 3.5);

        w.Restart(Functions.Mark);
        SchedulerScript back = w.Start(item, Ticker);
        Run(w.H, 1.0);
        Assert.Equal(new[] { "tick:4" }, w.MarksOf(back));
        Assert.True(w.Store.Flush());
        Assert.Null(w.Store.RejectedReason(item));
    }

    // ---- trying a set-aside state again ----

    private static ScriptStateRecord Rejected(World w, UUID item)
    {
        Assert.True(w.Store.Flush());
        (long id, byte[] state, string reason)? pending = w.Store.PendingRejected(item);
        Assert.NotNull(pending);
        ScriptStateRecord record = ScriptStateRecord.Decode(pending.Value.state, "SLua", out string why);
        Assert.True(record != null, "the set-aside state is a record: " + why);
        return record;
    }

    [RequiresNativeFact]
    public void ASetAsideStateIsPutBackOnceTheFunctionItHoldsIsBoundAgain()
    {
        using var w = new World(Functions.Mark | Functions.Gone);
        UUID item = UUID.Random();
        w.Start(item, HoldsGone);
        Run(w.H, 2.5); // two ticks counted before the state is set aside

        w.Restart(Functions.Mark);
        w.Start(item, HoldsGone);
        Assert.True(w.Store.Flush());
        w.Restart(Functions.Mark | Functions.Gone | Functions.Extra); // a later build binds the function again
        SchedulerScript fresh = w.Start(item, HoldsGone);
        Run(w.H, 1.5);
        Assert.Equal(new[] { "tick:1" }, w.MarksOf(fresh).Where(m => m.StartsWith("tick")).ToArray()); // the fresh run counts from 1

        ScriptStateRecord record = Rejected(w, item);
        RetryOutcome outcome = w.Retry(fresh, record);
        Assert.True(outcome.Restored, outcome.Reason);
        Run(w.H, 1.0);
        Assert.Equal("tick:3", w.MarksOf(fresh).Last()); // the count went on from the two ticks of the old state
    }

    [RequiresNativeFact]
    public void AStateThatStillCannotLoadStaysSetAsideWithItsReasonAndTheRunningScriptIsLeftAlone()
    {
        using var w = new World(Functions.Mark | Functions.Gone);
        UUID item = UUID.Random();
        w.Start(item, HoldsGone);
        Run(w.H, 2.5);

        w.Restart(Functions.Mark);
        SchedulerScript fresh = w.Start(item, HoldsGone);
        Run(w.H, 1.5); // the fresh script has counted one tick
        ScriptStateRecord record = Rejected(w, item);

        RetryOutcome outcome = w.Retry(fresh, record);
        Assert.False(outcome.Restored);
        Assert.Contains("ossl.Gone", outcome.Reason);
        Run(w.H, 1.0);
        Assert.Equal("tick:2", w.MarksOf(fresh).Last()); // the fresh script went on from its own count
        Assert.NotNull(w.Store.PendingRejected(item)); // still set aside, so it can be tried again
    }

    [RequiresNativeFact]
    public void ARetriedStateIsNotOfferedAgain()
    {
        using var w = new World(Functions.Mark | Functions.Gone);
        UUID item = UUID.Random();
        w.Start(item, Ticker);
        Run(w.H, 2.5);
        w.Restart(Functions.Mark);
        w.Start(item, Ticker);
        Assert.True(w.Store.Flush());
        w.Store.Reject(item, "set aside by the test"); // the row the fresh start would have saved is moved aside like a refused state
        Assert.True(w.Store.Flush());

        (long id, byte[] state, string reason)? pending = w.Store.PendingRejected(item);
        Assert.NotNull(pending);
        Assert.Equal("set aside by the test", pending.Value.reason);
        w.Store.MarkRetried(pending.Value.id);
        Assert.True(w.Store.Flush());
        Assert.Null(w.Store.PendingRejected(item));
        Assert.NotNull(w.Store.RejectedState(item)); // the row is kept
    }
}
