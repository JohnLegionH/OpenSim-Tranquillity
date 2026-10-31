/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse.StructuredData;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// The Linden Lab viewer sends GameControlData only to a region whose SimulatorFeatures map has the top-level
    /// boolean GameControlEnabled set to true (viewer release 26.5, llviewerregion.cpp gameControlEnabled()).
    /// The key is added for a region that has game control enabled and for no other.
    /// Each test builds its own scene and module instance, so the class runs in parallel.
    /// </summary>
    public class GameControlSimulatorFeaturesTests
    {
        private static SimulatorFeaturesModule ModuleFor(Scene scene)
        {
            var module = new SimulatorFeaturesModule();
            module.Initialise(new IniConfigSource());
            module.AddRegion(scene);
            return module;
        }

        private static Scene NewScene(bool gameControl)
        {
            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene();
            scene.GameControlEnabled = gameControl;
            return scene;
        }

        [Fact]
        public void AnEnabledRegionAdvertisesGameControlEnabledAsATopLevelTrue()
        {
            SimulatorFeaturesModule module = ModuleFor(NewScene(gameControl: true));

            Assert.True(module.TryGetFeature("GameControlEnabled", out OSD value));
            Assert.Equal(OSDType.Boolean, value.Type);
            Assert.True(value.AsBoolean());
            Assert.False(module.TryGetOpenSimExtraFeature("GameControlEnabled", out _), "must not sit under OpenSimExtras");
        }

        [Fact]
        public void ARegionWithGameControlOffAdvertisesNothing()
        {
            Assert.True(ModuleFor(NewScene(gameControl: true)).TryGetFeature("GameControlEnabled", out _),
                "control case: an enabled region advertises it");

            SimulatorFeaturesModule module = ModuleFor(NewScene(gameControl: false));

            Assert.False(module.TryGetFeature("GameControlEnabled", out _));
            Assert.False(module.GetFeatures().ContainsKey("GameControlEnabled"));
        }

        [Fact]
        public void TheKeyIsPerRegion()
        {
            SimulatorFeaturesModule on = ModuleFor(NewScene(gameControl: true));
            SimulatorFeaturesModule off = ModuleFor(NewScene(gameControl: false));

            Assert.True(on.GetFeatures().ContainsKey("GameControlEnabled"));
            Assert.False(off.GetFeatures().ContainsKey("GameControlEnabled"));
        }
    }
}
