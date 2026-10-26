/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>
/// The engine is off unless [NGC.Luau] Enabled = true: a stock install loads nothing, and the region keeps
/// the script engines it had before.
/// </summary>
public class RegistrationTests
{
    private static IScriptModule[] Engines(Scene scene) => scene.RequestModuleInterfaces<IScriptModule>();

    private static Scene Region(IConfigSource config, out LuauEngine engine, RuntimeLoader runtime = null)
    {
        Scene scene = new SceneHelpers().SetupScene();
        engine = new LuauEngine(new RecordingLog(), runtime ?? ((settings, log) => true));
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

    [Fact]
    public void EnabledTrueRegistersTheEngineUnderItsName()
    {
        Scene scene = Region(Config(EngineIdentity.ConfigSection, "true"), out LuauEngine engine);
        IScriptModule registered = Assert.Single(Engines(scene));
        Assert.Same(engine, registered);
        Assert.Equal("NGC.Luau", registered.ScriptEngineName);
    }

    [Fact]
    public void AnotherEnginesEnabledKeyDoesNotEnableThisOne()
    {
        Scene scene = Region(Config("InWorldz.Phlox", "true"), out _);
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
}
