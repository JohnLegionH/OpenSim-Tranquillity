/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Reflection;
using InWorldz.Phlox.Glue;
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
/// Which bound functions run on the real LSLSystemAPI: every one whose host dependencies the engine's host satisfies. The
/// others stay on the fallback, each with the host members it needs and the engine does not provide. The split is read from
/// the Phlox source by the generator (a function's own use of a host member and that of every method it calls), and
/// checked here by running the served functions against the real API with a tripwire on those members.
/// </summary>
[Collection("native-library")]
public class ServedByRealApiTests
{
    /// <summary>The host members the engine does not provide yet; a function that reaches one is not served.</summary>
    private static readonly string[] Lacking =
    {
        "Ossl", "ListenManager", "StateManager", "AsyncCommands", "NotecardCache", "ScriptEngine", "PostDataserverToPrim",
        "SendToWorldComm", "ExperienceGrantRestored", "ExperienceCannotRun", "SetMinEventDelay", "GetEventQueueFreeSpacePercentage",
    };

    private static string NameOf(string notServedEntry) => notServedEntry.Substring(0, notServedEntry.IndexOf(':'));

    [Fact]
    public void EveryBoundFunctionIsServedOrListedWithTheHostMembersItNeeds()
    {
        string[] bound = LlBindings.All.Select(f => f.ApiName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] served = LlServed.Served;
        string[] notServed = LlServed.NotServed.Select(NameOf).ToArray();

        Assert.Equal(bound, served.Concat(notServed).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Empty(served.Intersect(notServed));
        Assert.Equal(served.Length, served.Distinct().Count());
        Assert.True(served.Length > 400, "only " + served.Length + " functions are served");
        Assert.True(notServed.Length > 20, "only " + notServed.Length + " functions are not served");
    }

    [Fact]
    public void EachFunctionThatIsNotServedNamesHostMembersTheEngineLacks()
    {
        Assert.NotEmpty(LlServed.NotServed);
        foreach (string entry in LlServed.NotServed)
        {
            string[] needs = entry.Substring(entry.IndexOf(':') + 1).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(needs);
            Assert.All(needs, n => Assert.Contains(n, Lacking));
        }
    }

    [Fact]
    public void FunctionsThatNeedAListenManagerOrWorldCommunicationAreNotServedAndPlainOnesAre()
    {
        string[] served = LlServed.Served;
        string[] notServed = LlServed.NotServed.Select(NameOf).ToArray();

        Assert.All(new[] { "llListen", "llListenRemove", "llSay", "llMessageLinked", "llMinEventDelay", "llSensor" },
            n => Assert.Contains(n, notServed));
        Assert.All(new[] { "llGetPos", "llSetPos", "llSetTimerEvent", "llResetScript", "llStringLength", "llName2Key", "llInstantMessage" },
            n => Assert.Contains(n, served));
    }

    // ---- the router ----

    [Fact]
    public void TheRouterSendsAServedFunctionToTheRealApiAndTheOthersToTheFallback()
    {
        (ILSLSystemAPI real, FakeLslApi realFake) = FakeLslApi.Create();
        (ILSLSystemAPI fallback, FakeLslApi fallbackFake) = FakeLslApi.Create();
        ILSLSystemAPI router = ApiRouter.Create(real, fallback);
        realFake.Returns("llStringLength", 11);
        fallbackFake.Returns("llStringLength", 22);
        realFake.Returns("llListen", 33);
        fallbackFake.Returns("llListen", 44);

        Assert.Equal(11, router.llStringLength("x"));
        Assert.Equal(44, router.llListen(0, "", "", ""));

        Assert.Equal(new[] { "llStringLength" }, realFake.Calls.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "llListen" }, fallbackFake.Calls.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void TheQuestionWhetherACallNeedsTheServiceGoesWhereTheCallWillGo()
    {
        (ILSLSystemAPI real, FakeLslApi realFake) = FakeLslApi.Create();
        (ILSLSystemAPI fallback, FakeLslApi fallbackFake) = FakeLslApi.Create();
        ILSLSystemAPI router = ApiRouter.Create(real, fallback);
        realFake.Returns("NeedsService", true);
        fallbackFake.Returns("NeedsService", false);

        Assert.True(router.NeedsService("llName2Key", new object[] { "x" }));
        Assert.False(router.NeedsService("llListen", new object[0]));
    }

    [RequiresNativeFact]
    public void AScriptsServedAndUnservedCallsRunOnTheRealApiAndTheFallbackRespectively()
    {
        (ILSLSystemAPI fallback, FakeLslApi fallbackFake) = FakeLslApi.Create();
        fallbackFake.Returns("llListen", 7);
        using var rig = new EngineApiRig(fallback, ("MinTimerInterval", "0.5"));
        SchedulerScript s = rig.H.AddAndStart("ll.SetTimerEvent(2.0)\nlocal h = ll.Listen(0, '', '', '')\nossl.Mark('handle', tostring(h))\nossl.Count()");

        // The first call of the real API in a process compiles code and can overrun the script's run window, which the runtime
        // punishes with a sleep; moving the clock past it is not what is under test.
        for (int i = 0; i < 30 && rig.H.CountOf(s) == 0; i++) rig.H.Advance(1.0);

        Assert.True(rig.H.CountOf(s) == 1, "state " + s.State + " faulted=[" + string.Join(",", rig.H.FaultedScripts.Select(f => f.FaultMessage)) + "] errors=[" + string.Join(",", rig.H.ReportedErrors) + "] fallback=[" + string.Join(",", fallbackFake.Calls.Select(c => c.Name)) + "] backend=[" + string.Join(",", rig.BackendCalls) + "]");
        Assert.Contains("SetTimer 2", rig.BackendCalls.ToArray()); // the real API reached the engine's host
        Assert.DoesNotContain(fallbackFake.Calls, c => c.Name == "llSetTimerEvent");
        Assert.Contains(fallbackFake.Calls, c => c.Name == "llListen"); // the fallback served the listen
        Assert.Equal(new[] { "handle:7" }, rig.H.MarksOf(s));
    }

    // ---- the tripwire ----

    /// <summary>The engine's host with a note of every member it does not provide that a call reaches for.</summary>
    private sealed class TripwireHost : IScriptHostServices
    {
        private readonly IScriptHostServices m_inner;
        public readonly ConcurrentQueue<string> Touched = new();
        public string Current = "";

        public TripwireHost(IScriptHostServices inner) { m_inner = inner; }

        private T Trip<T>(string member, T value) { Touched.Enqueue(Current + " -> " + member); return value; }
        private void Trip(string member) => Touched.Enqueue(Current + " -> " + member);

        public string Name => m_inner.Name;
        public Scene World => m_inner.World;
        public IConfigSource ConfigSource => m_inner.ConfigSource;
        public IScriptModule ScriptModule => m_inner.ScriptModule;
        public IScriptEngine ScriptEngine => Trip("ScriptEngine", m_inner.ScriptEngine);
        public ServiceCallDeferralMode ServiceCallDeferral => m_inner.ServiceCallDeferral;
        public bool AllowGodFunctions => m_inner.AllowGodFunctions;
        public bool AutomaticLinkPermission => m_inner.AutomaticLinkPermission;
        public float MinTimerInterval => m_inner.MinTimerInterval;
        public bool ResetThrottle => m_inner.ResetThrottle;
        public bool ChatThrottle => m_inner.ChatThrottle;
        public bool BotThrottle => m_inner.BotThrottle;
        public bool PhysicsThrottle => m_inner.PhysicsThrottle;
        public bool LinkMessageThrottle => m_inner.LinkMessageThrottle;
        public bool NotecardThrottle => m_inner.NotecardThrottle;
        public bool FormatStringThrottle => m_inner.FormatStringThrottle;
        public bool HttpInFlightThrottle => m_inner.HttpInFlightThrottle;
        public bool NotecardHalcyonTiming => m_inner.NotecardHalcyonTiming;
        public bool NotecardCacheEnabled => m_inner.NotecardCacheEnabled;
        public PhloxNotecardCache NotecardCache => Trip("NotecardCache", m_inner.NotecardCache);
        public int PhysicsFrameTimeAvg => m_inner.PhysicsFrameTimeAvg;
        public IScriptOsslChecks Ossl => Trip("Ossl", m_inner.Ossl);
        public IScriptListenService ListenManager => Trip("ListenManager", m_inner.ListenManager);
        public IScriptStateTracker StateManager => Trip("StateManager", m_inner.StateManager);
        public AsyncCommandManager AsyncCommands => Trip("AsyncCommands", m_inner.AsyncCommands);

        public void SysReturn(UUID itemId, object retValue, int delay) => m_inner.SysReturn(itemId, retValue, delay);
        public void SysReturnSequenced(UUID itemId, object retValue, int delay, int seq) => m_inner.SysReturnSequenced(itemId, retValue, delay, seq);
        public bool PostScriptEvent(UUID itemID, EventParams parms) => m_inner.PostScriptEvent(itemID, parms);
        public bool PostObjectEvent(uint localID, EventParams parms) => m_inner.PostObjectEvent(localID, parms);
        public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value) => m_inner.PostObjectLinksetDataEvent(localID, action, name, value);
        public int PostDataserverToPrim(SceneObjectPart part, UUID skip, UUID asker, string queryId, string data) => Trip("PostDataserverToPrim", m_inner.PostDataserverToPrim(part, skip, asker, queryId, data));
        public bool HasScript(UUID itemID, out bool running) => m_inner.HasScript(itemID, out running);
        public void SetTimerEvent(uint localID, UUID itemID, float sec) => m_inner.SetTimerEvent(localID, itemID, sec);
        public void SetMinEventDelay(UUID itemID, float seconds) { Trip("SetMinEventDelay"); m_inner.SetMinEventDelay(itemID, seconds); }
        public void SetItemRunningFlag(uint localId, UUID itemId, bool running) => m_inner.SetItemRunningFlag(localId, itemId, running);
        public void ResetScript(UUID itemID) => m_inner.ResetScript(itemID);
        public void ApiResetScript(UUID itemID) => m_inner.ApiResetScript(itemID);
        public void RequestParcelCheck(UUID itemId) => m_inner.RequestParcelCheck(itemId);
        public void SendToWorldComm(Action<IWorldComm> send) { Trip("SendToWorldComm"); m_inner.SendToWorldComm(send); }
        public float GetEventQueueFreeSpacePercentage(UUID itemID) => Trip("GetEventQueueFreeSpacePercentage", m_inner.GetEventQueueFreeSpacePercentage(itemID));
        public void ExperienceGrantRestored() { Trip("ExperienceGrantRestored"); m_inner.ExperienceGrantRestored(); }
        public void ExperienceCannotRun(UUID experience, int code) { Trip("ExperienceCannotRun"); m_inner.ExperienceCannotRun(experience, code); }
    }

    private static object ArgFor(ParameterInfo p)
    {
        Type t = p.ParameterType;
        if (t == typeof(string)) return p.Name != null && p.Name.Contains("key", StringComparison.OrdinalIgnoreCase) ? UUID.Zero.ToString() : "";
        if (t == typeof(int)) return 0;
        if (t == typeof(float)) return 0f;
        if (t == typeof(Vector3)) return Vector3.Zero;
        if (t == typeof(Quaternion)) return Quaternion.Identity;
        if (t == typeof(InWorldz.Phlox.Types.LSLList)) return new InWorldz.Phlox.Types.LSLList();
        return null;
    }

    [RequiresNativeFact]
    public void NoServedFunctionReachesForAHostMemberTheEngineLacks()
    {
        using var rig = new EngineApiRig();
        var tripwire = new TripwireHost(rig.Host);
        var api = new LSLSystemAPI(tripwire, rig.Object.RootPart, rig.Object.RootPart.LocalId, UUID.Random())
        {
            RuntimeAccess = new EngineScriptAccess(new SchedulerScript(0, 0, new byte[0], ScriptLanguage.Slua)),
        };
        MethodInfo[] methods = typeof(ILSLSystemAPI).GetMethods().Where(m => LlServed.Served.Contains(m.Name)).ToArray();
        Assert.True(methods.Length > 400);

        var ran = new List<string>();
        foreach (MethodInfo m in methods)
        {
            tripwire.Current = m.Name;
            object[] args = m.GetParameters().Select(ArgFor).ToArray();
            var ctx = new SyscallContext(UUID.Random(), SyscallContext.NextSeq());
            Task call = Task.Run(() =>
            {
                ctx.Enter(); // the call's delay goes to its context, as when the engine runs it
                try { m.Invoke(api, args); }
                catch (TargetInvocationException) { } // a default argument can be refused; only what the call reached for matters
                finally { SyscallContext.Exit(); }
            });
            Assert.True(call.Wait(TimeSpan.FromSeconds(20)), m.Name + " did not return");
            ran.Add(m.Name);
        }

        Assert.Empty(tripwire.Touched);
        Assert.Equal(methods.Length, ran.Count);
    }
}
