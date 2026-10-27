/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// How an NPC bake is told apart from an avatar's bake and from anything a person made: its asset description
/// carries the input hash and, after it, a mark.
/// </summary>
public static class NpcBakeMark
{
    public static string Describe(string inputHash) => "";

    public static bool IsMarked(AssetMetadata metadata) => false;
}

/// <summary>What one NPC's removal did to the asset store.</summary>
public sealed record NpcCleanupResult(IReadOnlyList<UUID> Removed, IReadOnlyList<UUID> WouldRemove, IReadOnlyList<UUID> Kept);

/// <summary>
/// Deletes the bakes of an NPC that has gone, when no other NPC holds them and this module made and marked them.
/// </summary>
public sealed class NpcBakeCleanup
{
    public NpcBakeCleanup(NpcBakeShare share, bool enabled, bool dryRun) { }

    /// <summary>Every asset this cleanup has deleted.</summary>
    public IReadOnlyList<UUID> Removed => new List<UUID>();

    /// <summary>Every asset a dry run found it would have deleted.</summary>
    public IReadOnlyList<UUID> WouldRemove => new List<UUID>();

    public NpcCleanupResult Run(UUID npc, IAssetService assets)
        => new(new List<UUID>(), new List<UUID>(), new List<UUID>());
}
