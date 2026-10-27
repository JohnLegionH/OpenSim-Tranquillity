/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using Phlox.ScriptEngine;
using Phlox.ScriptEngine.AsyncCommand;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A real LSLSystemAPI, built through Phlox's two seams (IScriptHostServices and IScriptRuntimeAccess) with no PhloxEngine and
/// no interpreter, serves the generated ll bindings. Scripts call string, list and maths functions that need no scene and
/// no delay; the same scripts run against the fake that answers what LSL documents, and the two runs must agree. Functions
/// with a scene effect, a delay or an asynchronous answer (here llOwnerSay, which only reports the result) stay on the fake.
/// </summary>
[Collection("native-library")]
public class RealLslSystemApiTests
{
    /// <summary>The host services of an engine that has none. The functions under test must not ask for any, so every touch is noted.</summary>
    private sealed class StandInHost : IScriptHostServices
    {
        public readonly ConcurrentQueue<string> Touched = new();
        private T N<T>(string what, T value = default) { Touched.Enqueue(what); return value; }

        public string Name => N("Name", "StandIn");
        public Scene World => N<Scene>("World");
        public IConfigSource ConfigSource => N<IConfigSource>("ConfigSource");
        public IScriptModule ScriptModule => N<IScriptModule>("ScriptModule");
        public IScriptEngine ScriptEngine => N<IScriptEngine>("ScriptEngine");
        public ServiceCallDeferralMode ServiceCallDeferral => N("ServiceCallDeferral", ServiceCallDeferralMode.Auto);
        public bool AllowGodFunctions => N("AllowGodFunctions", false);
        public bool AutomaticLinkPermission => N("AutomaticLinkPermission", false);
        public float MinTimerInterval => N("MinTimerInterval", 0f);
        public bool ResetThrottle => N("ResetThrottle", false);
        public bool ChatThrottle => N("ChatThrottle", false);
        public bool BotThrottle => N("BotThrottle", false);
        public bool PhysicsThrottle => N("PhysicsThrottle", false);
        public bool LinkMessageThrottle => N("LinkMessageThrottle", false);
        public bool NotecardThrottle => N("NotecardThrottle", false);
        public bool FormatStringThrottle => N("FormatStringThrottle", false);
        public bool HttpInFlightThrottle => N("HttpInFlightThrottle", false);
        public bool NotecardHalcyonTiming => N("NotecardHalcyonTiming", false);
        public bool NotecardCacheEnabled => N("NotecardCacheEnabled", false);
        public PhloxNotecardCache NotecardCache => N<PhloxNotecardCache>("NotecardCache");
        public int PhysicsFrameTimeAvg => N("PhysicsFrameTimeAvg", 0);
        public IScriptOsslChecks Ossl => N<IScriptOsslChecks>("Ossl");
        public IScriptListenService ListenManager => N<IScriptListenService>("ListenManager");
        public IScriptStateTracker StateManager => N<IScriptStateTracker>("StateManager");
        public AsyncCommandManager AsyncCommands => N<AsyncCommandManager>("AsyncCommands");

        public void SysReturn(UUID itemId, object retValue, int delay) => N<int>("SysReturn");
        public void SysReturnSequenced(UUID itemId, object retValue, int delay, int seq) => N<int>("SysReturnSequenced");
        public bool PostScriptEvent(UUID itemID, EventParams parms) => N("PostScriptEvent", false);
        public bool PostObjectEvent(uint localID, EventParams parms) => N("PostObjectEvent", false);
        public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value) => N("PostObjectLinksetDataEvent", false);
        public int PostDataserverToPrim(SceneObjectPart part, UUID skip, UUID asker, string queryId, string data) => N("PostDataserverToPrim", 0);
        public bool HasScript(UUID itemID, out bool running) { running = false; return N("HasScript", false); }
        public void SetTimerEvent(uint localID, UUID itemID, float sec) => N<int>("SetTimerEvent");
        public void SetMinEventDelay(UUID itemID, float seconds) => N<int>("SetMinEventDelay");
        public void SetItemRunningFlag(uint localId, UUID itemId, bool running) => N<int>("SetItemRunningFlag");
        public void ResetScript(UUID itemID) => N<int>("ResetScript");
        public void ApiResetScript(UUID itemID) => N<int>("ApiResetScript");
        public void RequestParcelCheck(UUID itemId) => N<int>("RequestParcelCheck");
        public void SendToWorldComm(Action<IWorldComm> send) => N<int>("SendToWorldComm");
        public float GetEventQueueFreeSpacePercentage(UUID itemID) => N("GetEventQueueFreeSpacePercentage", 100f);
        public void ExperienceGrantRestored() => N<int>("ExperienceGrantRestored");
        public void ExperienceCannotRun(UUID experience, int code) => N<int>("ExperienceCannotRun", 0);
    }

    private sealed class StandInScript : IScriptRuntimeAccess
    {
        public RuntimeState ScriptState { get; } = new RuntimeState();
        public IEnumerable<int> EventTypesHandledIn(int lslState) => Array.Empty<int>();
    }

    /// <summary>Passes each call to the real API and notes it, except for the functions that stay on the fake, which only get noted.</summary>
    public class Forwarder : DispatchProxy
    {
        public sealed record Call(string Name, object[] Args);

        private readonly ConcurrentQueue<Call> m_calls = new();
        private ILSLSystemAPI m_target;
        private HashSet<string> m_faked;

        public Call[] Calls => m_calls.ToArray();

        public static (ILSLSystemAPI Api, Forwarder Forwarder) Create(ILSLSystemAPI target, params string[] faked)
        {
            ILSLSystemAPI api = DispatchProxy.Create<ILSLSystemAPI, Forwarder>();
            var f = (Forwarder)(object)api;
            f.m_target = target;
            f.m_faked = new HashSet<string>(faked);
            return (api, f);
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            m_calls.Enqueue(new Call(targetMethod.Name, args));
            if (m_faked.Contains(targetMethod.Name)) return FakeLslApi.DefaultOf(targetMethod.ReturnType);
            try { return targetMethod.Invoke(m_target, args); }
            catch (TargetInvocationException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>The scheduler harness with the generated ll bindings registered in every VM, served by a real LSLSystemAPI.</summary>
    private sealed class RealApiRig : IDisposable
    {
        public readonly StandInHost Host = new();
        public readonly LSLSystemAPI Real;
        public readonly Forwarder Calls;
        public readonly SchedulerHarness H;

        public RealApiRig()
        {
            Real = new LSLSystemAPI(Host, null, 1, UUID.Random()) { RuntimeAccess = new StandInScript() };
            (ILSLSystemAPI api, Calls) = Forwarder.Create(Real, "llOwnerSay");
            H = new SchedulerHarness(configure: o =>
            {
                Func<Bindings.Vm> inner = o.VmFactory;
                o.VmFactory = () =>
                {
                    Bindings.Vm vm = inner();
                    LlBindings.Register(vm, script => new ScriptContext(script, api));
                    return vm;
                };
            });
        }

        public void Dispose() => H.Dispose();
    }

    private static string Fmt(object o) => o switch
    {
        null => "null",
        float f => f.ToString(CultureInfo.InvariantCulture),
        LSLList l => "[" + string.Join(",", l.Data.Select(Fmt)) + "]",
        _ => Convert.ToString(o, CultureInfo.InvariantCulture),
    };

    private static string[] Recorded(IEnumerable<(string Name, object[] Args)> calls) =>
        calls.Select(c => c.Name + "(" + string.Join("|", c.Args.Select(Fmt)) + ")").ToArray();

    private static string[] Says(IEnumerable<(string Name, object[] Args)> calls) =>
        calls.Where(c => c.Name == "llOwnerSay").Select(c => Fmt(c.Args[0])).ToArray();

    private static void RunToIdle(SchedulerHarness h, string source, bool lsl)
    {
        h.AddAndStart(source, lsl);
        for (int i = 0; i < 10; i++)
        {
            h.Pump();
            h.Advance(0.5);
        }
        Assert.Empty(h.FaultedScripts);
        Assert.Empty(h.ReportedErrors);
    }

    /// <summary>What LSL documents each function to answer for the script below, which the fake is told to say.</summary>
    private static void TeachTheFake(FakeLslApi fake)
    {
        fake.Returns("llStringLength", 5);
        fake.Returns("llToUpper", "ABC");
        fake.Returns("llGetSubString", "hello");
        fake.Returns("llDumpList2String", "a-b-c");
        fake.Returns("llGetListLength", 3);
        fake.Returns("llList2Integer", 5);
        fake.Returns("llAbs", 7);
        fake.Returns("llPow", 1024f);
        fake.Returns("llFloor", 2);
    }

    private const string LslPureCalls =
        "default { state_entry() {" +
        " llOwnerSay((string)llStringLength(\"hello\"));" +
        " llOwnerSay(llToUpper(\"abc\"));" +
        " llOwnerSay(llGetSubString(\"hello world\", 0, 4));" +
        " llOwnerSay(llDumpList2String([\"a\", \"b\", \"c\"], \"-\"));" +
        " llOwnerSay((string)llGetListLength([1, 2, 3]));" +
        " llOwnerSay((string)llList2Integer([4, 5, 6], 1));" +
        " llOwnerSay((string)llAbs(-7));" +
        " llOwnerSay((string)((integer)llPow(2.0, 10.0)));" +
        " llOwnerSay((string)llFloor(2.7));" +
        " } }";

    private static readonly string[] ExpectedSays = { "5", "ABC", "hello", "a-b-c", "3", "5", "7", "1024", "2" };

    [RequiresNativeFact]
    public void AnLslScriptGetsFromTheRealApiWhatTheFakeWasToldToAnswer()
    {
        using var fakeRig = new BindingRig();
        TeachTheFake(fakeRig.Fake);
        RunToIdle(fakeRig.H, LslPureCalls, lsl: true);
        var expected = fakeRig.Fake.Calls.Select(c => (c.Name, c.Args)).ToArray();

        using var rig = new RealApiRig();
        RunToIdle(rig.H, LslPureCalls, lsl: true);
        var actual = rig.Calls.Calls.Select(c => (c.Name, c.Args)).ToArray();

        Assert.Equal(ExpectedSays, Says(expected));
        Assert.Equal(ExpectedSays, Says(actual));
        Assert.Equal(Recorded(expected), Recorded(actual));
        Assert.Contains(actual, c => c.Name == "llStringLength");
        Assert.Contains(actual, c => c.Name == "llToUpper");
        Assert.Contains(actual, c => c.Name == "llDumpList2String");
    }

    private const string SluaPureCalls =
        "ll.OwnerSay(tostring(ll.StringLength('hello')))\n" +
        "ll.OwnerSay(ll.ToUpper('abc'))\n" +
        "ll.OwnerSay(ll.GetSubString('hello world', 1, 5))\n" +
        "ll.OwnerSay(tostring(ll.Abs(-7)))\n" +
        "ll.OwnerSay(tostring(math.floor(ll.Pow(2, 10))))\n" +
        "ll.OwnerSay(tostring(ll.Floor(2.7)))";

    [RequiresNativeFact]
    public void AnSLuaScriptGetsFromTheRealApiWhatTheFakeWasToldToAnswer()
    {
        using var fakeRig = new BindingRig();
        TeachTheFake(fakeRig.Fake);
        RunToIdle(fakeRig.H, SluaPureCalls, lsl: false);
        var expected = fakeRig.Fake.Calls.Select(c => (c.Name, c.Args)).ToArray();

        using var rig = new RealApiRig();
        RunToIdle(rig.H, SluaPureCalls, lsl: false);
        var actual = rig.Calls.Calls.Select(c => (c.Name, c.Args)).ToArray();

        Assert.Equal(new[] { "5", "ABC", "hello", "7", "1024", "2" }, Says(expected));
        Assert.Equal(Says(expected), Says(actual));
        Assert.Equal(Recorded(expected), Recorded(actual));
    }

    [RequiresNativeFact]
    public void TheRealApiNeededNothingFromTheHostForThesePureFunctions()
    {
        using var rig = new RealApiRig();
        RunToIdle(rig.H, LslPureCalls, lsl: true);

        Assert.Empty(rig.Host.Touched);
        Assert.IsType<LSLSystemAPI>(rig.Real);
        Assert.Null(rig.Real.Script); // no Phlox interpreter behind it
        Assert.Equal(ExpectedSays, Says(rig.Calls.Calls.Select(c => (c.Name, c.Args))));
    }
}
