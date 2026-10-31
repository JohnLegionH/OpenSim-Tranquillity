/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// Whether game-controller input (the viewer's GameControlData message) is accepted for a region.
/// </summary>
/// <remarks>
/// The grid-wide switch is <c>[GameControl] Enabled</c> in the simulator configuration. It is off when the
/// section or key is missing. A region can only opt out: <c>GameControlEnabled = false</c> in the region's own
/// settings turns it off for that region. A region setting never turns it on when the grid-wide switch is off.
/// </remarks>
public static class GameControlConfig
{
    public const string SectionName = "GameControl";
    public const string EnabledKey = "Enabled";
    public const string RegionOptOutKey = "GameControlEnabled";

    public static bool IsEnabled(IConfigSource config, RegionInfo region)
    {
        IConfig section = config?.Configs[SectionName];
        if (section is null || !section.GetBoolean(EnabledKey, false))
            return false;

        string regionSetting = region?.GetSetting(RegionOptOutKey);
        if (regionSetting is not null && bool.TryParse(regionSetting.Trim(), out bool regionEnabled) && !regionEnabled)
            return false;

        return true;
    }
}
