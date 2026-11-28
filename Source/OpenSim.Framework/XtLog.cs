/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

/// <summary>
/// TEMPORARY measurement aid for border crossings. Not for upstream.
/// One Debug line per timing point: "[XT] point unix-microseconds region id detail pid=N".
/// The clock is the wall clock read once at process start and advanced by Stopwatch.
/// </summary>
public static class XtLog
{
    private static readonly ILogger s_log = LoggerProvider.CreateLogger("XT");
    private static readonly long s_baseTicks = DateTime.UtcNow.Ticks;
    private static readonly long s_baseStamp = Stopwatch.GetTimestamp();
    private static readonly int s_pid = Environment.ProcessId;
    private static readonly string s_ackSwitch = Path.Combine(AppContext.BaseDirectory, "xt-ack-now");

    /// <summary>Event queue messages taken by the poll being answered on this thread.</summary>
    [ThreadStatic] public static List<string> PendingEvents;
    [ThreadStatic] public static string PendingAgent;

    public static bool Enabled => s_log.IsEnabled(LogLevel.Debug);

    /// <summary>Scratch-only switch: while the file exists, a UseCircuitCode on an existing circuit is ACKed at once.</summary>
    public static bool AckNow => File.Exists(s_ackSwitch);

    public static void Mark(string point, string region, object id, string detail = "")
    {
        if (!s_log.IsEnabled(LogLevel.Debug))
            return;
        long ticks = s_baseTicks + (long)((Stopwatch.GetTimestamp() - s_baseStamp) * (10_000_000.0 / Stopwatch.Frequency));
        long micros = (ticks - DateTime.UnixEpoch.Ticks) / 10;
        s_log.LogDebug($"[XT] {point} {micros} {(region ?? "-").Replace(' ', '_')} {id} {detail} pid={s_pid}");
    }

    /// <summary>The "message" name of a serialised event queue element.</summary>
    public static string EventName(byte[] element)
    {
        string s = System.Text.Encoding.UTF8.GetString(element);
        int k = s.IndexOf("<key>message</key><string>", StringComparison.Ordinal);
        if (k < 0)
            return "?";
        k += "<key>message</key><string>".Length;
        int e = s.IndexOf('<', k);
        return e < 0 ? "?" : s.Substring(k, e - k);
    }
}
