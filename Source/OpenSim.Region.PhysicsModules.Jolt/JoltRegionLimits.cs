/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A region's own [<Region Name>] section may override [Jolt] MaxBodies, MaxBodyPairs or MaxContactConstraints with
// the keys Jolt_MaxBodies, Jolt_MaxBodyPairs and Jolt_MaxContactConstraints - named for their owner the way
// AISv3Module's AIS_Enabled is, because a region section holds settings for many modules. The bare names
// (MaxBodies, MaxBodyPairs, MaxContactConstraints) are not read there; they stay the [Jolt] section's own keys and
// are unchanged by this. One region can run a different limit without touching every other region in the process.
// A region with no override, or no [<Region Name>] section at all, is sized exactly as before.
//
// The section must be in the running configuration (OpenSim.ini, a config-include file, or a file under an
// --inidirectory folder) - the same object [Jolt] itself is read from. It is NOT the region's own Regions/*.ini
// file: RegionInfo's file constructor parses that file into a throwaway IniConfigSource to build its own typed
// fields (RegionInfo.cs, the "IniConfigSource source = new IniConfigSource(filename)" branch) and never merges it
// into the configuration IConfigSource every module receives as Scene.Config, so a key added only there is never
// read by anything. Confirmed live against a running region server: the same keys in a region's own Regions/*.ini
// file had no effect; in a config/overrides/*.ini file they did.

using System.Collections.Generic;
using System.Globalization;
using Nini.Config;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    /// <summary>
    /// MaxBodies, MaxBodyPairs and MaxContactConstraints as one region's own config section overrides them. 0 in
    /// any field means no override there: JoltConfig.ToBackendSettings falls back to the process-wide [Jolt] value,
    /// or to automatic sizing, exactly as it does when the region has no section at all.
    /// </summary>
    internal readonly struct JoltRegionLimits
    {
        internal readonly int MaxBodies;
        internal readonly int MaxBodyPairs;
        internal readonly int MaxContactConstraints;

        private JoltRegionLimits(int maxBodies, int maxBodyPairs, int maxContactConstraints)
        {
            MaxBodies = maxBodies;
            MaxBodyPairs = maxBodyPairs;
            MaxContactConstraints = maxContactConstraints;
        }

        /// <summary>
        /// Reads [&lt;regionName&gt;] from the same config source [Jolt] was read from - <c>Scene.Config</c>, the
        /// configuration built from OpenSim.ini and its includes, NOT the region's own Regions/*.ini file (see the
        /// file header). The convention itself is the one <c>AutoBackupModule</c> and
        /// <c>ServerSideBakingRegion.ResolveEnabled</c> use, with the AIS_Enabled-style Jolt_ prefix: a region
        /// named the same as an existing section (for example a region called "Jolt") then reads that section's
        /// own Jolt_MaxBodies key, never its bare MaxBodies. A missing section, or a key the section does not set,
        /// carries no override (0). An unparsable or out-of-range value warns, exactly as an invalid [Jolt] value
        /// does, and carries no override either - a typo in one region's section must never take that region down.
        /// </summary>
        internal static JoltRegionLimits FromConfig(IConfigSource source, string regionName, List<string> warnings)
        {
            IConfig regionConfig = string.IsNullOrEmpty(regionName) ? null : source?.Configs[regionName];
            if (regionConfig == null)
                return default;
            return new JoltRegionLimits(
                Key(regionConfig, regionName, "MaxBodies", warnings),
                Key(regionConfig, regionName, "MaxBodyPairs", warnings),
                Key(regionConfig, regionName, "MaxContactConstraints", warnings));
        }

        private static int Key(IConfig regionConfig, string regionName, string key, List<string> warnings)
        {
            string regionKey = "Jolt_" + key;
            string raw = regionConfig.GetString(regionKey, null);
            if (raw == null)
                return 0;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                && v >= 0 && v <= JoltConfig.JoltMaxBodies)
                return v;
            warnings?.Add($"[{regionName}] {regionKey} = \"{raw}\" is invalid (expected an integer in [0, {JoltConfig.JoltMaxBodies}]); using [Jolt] {key}.");
            return 0;
        }

        /// <summary>
        /// Where one limit's effective value came from, for the startup line: this region's own section, the
        /// process-wide [Jolt] setting, or "automatic" (MaxBodies computed from the region's capacity; the other
        /// two are then the fixed constants, scaled by area under [Jolt] ScaleCapsWithArea).
        /// </summary>
        internal static string Source(int regionValue, int processValue, string regionName, string key)
            => regionValue > 0 ? $"region override, [{regionName}] Jolt_{key} = {regionValue}"
             : processValue > 0 ? $"process setting, [Jolt] {key} = {processValue}"
             : "automatic";
    }
}
