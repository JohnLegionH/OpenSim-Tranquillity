/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How the Jolt scene gives a vehicle the wind: it asks the region's wind module (IWindModule.WindSpeed) for the position,
/// and a region without one gives none. Serial with the other native tests: a JoltScene loads the native library.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleWindHostTests
{

    private sealed class StubWindModule : IWindModule
    {
        public readonly List<(int X, int Y, int Z)> Asked = new();
        public string Name => "stub";
        public Type ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void Close() { }
        public void AddRegion(Scene scene) { }
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }
        public Vector3 WindSpeed(int x, int y, int z) { Asked.Add((x, y, z)); return new Vector3(x, y, 0f); }
        public void WindSet(int type, Vector3 loc, Vector3 speed) { }
        public void WindParamSet(string plugin, string param, float value) { }
        public float WindParamGet(string plugin, string param) => 0f;
        public string WindActiveModelPluginName => "stub";
    }

    private static JoltScene NewScene()
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt");
        var scene = new JoltScene();
        scene.Initialise(config);
        return scene;
    }

    [Fact]
    public void The_scene_asks_its_wind_module_for_the_wind_at_the_whole_metre_below_the_position()
    {
        JoltScene scene = NewScene();
        try
        {
            var module = new StubWindModule();
            scene.UseWindModule(module);
            Assert.True(scene.TryGetWind(new Vector3(40.9f, 17.2f, 23.5f), out Vector3 wind));
            Assert.Equal((40, 17, 23), module.Asked.Single());
            Assert.Equal(new Vector3(40f, 17f, 0f), wind);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_scene_with_no_wind_module_gives_no_wind_and_a_position_that_is_not_finite_is_not_asked()
    {
        JoltScene scene = NewScene();
        try
        {
            Assert.False(scene.TryGetWind(new Vector3(40f, 17f, 23f), out Vector3 none));
            Assert.Equal(Vector3.Zero, none);

            var module = new StubWindModule();
            scene.UseWindModule(module);
            Assert.False(scene.TryGetWind(new Vector3(float.NaN, 17f, 23f), out _));
            Assert.Empty(module.Asked);
        }
        finally { scene.Dispose(); }
    }
}
