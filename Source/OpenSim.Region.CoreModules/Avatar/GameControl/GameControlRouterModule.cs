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

namespace OpenSim.Region.CoreModules.Avatar.GameControl;

/// <summary>Posts game_control to the scripts that may hear the avatar's game controller.</summary>
public sealed class GameControlRouterModule : INonSharedRegionModule
{
    /// <summary>PERMISSION_GAME_CONTROL.</summary>
    public const int PermissionGameControl = 0x100000;

    public const string EventName = "game_control";

    public string Name => "GameControlRouterModule";

    public Type ReplaceableInterface => null;

    /// <summary>True while the module is subscribed to its region's events.</summary>
    public bool Active => false;

    /// <summary>How many script items are indexed as holding the game-control grant of <paramref name="granter"/>.</summary>
    public int GrantIndexCount(UUID granter) => 0;

    public void Initialise(IConfigSource source) { }

    public void AddRegion(Scene scene) { }

    public void RegionLoaded(Scene scene) { }

    public void RemoveRegion(Scene scene) { }

    public void Close() { }
}
