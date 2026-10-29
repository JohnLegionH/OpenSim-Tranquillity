/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A viewer offers Lua in its script editor, and creates new scripts as Lua, only when the region's simulator features
/// say "LuaScriptsEnabled". The engine says it when it is on and working, and says nothing otherwise. The Lua default
/// script a new script gets must be valid SLua.
/// </summary>
[Collection("native-library")]
public class LuaDefaultAndFeatureTests
{
    private sealed class StandInFeatures : ISimulatorFeaturesModule
    {
        public readonly OSDMap Features = new();
        public event SimulatorFeaturesRequestDelegate OnSimulatorFeaturesRequest { add { } remove { } }
        public void AddFeature(string name, OSD value) => Features[name] = value;
        public void AddOpenSimExtraFeature(string name, OSD value) { }
        public bool RemoveFeature(string name) => Features.Remove(name);
        public bool TryGetFeature(string name, out OSD value) => Features.TryGetValue(name, out value);
        public bool TryGetOpenSimExtraFeature(string name, out OSD value) { value = null; return false; }
        public bool OpenSimExtraFeatureContains(string name) => false;
        public OSDMap GetFeatures() => Features;
    }

    private static StandInFeatures Region(string enabled, bool runtimeLoads = true, bool worldComm = true, bool withFeaturesModule = true)
    {
        Scene scene = new SceneHelpers().SetupScene();
        var features = new StandInFeatures();
        if (withFeaturesModule)
            scene.RegisterModuleInterface<ISimulatorFeaturesModule>(features);
        if (worldComm)
            scene.RegisterModuleInterface<IWorldComm>(new ChatAndListenTests.StandInWorldComm());
        var source = new IniConfigSource();
        if (enabled != null)
            source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", enabled);
        var engine = new SLuaEngine(new RecordingLog(), (settings, log) => runtimeLoads);
        engine.Initialise(source);
        engine.AddRegion(scene);
        engine.RegionLoaded(scene);
        return features;
    }

    [Fact]
    public void AnEngineThatIsOnAndWorkingSaysLuaScriptsAreEnabledUnderTheKeyTheViewerReads()
    {
        StandInFeatures features = Region("true");

        Assert.True(features.Features.TryGetValue("LuaScriptsEnabled", out OSD value));
        Assert.IsType<OSDBoolean>(value);
        Assert.True(value.AsBoolean());
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public void AnEngineThatIsOffSaysNothingAndTheFeaturesAreAsTheyWere(string enabled)
    {
        // the same region with the engine on does say it, so the empty answer below is the engine's choice
        Assert.True(Region("true").Features.ContainsKey("LuaScriptsEnabled"));

        StandInFeatures features = Region(enabled);

        Assert.False(features.Features.ContainsKey("LuaScriptsEnabled"));
        Assert.Empty(features.Features);
    }

    [Fact]
    public void AnEngineWhoseRuntimeDidNotLoadSaysNothing()
    {
        Assert.True(Region("true").Features.ContainsKey("LuaScriptsEnabled"));

        Assert.Empty(Region("true", runtimeLoads: false).Features);
    }

    [Fact]
    public void AnEngineThatDisablesItselfForWantOfWorldCommunicationSaysNothing()
    {
        Assert.True(Region("true").Features.ContainsKey("LuaScriptsEnabled"));

        Assert.Empty(Region("true", worldComm: false).Features);
    }

    [Fact]
    public void ARegionWithNoSimulatorFeaturesModuleStillLoadsTheEngine()
    {
        // nothing to tell, and nothing to fail on
        StandInFeatures features = Region("true", withFeaturesModule: false);

        Assert.Empty(features.Features);
    }

    // ---- the Lua default script is valid SLua ----

    [RequiresNativeFact]
    public void TheLuaDefaultScriptCompilesAsSluaOnTheLuauTarget()
    {
        Assert.NotEmpty(DefaultLuaScript.Text);
        RealLibrary.Load();

        CompileResult result = new ScriptCompiler().Compile(DefaultLuaScript.Text, ScriptCompileTarget.Luau);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(ScriptLanguage.Slua, result.Language);
        Assert.NotEmpty(result.Asset);
    }

    [RequiresNativeFact]
    public void TheLuaDefaultScriptAlsoCompilesWithNoTargetBecauseItBeginsWithTheSluaMarker()
    {
        Assert.NotEmpty(DefaultLuaScript.Text);
        RealLibrary.Load();

        CompileResult result = new ScriptCompiler().Compile(DefaultLuaScript.Text);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(ScriptLanguage.Slua, result.Language);
    }
}
