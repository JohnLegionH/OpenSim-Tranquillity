/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// changed(CHANGED_WEATHER) from the wind module: each real change of the weather state raises the script changed event
/// once for every prim whose scripts handle changed(), with the bit 65536 and nothing else; a set that changes nothing,
/// a refused set and a region with weather off raise none.
/// </summary>
// Each test owns its scene and module; MainConsole is not touched (the assembly runs its tests one at a time anyway).
public class WeatherChangedTests : OpenSimTestCase
{
    private static readonly UUID Obj = UUID.Random();
    private static readonly UUID Owner = UUID.Random();

    private static (TestScene Scene, WindModule Module) Region(bool weather, string interval = "0")
    {
        TestScene scene = new SceneHelpers().SetupScene("Weather test region", UUID.Random(), 1000, 1000, 256, 256, new IniConfigSource());
        var config = new IniConfigSource();
        config.AddConfig("Wind").Set("wind_plugin", "SimpleRandomWind");
        IConfig w = config.AddConfig("Weather");
        w.Set("enabled", weather ? "true" : "false");
        w.Set("min_change_interval", interval);
        var module = new WindModule();
        module.Initialise(config);
        module.AddRegion(scene);
        return (scene, module);
    }

    // A prim whose scripts handle the given events, as an engine reports them (SceneObjectPart.SetScriptEvents).
    private static SceneObjectPart Scripted(SceneObjectPart part, scriptEvents events)
    {
        part.SetScriptEvents(UUID.Random(), (ulong)events);
        return part;
    }

    private static List<(uint LocalId, uint Change)> Record(Scene scene)
    {
        var seen = new List<(uint, uint)>();
        scene.EventManager.OnScriptChangedEvent += (id, change, _) => { lock (seen) seen.Add((id, change)); };
        return seen;
    }

    [Fact]
    public void TheWeatherConstantIsTheBitAboveEverySecondLifeOpenSimAndHalcyonFlag()
    {
        Assert.Equal(65536u, (uint)Changed.WEATHER);
        // The highest flag the core defines besides it (OpenSimulator's CHANGED_POSITION) is below it.
        Assert.True((uint)Changed.WEATHER > (uint)Changed.POSITION);
        foreach (Changed c in Enum.GetValues<Changed>().Where(c => c != Changed.WEATHER))
            Assert.Equal(0u, (uint)c & (uint)Changed.WEATHER);
    }

    [Fact]
    public void AChangeReachesEveryPrimWhoseScriptsHandleChangedOnce()
    {
        var (scene, module) = Region(weather: true);
        SceneObjectGroup a = SceneHelpers.AddSceneObject(scene, "handles changed", Owner);
        Scripted(a.RootPart, scriptEvents.changed | scriptEvents.touch_start);
        SceneObjectGroup b = SceneHelpers.AddSceneObject(scene, "touch only", Owner);
        Scripted(b.RootPart, scriptEvents.touch_start);
        SceneHelpers.AddSceneObject(scene, "no scripts", Owner);
        SceneObjectGroup linkset = SceneHelpers.CreateSceneObject(2, Owner, "linkset", 0x40);
        scene.AddSceneObject(linkset);
        SceneObjectPart child = Scripted(linkset.Parts.Single(p => !p.IsRoot), scriptEvents.changed);

        var seen = Record(scene);
        Assert.Equal(WeatherResult.Changed, module.Weather.Set("storm", 0.7f, 0f, Obj, Owner));

        Assert.Equal(
            new[] { (a.RootPart.LocalId, 65536u), (child.LocalId, 65536u) }.OrderBy(x => x.Item1),
            seen.OrderBy(x => x.LocalId));
    }

    [Fact]
    public void EachRealChangeIsHeardOnceAndASetThatChangesNothingIsNotHeard()
    {
        var (scene, module) = Region(weather: true);
        SceneObjectPart listener = Scripted(SceneHelpers.AddSceneObject(scene, "listener", Owner).RootPart, scriptEvents.changed);
        var seen = Record(scene);

        Assert.Equal(WeatherResult.Changed, module.Weather.Set("storm", 0.7f, 0f, Obj, Owner));
        Assert.Equal(WeatherResult.Unchanged, module.Weather.Set("storm", 0.7f, 0f, Obj, Owner));
        Assert.Equal(WeatherResult.Unchanged, module.Weather.Set("storm", 0.72f, 0f, Obj, Owner));
        Assert.Single(seen);

        Assert.Equal(WeatherResult.Changed, module.Weather.Set("clear", 0f, 0f, Obj, Owner));
        Assert.Equal(new[] { (listener.LocalId, 65536u), (listener.LocalId, 65536u) }, seen);
    }

    [Fact]
    public void ARefusedSetIsNotHeard()
    {
        var (scene, module) = Region(weather: true, interval: "5");
        Scripted(SceneHelpers.AddSceneObject(scene, "listener", Owner).RootPart, scriptEvents.changed);
        var seen = Record(scene);

        Assert.Equal(WeatherResult.Invalid, module.Weather.Set("storm", 2f, 0f, Obj, Owner));
        Assert.Empty(seen);
        Assert.Equal(WeatherResult.Changed, module.Weather.Set("storm", 0.5f, 0f, Obj, Owner));
        Assert.Single(seen);
        Assert.Equal(WeatherResult.Throttled, module.Weather.Set("clear", 0f, 0f, Obj, Owner));
        Assert.Single(seen);
    }

    [Fact]
    public void WithWeatherOffNothingIsSetOrHeard()
    {
        var (scene, module) = Region(weather: false);
        Scripted(SceneHelpers.AddSceneObject(scene, "listener", Owner).RootPart, scriptEvents.changed);
        var seen = Record(scene);

        Assert.False(module.Weather.Enabled);
        Assert.Equal(WeatherResult.Disabled, module.Weather.Set("storm", 0.5f, 0f, Obj, Owner));
        Assert.Empty(seen);
        Assert.Equal(0, module.Weather.Current.Serial);
    }

    [Fact]
    public void ARegionsChangeIsNotHeardInAnotherRegion()
    {
        var (scene1, module1) = Region(weather: true);
        var (scene2, _) = Region(weather: true);
        Scripted(SceneHelpers.AddSceneObject(scene1, "here", Owner).RootPart, scriptEvents.changed);
        Scripted(SceneHelpers.AddSceneObject(scene2, "there", Owner).RootPart, scriptEvents.changed);
        var seen1 = Record(scene1);
        var seen2 = Record(scene2);

        module1.Weather.Set("storm", 0.5f, 0f, Obj, Owner);
        Assert.Single(seen1);
        Assert.Empty(seen2);
    }

    [Fact]
    public void TellScriptsCountsThePrimsTold()
    {
        var (scene, _) = Region(weather: true);
        Scripted(SceneHelpers.AddSceneObject(scene, "one", Owner).RootPart, scriptEvents.changed);
        Scripted(SceneHelpers.AddSceneObject(scene, "two", Owner).RootPart, scriptEvents.changed);
        Scripted(SceneHelpers.AddSceneObject(scene, "not this", Owner).RootPart, scriptEvents.timer);
        Assert.Equal(2, WindModule.TellScriptsWeatherChanged(scene));
        Assert.Equal(0, WindModule.TellScriptsWeatherChanged(null));
    }
}
