/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// The bakes the NPCs of a simulator hold between them. An NPC is baked like an avatar, under its own id, but NPCs
/// made from one outfit (a village of bots) need not each composite and store the same textures: a bake is found
/// here by its channel and input hash, and an NPC whose inputs hash the same points its index at the asset that is
/// already stored.
///
/// <para>
/// Sharing is why an NPC's old bake cannot simply be superseded the way an avatar's is: another NPC may still use
/// it. <see cref="Hold"/> therefore hands an asset back for deletion only when no NPC holds it any more, and only
/// when this process stored it. An asset known only from a stored index (an earlier run's) is never handed back,
/// because who else uses it cannot be known. State is in memory; a restart starts with nothing shared, which costs
/// one bake per outfit and deletes nothing.
/// </para>
/// </summary>
public sealed class NpcBakeShare
{
    private sealed class Entry
    {
        public BakeChannel Channel;
        public string Hash;
        public bool StoredHere;
        public int Holders;
    }

    private readonly object m_lock = new();
    private readonly Dictionary<(BakeChannel, string), UUID> m_byHash = new();
    private readonly Dictionary<UUID, Entry> m_entries = new();
    private readonly Dictionary<(UUID, BakeChannel), UUID> m_current = new();

    /// <summary>The asset already stored for this channel and input hash, if there is one.</summary>
    public bool TryFind(BakeChannel channel, string hash, out UUID assetId)
    {
        lock (m_lock)
            return m_byHash.TryGetValue((channel, hash), out assetId);
    }

    /// <summary>A bake this process has just stored; it may be deleted later, once no NPC holds it.</summary>
    public void NoteStored(BakeChannel channel, string hash, UUID assetId)
    {
        lock (m_lock)
        {
            var entry = EntryFor(assetId, channel, hash);
            entry.StoredHere = true;
            m_byHash[(channel, hash)] = assetId;
        }
    }

    /// <summary>
    /// The NPC now uses <paramref name="assetId"/> for the channel. Returns the asset it used before when that one
    /// is now held by nobody and may be deleted, otherwise <see cref="UUID.Zero"/>.
    /// </summary>
    public UUID Hold(UUID npc, BakeChannel channel, string hash, UUID assetId)
    {
        lock (m_lock)
        {
            if (m_current.TryGetValue((npc, channel), out var previous) && previous == assetId) return UUID.Zero;

            var entry = EntryFor(assetId, channel, hash);
            entry.Holders++;
            if (!m_byHash.ContainsKey((channel, hash))) m_byHash[(channel, hash)] = assetId;
            m_current[(npc, channel)] = assetId;

            if (previous.IsZero() || !m_entries.TryGetValue(previous, out var old)) return UUID.Zero;
            old.Holders--;
            if (old.Holders > 0 || !old.StoredHere) return UUID.Zero;

            m_entries.Remove(previous);
            if (m_byHash.TryGetValue((old.Channel, old.Hash), out var offered) && offered == previous)
                m_byHash.Remove((old.Channel, old.Hash));
            return previous;
        }
    }

    /// <summary>
    /// The NPC is gone. Its holds are let go; returns the assets that nobody holds now and that this process stored.
    /// </summary>
    public IReadOnlyList<UUID> Release(UUID npc) => new List<UUID>();

    /// <summary>The NPC is gone. Its hold is let go; its bakes stay stored and findable for the next NPC in that outfit.</summary>
    public void Drop(UUID npc)
    {
        lock (m_lock)
        {
            foreach (var channel in System.Enum.GetValues<BakeChannel>())
            {
                if (!m_current.Remove((npc, channel), out var asset)) continue;
                if (m_entries.TryGetValue(asset, out var entry)) entry.Holders--;
            }
        }
    }

    private Entry EntryFor(UUID assetId, BakeChannel channel, string hash)
    {
        if (!m_entries.TryGetValue(assetId, out var entry))
            m_entries[assetId] = entry = new Entry { Channel = channel, Hash = hash };
        return entry;
    }
}
