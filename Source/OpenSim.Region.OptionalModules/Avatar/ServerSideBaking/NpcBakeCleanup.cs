/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// How an NPC bake is told apart from an avatar's bake and from anything a person made: its asset description
/// carries the input hash and, after it, a mark. The bake's name (<see cref="BakeOrchestrator.AssetNameFor"/>) and
/// its type are checked as well, so a texture a person made is never taken for one, whatever its description says.
/// </summary>
public static class NpcBakeMark
{
    /// <summary>The word that follows the hash in an NPC bake's description.</summary>
    public const string Mark = "npc-bake";

    /// <summary>The description an NPC bake is stored with: the input hash, then the mark.</summary>
    public static string Describe(string inputHash) => inputHash + " " + Mark;

    /// <summary>True for a texture named as a bake whose description ends with the mark.</summary>
    public static bool IsMarked(AssetMetadata metadata)
        => metadata is not null
           && metadata.Type == (sbyte)AssetType.Texture
           && metadata.Name is not null && metadata.Name.StartsWith("bake:", StringComparison.Ordinal)
           && metadata.Description is not null && metadata.Description.EndsWith(" " + Mark, StringComparison.Ordinal);
}

/// <summary>What one NPC's removal did to the asset store.</summary>
/// <param name="Removed">Assets deleted.</param>
/// <param name="WouldRemove">Assets a dry run found it would have deleted.</param>
/// <param name="Kept">Assets nobody holds any more that were left alone because they do not carry the mark.</param>
public sealed record NpcCleanupResult(IReadOnlyList<UUID> Removed, IReadOnlyList<UUID> WouldRemove, IReadOnlyList<UUID> Kept);

/// <summary>
/// Deletes the bakes of an NPC that has gone, when no other NPC holds them and this module made and marked them.
/// An NPC's holds are let go in every mode, so the count of holders stays right when the cleanup is off.
///
/// <para>
/// Only bakes this process stored are offered by <see cref="NpcBakeShare.Release"/>, and each one is looked up
/// again before it is deleted: it must still be there and must carry <see cref="NpcBakeMark"/>. A bake left by an
/// earlier run, or by a crash, is not known to the share and stays where it is.
/// </para>
/// </summary>
public sealed class NpcBakeCleanup
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(NpcBakeCleanup));

    private readonly NpcBakeShare m_share;
    private readonly bool m_enabled;
    private readonly bool m_dryRun;
    private readonly object m_lock = new();
    private readonly List<UUID> m_removed = new();
    private readonly List<UUID> m_wouldRemove = new();

    public NpcBakeCleanup(NpcBakeShare share, bool enabled, bool dryRun)
    {
        m_share = share;
        m_enabled = enabled;
        m_dryRun = dryRun;
    }

    /// <summary>Every asset this cleanup has deleted.</summary>
    public IReadOnlyList<UUID> Removed { get { lock (m_lock) return m_removed.ToArray(); } }

    /// <summary>Every asset a dry run found it would have deleted.</summary>
    public IReadOnlyList<UUID> WouldRemove { get { lock (m_lock) return m_wouldRemove.ToArray(); } }

    /// <summary>The NPC is gone: let go of its bakes, and delete (or in a dry run, list) those nobody else holds.</summary>
    public NpcCleanupResult Run(UUID npc, IAssetService assets)
    {
        var removed = new List<UUID>();
        var would = new List<UUID>();
        var kept = new List<UUID>();
        if (m_share is null || assets is null) return new NpcCleanupResult(removed, would, kept);

        var released = m_share.Release(npc);
        if (!m_enabled) return new NpcCleanupResult(removed, would, kept);

        foreach (var id in released)
        {
            try
            {
                var metadata = assets.GetMetadata(id.ToString());
                if (metadata is null) continue;   // already gone
                if (!NpcBakeMark.IsMarked(metadata))
                {
                    kept.Add(id);
                    m_log.LogDebug("[SSB]: NPC {Npc}: asset {Asset} is not marked as an NPC bake; not touched", npc, id);
                    continue;
                }
                if (m_dryRun)
                {
                    would.Add(id);
                    m_log.LogInformation("[SSB]: NPC bake cleanup (dry run): NPC {Npc} is gone; would remove bake {Asset} ({Name})",
                        npc, id, metadata.Name);
                    continue;
                }
                if (assets.Delete(id.ToString()))
                {
                    removed.Add(id);
                    m_log.LogInformation("[SSB]: NPC bake cleanup: NPC {Npc} is gone; removed bake {Asset} ({Name})", npc, id, metadata.Name);
                }
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "[SSB]: NPC bake cleanup: NPC {Npc}: bake {Asset} could not be removed", npc, id);
            }
        }

        lock (m_lock)
        {
            m_removed.AddRange(removed);
            m_wouldRemove.AddRange(would);
        }
        return new NpcCleanupResult(removed, would, kept);
    }
}
