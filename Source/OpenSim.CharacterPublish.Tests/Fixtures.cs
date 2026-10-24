/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish.Tests;

/// <summary>Neutral fixture values: random-looking but fixed ids, no real names or assets.</summary>
internal static class Fixtures
{
    public static readonly UUID Creator = new("11111111-2222-4333-8444-555555555555");
    public static readonly UUID Owner = new("66666666-7777-4888-8999-aaaaaaaaaaaa");
    public static readonly UUID Group = new("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff");
    public static readonly UUID TexA = new("0a1b2c3d-4e5f-4a6b-8c7d-8e9fa0b1c2d3");
    public static readonly UUID TexB = new("1a2b3c4d-5e6f-4a7b-8c8d-9eaf0b1c2d3e");
    public static readonly UUID TexC = new("2a3b4c5d-6e7f-4a8b-8c9d-aeb00c1d2e3f");

    public static PermissionBlock Perms => PermissionBlock.ForOwner(Creator, Owner, PermissionMasks.CopyModifyTransfer);

    /// <summary>A minimal valid wearable of the given type with a few values of every kind of formatting.</summary>
    public static WearableDocument Wearable(WearableKind kind) => new()
    {
        Name = "Test " + kind,
        Description = "A fixture wearable",
        Permissions = Perms,
        SaleType = SaleType.Not,
        SalePrice = 10,
        Type = kind,
        Parameters = new SortedDictionary<int, float> { [1] = 0f, [2] = 0.59f, [3] = -0.59f, [4] = 1.2f, [5] = 24f, [6] = -1f, [77] = 0.5f },
        Textures = new SortedDictionary<int, UUID> { [3] = TexA, [8] = TexB, [44] = TexC },
    };

    public static IEnumerable<WearableKind> AllKinds => Enum.GetValues<WearableKind>().Where(k => k != WearableKind.Invalid);
}
