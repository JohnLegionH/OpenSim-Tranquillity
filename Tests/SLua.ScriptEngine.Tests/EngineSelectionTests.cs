/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// What the engine claims when the region asks (IScriptEngineSelection.ClaimsScript): a script whose first line is
/// "--!slua", and, only when ClaimHeaderlessScripts is on, every other script without an engine header. The region's
/// own ordering (header, saved state, claim, default) is covered by SelectionTests.
/// </summary>
public class EngineSelectionTests
{
    private static SLuaEngine Engine(bool enabled = true, bool runtimeLoads = true, bool claimHeaderless = false)
    {
        var engine = new SLuaEngine(new RecordingLog(), (settings, log) => runtimeLoads);
        var source = new IniConfigSource();
        IConfig section = source.AddConfig(EngineIdentity.ConfigSection);
        section.Set("Enabled", enabled ? "true" : "false");
        if (claimHeaderless)
            section.Set("ClaimHeaderlessScripts", "true");
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
    [InlineData("")]
    [InlineData(null)]
    public void WithTheSettingOffNothingElseIsClaimed(string source)
    {
        SLuaEngine engine = Engine();
        Assert.True(engine.ClaimsScript("--!slua\nprint(1)"));   // the engine is up, so the refusals below are its own
        Assert.False(engine.ClaimsScript(source));
    }

    [Fact]
    public void AnEngineThatIsNotEnabledClaimsNothing()
    {
        Assert.True(Engine().ClaimsScript("--!slua\nprint(1)"));
        Assert.False(Engine(enabled: false).ClaimsScript("--!slua\nprint(1)"));
        Assert.False(Engine(enabled: false, claimHeaderless: true).ClaimsScript("default {}"));
    }

    [Fact]
    public void AnEngineWhoseRuntimeDidNotLoadClaimsNothing()
    {
        Assert.True(Engine().ClaimsScript("--!slua\nprint(1)"));
        Assert.False(Engine(runtimeLoads: false).ClaimsScript("--!slua\nprint(1)"));
        Assert.False(Engine(runtimeLoads: false, claimHeaderless: true).ClaimsScript("default {}"));
    }

    [Fact]
    public void AnEngineThatHasDisabledItselfClaimsNothing()
    {
        SLuaEngine engine = Engine(claimHeaderless: true);
        Assert.True(engine.ClaimsScript("--!slua\nprint(1)"));
        Assert.True(engine.ClaimsScript("default {}"));
        typeof(SLuaEngine).GetField("m_disabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(engine, true);
        Assert.True(engine.IsDisabled);
        Assert.False(engine.ClaimsScript("--!slua\nprint(1)"));
        Assert.False(engine.ClaimsScript("default {}"));
    }

    [Theory]
    [InlineData("default { state_entry() {} }")]
    [InlineData("// an ordinary comment\ndefault {}")]
    [InlineData("-- plain Lua\nprint(1)")]
    [InlineData("//Unknown:\ndefault {}")]
    [InlineData("--!slua\nprint(1)")]
    public void WithTheSettingOnEveryScriptWithoutAnEngineHeaderIsClaimed(string source)
    {
        Assert.True(Engine(claimHeaderless: true).ClaimsScript(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NoSourceIsClaimedEvenWithTheSettingOn(string source)
    {
        // A crossing carries no source; the region then routes by saved state alone.
        SLuaEngine engine = Engine(claimHeaderless: true);
        Assert.True(engine.ClaimsScript("default {}"));
        Assert.False(engine.ClaimsScript(source));
    }

    [Fact]
    public void TheEngineNamesNoHeaderAliases()
    {
        Assert.NotNull(Engine().HeaderAliases);
        Assert.Empty(Engine().HeaderAliases);
    }

    [Fact]
    public void TheHeaderlessSettingIsOffUntilTheKeyIsSet()
    {
        var off = new IniConfigSource();
        off.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", "true");
        var on = new IniConfigSource();
        IConfig section = on.AddConfig(EngineIdentity.ConfigSection);
        section.Set("Enabled", "true");
        section.Set("ClaimHeaderlessScripts", "true");

        Assert.True(SLuaSettings.Read(on, new RecordingLog()).ClaimHeaderlessScripts);
        Assert.False(SLuaSettings.Read(off, new RecordingLog()).ClaimHeaderlessScripts);
        Assert.False(new SLuaSettings().ClaimHeaderlessScripts);
    }
}
