/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A test that pumps the scheduler on its manual clock and waits for a script's mark. A script whose first call overruns its
/// run window (a first call that compiles code, on a slow or loaded machine) is punished with a delay on the scheduler's clock;
/// the manual clock stays where it is, so a wait that only pumps never sees the script run again.
/// </summary>
[Collection("native-library")]
public class ManualClockWaitTests
{
    [RequiresNativeFact]
    public void AScriptPunishedForOverrunningItsFirstWindowStillReachesItsMark()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Burn(60)\nossl.Mark('r', 'done')");

        Assert.True(h.PumpAdvancing(() => h.MarksOf(s).Any(m => m.StartsWith("r:")), 5000),
            "the script said nothing; punishments: " + s.Punishments + ", state: " + s.State);
        Assert.Equal(new[] { "r:done" }, h.MarksOf(s));
        Assert.Equal(1, s.Punishments); // the delay is the point: without it the script never slept and the clock did not matter
    }

    [RequiresNativeFact]
    public void AWaitForAnImpossibleConditionEndsAtTheCapAndSaysSo()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("ossl.Mark('r', 'done')");
        var started = DateTime.UtcNow;

        Assert.False(h.PumpAdvancing(() => h.MarksOf(s).Any(m => m.StartsWith("never:")), 300));
        Assert.InRange((DateTime.UtcNow - started).TotalMilliseconds, 250, 5000);
        Assert.Equal(new[] { "r:done" }, h.MarksOf(s)); // it pumped: the script ran
    }
}
