/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.GltfExport;

/// <summary>
/// Keeps the managed heap from filling up with garbage between collections. Each build allocates a few large arrays (geometry
/// buffers, PNG and glTF bytes) that go to the large object heap, which the runtime collects only in a full collection; left
/// alone it lets that garbage grow towards the heap limit before collecting. After each build, once the heap is past a trigger
/// the governor runs one full collection, so the peak depends on the memory cap and on what one build needs, not on how long the run has gone on.
/// The trigger is a quarter of the cap, and never less than twice what the last collection left, so a run whose live data is
/// large collects less often and never after every build.
/// </summary>
internal sealed class HeapGovernor
{
    private const long Floor = 64L * 1024 * 1024;
    private readonly long m_low, m_high;
    private readonly Func<long> m_heapBytes;
    private readonly Action m_collect;
    private long m_trigger;

    public int Collections { get; private set; }
    /// <summary>The heap size, in bytes, past which the next build's end collects.</summary>
    internal long Trigger => m_trigger;

    public HeapGovernor(long capBytes) : this(capBytes, () => GC.GetTotalMemory(false), () => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, false))
    {
    }

    /// <summary>For the tests: the heap size and the collection are replaced.</summary>
    internal HeapGovernor(long capBytes, Func<long> heapBytes, Action collect)
    {
        m_heapBytes = heapBytes;
        m_collect = collect;
        m_low = Math.Max(Floor, capBytes / 4);
        m_high = Math.Max(Floor, capBytes / 4 * 3);
        m_trigger = m_low;
    }

    /// <summary>Call after a build is finished; collects when the heap is past the trigger.</summary>
    public void AfterBuild()
    {
        if (m_heapBytes() < m_trigger)
            return;
        m_collect();
        Collections++;
        m_trigger = Math.Clamp(m_heapBytes() * 2, m_low, m_high);
    }
}
