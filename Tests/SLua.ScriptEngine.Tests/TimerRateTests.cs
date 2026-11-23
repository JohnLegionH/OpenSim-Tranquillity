/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// How often a script's timer event runs: once per interval, as llSetTimerEvent defines it
/// (https://wiki.secondlife.com/wiki/LlSetTimerEvent). The clock is the test clock, moved in steps
/// smaller than the interval so every tick is seen on its own.
/// </summary>
[Collection("native-library")]
public class TimerRateTests
{
    private static string LslTimer(string interval) =>
        "default { state_entry() { llSetTimerEvent(" + interval + "); } timer() { llOwnerSay(\"t\"); } }";

    /// <summary>Events the script has run so far (its main function is not counted by a timer test's difference).</summary>
    private static long Ticks(SchedulerScript s, SchedulerHarness h) => s.Dispatches;

    private static int TicksIn(SchedulerHarness h, SchedulerScript s, double seconds, double step)
    {
        h.Pump();
        long before = Ticks(s, h);
        for (double t = 0; t < seconds - 1e-9; t += step)
            h.Advance(step);
        return (int)(Ticks(s, h) - before);
    }

    [RequiresNativeFact]
    public void AnLslTwoSecondTimerTicksTenTimesInTwentySeconds()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(LslTimer("2.0"), lsl: true);
        Assert.Equal(10, TicksIn(h, s, 20.0, 0.25));
    }

    [RequiresNativeFact]
    public void AnLslHalfSecondTimerTicksFortyTimesInTwentySeconds()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(LslTimer("0.5"), lsl: true);
        Assert.Equal(40, TicksIn(h, s, 20.0, 0.25));
    }

    [RequiresNativeFact]
    public void ASluaTwoSecondTimerTicksTenTimesInTwentySeconds()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("LLTimers:every(2.0, function() ossl.Count() end)");
        Assert.Equal(10, TicksIn(h, s, 20.0, 0.25));
    }

    [RequiresNativeFact]
    public void ASluaHalfSecondTimerTicksFortyTimesInTwentySeconds()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("LLTimers:every(0.5, function() ossl.Count() end)");
        Assert.Equal(40, TicksIn(h, s, 20.0, 0.25));
    }
}
