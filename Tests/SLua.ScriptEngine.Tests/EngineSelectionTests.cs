/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// What the engine claims when the region asks (IScriptEngineSelection): a script whose first line is "--!slua", and a
/// script the viewer saved with the Lua or LSL-on-Lua compile target. Nothing else, and nothing at all when the engine is
/// off or its runtime did not load. The region's own ordering (header, saved state, claim, default) is covered by SelectionTests.
/// </summary>
public class EngineSelectionTests
{
    private static SLuaEngine Engine(bool enabled = true, bool runtimeLoads = true, params (string Key, string Value)[] keys)
    {
        var engine = new SLuaEngine(new RecordingLog(), (settings, log) => runtimeLoads);
        var source = new IniConfigSource();
        IConfig section = source.AddConfig(EngineIdentity.ConfigSection);
        section.Set("Enabled", enabled ? "true" : "false");
        foreach ((string key, string value) in keys)
            section.Set(key, value);
        engine.Initialise(source);
        return engine;
    }

    [Theory]
    [InlineData("--!slua\nprint(1)")]
    [InlineData("--!slua\r\nprint(1)")]
    [InlineData("--!slua")]
    [InlineData("--!SLua\nprint(1)")]
    [InlineData("  \n\t--!slua\nprint(1)")]
    [InlineData("--!slua -- the new engine\nprint(1)")]
    public void AnEnabledEngineClaimsAScriptWhoseFirstLineIsDashDashBangSlua(string source)
    {
        Assert.True(Engine().ClaimsScript(source));
    }

    [Theory]
    [InlineData("--!sluax\nprint(1)")]
    [InlineData("--!lua\nprint(1)")]
    [InlineData("-- plain Lua\nprint(1)")]
    [InlineData("print(1)\n--!slua")]
    [InlineData("default { state_entry() {} }")]
    [InlineData("// --!slua\ndefault {}")]
    [InlineData("//SLua:slua\nprint(1)")]
    [InlineData("//Unknown:\ndefault {}")]
    [InlineData("")]
    [InlineData(null)]
    public void NothingElseIsClaimed(string source)
    {
        SLuaEngine engine = Engine();
        Assert.True(engine.ClaimsScript("--!slua\nprint(1)"));   // the engine is up, so the refusals below are its own
        Assert.False(engine.ClaimsScript(source));
    }

    [Fact]
    public void AnLslScriptWithoutAHeaderIsNotClaimedWhateverTheConfigurationSays()
    {
        // The engine runs no script in this part, so it never takes one that another engine would run.
        SLuaEngine engine = Engine(keys: ("ClaimHeaderlessScripts", "true"));
        Assert.False(engine.ClaimsScript("default { state_entry() {} }"));
    }

    [Fact]
    public void AnEngineThatIsNotEnabledClaimsNothing()
    {
        Assert.True(Engine().ClaimsScript("--!slua\nprint(1)"));
        Assert.False(Engine(enabled: false).ClaimsScript("--!slua\nprint(1)"));
    }

    [Fact]
    public void AnEngineWhoseRuntimeDidNotLoadClaimsNothing()
    {
        Assert.True(Engine().ClaimsScript("--!slua\nprint(1)"));
        Assert.False(Engine(runtimeLoads: false).ClaimsScript("--!slua\nprint(1)"));
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau)]
    public void AnEnabledEngineAcceptsTheViewersLuaCompileTargets(ScriptCompileTarget target)
    {
        Assert.True(Engine().AcceptsCompileTarget(target));
    }

    [Fact]
    public void AScriptWithNoCompileTargetIsNotAccepted()
    {
        Assert.False(Engine().AcceptsCompileTarget(ScriptCompileTarget.Unspecified));
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau)]
    public void AnEngineThatIsOffOrWhoseRuntimeDidNotLoadAcceptsNoCompileTarget(ScriptCompileTarget target)
    {
        Assert.False(Engine(enabled: false).AcceptsCompileTarget(target));
        Assert.False(Engine(runtimeLoads: false).AcceptsCompileTarget(target));
    }

    [Fact]
    public void TheEngineNamesNoHeaderAliases()
    {
        Assert.NotNull(Engine().HeaderAliases);
        Assert.Empty(Engine().HeaderAliases);
    }
}
