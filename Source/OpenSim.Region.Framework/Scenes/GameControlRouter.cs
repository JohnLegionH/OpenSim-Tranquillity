/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// Takes the region's game-controller events and posts game_control to the scripts that may hear them.
/// </summary>
public sealed class GameControlRouter
{
    public GameControlRouter(Scene scene)
    {
    }
}
