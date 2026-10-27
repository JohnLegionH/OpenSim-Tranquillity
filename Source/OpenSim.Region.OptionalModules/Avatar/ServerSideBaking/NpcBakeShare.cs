/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

public sealed class NpcBakeShare
{
    public bool TryFind(BakeChannel channel, string hash, out UUID assetId)
    {
        assetId = UUID.Zero;
        return false;
    }

    public void NoteStored(BakeChannel channel, string hash, UUID assetId)
    {
    }

    public UUID Hold(UUID npc, BakeChannel channel, string hash, UUID assetId)
    {
        return UUID.Zero;
    }

    public void Drop(UUID npc)
    {
    }
}
