/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The service lane: the 11 long-running (async) and 20 service-reaching functions never run on the script worker. A call
/// that may leave the process runs on a separate lane while its script waits, and the answer comes back the way Phlox's does
/// (PhloxExecutionScheduler.ProcessSyscallReturns): one answer for the call, only to the script still waiting for that call,
/// the value on the script's next turn, the call's delay applied after it, a fault raised in the script, and the function's
/// failure value if the deadline passes first (RunServiceCall, ProcessServiceDeadlines).
/// </summary>
[Collection("native-library")]
public class ServiceLaneTests
{
    private const string ZeroKey = "00000000-0000-0000-0000-000000000000";

    /// <summary>A key made from a name, the same every time (a script only gets a key that is a valid UUID).</summary>
    private static string KeyOf(string name) =>
        new UUID(new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)))).ToString();

    /// <summary>The scheduler harness with the generated ll bindings, a fake API and a service lane.</summary>
    private sealed class LaneRig : IDisposable
    {
        public readonly ILSLSystemAPI Api;
        public readonly FakeLslApi Fake;
        public readonly SchedulerHarness H;
        public readonly ServiceLane Lane;
        private readonly ConcurrentDictionary<long, UUID> m_items = new();

        public LaneRig(string deferral = "auto", int timeoutMs = 35000, int idleMs = 30000, int threads = 4)
        {
            (Api, Fake) = FakeLslApi.Create();
            H = new SchedulerHarness(configure: o =>
            {
                Func<Bindings.Vm> inner = o.VmFactory;
                o.VmFactory = () =>
                {
                    Bindings.Vm vm = inner();
                    LlBindings.Register(vm, s => new ScriptContext(s, Api, m_items.GetOrAdd(s.Id, _ => UUID.Random()), Lane), serviceLane: true);
                    return vm;
                };
            });
            Lane = new ServiceLane(H.Scheduler, deferral, threads, timeoutMs, idleMs);
        }

        /// <summary>Pumps the scheduler until the condition holds; the lane's answers are taken in by the pump.</summary>
        public bool PumpUntil(Func<bool> condition, int timeoutMs = -1) =>
            SchedulerHarness.WaitFor(() => { H.Pump(); return condition(); }, timeoutMs);

        public int CallsOf(string apiName) => Fake.Calls.Count(c => c.Name == apiName);

        public void Dispose()
        {
            Lane.Dispose();
            H.Dispose();
        }
    }

    private static string[] Marks(LaneRig rig, SchedulerScript s) => rig.H.MarksOf(s);

    /// <summary>Everything the rig saw, for a failure message.</summary>
    private static string Diag(LaneRig rig) =>
        "marks=[" + string.Join(",", rig.H.Marks) + "] faulted=[" + string.Join(",", rig.H.FaultedScripts.Select(f => f.FaultMessage)) +
        "] errors=[" + string.Join(",", rig.H.ReportedErrors) + "] calls=[" + string.Join(",", rig.Fake.Calls.Select(c => c.Name)) +
        "] log=[" + string.Join(" | ", rig.H.Log.Lines) + "]";

    // ---- where the calls run ----

    [RequiresNativeFact]
    public void AServiceCallThatNeedsTheServiceRunsOnTheLaneAndTheScriptGetsItsValue()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a => KeyOf(a[0].ToString()));
        SchedulerScript s = rig.H.AddAndStart("local k = ll.Name2Key('Alice')\nossl.Mark('k', tostring(k))");

        Assert.True(rig.PumpUntil(() => Marks(rig, s).Contains("k:" + KeyOf("Alice"))), Diag(rig));

        FakeLslApi.Call call = rig.Fake.Calls.Single(c => c.Name == "llName2Key");
        Assert.NotEqual(Environment.CurrentManagedThreadId, call.Thread);
        // the question Phlox's shim asks first, with the arguments as the shim would pass them
        FakeLslApi.Call asked = rig.Fake.Calls.Single(c => c.Name == "NeedsService");
        Assert.Equal("llName2Key", asked.Args[0]);
        Assert.Equal(new object[] { "Alice" }, (object[])asked.Args[1]);
        Assert.Equal(Environment.CurrentManagedThreadId, asked.Thread);
    }

    [RequiresNativeFact]
    public void AnAsyncCallRunsOnTheLaneWithoutAskingWhetherItNeedsTheService()
    {
        using var rig = new LaneRig();
        SchedulerScript s = rig.H.AddAndStart("ll.InstantMessage('" + ZeroKey + "', 'hi')\nossl.Mark('after', 'yes')");

        Assert.True(rig.PumpUntil(() => Marks(rig, s).Contains("after:yes")), Diag(rig));

        Assert.NotEqual(Environment.CurrentManagedThreadId, rig.Fake.Calls.Single(c => c.Name == "llInstantMessage").Thread);
        Assert.DoesNotContain(rig.Fake.Calls, c => c.Name == "NeedsService");
    }

    [RequiresNativeFact]
    public void AServiceCallThatNeedsNoServiceRunsOnTheWorkerAsInPhlox()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("NeedsService", false);
        rig.Fake.Runs("llName2Key", a => KeyOf(a[0].ToString()));
        SchedulerScript s = rig.H.AddAndStart("local k = ll.Name2Key('Bob')\nossl.Mark('k', tostring(k))");

        Assert.True(rig.PumpUntil(() => Marks(rig, s).Contains("k:" + KeyOf("Bob"))), Diag(rig));

        Assert.Equal(Environment.CurrentManagedThreadId, rig.Fake.Calls.Single(c => c.Name == "llName2Key").Thread);
    }

    [RequiresNativeFact]
    public void WithDeferralOffAServiceCallRunsOnTheWorkerAndIsNotAskedAbout()
    {
        using var rig = new LaneRig(deferral: "never");
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a => KeyOf(a[0].ToString()));
        SchedulerScript s = rig.H.AddAndStart("local k = ll.Name2Key('Cy')\nossl.Mark('k', tostring(k))");

        Assert.True(rig.PumpUntil(() => Marks(rig, s).Contains("k:" + KeyOf("Cy"))), Diag(rig));

        Assert.False(rig.Lane.Defers);
        Assert.Equal(Environment.CurrentManagedThreadId, rig.Fake.Calls.Single(c => c.Name == "llName2Key").Thread);
        Assert.DoesNotContain(rig.Fake.Calls, c => c.Name == "NeedsService");
    }

    private static string SLuaArg(string type) => type switch
    {
        "integer" => "1",
        "float" => "1.5",
        "string" => "'s'",
        "key" => "'" + ZeroKey + "'",
        "vector" => "vector(1, 2, 3)",
        "rotation" => "quaternion(1, 2, 3, 4)",
        _ => "{1, 'a'}",
    };

    [RequiresNativeFact]
    public void NoneOfTheFifteenAsyncAndTwentyTwoServiceFunctionsRunsOnTheScriptWorker()
    {
        BoundFunction[] functions = LlBindings.All.Where(f => f.Class is FunctionClass.Async or FunctionClass.Service).ToArray();
        Assert.Equal(15, functions.Count(f => f.Class == FunctionClass.Async));
        Assert.Equal(22, functions.Count(f => f.Class == FunctionClass.Service));

        using var rig = new LaneRig();
        rig.Fake.Returns("NeedsService", true);
        var scripts = new List<SchedulerScript>();
        foreach (BoundFunction f in functions)
            scripts.Add(rig.H.AddAndStart("ll." + f.Name + "(" + string.Join(", ", f.ParamTypes.Select(SLuaArg)) + ")\nossl.Count()"));

        Assert.True(rig.PumpUntil(() => scripts.All(x => rig.H.CountOf(x) == 1)),
            "scripts that did not finish: " + string.Join(",", functions.Where((f, i) => rig.H.CountOf(scripts[i]) == 0).Select(f => f.Name)));
        Assert.Empty(rig.H.FaultedScripts);

        var onWorker = new List<string>();
        foreach (BoundFunction f in functions)
        {
            FakeLslApi.Call call = rig.Fake.Calls.Single(c => c.Name == f.ApiName);
            if (call.Thread == Environment.CurrentManagedThreadId) onWorker.Add(f.Name);
        }
        Assert.Empty(onWorker);
    }

    [RequiresNativeFact]
    public void AScriptThatOverranBeforeItsServiceCallStillGetsTheValue()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a => KeyOf(a[0].ToString()));
        // Burn holds the thread past the run window, so the runtime puts the script to sleep on the harness's clock.
        SchedulerScript s = rig.H.AddAndStart("ossl.Burn(60)\nlocal k = ll.Name2Key('Dee')\nossl.Mark('k', tostring(k))");

        Assert.True(rig.PumpUntil(() => Marks(rig, s).Contains("k:" + KeyOf("Dee"))), Diag(rig));
    }

    // ---- a slow call stalls only its own script ----

    [RequiresNativeFact]
    public void ASlowServiceCallDoesNotDelayOtherScriptsTurns()
    {
        using var rig = new LaneRig();
        using var gate = new ManualResetEventSlim();
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a => { gate.Wait(TimeSpan.FromSeconds(4)); return KeyOf("slow-key"); });
        SchedulerScript slow = rig.H.AddAndStart("local k = ll.Name2Key('x')\nossl.Mark('k', tostring(k))");
        SchedulerScript other = rig.H.AddAndStart("ossl.Count()");

        var started = System.Diagnostics.Stopwatch.StartNew();
        rig.H.Pump();

        // the other script took its turn while the slow call is still blocked on the lane
        Assert.False(gate.IsSet);
        Assert.Equal(1, rig.H.CountOf(other));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), "the pump waited for the slow call: " + started.Elapsed);
        Assert.Equal(ScriptRunState.Waiting, slow.State);
        Assert.Empty(Marks(rig, slow));

        gate.Set();
        Assert.True(rig.PumpUntil(() => Marks(rig, slow).Contains("k:" + KeyOf("slow-key"))), Diag(rig));
        Assert.Equal(1, rig.H.CountOf(other));
    }

    // ---- the answers ----

    [RequiresNativeFact]
    public void AScriptsAnswersArriveOnceAndInTheOrderItAsked()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a => { Thread.Sleep(5 * (3 - (a[0].ToString()[0] - 'a'))); return KeyOf(a[0].ToString()); });
        SchedulerScript s = rig.H.AddAndStart(
            "ossl.Mark('r', tostring(ll.Name2Key('a')))\nossl.Mark('r', tostring(ll.Name2Key('b')))\nossl.Mark('r', tostring(ll.Name2Key('c')))\nossl.Count()");

        Assert.True(rig.PumpUntil(() => rig.H.CountOf(s) == 1), Diag(rig));
        for (int i = 0; i < 5; i++) { rig.H.Pump(); Thread.Sleep(10); }

        Assert.Equal(new[] { "r:" + KeyOf("a"), "r:" + KeyOf("b"), "r:" + KeyOf("c") }, Marks(rig, s));
        Assert.Equal(3, rig.CallsOf("llName2Key"));
        Assert.Equal(1, rig.H.CountOf(s));
    }

    [RequiresNativeFact]
    public void EachScriptGetsItsOwnAnswerWhenManyCallsAreInFlightTogether()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a => { Thread.Sleep(20); return KeyOf(a[0].ToString()); });
        var scripts = new List<SchedulerScript>();
        for (int i = 0; i < 6; i++)
            scripts.Add(rig.H.AddAndStart("ossl.Mark('r', tostring(ll.Name2Key('n" + i + "')))\nossl.Count()"));

        Assert.True(rig.PumpUntil(() => scripts.All(x => rig.H.CountOf(x) == 1)), Diag(rig));

        for (int i = 0; i < scripts.Count; i++)
            Assert.Equal(new[] { "r:" + KeyOf("n" + i) }, Marks(rig, scripts[i]));
        Assert.Equal(6, rig.CallsOf("llName2Key"));
    }

    [RequiresNativeFact]
    public void AnAsyncCallThatReturnsAKeyGivesTheKeyItRecordedWithSysReturn()
    {
        using var rig = new LaneRig();
        // Phlox: "async, and the key reaches the script through SysReturn" (Shim_llRezObjectWithParams)
        rig.Fake.Runs("llRezObjectWithParams", a => { SyscallContext.Current.SetResult(KeyOf("rezzed-key"), 0); return "not used"; });
        SchedulerScript s = rig.H.AddAndStart("local k = ll.RezObjectWithParams('Object', {})\nossl.Mark('k', tostring(k))");

        Assert.True(rig.PumpUntil(() => Marks(rig, s).Length > 0), Diag(rig));

        Assert.Equal(new[] { "k:" + KeyOf("rezzed-key") }, Marks(rig, s));
    }

    [RequiresNativeFact]
    public void TheDelayOfACallOnTheLaneIsTheScriptsAfterItsResult()
    {
        using var rig = new LaneRig();
        rig.Fake.Delays("llInstantMessage", 2000);
        SchedulerScript s = rig.H.AddAndStart("ll.InstantMessage('" + ZeroKey + "', 'hi')\nossl.Count()");

        Assert.True(rig.PumpUntil(() => s.State == ScriptRunState.Sleeping), Diag(rig));

        Assert.Equal(0, rig.H.CountOf(s));
        Assert.InRange(s.SleepUntil - rig.H.Clock.Now, 1.99, 2.01);
        rig.H.Advance(1.9);
        Assert.Equal(0, rig.H.CountOf(s));
        rig.H.Advance(0.2);
        Assert.Equal(1, rig.H.CountOf(s));
    }

    [RequiresNativeFact]
    public void ACallThatThrowsOnTheLaneStopsItsScriptWithTheError()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a => throw new InvalidOperationException("lane boom"));
        SchedulerScript s = rig.H.AddAndStart("ossl.Mark('r', tostring(ll.Name2Key('a')))\nossl.Count()");

        Assert.True(rig.PumpUntil(() => rig.H.FaultedScripts.Count > 0), Diag(rig));

        Assert.Same(s, rig.H.FaultedScripts.Single());
        Assert.Contains("lane boom", s.FaultMessage);
        Assert.Empty(Marks(rig, s));
    }

    // ---- the deadline ----

    [RequiresNativeFact]
    public void ACallPastItsDeadlineAnswersWithTheFailureValueAndTheLateAnswerIsDropped()
    {
        using var rig = new LaneRig(timeoutMs: 150);
        using var gate = new ManualResetEventSlim();
        int finished = 0;
        rig.Fake.Returns("NeedsService", true);
        // llGetNotecardLineSync answers three newlines when it is late (Phlox: Defer in Shim_llGetNotecardLineSync), not an empty value
        rig.Fake.Runs("llGetNotecardLineSync", a => { gate.Wait(TimeSpan.FromSeconds(10)); Interlocked.Exchange(ref finished, 1); return "the late line"; });
        SchedulerScript s = rig.H.AddAndStart("ossl.Mark('r', ll.GetNotecardLineSync('card', 1))\nossl.Count()");

        Assert.True(rig.PumpUntil(() => rig.H.CountOf(s) == 1), Diag(rig));
        Assert.Equal(new[] { "r:\n\n\n" }, Marks(rig, s));

        gate.Set();
        Assert.True(SchedulerHarness.WaitFor(() => Volatile.Read(ref finished) == 1));
        for (int i = 0; i < 5; i++) { rig.H.Pump(); Thread.Sleep(10); }

        Assert.Equal(new[] { "r:\n\n\n" }, Marks(rig, s));
        Assert.Equal(1, rig.H.CountOf(s));
        Assert.Empty(rig.H.FaultedScripts);
    }

    [RequiresNativeFact]
    public void AnAnswerForACallTheScriptHasMovedPastIsDropped()
    {
        using var rig = new LaneRig(timeoutMs: 1500);
        using var first = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        int calls = 0, firstFinished = 0;
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                first.Wait(TimeSpan.FromSeconds(10));
                Interlocked.Exchange(ref firstFinished, 1);
                return KeyOf("stale");
            }
            second.Wait(TimeSpan.FromSeconds(10));
            return KeyOf("fresh");
        });
        SchedulerScript s = rig.H.AddAndStart("ossl.Mark('first', tostring(ll.Name2Key('a')))\nossl.Mark('second', tostring(ll.Name2Key('b')))\nossl.Count()");

        // the first call passes its deadline and the script goes on to the second, which is still being worked on
        Assert.True(rig.PumpUntil(() => rig.CallsOf("llName2Key") == 2 && Marks(rig, s).Length == 1 && s.State == ScriptRunState.Waiting), Diag(rig));

        // the first call's answer arrives now, while the script waits for the second
        first.Set();
        Assert.True(SchedulerHarness.WaitFor(() => Volatile.Read(ref firstFinished) == 1));
        for (int i = 0; i < 5; i++) { rig.H.Pump(); Thread.Sleep(10); }
        Assert.Equal(ScriptRunState.Waiting, s.State);
        Assert.Equal(new[] { "first:" + ZeroKey }, Marks(rig, s));

        second.Set();
        Assert.True(rig.PumpUntil(() => rig.H.CountOf(s) == 1), Diag(rig));
        Assert.Equal(new[] { "first:" + ZeroKey, "second:" + KeyOf("fresh") }, Marks(rig, s));
    }

    [RequiresNativeFact]
    public void TheLanesThreadsGoWhenItIsIdle()
    {
        using var rig = new LaneRig(idleMs: 60);
        rig.Fake.Returns("NeedsService", true);
        rig.Fake.Runs("llName2Key", a => KeyOf("idle"));
        SchedulerScript s = rig.H.AddAndStart("ossl.Mark('r', tostring(ll.Name2Key('a')))\nossl.Count()");

        Assert.True(rig.PumpUntil(() => rig.H.CountOf(s) == 1), Diag(rig));
        Assert.True(SchedulerHarness.WaitFor(() => rig.Lane.ThreadCount == 0), "lane threads alive: " + rig.Lane.ThreadCount);
    }

    // ---- the failure values ----

    /// <summary>Phlox's shim read independently of the generator: the failure value of each deferred ll call.</summary>
    private static Dictionary<string, string> ShimFailures()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Source", "InWorldz.Phlox", "Glue", "SyscallShim.cs")))
            dir = Path.GetDirectoryName(dir);
        string text = File.ReadAllText(Path.Combine(dir, "Source", "InWorldz.Phlox", "Glue", "SyscallShim.cs"));
        var map = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(text, "Defer\\(self, \"(ll\\w+)\", [^\\n]*, (null|\"[^\"]*\"|-?\\d+|new LSLList\\(\\)), (?:true|false)\\);"))
            map[m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    [Fact]
    public void EveryServiceFunctionHasTheFailureValueOfPhloxsShim()
    {
        Dictionary<string, string> shim = ShimFailures();
        BoundFunction[] services = LlBindings.All.Where(f => f.Class == FunctionClass.Service).ToArray();
        Assert.Equal(22, services.Length);

        foreach (BoundFunction f in services)
        {
            Assert.True(shim.TryGetValue(f.ApiName, out string literal), f.ApiName + " has no Defer call in the shim");
            object expected = literal switch
            {
                "null" => null,
                "new LSLList()" => new LSLList(),
                _ when literal.StartsWith('"') => Regex.Unescape(literal.Trim('"')),
                _ => int.Parse(literal),
            };
            if (expected is LSLList)
            {
                var list = Assert.IsType<LSLList>(f.FailureValue);
                Assert.Empty(list.Data);
            }
            else
                Assert.Equal(expected, f.FailureValue);
        }
    }
}
