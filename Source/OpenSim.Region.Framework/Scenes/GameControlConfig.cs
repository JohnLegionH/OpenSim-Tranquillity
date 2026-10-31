/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes;

// Temporary stub so the tests compile: every member returns a default value.
public static class GameControlConfig
{
    public static bool IsEnabled(IConfigSource config, RegionInfo region) => false;
}
