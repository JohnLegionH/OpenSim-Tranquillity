/*
 * Legion Grid - NGC develop plugin registration for the Jolt physics region module.
 *
 * NGC develop replaced Mono.Addins with the IPluginRegistryProvider host: instead of an
 * [Extension] attribute on the module class (the classic OpenSim / Legion-scratch mechanism),
 * each physics-module assembly ships a PluginRegistration that registers its region module under
 * /OpenSim/RegionModules. Mirrors Source/OpenSim.Region.PhysicsModules.BulletS/PluginRegistration.cs.
 * The module class (LegionJoltScene) is otherwise unchanged; it still self-selects on
 * [Startup] physics = Jolt via its Name.
 */

using System;
using System.Reflection;
using OpenSim.Framework;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    public class PluginRegistration : IPluginRegistryProvider
    {
        public void RegisterPlugins(PluginRegistry registry)
        {
            RegisterByName(registry, "/OpenSim/RegionModules", "LegionJoltPhysicsScene",
                "OpenSim.Region.PhysicsModules.LegionJolt.LegionJoltScene", "LegionJoltPhysicsScene");
        }

        private static void RegisterByName(PluginRegistry registry, string extensionPath, string id, string typeName, string displayName)
        {
            Assembly assembly = typeof(PluginRegistration).Assembly;
            Type type = assembly.GetType(typeName, false);
            if (type == null)
                return;

            registry.Register(
                extensionPath,
                new PluginDescriptor(id, type, displayName, "0.9"));
        }
    }
}
