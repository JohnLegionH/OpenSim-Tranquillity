/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OpenSim.Framework;

/// <summary>
/// TEMPORARY measurement aid for border crossings. Not for upstream.
/// Writes one tab separated line per mark to the file named by the environment variable CROSSTRACE_FILE, and only for
/// objects whose name starts with "XT-" (<see cref="Mark"/>) or for anything (<see cref="MarkAny"/>). With the variable
/// unset every call returns at once. Columns: UTC microseconds since the Unix epoch, process id, region, name, phase,
/// detail. Region servers of one test share one machine clock, so their lines can be ordered against each other.
/// </summary>
public static class CrossTraceLog
{
    private static readonly string s_path = Environment.GetEnvironmentVariable("CROSSTRACE_FILE");
    private static readonly object s_lock = new();
    private static StreamWriter s_writer;
    private static readonly long s_baseTicks = DateTime.UtcNow.Ticks;
    private static readonly long s_baseStamp = Stopwatch.GetTimestamp();
    private static readonly int s_pid = Environment.ProcessId;

    public static bool Enabled => s_path is not null;

    public static bool Wanted(string name) => s_path is not null && name is not null && name.Contains("XT-", StringComparison.Ordinal);

    public static long NowMicros()
    {
        long ticks = s_baseTicks + (long)((Stopwatch.GetTimestamp() - s_baseStamp) * (10_000_000.0 / Stopwatch.Frequency));
        return (ticks - DateTime.UnixEpoch.Ticks) / 10;
    }

    public static void Mark(string region, string name, string phase, string detail = "")
    {
        if (Wanted(name))
            Write(region, name, phase, detail);
    }

    public static void MarkAny(string region, string who, string phase, string detail = "")
    {
        if (s_path is not null)
            Write(region, who, phase, detail);
    }

    private static void Write(string region, string name, string phase, string detail)
    {
        long t = NowMicros();
        try
        {
            lock (s_lock)
            {
                s_writer ??= new StreamWriter(new FileStream(s_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                s_writer.Write($"{t}\t{s_pid}\t{region}\t{name}\t{phase}\t{detail}\n");
            }
        }
        catch
        {
            // a measurement aid never disturbs the code it measures
        }
    }
}
