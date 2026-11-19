/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse.StructuredData;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The engine is off unless [SLua] Enabled = true: a stock install loads nothing (not even the native library), and the
/// region keeps the script engines it had before. Switched on with a usable library, it registers as one of the region's
/// script engines and the region lists it to viewers.
/// </summary>
// Each test builds its own scene and engine, and the runtime loader is replaced, so no test loads the native library.
// SceneHelpers.SetupScene sets the process-wide MainConsole.Instance, as every scene test does; nothing here reads it,
// so the class runs in parallel with the others.
public class RegistrationTests
{
    private static IScriptModule[] Engines(Scene scene) => scene.RequestModuleInterfaces<IScriptModule>();

    private static Scene Region(IConfigSource config, out SLuaEngine engine, RuntimeLoader runtime = null)
    {
        Scene scene = new SceneHelpers().SetupScene();
        engine = new SLuaEngine(new RecordingLog(), runtime ?? ((settings, log) => true));
        engine.Initialise(config);
        engine.AddRegion(scene);
        engine.RegionLoaded(scene);
        return scene;
    }

    private static IConfigSource Config(string section, string enabled)
    {
        var source = new IniConfigSource();
        if (section != null)
        {
            IConfig c = source.AddConfig(section);
            if (enabled != null) c.Set("Enabled", enabled);
        }
        return source;
    }

    [Fact]
    public void NoConfigSectionRegistersNothing()
    {
        Scene scene = Region(Config(null, null), out _);
        Assert.Empty(Engines(scene));
    }

    [Fact]
    public void SectionWithoutEnabledRegistersNothing()
    {
        Scene scene = Region(Config(EngineIdentity.ConfigSection, null), out _);
        Assert.Empty(Engines(scene));
    }

    [Fact]
    public void EnabledFalseRegistersNothing()
    {
        Scene scene = Region(Config(EngineIdentity.ConfigSection, "false"), out _);
        Assert.Empty(Engines(scene));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void WhenOffTheNativeLibraryIsNeverLookedFor(string enabled)
    {
        int loads = 0;
        var log = new RecordingLog();
        var engine = new SLuaEngine(log, (settings, l) => { loads++; return true; });
        engine.Initialise(Config(EngineIdentity.ConfigSection, enabled));
        engine.AddRegion(new SceneHelpers().SetupScene());

        Assert.Equal(0, loads);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void EnabledTrueRegistersTheEngineUnderItsName()
    {
        Scene scene = Region(Config(EngineIdentity.ConfigSection, "true"), out SLuaEngine engine);
        IScriptModule registered = Assert.Single(Engines(scene));
        Assert.Same(engine, registered);
        Assert.Equal("SLua", registered.ScriptEngineName);
    }

    [Fact]
    public void EnabledTrueLoadsTheNativeLibraryOnce()
    {
        int loads = 0;
        var engine = new SLuaEngine(new RecordingLog(), (settings, l) => { loads++; return true; });
        engine.Initialise(Config(EngineIdentity.ConfigSection, "true"));
        engine.AddRegion(new SceneHelpers().SetupScene());

        Assert.Equal(1, loads);
    }

    [Fact]
    public void EnabledButWithoutAUsableLibraryRegistersNothing()
    {
        Scene scene = Region(Config(EngineIdentity.ConfigSection, "true"), out _, (settings, log) => false);
        Assert.Empty(Engines(scene));
    }

    [Fact]
    public void AnotherEnginesEnabledKeyDoesNotEnableThisOne()
    {
        Scene scene = Region(Config("InWorldz.Phlox", "true"), out _);
        Assert.Empty(Engines(scene));
    }

    [Fact]
    public void TheEngineIsARegionModuleTheRegionServerCanDiscoverAndMake()
    {
        // The region server makes every public, non-abstract INonSharedRegionModule it finds with its parameterless
        // constructor (RegionModulesControllerPlugin through DotNetCorePluginsDiscovery).
        Type type = typeof(SLuaEngine);
        Assert.True(type.IsPublic);
        Assert.False(type.IsAbstract);
        Assert.True(typeof(INonSharedRegionModule).IsAssignableFrom(type));
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
    }

    [Fact]
    public void TheEngineIsLoadedWithTheRegionsDeferredModulesAndNeverSkipped()
    {
        // The region server adds a module that names a replaceable interface after every module that names none, and
        // skips it only when a module has registered that interface. The engine names one that nothing registers, so it
        // is always loaded, and always after the region's other script engines (which name none).
        Type marker = new SLuaEngine(new RecordingLog(), (s, l) => true).ReplaceableInterface;
        Assert.NotNull(marker);
        Assert.True(marker.IsInterface);
        Assert.False(marker.IsAssignableFrom(typeof(SLuaEngine)));

        Scene scene = Region(Config(EngineIdentity.ConfigSection, "true"), out _);
        object registered = typeof(Scene).GetMethod("RequestModuleInterface").MakeGenericMethod(marker).Invoke(scene, null);
        Assert.Null(registered);
    }

    [Fact]
    public void AddedInTheRegionServersOrderTheEngineComesAfterTheOtherEngines()
    {
        // RegionModulesControllerPlugin.AddRegionToModules: Initialise and AddRegion of the modules with no replaceable
        // interface (Phlox, YEngine), then Initialise and AddRegion of the deferred ones (this engine), then RegionLoaded.
        Scene scene = new SceneHelpers().SetupScene();
        var other = new OtherEngine("InWorldz.Phlox");
        other.Initialise(new IniConfigSource());
        other.AddRegion(scene);
        var engine = new SLuaEngine(new RecordingLog(), (s, l) => true);
        engine.Initialise(Config(EngineIdentity.ConfigSection, "true"));
        engine.AddRegion(scene);

        // A module that asks for "the" script engine gets the one it had before.
        Assert.Same(other, scene.RequestModuleInterface<IScriptModule>());
        Assert.Equal(new[] { "InWorldz.Phlox", "SLua" }, Engines(scene).Select(e => e.ScriptEngineName).ToArray());
    }

    private sealed class OtherEngine : MockScriptEngine, IScriptModule
    {
        public OtherEngine(string name) { ScriptEngineName = name; }
        public new string ScriptEngineName { get; }
    }

    [Fact]
    public void TheEngineMadeAsTheRegionServerMakesItRegistersNothingByDefault()
    {
        var engine = (INonSharedRegionModule)Activator.CreateInstance(typeof(SLuaEngine));
        Scene scene = new SceneHelpers().SetupScene();
        engine.Initialise(new IniConfigSource());
        engine.AddRegion(scene);
        engine.RegionLoaded(scene);

        Assert.Empty(Engines(scene));
    }

    [Fact]
    public void TheRegionServerDefaultsCarryNoSectionForTheEngine()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Tranquillity.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        string[] defaults = Directory.GetFiles(Path.Combine(dir, "Source", "OpenSim.Server.RegionServer"), "*.ini", SearchOption.AllDirectories);
        Assert.NotEmpty(defaults);
        foreach (string file in defaults)
            Assert.DoesNotContain("[" + EngineIdentity.ConfigSection + "]", File.ReadAllText(file));
    }

    // ---- what the region tells viewers (OpenSimExtras["ScriptEngines"]) ----

    private static string[] ListedEngines(IConfigSource config, RuntimeLoader runtime = null)
    {
        Scene scene = Region(config, out _, runtime);
        var features = new SimulatorFeaturesModule();
        SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), features);

        Assert.True(features.TryGetOpenSimExtraFeature("ScriptEngines", out OSD list));
        return ((OSDArray)list).Select(e => e.AsString()).ToArray();
    }

    [Fact]
    public void OffTheRegionListsNoSLuaEngine()
    {
        Assert.Empty(ListedEngines(Config(EngineIdentity.ConfigSection, "false")));
        Assert.Empty(ListedEngines(new IniConfigSource()));
    }

    [Fact]
    public void OnTheRegionListsTheEngineByName()
    {
        Assert.Equal(new[] { "SLua" }, ListedEngines(Config(EngineIdentity.ConfigSection, "true")));
    }

    [Fact]
    public void OnWithoutAUsableLibraryTheRegionListsNoSLuaEngine()
    {
        Assert.Empty(ListedEngines(Config(EngineIdentity.ConfigSection, "true"), (settings, log) => false));
    }
}
