/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace OpenSim.Archive.GltfExport;

/// <summary>
/// An optional memory log of an export, off unless <see cref="ExportOptions.DiagnosticsPath"/> is set. Every so many builds
/// (and while the archive is read, every so many object files) it appends one JSON line: the managed heap, the working set,
/// the size of each generation after the last collection, and the size of each structure that lives across builds. It reads
/// counters only; it never forces a collection, so it does not change how the run behaves.
/// </summary>
internal sealed class ExportDiagnostics
{
    private readonly string m_path;
    private readonly int m_every;
    private readonly Stopwatch m_clock = Stopwatch.StartNew();
    private readonly Dictionary<string, int> m_next = new Dictionary<string, int>();

    private readonly bool m_collect;

    public ExportDiagnostics(string path, int every, bool collect = false)
    {
        m_path = path;
        m_collect = collect;
        m_every = every > 0 ? every : 100;
    }

    /// <summary>True when a line is due for this count (the count of builds done, or of object files read) in this phase.</summary>
    public bool Due(string phase, int count) => !m_next.TryGetValue(phase, out int next) || count >= next;

    /// <summary>Appends a line. <paramref name="structures"/> are name and count (or byte size) pairs, written as they come.</summary>
    public void Write(string phase, int count, IEnumerable<KeyValuePair<string, long>> structures)
    {
        m_next[phase] = count + m_every;
        // optional: a full collection first, so the heap size read is what the run holds and not what it has not yet let go of
        long live = m_collect ? CollectNow() : -1;
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"phase\":\"").Append(phase).Append('"');
        Add(sb, "count", count);
        Add(sb, "seconds", (long)m_clock.Elapsed.TotalSeconds);
        Add(sb, "managedHeapBytes", GC.GetTotalMemory(false));
        if (m_collect)
            Add(sb, "liveHeapBytes", live);
        Add(sb, "heapSizeAfterLastGcBytes", info.HeapSizeBytes);
        Add(sb, "fragmentedBytes", info.FragmentedBytes);
        Add(sb, "gen0Bytes", Gen(info, 0));
        Add(sb, "gen1Bytes", Gen(info, 1));
        Add(sb, "gen2Bytes", Gen(info, 2));
        Add(sb, "lohBytes", Gen(info, 3));
        Add(sb, "pohBytes", Gen(info, 4));
        Add(sb, "collections0", GC.CollectionCount(0));
        Add(sb, "collections2", GC.CollectionCount(2));
        using (Process me = Process.GetCurrentProcess())
        {
            Add(sb, "workingSetBytes", me.WorkingSet64);
            Add(sb, "peakWorkingSetBytes", me.PeakWorkingSet64);
        }
        foreach (KeyValuePair<string, long> s in structures)
            Add(sb, s.Key, s.Value);
        sb.Append("}\n");
        File.AppendAllText(m_path, sb.ToString(), new UTF8Encoding(false));
    }

    private static long CollectNow()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        return GC.GetTotalMemory(false);
    }

    private static long Gen(GCMemoryInfo info, int index) => index < info.GenerationInfo.Length ? info.GenerationInfo[index].SizeAfterBytes : 0;

    private static void Add(StringBuilder sb, string name, long value) =>
        sb.Append(",\"").Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
}
