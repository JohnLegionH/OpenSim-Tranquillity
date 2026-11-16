/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// An SLua script and an LSL script call functions of each category through the generated bindings; the fake ILSLSystemAPI
/// records the calls with their arguments and answers with set values. A return value reaches the script when the script
/// passes it on to a second call, so the recorded arguments of that call show what the script received.
/// </summary>
[Collection("native-library")]
public class LlEndToEndTests
{
    private static string Fmt(object o) => o switch
    {
        null => "null",
        float f => f.ToString(CultureInfo.InvariantCulture),
        Vector3 v => $"<{Fmt(v.X)},{Fmt(v.Y)},{Fmt(v.Z)}>",
        Quaternion q => $"<{Fmt(q.X)},{Fmt(q.Y)},{Fmt(q.Z)},{Fmt(q.W)}>",
        LSLList l => "[" + string.Join(",", l.Data.Select(Fmt)) + "]",
        _ => Convert.ToString(o, CultureInfo.InvariantCulture),
    };

    private static string[] Recorded(BindingRig rig) =>
        rig.Fake.Calls.Select(c => c.Name + "(" + string.Join("|", c.Args.Select(Fmt)) + ")").ToArray();

    /// <summary>Runs the script; delayed calls are waited out by moving the clock.</summary>
    private static string[] Run(BindingRig rig, string source, bool lsl)
    {
        SchedulerScript s = rig.H.AddAndStart(source, lsl);
        for (int i = 0; i < 30; i++)
        {
            rig.H.Pump();
            rig.H.Advance(2.1);
        }
        Assert.Empty(rig.H.FaultedScripts);
        Assert.Empty(rig.H.ReportedErrors);
        return Recorded(rig);
    }

    private static string[] Lsl(BindingRig rig, string body) =>
        Run(rig, "default { state_entry() { " + body + " } }", lsl: true);

    // ---- strings ----

    private static BindingRig StringRig()
    {
        var rig = new BindingRig();
        rig.Fake.Returns("llStringLength", 5);
        rig.Fake.Returns("llToUpper", "ABC");
        return rig;
    }

    private static readonly string[] StringCalls = { "llStringLength(hello)", "llToUpper(abc)", "llOwnerSay(5 ABC)" };

    [RequiresNativeFact]
    public void AnSLuaScriptCallsStringFunctionsAndGetsTheFakesAnswers()
    {
        using BindingRig rig = StringRig();
        Assert.Equal(StringCalls, Run(rig,
            "local n = ll.StringLength('hello')\nlocal u = ll.ToUpper('abc')\nll.OwnerSay(tostring(n) .. ' ' .. u)", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptCallsStringFunctionsAndGetsTheFakesAnswers()
    {
        using BindingRig rig = StringRig();
        Assert.Equal(StringCalls, Lsl(rig,
            "integer n = llStringLength(\"hello\"); string u = llToUpper(\"abc\"); llOwnerSay((string)n + \" \" + u);"));
    }

    // ---- lists ----

    private static BindingRig ListRig()
    {
        var rig = new BindingRig();
        rig.Fake.Returns("llGetListLength", 3);
        rig.Fake.Returns("llDumpList2String", "1,a");
        return rig;
    }

    private static readonly string[] ListCalls = { "llGetListLength([1,2.5,a])", "llDumpList2String([1,a]|,)", "llOwnerSay(3 1,a)" };

    [RequiresNativeFact]
    public void AnSLuaScriptCallsListFunctionsWithAMixedList()
    {
        using BindingRig rig = ListRig();
        Assert.Equal(ListCalls, Run(rig,
            "local c = ll.GetListLength({1, 2.5, 'a'})\nlocal s = ll.DumpList2String({1, 'a'}, ',')\nll.OwnerSay(tostring(c) .. ' ' .. s)", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptCallsListFunctionsWithAMixedList()
    {
        // llGetListLength is evaluated inside the compiled LSL script and never reaches the host, so the list goes
        // through llDumpList2String; the list comes back from a call so the compiler cannot fold it
        using BindingRig rig = ListRig();
        rig.Fake.Returns("llGetPrimitiveParams", new LSLList(new object[] { 1, 2.5f, "a" }));
        Assert.Equal(new[] { "llGetPrimitiveParams([5])", "llDumpList2String([1,2.5,a]|,)", "llOwnerSay(1,a)" }, Lsl(rig,
            "list l = llGetPrimitiveParams([5]); string s = llDumpList2String(l, \",\"); llOwnerSay(s);"));
    }

    // ---- maths ----

    private static BindingRig MathRig()
    {
        var rig = new BindingRig();
        rig.Fake.Returns("llAbs", 9);
        rig.Fake.Returns("llSqrt", 4f);
        rig.Fake.Returns("llVecMag", 5f);
        return rig;
    }

    private static readonly string[] MathCalls = { "llAbs(-9)", "llAbs(9)", "llSqrt(16)", "llSqrt(4)", "llVecMag(<3,4,0>)", "llSqrt(5)" };

    [RequiresNativeFact]
    public void AnSLuaScriptCallsMathFunctionsAndPassesTheirResultsOn()
    {
        using BindingRig rig = MathRig();
        Assert.Equal(MathCalls, Run(rig,
            "ll.Abs(ll.Abs(-9))\nll.Sqrt(ll.Sqrt(16.0))\nll.Sqrt(ll.VecMag(vector(3, 4, 0)))", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptCallsMathFunctionsAndPassesTheirResultsOn()
    {
        using BindingRig rig = MathRig();
        Assert.Equal(MathCalls, Lsl(rig,
            "llAbs(llAbs(-9)); llSqrt(llSqrt(16.0)); llSqrt(llVecMag(<3, 4, 0>));"));
    }

    // ---- prim params ----

    private static BindingRig PrimRig()
    {
        var rig = new BindingRig();
        rig.Fake.Returns("llGetPos", new Vector3(1, 2, 3));
        rig.Fake.Returns("llGetPrimitiveParams", new LSLList(new object[] { 7, "name" }));
        return rig;
    }

    private static readonly string[] PrimCalls =
    {
        "llGetPos()", "llSetPos(<1,2,3>)", "llSetPrimitiveParams([27,nm])", "llGetPrimitiveParams([27])", "llDumpList2String([7,name]|;)",
    };

    [RequiresNativeFact]
    public void AnSLuaScriptReadsAndSetsPrimParamsAndAVectorComesBack()
    {
        using BindingRig rig = PrimRig();
        Assert.Equal(PrimCalls, Run(rig,
            "ll.SetPos(ll.GetPos())\nll.SetPrimitiveParams({27, 'nm'})\nll.DumpList2String(ll.GetPrimitiveParams({27}), ';')", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptReadsAndSetsPrimParamsAndAVectorComesBack()
    {
        using BindingRig rig = PrimRig();
        Assert.Equal(PrimCalls, Lsl(rig,
            "llSetPos(llGetPos()); llSetPrimitiveParams([27, \"nm\"]); llDumpList2String(llGetPrimitiveParams([27]), \";\");"));
    }

    // ---- communication ----

    private static readonly string[] CommunicationCalls =
    {
        "llSay(0|hi)", "llRegionSayTo(6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11|5|there)", "llMessageLinked(-1|7|msg|k1)",
    };

    [RequiresNativeFact]
    public void AnSLuaScriptSpeaksAndSendsMessages()
    {
        using var rig = new BindingRig();
        Assert.Equal(CommunicationCalls, Run(rig,
            "ll.Say(0, 'hi')\nll.RegionSayTo('6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11', 5, 'there')\nll.MessageLinked(-1, 7, 'msg', 'k1')", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptSpeaksAndSendsMessages()
    {
        using var rig = new BindingRig();
        Assert.Equal(CommunicationCalls, Lsl(rig,
            "llSay(0, \"hi\"); llRegionSayTo((key)\"6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11\", 5, \"there\"); llMessageLinked(-1, 7, \"msg\", (key)\"k1\");"));
    }

    // ---- time ----

    private static BindingRig TimeRig()
    {
        var rig = new BindingRig();
        rig.Fake.Returns("llGetUnixTime", 1_700_000_000);
        rig.Fake.Returns("llGetTimestamp", "2026-01-02T03:04:05.000000Z");
        rig.Fake.Returns("llGetTime", 12.5f);
        return rig;
    }

    private static readonly string[] TimeCalls =
    {
        "llGetUnixTime()", "llGetTimestamp()", "llGetTime()", "llOwnerSay(1700000000 2026-01-02T03:04:05.000000Z)", "llSqrt(12.5)",
    };

    [RequiresNativeFact]
    public void AnSLuaScriptReadsTheTime()
    {
        using BindingRig rig = TimeRig();
        Assert.Equal(TimeCalls, Run(rig,
            "local u = ll.GetUnixTime()\nlocal t = ll.GetTimestamp()\nlocal f = ll.GetTime()\nll.OwnerSay(tostring(u) .. ' ' .. t)\nll.Sqrt(f)", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptReadsTheTime()
    {
        using BindingRig rig = TimeRig();
        Assert.Equal(TimeCalls, Lsl(rig,
            "integer u = llGetUnixTime(); string t = llGetTimestamp(); float f = llGetTime(); llOwnerSay((string)u + \" \" + t); llSqrt(f);"));
    }

    // ---- inventory ----

    private static BindingRig InventoryRig()
    {
        var rig = new BindingRig();
        rig.Fake.Returns("llGetInventoryNumber", 2);
        rig.Fake.Returns("llGetInventoryName", "Box");
        return rig;
    }

    private static readonly string[] InventoryCalls =
    {
        "llGetInventoryNumber(1)", "llGetInventoryName(1|1)", "llGiveInventory(6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11|Box)",
    };

    [RequiresNativeFact]
    public void AnSLuaScriptCountsNamesAndGivesInventory()
    {
        using BindingRig rig = InventoryRig();
        Assert.Equal(InventoryCalls, Run(rig,
            "local n = ll.GetInventoryNumber(1)\nlocal name = ll.GetInventoryName(1, n)\nll.GiveInventory('6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11', name)", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptCountsNamesAndGivesInventory()
    {
        using BindingRig rig = InventoryRig();
        Assert.Equal(InventoryCalls, Lsl(rig,
            "integer n = llGetInventoryNumber(1); string name = llGetInventoryName(1, n - 1); llGiveInventory((key)\"6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11\", name);"));
    }

    // ---- a failing call ----

    [RequiresNativeFact]
    public void AWrongArgumentFromAnSLuaScriptIsAScriptErrorAndTheApiIsNotCalled()
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart("ll.Say('zero', 5)");
        for (int i = 0; i < 5; i++) { rig.H.Pump(); rig.H.Advance(1); }
        Assert.Equal(0, rig.CallsOf("llSay"));
        Assert.NotEmpty(rig.H.FaultedScripts);
    }
}
