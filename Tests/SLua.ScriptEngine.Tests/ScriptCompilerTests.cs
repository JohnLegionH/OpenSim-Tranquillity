/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>Loads the real native library once for a test class that compiles with it.</summary>
internal static class RealLibrary
{
    public static void Load()
    {
        RuntimeLoadResult r = NativeRuntime.Load(new NativeRuntimeOptions());
        Assert.True(r.Ok, r.Message);
    }
}

/// <summary>
/// Compiling through the real library. Loading the library is process-wide state, so these run one at a time with
/// the other tests that load it.
/// </summary>
[Collection("native-library")]
public class ScriptCompilerTests
{
    private const string GoodLsl = "default\n{\n    state_entry()\n    {\n        llSay(0, \"hello\");\n    }\n}\n";

    private static CompileResult Compile(string source)
    {
        RealLibrary.Load();
        return new ScriptCompiler().Compile(source);
    }

    // ---- good scripts give bytecode, bad ones give errors with line numbers ----

    [RequiresNativeFact]
    public void AGoodSluaScriptGivesBytecode()
    {
        CompileResult r = Compile("--!slua\nll.Say(0, 'hello')\n");
        Assert.True(r.Success);
        Assert.Equal(ScriptLanguage.Slua, r.Language);
        Assert.NotEmpty(r.Asset);
        Assert.Empty(r.Errors);
        Assert.False(r.Refused);
    }

    [RequiresNativeFact]
    public void AGoodLslScriptGivesBytecode()
    {
        CompileResult r = Compile(GoodLsl);
        Assert.True(r.Success);
        Assert.Equal(ScriptLanguage.Lsl, r.Language);
        Assert.NotEmpty(r.Asset);
        Assert.Empty(r.Errors);
    }

    [RequiresNativeFact]
    public void ABadSluaScriptGivesTheErrorsLineNumber()
    {
        CompileResult r = Compile("--!slua\nlocal a = 1\nlocal b = = 2\n");
        Assert.False(r.Success);
        Assert.Null(r.Asset);
        CompileError e = Assert.Single(r.Errors);
        Assert.Equal(3, e.Line);
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }

    [RequiresNativeFact]
    public void ABadLslScriptGivesTheErrorsRealSourceLine()
    {
        // The runtime reports an LSL error on the line it is on; the editor marks that line.
        string source = "default\n{\n    state_entry()\n    {\n        integer a = 1;\n        integer b = \"x\";\n    }\n}\n";
        CompileResult r = Compile(source);
        Assert.False(r.Success);
        CompileError e = Assert.Single(r.Errors);
        Assert.Equal(6, e.Line);
        Assert.Contains("assigned a string", e.Message);
    }

    [RequiresNativeFact]
    public void ALslSyntaxErrorAlsoGetsTheRealSourceLine()
    {
        string source = "default\n{\n    state_entry()\n    {\n        llSay(0, \"hi\"\n    }\n}\n";
        CompileResult r = Compile(source);
        Assert.False(r.Success);
        Assert.Equal(6, r.Errors[0].Line);
    }

    [RequiresNativeFact]
    public void SeveralLslErrorsAreListedOneEachInSourceOrder()
    {
        string source = "default\n{\n    state_entry()\n    {\n        llNoSuchOne();\n        llNoSuchTwo();\n    }\n}\n";
        CompileResult r = Compile(source);
        Assert.Equal(2, r.Errors.Count);
        Assert.Equal(5, r.Errors[0].Line);
        Assert.Contains("llNoSuchOne", r.Errors[0].Message);
        Assert.Equal(6, r.Errors[1].Line);
        Assert.Contains("llNoSuchTwo", r.Errors[1].Message);
    }

    [RequiresNativeFact]
    public void ErrorsReadInTheEditorsFormat()
    {
        CompileResult r = Compile("--!slua\nlocal b = = 2\n");
        string line = Assert.Single(r.EditorErrors());
        Assert.StartsWith("(2,0) Error: ", line);
    }

    [RequiresNativeFact]
    public void AnEmptyScriptIsACompileErrorNotAnException()
    {
        CompileResult r = Compile("");
        Assert.False(r.Success);
        Assert.Single(r.Errors);
    }

    // ---- LSL that calls os*, iw* or bot* is refused, naming the functions and the Phlox engine ----

    [RequiresNativeFact]
    public void LslCallingOsIwOrBotFunctionsIsRefusedNamingEachAndPhloxWhenTheLibraryHasNoHostList()
    {
        RealLibrary.Load();
        using var noList = new NoHostList();
        string source = "default\n{\n    state_entry()\n    {\n        osGetGridName();\n        iwFoo();\n        botBar();\n    }\n}\n";
        CompileResult r = new ScriptCompiler().Compile(source); // not Compile(): that loads the library again, which gives it the list
        Assert.False(r.Success);
        Assert.True(r.Refused);
        Assert.Null(r.Asset);
        string message = string.Join("\n", r.Errors.Select(e => e.Message));
        Assert.Contains("osGetGridName", message);
        Assert.Contains("iwFoo", message);
        Assert.Contains("botBar", message);
        Assert.Contains("Phlox", message);
    }

    [RequiresNativeFact]
    public void WithTheHostListAnOsFunctionCompilesAndAnUnknownOneIsAnOrdinaryErrorNamingIt()
    {
        OsBindings.ApplyHostList();
        Assert.True(Compile("default\n{\n    state_entry()\n    {\n        osGetGridName();\n    }\n}\n").Success);

        CompileResult r = Compile("default\n{\n    state_entry()\n    {\n        osGetGridName();\n        iwFoo();\n    }\n}\n");
        Assert.False(r.Success);
        Assert.False(r.Refused);
        string message = string.Join("\n", r.Errors.Select(e => e.Message));
        Assert.Contains("iwFoo", message);
        Assert.DoesNotContain("osGetGridName", message);
    }

    [RequiresNativeFact]
    public void AnOrdinaryLslErrorIsNotARefusal()
    {
        CompileResult r = Compile("default\n{\n    state_entry()\n    {\n        llNoSuch();\n    }\n}\n");
        Assert.False(r.Success);
        Assert.False(r.Refused);
    }

    [RequiresNativeFact]
    public void SluaMayUseTheOsFunctionTable()
    {
        CompileResult r = Compile("--!slua\nossl.GetGridName()\n");
        Assert.True(r.Success);
        Assert.False(r.Refused);
    }

    // ---- what the first line means ----

    [RequiresNativeFact]
    public void AHeaderWithNoLanguageSuffixIsSlua()
    {
        CompileResult r = Compile("//SLua:\nll.Say(0, 'x')\n");
        Assert.True(r.Success);
        Assert.Equal(ScriptLanguage.Slua, r.Language);
    }

    [RequiresNativeFact]
    public void AnLslSuffixIsNotSniffedAsSluaFromItsBody()
    {
        // The header decides; the "--" test applies only to a script with no header naming this engine.
        CompileResult r = Compile("//SLua:LSL\n--!slua\nll.Say(0, 'x')\n");
        Assert.Equal(ScriptLanguage.Lsl, r.Language);
        Assert.False(r.Success);
    }

    [RequiresNativeFact]
    public void AnExplicitLslSuffixIsLsl()
    {
        CompileResult r = Compile("//SLua:lsl\n" + GoodLsl);
        Assert.True(r.Success);
        Assert.Equal(ScriptLanguage.Lsl, r.Language);
    }

    [RequiresNativeFact]
    public void TheEngineNameInTheHeaderIsReadWithoutRegardToCase()
    {
        CompileResult lsl = Compile("//SLUA:LSL\n" + GoodLsl);
        Assert.True(lsl.Success);
        Assert.Equal(ScriptLanguage.Lsl, lsl.Language);
        CompileResult slua = Compile("//slua:\nll.Say(0, 'x')\n");
        Assert.True(slua.Success);
        Assert.Equal(ScriptLanguage.Slua, slua.Language);
    }

    [RequiresNativeFact]
    public void ASluaSuffixIsSluaAndKeepsLineNumbersWithTheHeaderBlanked()
    {
        CompileResult ok = Compile("//SLua:slua\nll.Say(0, 'x')\n");
        Assert.True(ok.Success);
        Assert.Equal(ScriptLanguage.Slua, ok.Language);

        CompileResult bad = Compile("//SLua:slua\nlocal a = 1\nlocal b = = 2\n");
        Assert.Equal(3, Assert.Single(bad.Errors).Line);
    }

    [RequiresNativeFact]
    public void TheLanguageSuffixIsCaseInsensitive()
    {
        CompileResult r = Compile("//SLua:SLua\nll.Say(0, 'x')\n");
        Assert.True(r.Success);
        Assert.Equal(ScriptLanguage.Slua, r.Language);
    }

    [RequiresNativeFact]
    public void AnUnknownLanguageSuffixIsAnErrorOnLineOneNamingTheLanguagesThisEngineRuns()
    {
        CompileResult r = Compile("//SLua:python\nprint('x')\n");
        Assert.False(r.Success);
        Assert.Equal(ScriptLanguage.Unsupported, r.Language);
        CompileError e = Assert.Single(r.Errors);
        Assert.Equal(1, e.Line);
        Assert.Contains("python", e.Message);
        Assert.Contains("lsl", e.Message);
        Assert.Contains("slua", e.Message);
    }

    [RequiresNativeFact]
    public void WithoutAHeaderTextStartingWithTwoDashesIsSluaAnythingElseIsLsl()
    {
        Assert.Equal(ScriptLanguage.Slua, Compile("--!slua\nll.Say(0, 'x')\n").Language);
        Assert.Equal(ScriptLanguage.Slua, Compile("  \n  -- note\nll.Say(0, 'x')\n").Language);
        Assert.Equal(ScriptLanguage.Lsl, Compile(GoodLsl).Language);
        Assert.Equal(ScriptLanguage.Lsl, Compile("// a comment\n" + GoodLsl).Language);
    }
}
