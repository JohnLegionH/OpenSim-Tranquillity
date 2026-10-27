/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.RegularExpressions;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The engine on runtime API 4: the version it requires, the names it gives the library, the compile error lines the
/// library now reports exactly, and delays reported by the step that asked for them.
/// </summary>
[Collection("native-library")]
public class Api4AdoptionTests
{
    private const string OsCallingLsl = "default\n{\n    state_entry()\n    {\n        osGetGridName();\n    }\n}\n";

    private static NativeRuntimeOptions OptionsReporting(int version) => new()
    {
        ConfiguredPath = "x",
        BaseDirectory = null,
        FileExists = _ => true,
        TryLoad = _ => new IntPtr(1),
        ApiVersion = () => version,
        GetEnvironmentVariable = _ => null,
    };

    // ---- a. API 4 is required ----

    [Fact]
    public void TheEngineRequiresApiVersionFive() => Assert.Equal(5, NativeRuntime.RequiredApiVersion);

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ALibraryOfAnotherApiVersionIsRefusedNamingBothVersions(int version)
    {
        RuntimeLoadResult r = NativeRuntime.Load(OptionsReporting(version));
        Assert.False(r.Ok);
        Assert.Contains($"API version {version}", r.Message);
        Assert.Contains("requires 5", r.Message);
    }

    [Fact]
    public void ALibraryOfApiVersionFiveIsAccepted() => Assert.True(NativeRuntime.Load(OptionsReporting(5)).Ok);

    [RequiresNativeFact]
    public void TheRealLibraryReportsApiVersionFiveAndTheStepResultIsThirtyTwoBytes()
    {
        RealLibrary.Load();
        Assert.Equal(5, Host.ApiVersion());
        Assert.Contains("api=5", Host.Version());
        Assert.Equal(32, Host.NativeStructSize(1));
    }

    // ---- b. the engine names itself to the library before the first compile ----

    [RequiresNativeFact]
    public void LoadingTheLibraryGivesItTheEngineName()
    {
        RealLibrary.Load();
        Assert.Equal(EngineIdentity.EngineName, Host.EngineName);
    }

    [RequiresNativeFact]
    public void ARefusalNamesThisEngineAndPhloxAndTellsTheScripterToRemoveTheHeader()
    {
        RealLibrary.Load();
        CompileResult r = new ScriptCompiler().Compile("//SLua:lsl\n" + OsCallingLsl);
        Assert.True(r.Refused);
        string message = string.Join("\n", r.Errors.Select(e => e.Message));
        Assert.Contains("osGetGridName", message);
        Assert.Contains("the SLua script engine", message);
        Assert.Contains("Phlox engine", message);
        Assert.Contains("Remove the //SLua: header", message);
        Assert.DoesNotContain("script header //Phlox", message); // Phlox is not selected by "//Phlox"
    }

    [RequiresNativeFact]
    public void ARefusalOfAScriptWithNoHeaderDoesNotTellTheScripterToRemoveOne()
    {
        RealLibrary.Load();
        CompileResult r = new ScriptCompiler().Compile(OsCallingLsl);
        Assert.True(r.Refused);
        string message = string.Join("\n", r.Errors.Select(e => e.Message));
        Assert.Contains("Phlox engine", message);
        Assert.DoesNotContain("Remove the //SLua: header", message);
    }

    // ---- c. LSL error lines come out of the library exactly ----

    [RequiresNativeFact]
    public void TheLibraryReportsAnLslErrorOnTheLineItIsOn()
    {
        RealLibrary.Load();
        string source = "default\n{\n    state_entry()\n    {\n        integer a = 1;\n        integer b = \"x\";\n    }\n}\n";
        SluaException e = Assert.Throws<SluaException>(() => Host.Compile(source, lsl: true));
        Match m = Regex.Match(e.Message, @"code -?\d+: (\d+): ");
        Assert.True(m.Success, e.Message);
        Assert.Equal("6", m.Groups[1].Value);
    }

    [RequiresNativeFact]
    public void TheEngineGivesTheLibrarysLslErrorLineAsItIs()
    {
        RealLibrary.Load();
        string source = "default\n{\n    state_entry()\n    {\n        integer a = 1;\n        integer b = \"x\";\n    }\n}\n";
        CompileResult r = new ScriptCompiler().Compile(source);
        Assert.Equal(6, Assert.Single(r.Errors).Line);
    }

    [RequiresNativeFact]
    public void AnLslErrorOnTheFirstLineIsLineOne()
    {
        RealLibrary.Load();
        CompileResult r = new ScriptCompiler().Compile("integer 5;\ndefault\n{\n    state_entry()\n    {\n    }\n}\n");
        Assert.False(r.Success);
        Assert.Equal(1, r.Errors[0].Line);
    }

    // ---- d. a delay is served by the step that asked for it ----

    [RequiresNativeFact]
    public void AnLslSleepEndsTheTurnAndTheNextStatementRunsAfterTheDelay()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("default\n{\n    state_entry()\n    {\n        llSleep(1.0);\n        llOwnerSay(\"after\");\n    }\n}\n", lsl: true);
        h.Pump();
        Assert.Empty(h.MarksOf(s)); // the sleep ended the turn: nothing after it has run
        Assert.Equal(ScriptRunState.Sleeping, s.State);

        h.Advance(1.05);
        Assert.Equal(new[] { "say:after" }, h.MarksOf(s));
        Assert.Equal(0, s.Punishments);
    }

    [RequiresNativeFact]
    public void ADelayThatIsReportedInTheStepThatAskedForItIsNotAPunishment()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Sleep(1.0) ossl.Count()");
        h.Pump();
        Assert.Equal(0, h.CountOf(s));
        Assert.Equal(0, s.Punishments);

        h.Advance(0.9); // not yet due: the delay is one second, not shorter and not doubled
        Assert.Equal(0, h.CountOf(s));
        h.Advance(0.2);
        Assert.Equal(1, h.CountOf(s));
        Assert.Equal(0, s.Punishments);
    }

    [RequiresNativeFact]
    public void AnOverrunInAHostCallIsStillPunishedBeyondTheDelayTheScriptAskedFor()
    {
        using var h = new SchedulerHarness(quantaMs: 1.0);
        SchedulerScript burner = h.AddAndStart("while true do ossl.Burn(20) end");
        h.Passes(2);
        Assert.Equal(1, burner.Punishments);
        Assert.Equal(ScriptRunState.Sleeping, burner.State);
    }
}
