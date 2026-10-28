/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.VM;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A script survives a region restart: the scheduler saves the scripts that changed on a timer and every script when it stops, and a
/// script that joins a scheduler with a record of its own engine is restored from it instead of started: its globals and LSL state,
/// its timer with the time it had left, the sleep it had not finished, the events that waited, its listens and its Running flag, and a
/// script a fault stopped stays stopped with the reason. The scheduler's clock is moved by hand; a restart is a second scheduler
/// over the same database, whose clock starts again at zero.
/// </summary>
[Collection("native-library")]
public class StateRestartTests
{
    private sealed class World : IDisposable
    {
        public readonly TempFolder Dir = new();
        public readonly UUID Asset = UUID.Random();
        public SqliteStateStore Store;
        public SchedulerHarness H;
        private readonly int m_intervalMs;

        public World(int intervalMs = 2500)
        {
            m_intervalMs = intervalMs;
            Boot();
        }

        public string Db => System.IO.Path.Combine(Dir.Path, "state.db");

        /// <summary>A new scheduler over the same database, as a region restart gives.</summary>
        public void Boot()
        {
            Store = new SqliteStateStore(Db, "SLua");
            H = new SchedulerHarness(configure: o =>
            {
                o.StateSink = Store;
                o.EngineName = "SLua";
                o.StateSaveIntervalMs = m_intervalMs;
            });
        }

        /// <summary>The region stops: the scheduler saves every script, the writer finishes and the database closes.</summary>
        public void Restart()
        {
            H.Dispose();
            Store.Dispose();
            Boot();
        }

        /// <summary>The engine's join: the record saved for the item and asset, if the engine owns one, goes with the script into the scheduler before it starts.</summary>
        public SchedulerScript Start(UUID item, string source, bool lsl = false, Action<SchedulerScript> before = null)
        {
            SchedulerScript s = H.Add(source, lsl);
            s.ItemId = item;
            s.AssetId = Asset;
            byte[] saved = Store.Load(item, Asset);
            if (saved != null)
                s.RestoreRecord = ScriptStateRecord.Decode(saved, "SLua", out _);
            before?.Invoke(s);
            for (int i = 0; i < 1000 && s.State == ScriptRunState.Loading; i++)
                H.Scheduler.DoWork(H.Clock.Now);
            H.Scheduler.Start(s);
            H.Pump();
            return s;
        }

        public void Dispose()
        {
            H.Dispose();
            Store.Dispose();
            Dir.Dispose();
        }
    }

    /// <summary>Moves the clock in half seconds: one jump of a long time fires a timer once, as a stall of the real clock would.</summary>
    private static void Run(SchedulerHarness h, double seconds)
    {
        for (double done = 0; done < seconds - 1e-9; done += 0.5)
            h.Advance(0.5);
    }

    private const string Counter = @"counter = 0
ossl.Mark('main', 'ran')
LLEvents:on('timer', function() counter = counter + 1; ossl.Mark('n', counter) end)
ll.SetTimerEvent(10.0)
";

    [RequiresNativeFact]
    public void ACounterKeepsCountingFromWhereItWasAndItsTimerCarriesOnWithTheTimeItHadLeft()
    {
        using var w = new World();
        UUID item = UUID.Random();
        SchedulerScript s = w.Start(item, Counter);
        Run(w.H, 35);
        Assert.Equal(new[] { "main:ran", "n:1", "n:2", "n:3" }, w.H.MarksOf(s)); // ticks at 10, 20 and 30; 5 seconds were left to the next

        w.Restart();
        SchedulerScript back = w.Start(item, Counter);
        Assert.Empty(w.H.MarksOf(back)); // the main chunk did not run again
        Run(w.H, 4.5);
        Assert.Empty(w.H.MarksOf(back)); // the tick is not a whole interval away
        Run(w.H, 1.0);
        Assert.Equal(new[] { "n:4" }, w.H.MarksOf(back)); // 5 seconds after the save, and the count went on
    }

    private const string TwoStates = @"
integer n;
default { state_entry() { llOwnerSay(""entry""); state two; } }
state two
{
    state_entry() { llOwnerSay(""two""); n = 5; llSetTimerEvent(4.0); }
    timer() { n++; llOwnerSay(""t"" + (string)n); }
}
";

    [RequiresNativeFact]
    public void AnLslScriptKeepsItsStateAndGlobalsAndDoesNotRunStateEntryAgain()
    {
        using var w = new World();
        UUID item = UUID.Random();
        SchedulerScript s = w.Start(item, TwoStates, lsl: true);
        Run(w.H, 9);
        Assert.Equal(new[] { "say:entry", "say:two", "say:t6", "say:t7" }, w.H.MarksOf(s));

        w.Restart();
        SchedulerScript back = w.Start(item, TwoStates, lsl: true);
        Run(w.H, 2.5);
        Assert.Empty(w.H.MarksOf(back)); // 3 seconds were left on the timer
        Run(w.H, 1.0);
        Assert.Equal(new[] { "say:t8" }, w.H.MarksOf(back)); // state two's timer handler, with n as it was; neither state_entry ran again
    }

    private const string Sleeper = @"LLEvents:on('timer', function()
    ll.SetTimerEvent(0.0)
    ossl.Mark('a', 'before')
    ll.Sleep(10)
    ossl.Mark('b', 'after')
end)
ll.SetTimerEvent(1.0)
";

    [RequiresNativeFact]
    public void ASleepingScriptFinishesTheSleepItHadLeft()
    {
        using var w = new World();
        UUID item = UUID.Random();
        SchedulerScript s = w.Start(item, Sleeper);
        Run(w.H, 4);
        Assert.Equal(new[] { "a:before" }, w.H.MarksOf(s)); // asleep since 1, until 11: 7 seconds left

        w.Restart();
        SchedulerScript back = w.Start(item, Sleeper);
        Run(w.H, 6.5);
        Assert.Empty(w.H.MarksOf(back));
        Run(w.H, 1.0);
        Assert.Equal(new[] { "b:after" }, w.H.MarksOf(back));
    }

    private const string Queued = @"LLEvents:on('dataserver', function(query, data)
    ossl.Mark('d', data)
    if data == 'one' then ll.Sleep(100) end
end)
";

    [RequiresNativeFact]
    public void TheEventsThatWaitedComeBackInOrder()
    {
        using var w = new World();
        UUID item = UUID.Random();
        SchedulerScript s = w.Start(item, Queued);
        w.H.Scheduler.Post(s, "dataserver", Value.Key("q"), Value.Str("one"));
        w.H.Pump();
        w.H.Scheduler.Post(s, "dataserver", Value.Key("q"), Value.Str("two"));
        w.H.Scheduler.Post(s, "dataserver", Value.Key("q"), Value.Str("three"));
        w.H.Pump();
        Assert.Equal(new[] { "d:one" }, w.H.MarksOf(s)); // asleep, with two events waiting

        w.Restart();
        SchedulerScript back = w.Start(item, Queued);
        Run(w.H, 101);
        Assert.Equal(new[] { "d:two", "d:three" }, w.H.MarksOf(back));
    }

    private const string Faulting = @"LLEvents:on('timer', function() error('boom') end)
ll.SetTimerEvent(1.0)
";

    [RequiresNativeFact]
    public void AScriptAFaultStoppedStaysStoppedWithItsReason()
    {
        using var w = new World();
        UUID item = UUID.Random();
        SchedulerScript s = w.Start(item, Faulting);
        Run(w.H, 2);
        Assert.True(s.Killed);
        Assert.Contains("boom", s.FaultMessage);

        w.Restart();
        SchedulerScript back = w.Start(item, Faulting);
        Run(w.H, 5);

        Assert.True(back.Killed);
        Assert.False(back.Running);
        Assert.Equal(ScriptRunState.Faulted, back.State);
        Assert.Contains("boom", back.FaultMessage);
        Assert.Equal(1, w.H.FaultedScripts.Count(f => ReferenceEquals(f, s))); // the fault was reported when it happened
        Assert.Equal(0, w.H.FaultedScripts.Count(f => ReferenceEquals(f, back))); // and is not reported again at each restart
        Assert.Equal(1, w.H.FaultRestoredScripts.Count(f => ReferenceEquals(f, back))); // the engine is told once, for its log line and the item's flag
        Assert.Equal(0, back.Steps);
    }

    [RequiresNativeFact]
    public void AScriptTheRunningFlagStoppedStaysStoppedAndCarriesOnWhenSetRunning()
    {
        using var w = new World();
        UUID item = UUID.Random();
        SchedulerScript s = w.Start(item, Counter);
        Run(w.H, 14); // one tick, 6 seconds to the next
        w.H.Scheduler.SetRunning(s, false);
        w.H.Pump();
        Assert.Equal(new[] { "main:ran", "n:1" }, w.H.MarksOf(s));

        w.Restart();
        SchedulerScript back = w.Start(item, Counter);
        Run(w.H, 30);
        Assert.False(back.Running);
        Assert.Empty(w.H.MarksOf(back));

        w.H.Scheduler.SetRunning(back, true);
        Run(w.H, 5.5);
        Assert.Empty(w.H.MarksOf(back)); // the timer carries on with the 6 seconds it kept
        Run(w.H, 1.0);
        Assert.Equal(new[] { "n:2" }, w.H.MarksOf(back));
    }

    [RequiresNativeFact]
    public void AScriptsListensGoToTheRecordAndComeBackBeforeItRuns()
    {
        using var w = new World();
        UUID item = UUID.Random();
        var listen = new ActiveListen { Handle = 3, Channel = 42, Name = "n", Key = UUID.Zero.ToString(), Message = "m", Inactive = true };
        w.Start(item, Counter, before: s => s.ListenSnapshot = () => new[] { listen });

        w.Restart();
        ActiveListen[] given = null;
        long stepsWhenGiven = -1;
        w.Start(item, Counter, before: s => s.ListenRestore = l => { given = l; stepsWhenGiven = s.Steps; });

        Assert.NotNull(given);
        Assert.Equal(listen, Assert.Single(given));
        Assert.Equal(0, stepsWhenGiven);
    }

    [RequiresNativeFact]
    public void OnTheTimerOnlyTheScriptsThatChangedAreSavedAndTheShutdownSavesAll()
    {
        using var w = new World(intervalMs: 2000);
        UUID ticking = UUID.Random(), idle = UUID.Random();
        w.Start(ticking, "LLEvents:on('timer', function() ossl.Count() end)\nll.SetTimerEvent(1.0)\n");
        w.Start(idle, "local x = 1\n");

        // the scripts changed when they started; the first save comes one interval later, and a second one only for the script that kept changing
        for (int second = 0; second < 3; second++)
            Run(w.H, 1.0);
        Assert.True(w.Store.Flush());
        Assert.Equal(2, w.Store.WritesDone); // both were new
        Assert.NotNull(w.Store.Load(ticking, w.Asset));
        Assert.NotNull(w.Store.Load(idle, w.Asset));

        for (int second = 0; second < 3; second++)
            Run(w.H, 1.0);
        Assert.True(w.Store.Flush());
        Assert.Equal(4, w.Store.WritesDone); // two more saves, each of the one script whose timer kept running; the idle one was not written again

        long before = w.Store.WritesDone;
        w.H.Dispose(); // the scheduler stops: it saves every script
        Assert.True(w.Store.Flush());
        Assert.Equal(before + 2, w.Store.WritesDone);
        w.H = null;
        w.Store.Dispose();
        w.Boot();
    }

    // ---- a state the runtime refuses ----

    [RequiresNativeFact]
    public void AStateTheRuntimeCannotReadIsRejectedAndTheScriptStartsFresh()
    {
        using var w = new World();
        UUID item = UUID.Random();
        w.Start(item, Counter);
        Run(w.H, 11);
        w.Restart();
        // damage the runtime's part of the saved record
        ScriptStateRecord saved = ScriptStateRecord.Decode(w.Store.Load(item, w.Asset), "SLua", out string why);
        Assert.True(saved != null, "the stopped scheduler saved the script: " + why);
        saved.Native = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };
        w.Store.Save(item, w.Asset, saved.Encode());
        Assert.True(w.Store.Flush());

        SchedulerScript back = w.Start(item, Counter);

        Assert.Equal(new[] { "main:ran" }, w.H.MarksOf(back)); // a fresh start: the main chunk ran
        Assert.True(w.Store.Flush());
        Assert.False(string.IsNullOrEmpty(w.Store.RejectedReason(item)));
        Assert.Equal(saved.Encode().Length, w.Store.RejectedState(item).Length); // the old state is kept on record
    }

    [RequiresNativeFact]
    public void AStateFromAnotherRuntimeBuildStartsFreshAndIsRejectedWithItsReason()
    {
        using var w = new World();
        UUID item = UUID.Random();
        w.Start(item, Counter);
        Run(w.H, 11);
        w.Restart();
        ScriptStateRecord saved = ScriptStateRecord.Decode(w.Store.Load(item, w.Asset), "SLua", out string why);
        Assert.True(saved != null, "the stopped scheduler saved the script: " + why);
        // the runtime's header holds the build stamp as text; change one letter of it
        int at = System.Text.Encoding.ASCII.GetString(saved.Native).IndexOf("runtime=", StringComparison.Ordinal);
        Assert.True(at > 0, "the stamp is in the header");
        saved.Native[at + 1] = (byte)'X';
        w.Store.Save(item, w.Asset, saved.Encode());
        Assert.True(w.Store.Flush());

        SchedulerScript back = w.Start(item, Counter);

        Assert.Equal(new[] { "main:ran" }, w.H.MarksOf(back));
        Assert.True(w.Store.Flush());
        Assert.Contains("runtime", w.Store.RejectedReason(item));
    }

    // ---- the join: what the engine does when it looks for a script's state ----

    private static StateLoadResult Join(SqliteStateStore store, UUID item, UUID asset, List<int> waits, int retries = 3) =>
        StateJoin.Load(store, "SLua", item, asset, retries, 250, waits.Add, null);

    [Fact]
    public void WithNoRowTheScriptStartsFresh()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(System.IO.Path.Combine(dir.Path, "s.db"), "SLua");
        StateLoadResult r = Join(store, UUID.Random(), UUID.Random(), new List<int>());
        Assert.Equal(StateLoadKind.Fresh, r.Kind);
        Assert.Null(r.Record);
    }

    [Fact]
    public void ARecordOfThisEngineForTheItemAndAssetIsRestored()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(System.IO.Path.Combine(dir.Path, "s.db"), "SLua");
        UUID item = UUID.Random(), asset = UUID.Random();
        store.Save(item, asset, new ScriptStateRecord { Engine = "SLua", LslState = 4 }.Encode());
        store.Flush();

        StateLoadResult r = Join(store, item, asset, new List<int>());

        Assert.Equal(StateLoadKind.Restore, r.Kind);
        Assert.Equal(4, r.Record.LslState);
    }

    [Fact]
    public void ARecordThatNamesAnotherEngineStartsTheScriptFreshAndIsLeftAlone()
    {
        using var dir = new TempFolder();
        string db = System.IO.Path.Combine(dir.Path, "s.db");
        using var store = new SqliteStateStore(db, "SLua");
        UUID item = UUID.Random(), asset = UUID.Random();
        // a record another engine wrote, filed under this engine's name (a row moved by hand, a store copied between engines)
        store.Save(item, asset, new ScriptStateRecord { Engine = "Other", LslState = 4 }.Encode());
        store.Flush();

        StateLoadResult r = Join(store, item, asset, new List<int>());

        Assert.Equal(StateLoadKind.Fresh, r.Kind);
        Assert.Null(r.Record);
        Assert.True(store.Flush());
        Assert.True(store.HasSavedState(item, asset)); // not deleted, not rejected, not overwritten
        Assert.Null(store.RejectedReason(item));
    }

    [Fact]
    public void ARecordThatIsNotARecordIsRejectedWithItsReasonAndTheScriptStartsFresh()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(System.IO.Path.Combine(dir.Path, "s.db"), "SLua");
        UUID item = UUID.Random(), asset = UUID.Random();
        store.Save(item, asset, new byte[] { 1, 2, 3 });
        store.Flush();

        StateLoadResult r = Join(store, item, asset, new List<int>());

        Assert.Equal(StateLoadKind.Fresh, r.Kind);
        Assert.True(store.Flush());
        Assert.False(string.IsNullOrEmpty(store.RejectedReason(item)));
    }

    [Fact]
    public void AFailedReadIsRetriedAndThenTheScriptIsHeldWithTheReasonAndItsRowIsLeftAlone()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(System.IO.Path.Combine(dir.Path, "s.db"), "SLua");
        UUID item = UUID.Random(), asset = UUID.Random();
        store.Save(item, asset, new ScriptStateRecord { Engine = "SLua" }.Encode());
        store.Flush();
        int reads = 0;
        store.FailReadFor = id => { reads++; return true; };
        var waits = new List<int>();

        StateLoadResult r = Join(store, item, asset, waits);

        Assert.Equal(StateLoadKind.Held, r.Kind);
        Assert.False(string.IsNullOrEmpty(r.Reason));
        Assert.Equal(4, reads); // the first try and three retries
        Assert.Equal(new[] { 250, 250, 250 }, waits);
        Assert.Null(r.Record);
        store.FailReadFor = null;
        Assert.True(store.HasSavedState(item, asset));
        Assert.NotNull(store.Load(item, asset)); // the row is untouched
        Assert.Null(store.RejectedReason(item));
    }

    [Fact]
    public void AReadThatWorksOnARetryRestoresTheScript()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(System.IO.Path.Combine(dir.Path, "s.db"), "SLua");
        UUID item = UUID.Random(), asset = UUID.Random();
        store.Save(item, asset, new ScriptStateRecord { Engine = "SLua", LslState = 2 }.Encode());
        store.Flush();
        int reads = 0;
        store.FailReadFor = id => ++reads <= 2;

        StateLoadResult r = Join(store, item, asset, new List<int>());

        Assert.Equal(StateLoadKind.Restore, r.Kind);
        Assert.Equal(2, r.Record.LslState);
    }
}
