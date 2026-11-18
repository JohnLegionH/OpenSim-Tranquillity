/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>A fact that runs only when SLUA_SORT_TIMING names the result file, and where the native library can be found.</summary>
public sealed class SortTimingFactAttribute : FactAttribute
{
    public SortTimingFactAttribute()
    {
        Skip = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SLUA_SORT_TIMING"))
            ? "a measurement: set SLUA_SORT_TIMING to a result file name to run it"
            : new RequiresNativeFactAttribute().Skip;
    }
}

/// <summary>
/// A measurement, not a check: how long table.sort takes to sort 100,000 numbers (25 arrays of 4,000) with a comparator function, in a script run by
/// the engine's scheduler. The comparator is called from the sort through the runtime's yieldable-call path, which is the path the
/// runtime's yield-state change touches, so the figure is compared between two runtime library builds (pointed to with
/// SLUA_SHIM_PATH). It runs only when SLUA_SORT_TIMING is set to a file name, and appends one line per run to that file.
/// </summary>
[Collection("native-library")]
public class SortTimingTests
{
    // A script is held to 128 KiB by the runtime, so 100,000 numbers do not fit in one table; 25 sorts of 4,000 numbers sort
    // the same 100,000 elements, and only the sort calls are timed.
    private const string Script =
        "local n, reps = 4000, 25\n" +
        "local t = table.create and table.create(n) or {}\n" +
        "local x, calls, spent = 12345, 0, 0\n" +
        "for r = 1, reps do\n" +
        "  for i = 1, n do x = (x * 1103515245 + 12345) % 2147483648 t[i] = x end\n" +
        "  local c0 = os.clock()\n" +
        "  table.sort(t, function(a, b) calls += 1 return a < b end)\n" +
        "  spent += os.clock() - c0\n" +
        "  for i = 2, n do assert(t[i - 1] <= t[i]) end\n" +
        "end\n" +
        "ll.OwnerSay(string.format('%.4f s in sort, %d comparator calls', spent, calls))\n";

    private static readonly string OutFile = Environment.GetEnvironmentVariable("SLUA_SORT_TIMING");

    [SortTimingFact]
    public void SortingOneHundredThousandNumbersWithAComparatorIsTimed()
    {
        var lines = new List<string>();
        for (int run = 0; run < 5; run++)
        {
            using var h = new SchedulerHarness();
            var watch = Stopwatch.StartNew();
            h.AddAndStart(Script, lsl: false);
            for (int i = 0; i < 200 && h.Marks.Length == 0; i++) { h.Pump(); h.Advance(0.5); }
            watch.Stop();
            Assert.True(h.FaultedScripts.IsEmpty, "faulted: " + string.Join(" / ", h.FaultedScripts.Select(f => f.FaultMessage)));
            Assert.Single(h.Marks);
            lines.Add(Environment.GetEnvironmentVariable("SLUA_SHIM_PATH") + "\trun " + (run + 1) + "\tsort s=" + h.Marks[0] + "\twall s=" + watch.Elapsed.TotalSeconds.ToString("F3"));
        }
        File.AppendAllLines(OutFile, lines);
    }
}
