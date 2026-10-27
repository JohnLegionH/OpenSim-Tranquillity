/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.World.NPC;

/// <summary>
/// An avatar's appearance as a notecard in an object's contents, in the format osNpcSaveAppearance writes.
/// </summary>
public static class AppearanceNotecard
{
    public static TaskInventoryItem Save(SceneObjectPart host, IAssetService assets, string name, AvatarAppearance appearance, bool includeHuds)
        => null;

    public static bool TryLoad(SceneObjectPart host, IAssetService assets, string nameOrAssetId, out AvatarAppearance appearance, out string reason)
    {
        appearance = null;
        reason = null;
        return false;
    }
}
