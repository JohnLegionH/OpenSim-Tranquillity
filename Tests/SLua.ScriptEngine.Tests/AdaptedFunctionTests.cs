/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Runtime functions whose signature differs from ILSLSystemAPI's are bound through an adapter that converts between the
/// Second Life runtime's signature (its definitions, secondlife/lsl-definitions) and Phlox's. A runtime function Phlox does
/// not implement, or implements as something else, is a named Phlox gap and answers a script with "not available".
/// </summary>
[Collection("native-library")]
public class AdaptedFunctionTests
{
    private const string AgentKey = "6b3f1d52-8a47-4e0b-9c61-2f5d7e8a9b10";

    private static string KeyOf(string name) =>
        new UUID(new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)))).ToString();

    private static readonly string[] Adapted =
    {
        "llManageEstateAccess", "llOpenFloater", "llRequestAgentData", "llRequestDisplayName", "llRequestUsername",
        "llSetGroundTexture", "llSetLinkGLTFOverrides",
    };

    private static readonly string[] Gaps =
    {
        "llClearExperience", "llClearExperiencePermissions", "llFindNotecardTextSync", "llGetExperienceList", "llSetExperienceKey",
    };

    /// <summary>The scheduler harness with the generated bindings, a fake API and a service lane.</summary>
    internal sealed class LaneRig : IDisposable
    {
        public readonly ILSLSystemAPI Api;
        public readonly FakeLslApi Fake;
        public readonly SchedulerHarness H;
        public readonly ServiceLane Lane;
        private readonly ConcurrentDictionary<long, UUID> m_items = new();

        public LaneRig()
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
            Lane = new ServiceLane(H.Scheduler, "auto", 4, 35000, 30000);
        }

        public string[] Run(string source, bool lsl = false, int expectedMarks = 1)
        {
            SchedulerScript s = H.AddAndStart(source, lsl);
            SchedulerHarness.WaitFor(() => { H.Pump(); return H.MarksOf(s).Length >= expectedMarks; });
            return H.MarksOf(s);
        }

        public string[] Calls() =>
            Fake.Calls.Select(c => c.Name + "(" + string.Join("|", c.Args.Select(a => a is LSLList l ? "[" + string.Join(",", l.Data) + "]" : Convert.ToString(a))) + ")").ToArray();

        public void Dispose()
        {
            Lane.Dispose();
            H.Dispose();
        }
    }

    // ---- the three requests that answer with a query key ----

    [RequiresNativeTheory]
    [InlineData("RequestAgentData", "llRequestAgentData", "'" + AgentKey + "', 1", AgentKey + "|1")]
    [InlineData("RequestDisplayName", "llRequestDisplayName", "'" + AgentKey + "'", AgentKey)]
    [InlineData("RequestUsername", "llRequestUsername", "'" + AgentKey + "'", AgentKey)]
    public void ARequestReturnsTheQueryKeyAndTheRequestReachesPhloxsService(string lua, string api, string arguments, string expectedArguments)
    {
        using var rig = new LaneRig();
        // Phlox answers these through SysReturn (ReturnQueryKey); the dataserver event follows later.
        rig.Fake.Runs(api, a => { SyscallContext.Current.SetResult(KeyOf("query"), 0); return null; });

        string[] marks = rig.Run("local k = ll." + lua + "(" + arguments + ")\nossl.Mark('k', tostring(k))");

        Assert.Equal(new[] { "k:" + KeyOf("query") }, marks);
        Assert.Equal(new[] { api + "(" + expectedArguments + ")" }, rig.Calls());
    }

    [RequiresNativeFact]
    public void AnLslScriptGetsTheQueryKeyOfARequestToo()
    {
        using var rig = new LaneRig();
        rig.Fake.Runs("llRequestUsername", a => { SyscallContext.Current.SetResult(KeyOf("lsl-query"), 0); return null; });

        rig.Run("default { state_entry() { key k = llRequestUsername(\"" + AgentKey + "\"); llStringLength((string)k); } }", lsl: true, expectedMarks: 0);
        SchedulerHarness.WaitFor(() => { rig.H.Pump(); return rig.Fake.Calls.Any(c => c.Name == "llStringLength"); });

        Assert.Equal(new[] { "llRequestUsername(" + AgentKey + ")", "llStringLength(" + KeyOf("lsl-query") + ")" }, rig.Calls());
    }

    // ---- llManageEstateAccess: the runtime's action numbers, Phlox's numbers and a result ----

    [RequiresNativeTheory]
    [InlineData(4, 0)]
    [InlineData(8, 1)]
    [InlineData(16, 2)]
    [InlineData(32, 3)]
    [InlineData(64, 4)]
    [InlineData(128, 5)]
    [InlineData(0, -1)]
    [InlineData(2, -1)]
    public void ManageEstateAccessTranslatesTheRuntimesActionToPhloxsAndGivesTheResult(int runtimeAction, int phloxAction)
    {
        using var rig = new LaneRig();
        rig.Fake.Runs("llManageEstateAccess", a => { SyscallContext.Current.SetResult(1, 0); return null; });

        string[] marks = rig.Run("local r = ll.ManageEstateAccess(" + runtimeAction + ", '" + AgentKey + "')\nossl.Mark('r', (r == 1 or r == true) and 'yes' or 'no')");

        Assert.Equal(new[] { "r:yes" }, marks);
        Assert.Equal(new[] { "llManageEstateAccess(" + phloxAction + "|" + AgentKey + ")" }, rig.Calls());
    }

    // ---- the others ----

    [RequiresNativeFact]
    public void SetLinkGltfOverridesReturnsNothingWhateverPhloxAnswers()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llSetLinkGLTFOverrides", 5);

        string[] marks = rig.Run("ossl.Mark('n', select('#', ll.SetLinkGLTFOverrides(1, 2, {'metallic_factor', '0.5'})))");

        Assert.Equal(new[] { "n:0" }, marks);
        Assert.Equal(new[] { "llSetLinkGLTFOverrides(1|2|[metallic_factor,0.5])" }, rig.Calls());
    }

    [RequiresNativeFact]
    public void OpenFloaterReachesPhloxAndReturnsZero()
    {
        using var rig = new LaneRig();

        string[] marks = rig.Run("ossl.Mark('r', ll.OpenFloater('about', 'https://example.org/page', {'width', 300}))");

        Assert.Equal(new[] { "r:0" }, marks);
        Assert.Equal(new[] { "llOpenFloater(about|https://example.org/page|[width,300])" }, rig.Calls());
    }

    [RequiresNativeFact]
    public void SetGroundTextureSetsEachDetailTextureThroughPhloxAndReturnsZero()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llSetGroundTexture", 9);

        // 0 and 2 are TERRAIN_DETAIL_1 and _3 (one texture each); 4 is TERRAIN_HEIGHT_RANGE_SW (two numbers), which Phlox has
        // no call for and is skipped without disturbing the pairs after it.
        string[] marks = rig.Run("ossl.Mark('r', ll.SetGroundTexture({0, 'tex-a', 4, 10.0, 50.0, 2, 'tex-c', 12, 0.5}))");

        Assert.Equal(new[] { "r:0" }, marks);
        Assert.Equal(new[] { "llSetGroundTexture(tex-a|0)", "llSetGroundTexture(tex-c|2)" }, rig.Calls());
    }

    // ---- functions Phlox gained as Second Life defines them: bound, no longer refused ----

    private static readonly string[] BuiltInPhlox =
    {
        "llFindNotecardTextCount", "llGetAttachedListFiltered", "llGetGameControlMode", "llGetGameControlModeAxes",
        "llGetGameControlModeButtons", "llGiveAgentInventory", "llMapBeacon", "llRemoteDataSetRegion", "llRemoteLoadScript",
        "llSetParcelForSale", "llTransferOwnership",
    };

    [Fact]
    public void TheFunctionsPhloxGainedAreBoundAndAreNoGapAndNoUnavailableName()
    {
        Assert.All(BuiltInPhlox, n => Assert.Contains(LlBindings.All, f => f.ApiName == n));
        Assert.All(BuiltInPhlox, n => Assert.DoesNotContain(n, LlBindings.Unavailable));
        Assert.All(BuiltInPhlox, n => Assert.DoesNotContain(LlCoverage.PhloxGaps, r => r.StartsWith(n + ":")));
        Assert.All(BuiltInPhlox, n => Assert.Contains(n, LlServed.Served));
    }

    // One of each kind, reaching Phlox's interface from a Lua script: a function on existing services with a list argument and a
    // list result (llGetAttachedListFiltered), one with an integer result code and four arguments (llGiveAgentInventory), one with
    // a boolean first argument (llSetParcelForSale), and the ones that answer "no game-control data" or do nothing here.

    [RequiresNativeFact]
    public void GetAttachedListFilteredPassesTheOptionsAndReturnsTheKeysAsATable()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llGetAttachedListFiltered", new LSLList(new object[] { KeyOf("a"), KeyOf("b") }));

        string[] marks = rig.Run("local t = ll.GetAttachedListFiltered('" + AgentKey + "', {1, 1, 1, 2})\nossl.Mark('n', #t)\nossl.Mark('first', tostring(t[1]))", expectedMarks: 2);

        Assert.Equal(new[] { "n:2", "first:" + KeyOf("a") }, marks);
        Assert.Equal(new[] { "llGetAttachedListFiltered(" + AgentKey + "|[1,1,1,2])" }, rig.Calls());
    }

    [RequiresNativeFact]
    public void GiveAgentInventoryPassesItsFourArgumentsAndReturnsThePhloxCode()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llGiveAgentInventory", -4);

        string[] marks = rig.Run("ossl.Mark('r', ll.GiveAgentInventory('" + AgentKey + "', 'gifts', {'a', 'b'}, {0, 'x|y'}))");

        Assert.Equal(new[] { "r:-4" }, marks);
        Assert.Equal(new[] { "llGiveAgentInventory(" + AgentKey + "|gifts|[a,b]|[0,x|y])" }, rig.Calls());
    }

    [RequiresNativeFact]
    public void SetParcelForSaleTakesABooleanAndTheOptionsAndReturnsThePhloxCode()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llSetParcelForSale", 4);

        string[] marks = rig.Run("ossl.Mark('r', ll.SetParcelForSale(true, {1, 0}))");

        Assert.Equal(new[] { "r:4" }, marks);
        Assert.Equal(new[] { "llSetParcelForSale(1|[1,0])" }, rig.Calls());
    }

    [RequiresNativeFact]
    public void TheGameControlFunctionsAndTheMapBeaconReachPhlox()
    {
        using var rig = new LaneRig();
        rig.Fake.Returns("llGetGameControlMode", -1);

        string[] marks = rig.Run("ossl.Mark('m', ll.GetGameControlMode('" + AgentKey + "'))\nossl.Mark('a', #ll.GetGameControlModeAxes('" + AgentKey + "'))\nossl.Mark('b', ll.GetGameControlModeButtons('" + AgentKey + "'))\nll.MapBeacon('Region', vector(1, 2, 3), {})\nossl.Mark('done', 'yes')", expectedMarks: 4);

        Assert.Equal(new[] { "m:-1", "a:0", "b:0", "done:yes" }, marks);
        Assert.Equal(new[] { "llGetGameControlMode(" + AgentKey + ")", "llGetGameControlModeAxes(" + AgentKey + ")", "llGetGameControlModeButtons(" + AgentKey + ")", "llMapBeacon(Region|<1, 2, 3>|[])" }, rig.Calls());
    }

    // ---- the Phlox gaps stay on the not-available error ----

    [RequiresNativeFact]
    public void EveryPhloxGapAnswersAScriptWithNotAvailableAndEveryAdaptedFunctionDoesNot()
    {
        using var rig = new EngineRig(productionFunctions: true);
        string gaps = string.Join(",", Gaps.Select(n => "'" + n.Substring(2) + "'"));
        string adapted = string.Join(",", Adapted.Select(n => "'" + n.Substring(2) + "'"));
        UUID item = rig.Rez(Slua(
            "local refused = 0 for _, n in ipairs({" + gaps + "}) do local ok, err = pcall(ll[n]) if not ok and string.find(tostring(err), 'is not available on this engine yet', 1, true) then refused = refused + 1 end end\n" +
            "local open = 0 for _, n in ipairs({" + adapted + "}) do local ok, err = pcall(ll[n]) if not string.find(tostring(err), 'is not available on this engine yet', 1, true) then open = open + 1 end end\n" +
            "ossl.Mark('refused=' .. refused) ossl.Mark('open=' .. open)"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length >= 2));
        Assert.Contains("refused=" + Gaps.Length, rig.MarksOf(item));
        Assert.Contains("open=" + Adapted.Length, rig.MarksOf(item));
    }

    // ---- the table: every runtime function is accounted for ----

    [Fact]
    public void TheAdaptedFunctionsAreListedWithTheirSourceAndAreBound()
    {
        Assert.Equal(Adapted, LlCoverage.Adapted.Select(r => r.Substring(0, r.IndexOf(':'))).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(LlCoverage.Adapted, r => Assert.True(r.Length - r.IndexOf(':') > 20, "no source named: " + r));
        Assert.All(Adapted, n => Assert.Contains(LlBindings.All, f => f.ApiName == n));
    }

    [Fact]
    public void ThePhloxGapsAreListedWithAReason()
    {
        Assert.Equal(Gaps, LlCoverage.PhloxGaps.Select(r => r.Substring(0, r.IndexOf(':'))).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(LlCoverage.PhloxGaps, r => Assert.True(r.Length - r.IndexOf(':') > 20, "no reason named: " + r));
        Assert.All(Gaps, n => Assert.DoesNotContain(LlBindings.All, f => f.ApiName == n));
        Assert.All(Gaps, n => Assert.Contains(n, LlBindings.Unavailable));
    }

    [Fact]
    public void EveryRuntimeFunctionIsBoundOrAPhloxGapAndNoneIsBoth()
    {
        string[] names = File.ReadAllLines(LlBindingGeneratorTests.BuiltinsPath())
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\w+\s+(ll\w+)\(")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();
        string[] bound = LlBindings.All.Select(f => f.ApiName).ToArray();
        string[] gaps = LlCoverage.PhloxGaps.Select(r => r.Substring(0, r.IndexOf(':'))).ToArray();

        Assert.Equal(529, names.Length);
        Assert.Empty(bound.Intersect(gaps));
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), bound.Concat(gaps).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(gaps.OrderBy(n => n, StringComparer.Ordinal),
            LlCoverage.MissingFromSeam.Concat(LlCoverage.SignatureMismatch.Select(r => r.Substring(0, r.IndexOf(':')))).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryRuntimeFunctionRunsOnTheRealApiWaitsOnANamedHostMemberOrIsANamedPhloxGap()
    {
        string[] names = File.ReadAllLines(LlBindingGeneratorTests.BuiltinsPath())
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\w+\s+(ll\w+)\(")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();
        string[] waiting = LlServed.NotServed.Select(r => r.Substring(0, r.IndexOf(':'))).ToArray();
        string[] gaps = LlCoverage.PhloxGaps.Select(r => r.Substring(0, r.IndexOf(':'))).ToArray();

        Assert.Empty(LlServed.Served.Intersect(waiting));
        Assert.Empty(LlServed.Served.Intersect(gaps));
        Assert.Empty(waiting.Intersect(gaps));
        Assert.All(LlServed.NotServed, r => Assert.True(r.IndexOf(':') > 0 && r.Length > r.IndexOf(':') + 2, "no host member named: " + r));
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), LlServed.Served.Concat(waiting).Concat(gaps).OrderBy(n => n, StringComparer.Ordinal));
    }
}
