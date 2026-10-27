/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The os, iw and bot functions: the table built from the LSL signatures of Phlox's compiler table (Defaults), the list the
/// library's LSL compiler is given, and the choice among the signatures of one name.
/// </summary>
public class OsBindingTests
{
    private static bool IsHostFunction(string name) =>
        (name.StartsWith("os") || name.StartsWith("iw") || name.StartsWith("bot")) && name.Length > 2;

    private static string Type(VarType t) => t.ToString().ToLowerInvariant();

    private static string[] CompilerTable() =>
        Defaults.AllMethods.Where(s => IsHostFunction(s.FunctionName))
            .Select(s => $"{Type(s.ReturnType)} {s.FunctionName}({string.Join(", ", s.ParamTypes.Select(Type))})")
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static string[] BoundSignatures() =>
        OsBindings.All.SelectMany(f => f.Overloads)
            .Select(o => $"{o.ReturnType} {o.ApiName}({string.Join(", ", o.ParamTypes)})")
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void EverySignatureOfPhloxsCompilerTableIsBound()
    {
        string[] table = CompilerTable();
        Assert.Equal(433, table.Length);
        Assert.Equal(table, BoundSignatures());
    }

    [Fact]
    public void ThereIsOneFunctionPerNameInOneOfThreeTables()
    {
        Assert.Equal(402, OsBindings.All.Length);
        Assert.Equal(259, OsBindings.All.Count(f => f.Table == "ossl"));
        Assert.Equal(85, OsBindings.All.Count(f => f.Table == "iw"));
        Assert.Equal(58, OsBindings.All.Count(f => f.Table == "bot"));
        OsFunction sha = Assert.Single(OsBindings.All, f => f.ApiName == "osSHA256");
        Assert.Equal("SHA256", sha.Name);
        Assert.Equal("ossl", sha.Table);
        OsFunction bot = Assert.Single(OsBindings.All, f => f.ApiName == "botCreateBot");
        Assert.Equal("CreateBot", bot.Name);
        Assert.Equal("bot", bot.Table);
        Assert.Equal("iw", Assert.Single(OsBindings.All, f => f.ApiName == "iwFormatTime").Table);
    }

    [Fact]
    public void TheHostListHasOneLinePerSignatureInTheScriptsTypesWithTheParameterNames()
    {
        string[] lines = OsBindings.HostList.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(433, lines.Length);
        Assert.Contains("string osSHA256( string input )", lines);
        Assert.Contains("string osGetGridName()", lines);
        // key stays key (the C# interface cannot tell it from string) and the names are Phlox's
        Assert.Contains("key botCreateBot( string firstName, string lastName, string outfitName, vector startPosition, integer options )", lines);
        // both signatures of an overloaded name are listed
        Assert.Contains("void osNpcSay( key npc, string message )", lines);
        Assert.Contains("void osNpcSay( key npc, integer channel, string message )", lines);
        Assert.Equal(lines.Length, lines.Distinct().Count());
    }

    // ---- the choice among the signatures of one name (SLua: at call time) ----

    private static OsFunction Function(string api, params (string Ret, string[] Types)[] signatures) =>
        new OsFunction("ossl", api.Substring(2), api, signatures
            .Select(s => new BoundFunction(api.Substring(2), api, FunctionClass.NonBlocking, s.Ret, s.Types, (c, a) => null)).ToArray());

    private static string Chosen(OsFunction f, params Value[] args)
    {
        BoundFunction pick = OsBindings.Pick(f, args);
        Assert.NotNull(pick);
        return $"{pick.ReturnType} ({string.Join(", ", pick.ParamTypes)})";
    }

    [Fact]
    public void ASignatureOfTheCallsArgumentCountIsChosen()
    {
        OsFunction f = Function("osNpcSay", ("void", new[] { "key", "string" }), ("void", new[] { "key", "integer", "string" }));
        Assert.Equal("void (key, string)", Chosen(f, Value.Key("k"), Value.Str("hi")));
        Assert.Equal("void (key, integer, string)", Chosen(f, Value.Key("k"), Value.Int(0), Value.Str("hi")));
    }

    [Fact]
    public void SignaturesOfOneCountAreToldApartByTheTypesOfTheArguments()
    {
        OsFunction f = Function("osApproxEquals",
            ("integer", new[] { "float", "float" }), ("integer", new[] { "vector", "vector" }), ("integer", new[] { "rotation", "rotation" }));
        Assert.Equal("integer (float, float)", Chosen(f, Value.Num(1.5), Value.Num(2.5)));
        Assert.Equal("integer (vector, vector)", Chosen(f, Value.Vec(1, 2, 3), Value.Vec(1, 2, 3)));
        Assert.Equal("integer (rotation, rotation)", Chosen(f, Value.Rot(0, 0, 0, 1), Value.Rot(0, 0, 0, 1)));
    }

    [Fact]
    public void ThePickedSignatureGivesItsOwnReturnType()
    {
        OsFunction f = Function("osSlerp", ("rotation", new[] { "rotation", "rotation", "float" }), ("vector", new[] { "vector", "vector", "float" }));
        Assert.Equal("vector (vector, vector, float)", Chosen(f, Value.Vec(0, 0, 1), Value.Vec(1, 0, 0), Value.Num(0.5)));
    }

    [Fact]
    public void AnExactMatchBeatsOneThatNeedsWidening()
    {
        OsFunction f = Function("osFoo", ("void", new[] { "float" }), ("void", new[] { "integer" }));
        Assert.Equal("void (integer)", Chosen(f, Value.Int(3)));
        Assert.Equal("void (float)", Chosen(f, Value.Num(3.5)));
    }

    [Fact]
    public void AnIntegerReachesAFloatAndAStringAKeyWhenNothingIsExact()
    {
        Assert.Equal("void (float)", Chosen(Function("osFoo", ("void", new[] { "float" })), Value.Int(3)));
        Assert.Equal("void (key)", Chosen(Function("osFoo", ("void", new[] { "key" })), Value.Str("text")));
        Assert.Equal("void (string)", Chosen(Function("osFoo", ("void", new[] { "string" })), Value.Key("k")));
    }

    [Fact]
    public void TwoSignaturesOfTheSameScoreAreAnErrorNamingTheFunctionAndBoth()
    {
        OsFunction f = Function("osFoo", ("void", new[] { "key", "string" }), ("void", new[] { "string", "key" }));
        var e = Assert.Throws<ArgumentException>(() => OsBindings.Pick(f, new[] { Value.Str("a"), Value.Str("b") }));
        Assert.Contains("osFoo", e.Message);
        Assert.Contains("osFoo(key, string)", e.Message);
        Assert.Contains("osFoo(string, key)", e.Message);
    }

    [Fact]
    public void ACallNoSignatureTakesIsAnErrorNamingTheFunctionAndWhatItTakes()
    {
        OsFunction f = Function("osTeleportAgent", ("void", new[] { "string", "string", "vector", "vector" }), ("void", new[] { "string", "vector", "vector" }));
        var wrongCount = Assert.Throws<ArgumentException>(() => OsBindings.Pick(f, new[] { Value.Str("a") }));
        Assert.Contains("osTeleportAgent", wrongCount.Message);
        Assert.Contains("osTeleportAgent(string, vector, vector)", wrongCount.Message);
        Assert.Contains("(string)", wrongCount.Message); // what it was given
        var wrongType = Assert.Throws<ArgumentException>(() => OsBindings.Pick(f, new[] { Value.Str("a"), Value.Int(1), Value.Int(2) }));
        Assert.Contains("osTeleportAgent", wrongType.Message);
        Assert.Contains("(string, integer, integer)", wrongType.Message);
    }

    [Fact]
    public void AFunctionWithOneSignatureNamesItsOwnMistakeToo()
    {
        OsFunction f = Function("osSHA256", ("string", new[] { "string" }));
        var e = Assert.Throws<ArgumentException>(() => OsBindings.Pick(f, new[] { Value.Vec(1, 2, 3) }));
        Assert.Contains("osSHA256", e.Message);
    }
}

/// <summary>An SLua script and an LSL script call os, iw and bot functions through a fake ILSLSystemAPI that records the calls.</summary>
[Collection("native-library")]
public class OsCallTests
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

    private static string[] Run(BindingRig rig, string source, bool lsl)
    {
        SchedulerScript s;
        try { s = rig.H.AddAndStart(source, lsl); }
        catch (SluaException e) { Assert.Fail("the script did not compile: " + e.Message); return null; }
        for (int i = 0; i < 20; i++)
        {
            rig.H.Pump();
            rig.H.Advance(2.1);
        }
        Assert.Empty(rig.H.FaultedScripts);
        Assert.Empty(rig.H.ReportedErrors);
        return Recorded(rig);
    }

    private static string[] Lsl(BindingRig rig, string body)
    {
        OsBindings.ApplyHostList();
        return Run(rig, "default { state_entry() { " + body + " } }", lsl: true);
    }

    private const string Sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [RequiresNativeFact]
    public void AnSLuaScriptCallsAnOsAnIwAndABotFunctionAndGetsTheirAnswers()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("osSHA256", Sha);
        rig.Fake.Returns("iwFormatTime", "noon");
        rig.Fake.Returns("botCreateBot", "11111111-2222-3333-4444-555555555555");
        Assert.Equal(new[]
        {
            "osSHA256(abc)", "iwFormatTime(0|1|%H)", "botCreateBot(A|B|Outfit|<1,2,3>|0)",
            "llOwnerSay(" + Sha + " noon 11111111-2222-3333-4444-555555555555)",
        }, Run(rig,
            "local h = ossl.SHA256('abc')\nlocal t = iw.FormatTime(0, 1, '%H')\nlocal b = bot.CreateBot('A', 'B', 'Outfit', vector(1, 2, 3), 0)\n"
            + "ll.OwnerSay(h .. ' ' .. t .. ' ' .. tostring(b))", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptCallsTheSameFunctionsByTheirPlainNames()
    {
        using var rig = new BindingRig();
        rig.Fake.Returns("osSHA256", Sha);
        rig.Fake.Returns("iwFormatTime", "noon");
        rig.Fake.Returns("botCreateBot", "11111111-2222-3333-4444-555555555555");
        Assert.Equal(new[]
        {
            "osSHA256(abc)", "iwFormatTime(0|1|%H)", "botCreateBot(A|B|Outfit|<1,2,3>|0)",
            "llOwnerSay(" + Sha + " noon 11111111-2222-3333-4444-555555555555)",
        }, Lsl(rig,
            "string h = osSHA256(\"abc\"); string t = iwFormatTime(0, 1, \"%H\"); key b = botCreateBot(\"A\", \"B\", \"Outfit\", <1,2,3>, 0);"
            + " llOwnerSay(h + \" \" + t + \" \" + (string)b);"));
    }

    [RequiresNativeFact]
    public void AnSLuaScriptReachesEachSignatureOfAnOverloadedNameByItsArguments()
    {
        using var rig = new BindingRig();
        Assert.Equal(new[]
        {
            "osNpcSay(npc|hi)", "osNpcSay(npc|5|hello)",
            "osMakeNotecard(card|text)", "osMakeNotecard(card|[a,b])",
            "osApproxEquals(1|2)", "osApproxEquals(<1,2,3>|<1,2,3>)", "osApproxEquals(1|2|0.5)",
        }, Run(rig,
            "ossl.NpcSay('npc', 'hi')\nossl.NpcSay('npc', 5, 'hello')\nossl.MakeNotecard('card', 'text')\nossl.MakeNotecard('card', {'a', 'b'})\n"
            + "ossl.ApproxEquals(1.0, 2.0)\nossl.ApproxEquals(vector(1, 2, 3), vector(1, 2, 3))\nossl.ApproxEquals(1.0, 2.0, 0.5)", lsl: false));
    }

    [RequiresNativeFact]
    public void AnLslScriptReachesEachSignatureOfAnOverloadedNameAsPhloxsCompilerPicksIt()
    {
        using var rig = new BindingRig();
        Assert.Equal(new[]
        {
            "osNpcSay(npc|hi)", "osNpcSay(npc|5|hello)",
            "osMakeNotecard(card|text)", "osMakeNotecard(card|[a,b])",
            "osApproxEquals(1|2)", "osApproxEquals(<1,2,3>|<1,2,3>)",
        }, Lsl(rig,
            "osNpcSay(\"npc\", \"hi\"); osNpcSay(\"npc\", 5, \"hello\"); osMakeNotecard(\"card\", \"text\"); osMakeNotecard(\"card\", [\"a\", \"b\"]);"
            + " osApproxEquals(1.0, 2.0); osApproxEquals(<1,2,3>, <1,2,3>);"));
    }

    [RequiresNativeFact]
    public void AnSLuaCallNoSignatureTakesReportsAnErrorThatNamesTheFunction()
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart("ossl.TeleportAgent(1)", lsl: false);
        for (int i = 0; i < 10; i++) { rig.H.Pump(); rig.H.Advance(1.0); }
        string text = string.Join("\n", rig.H.ReportedErrors);
        Assert.Contains("osTeleportAgent", text);
        Assert.Empty(rig.Fake.Calls);
        Assert.NotEmpty(rig.H.ReportedErrors);
    }
}
