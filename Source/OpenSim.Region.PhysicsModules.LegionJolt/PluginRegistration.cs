/*
 * Legion Grid — Jolt physics region module.
 *
 * RC plugin registration. Mirrors Source/OpenSim.Region.PhysicsModules.ubODE/PluginRegistration.cs:
 * the host's DotNetCorePlugins discovery (DotNetCorePluginsDiscovery.GetExtensionNodes) scans the
 * plugin directory for assemblies exporting IPluginRegistryProvider and calls RegisterPlugins. We
 * register the region-module type at /OpenSim/RegionModules so the RegionModulesController picks it
 * up exactly like ubODE and BulletSim.
 *
 * Unlike ubODE (which splits ubODEModule : INonSharedRegionModule from ODEScene : PhysicsScene),
 * LegionJoltScene is BOTH the region module AND the PhysicsScene (mirroring BulletSim's BSScene), so
 * that single type is what we register.
 */

using System.Reflection;
using OpenSim.Framework;

namespace OpenSim.Region.PhysicsModules.LegionJolt;

public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        RegisterByName(registry, "/OpenSim/RegionModules", "LegionJoltPhysicsScene", "OpenSim.Region.PhysicsModules.LegionJolt.LegionJoltScene", "LegionJoltPhysicsScene");
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
