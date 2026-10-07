/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The region module the host loads for Jolt (see PluginRegistration.cs).
//
// The region module controller makes one instance of every registered non-shared module for every region and calls
// Initialise, AddRegion, RegionLoaded, RemoveRegion and Close on it, whatever [Startup] physics names. This module
// reads [Startup] physics and nothing else; unless it names Jolt, every call returns at once. It then never makes a
// JoltScene, so the native is not loaded or hashed, no [Jolt] key is read, no job pool or thread starts, no console
// command is registered and nothing is logged, and the backend, vehicle and binding assemblies are not loaded.
// With physics = Jolt it makes the JoltScene and hands every call to it. ubODE splits its module the same way:
// ubODEModule makes its ODEScene only when selected (Source/OpenSim.Region.PhysicsModules.ubODE/ODEModule.cs, AddRegion).

using System.Runtime.CompilerServices;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    public sealed class JoltModule : INonSharedRegionModule
    {
        // The [Startup] physics value that selects this module. Compared exactly, as every engine compares its own.
        internal const string EngineName = "Jolt";

        // The scene, made in Initialise only when Jolt is selected. Typed as the interface so that this class does
        // not refer to JoltScene's fields, and loading it loads none of the assemblies JoltScene needs.
        private INonSharedRegionModule m_scene;

        public string Name => EngineName;

        public System.Type ReplaceableInterface => null;

        /// <summary>True when [Startup] physics names Jolt.</summary>
        internal static bool Selected(IConfigSource source)
            => source?.Configs["Startup"]?.GetString("physics", string.Empty) == EngineName;

        public void Initialise(IConfigSource source)
        {
            if (!Selected(source))
                return;
            INonSharedRegionModule scene = MakeScene();
            scene.Initialise(source);   // throws, with its one clear error line, when the native or meshing is unusable
            m_scene = scene;
        }

        // Kept out of Initialise so that compiling Initialise does not load JoltScene.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static INonSharedRegionModule MakeScene() => new JoltScene();

        public void AddRegion(Scene scene) => m_scene?.AddRegion(scene);

        public void RegionLoaded(Scene scene) => m_scene?.RegionLoaded(scene);

        public void RemoveRegion(Scene scene) => m_scene?.RemoveRegion(scene);

        public void Close() => m_scene?.Close();
    }
}
