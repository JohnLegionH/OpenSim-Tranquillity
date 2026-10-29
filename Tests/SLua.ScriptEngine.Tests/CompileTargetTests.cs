/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A viewer's compile target ("luau" or "lsl-luau"), kept in the script item's flags: the engine accepts it, compiles
/// the script as the language it names whatever the first line says, owns the script even when its first line names
/// another engine, keeps the target when the compile fails, and reports it back when the editor asks.
/// </summary>
public class CompileTargetTests
{
    // ---- what the engine accepts ----

    private static SLuaEngine Engine(bool enabled = true, bool runtimeLoads = true)
    {
        var engine = new SLuaEngine(new RecordingLog(), (settings, log) => runtimeLoads);
        var source = new IniConfigSource();
        source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", enabled ? "true" : "false");
        engine.Initialise(source);
        return engine;
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau)]
    public void AnEnabledEngineAcceptsBothLuauTargets(ScriptCompileTarget target)
    {
        Assert.True(Engine().AcceptsCompileTarget(target));
    }

    [Fact]
    public void NoTargetIsNotAnAcceptedTarget()
    {
        Assert.True(Engine().AcceptsCompileTarget(ScriptCompileTarget.Luau));
        Assert.False(Engine().AcceptsCompileTarget(ScriptCompileTarget.Unspecified));
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau)]
    public void AnEngineThatIsOffOrWithoutItsRuntimeAcceptsNothing(ScriptCompileTarget target)
    {
        Assert.True(Engine().AcceptsCompileTarget(target));
        Assert.False(Engine(enabled: false).AcceptsCompileTarget(target));
        Assert.False(Engine(runtimeLoads: false).AcceptsCompileTarget(target));
    }

    // ---- the language the compiler uses ----

    private static (ScriptCompiler Compiler, List<(string Text, bool Lsl)> Calls) Compiler()
    {
        var calls = new List<(string Text, bool Lsl)>();
        var compiler = new ScriptCompiler(null, (text, lsl) => { calls.Add((text, lsl)); return new byte[] { 1 }; });
        return (compiler, calls);
    }

    [Theory]
    // the target decides the language, whatever the text or the first line says
    [InlineData("default { state_entry() {} }", ScriptCompileTarget.Luau, ScriptLanguage.Slua)]
    [InlineData("print(1)", ScriptCompileTarget.LslOnLuau, ScriptLanguage.Lsl)]
    [InlineData("--!slua\nprint(1)", ScriptCompileTarget.LslOnLuau, ScriptLanguage.Lsl)]
    [InlineData("//SLua:lsl\ndefault {}", ScriptCompileTarget.Luau, ScriptLanguage.Slua)]
    [InlineData("//SLua:slua\nprint(1)", ScriptCompileTarget.LslOnLuau, ScriptLanguage.Lsl)]
    [InlineData("//Phlox:\nprint(1)", ScriptCompileTarget.Luau, ScriptLanguage.Slua)]
    // no target: the first line, then the text, as before
    [InlineData("--!slua\nprint(1)", ScriptCompileTarget.Unspecified, ScriptLanguage.Slua)]
    [InlineData("print(1)", ScriptCompileTarget.Unspecified, ScriptLanguage.Lsl)]
    [InlineData("//SLua:slua\nprint(1)", ScriptCompileTarget.Unspecified, ScriptLanguage.Slua)]
    public void TheTargetDecidesTheLanguageTheScriptCompilesAs(string source, ScriptCompileTarget target, ScriptLanguage language)
    {
        var (compiler, calls) = Compiler();

        CompileResult result = compiler.Compile(source, target);

        Assert.True(result.Success, string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>()));
        Assert.Equal(language, result.Language);
        Assert.Equal(language == ScriptLanguage.Lsl, Assert.Single(calls).Lsl);
    }

    [Fact]
    public void AFirstLineThatNamesAnotherEngineIsBlankedWhenTheTargetMakesTheScriptLua()
    {
        var (compiler, calls) = Compiler();

        compiler.Compile("//Phlox:\nprint(1)", ScriptCompileTarget.Luau);

        string text = Assert.Single(calls).Text;
        Assert.StartsWith("\n", text);   // the line is gone, its newline kept so the error lines stay right
        Assert.DoesNotContain("Phlox", text);
    }

    // ---- in a region ----

    private static UUID RezWithTarget(EngineRig rig, string source, ScriptCompileTarget target, string engine = "SLua")
    {
        UUID id = UUID.Random();
        rig.Object.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = id,
            AssetID = UUID.Random(),
            Name = "Example Script",
            OwnerID = EngineRig.Owner,
            Type = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            ScriptRunning = true,
            Flags = ScriptCompileTargets.WithTarget(0u, target),
        }, false);
        rig.Scene.EventManager.TriggerRezScript(rig.LocalId, id, source, 0, false, engine, 0);
        return id;
    }

    private const string LuaMark = "ossl.Mark('lua')\n";
    private const string BadLua = "local a = = 2";
    private const string LslMark = "default { state_entry() { llOwnerSay(\"lsl\"); } }";

    [RequiresNativeFact]
    public void ALuauTargetRunsAScriptWithNoHeaderAsLua()
    {
        using var rig = new EngineRig();
        UUID item = RezWithTarget(rig, LuaMark, ScriptCompileTarget.Luau);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("lua")));
    }

    [RequiresNativeFact]
    public void ALslOnLuauTargetRunsAScriptWithNoHeaderAsLsl()
    {
        using var rig = new EngineRig();
        UUID item = RezWithTarget(rig, LslMark, ScriptCompileTarget.LslOnLuau);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("lsl")));
    }

    [RequiresNativeFact]
    public void ALuauTargetBeatsAFirstLineThatNamesAnotherEngine()
    {
        using var rig = new EngineRig();
        UUID item = RezWithTarget(rig, "//InWorldz.Phlox:\n" + LuaMark, ScriptCompileTarget.Luau);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("lua")));
    }

    [RequiresNativeFact]
    public void AScriptWithNoTargetStillGoesByItsFirstLine()
    {
        using var rig = new EngineRig();
        // LSL text, which this engine would run were it given the script
        UUID item = RezWithTarget(rig, "//InWorldz.Phlox:\n" + LslMark, ScriptCompileTarget.Unspecified, engine: "InWorldz.Phlox");

        Assert.False(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("lsl"), 600));
    }

    [RequiresNativeFact]
    public void AFailedCompileKeepsTheTargetOnTheItem()
    {
        using var rig = new EngineRig();
        // the editor asked for Lua and the text is not Lua: it cannot compile
        UUID item = RezWithTarget(rig, BadLua, ScriptCompileTarget.Luau);

        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Failures.Length == 1));
        Assert.Equal(item, rig.Messages.Failures[0].Item);
        Assert.Equal(ScriptCompileTarget.Luau, ScriptCompileTargets.FromFlags(rig.ItemOf(item).Flags));
    }

    // ---- what the engine reports when the editor asks ----

    private static (ScriptCompileTarget Target, bool Running)? ReplyFor(EngineRig rig, TestClient client, UUID item)
    {
        client.ScriptRunningReplies.Clear();
        rig.Scene.EventManager.TriggerGetScriptRunning(client, rig.Object.UUID, item);
        foreach (var reply in client.ScriptRunningReplies)
            if (reply.ItemID == item)
                return (reply.Target, reply.Running);
        return null;
    }

    private static TestClient ClientOf(EngineRig rig) => new(SceneHelpers.GenerateAgentData(UUID.Random()), rig.Scene);

    [RequiresNativeFact]
    public void TheEditorIsToldTheTargetKeptWithARunningScript()
    {
        using var rig = new EngineRig();
        UUID item = RezWithTarget(rig, LuaMark, ScriptCompileTarget.Luau);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("lua")));

        var reply = ReplyFor(rig, ClientOf(rig), item);

        Assert.NotNull(reply);
        Assert.Equal(ScriptCompileTarget.Luau, reply.Value.Target);
        Assert.True(reply.Value.Running);
    }

    [RequiresNativeFact]
    public void TheEditorIsToldTheTargetOfAScriptThatFailedToCompile()
    {
        using var rig = new EngineRig();
        UUID item = RezWithTarget(rig, BadLua, ScriptCompileTarget.Luau);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Failures.Length == 1));

        var reply = ReplyFor(rig, ClientOf(rig), item);

        Assert.NotNull(reply);
        Assert.Equal(ScriptCompileTarget.Luau, reply.Value.Target);
    }

    [RequiresNativeFact]
    public void ALuaScriptChosenByItsFirstLineIsReportedAsLuaAndAnLslOneAsLslOnLuau()
    {
        using var rig = new EngineRig();
        UUID lua = rig.Rez(EngineRig.Slua(LuaMark), item: null);
        UUID lsl = rig.Rez(EngineRig.Lsl(LslMark));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(lua).Contains("lua") && rig.MarksOf(lsl).Contains("lsl")));
        TestClient client = ClientOf(rig);

        Assert.Equal(ScriptCompileTarget.Luau, ReplyFor(rig, client, lua)?.Target);
        Assert.Equal(ScriptCompileTarget.LslOnLuau, ReplyFor(rig, client, lsl)?.Target);
    }

    [RequiresNativeFact]
    public void TheEngineSaysNothingAboutAScriptItDoesNotRun()
    {
        using var rig = new EngineRig();

        Assert.Null(ReplyFor(rig, ClientOf(rig), UUID.Random()));
    }
}
