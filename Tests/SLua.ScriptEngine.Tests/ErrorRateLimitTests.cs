/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.RegularExpressions;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A script that repeats the same error message tells its owner once, then a count after a quiet interval, instead of flooding the debug
/// channel. Different messages are not merged, and the switch turns the limit off. The error pause of 15 ms stays with every error.
/// </summary>
[Collection("native-library")]
public class ErrorRateLimitTests
{
    // The handler reports the same error 12 times, 0.1 s apart, and then stops.
    private const string Loops =
        "n = 0\nLLEvents:on('timer', function()\n  n = n + 1\n  if n <= 12 then ossl.Err('same problem') end\nend)\nll.SetTimerEvent(0.1)\nossl.Mark('start')\n";

    private static Dictionary<string, string> Quick => new() { ["ErrorQuietSeconds"] = "0.4", ["MinTimerInterval"] = "0.05" };

    private static string[] Texts(EngineRig rig) => rig.Messages.Shouts.Select(s => s.Text).ToArray();

    [RequiresNativeFact]
    public void ARepeatedErrorIsToldOnceAndThenAsACountAfterAQuietInterval()
    {
        using var rig = new EngineRig(Quick, productionFunctions: true);
        rig.Rez(EngineRig.Slua(Loops));

        Assert.True(SchedulerHarness.WaitFor(() => Texts(rig).Any(t => t.Contains("same problem"))), "the first one is told at once");
        Assert.True(SchedulerHarness.WaitFor(() => Texts(rig).Any(t => Regex.IsMatch(t, @"same problem.*repeated (\d+) more")), 20000), "the count follows the quiet");

        string[] all = Texts(rig);
        Assert.Equal(2, all.Count(t => t.Contains("same problem")));
        Match m = Regex.Match(all.Single(t => t.Contains("repeated")), @"repeated (\d+) more");
        Assert.Equal(11, int.Parse(m.Groups[1].Value)); // 12 errors: one told, eleven counted
    }

    [RequiresNativeFact]
    public void WhileTheErrorKeepsRepeatingNoCountIsTold()
    {
        using var rig = new EngineRig(Quick, productionFunctions: true);
        // errors come every 0.1 s without end, so the quiet interval of 0.4 s never passes
        rig.Rez(EngineRig.Slua("LLEvents:on('timer', function() ossl.Err('never ends') end)\nll.SetTimerEvent(0.1)\nossl.Mark('start')\n"));
        Assert.True(SchedulerHarness.WaitFor(() => Texts(rig).Length >= 1));

        Thread.Sleep(1500); // a real wait: it proves nothing more is told while the errors go on

        Assert.Single(Texts(rig));
    }

    [RequiresNativeFact]
    public void DifferentMessagesAreNotMerged()
    {
        using var rig = new EngineRig(Quick, productionFunctions: true);
        rig.Rez(EngineRig.Slua("n = 0\nLLEvents:on('timer', function()\n  n = n + 1\n  if n <= 3 then ossl.Err('problem ' .. n) end\nend)\nll.SetTimerEvent(0.1)\nossl.Mark('start')\n"));

        Assert.True(SchedulerHarness.WaitFor(() => Texts(rig).Count(t => t.Contains("problem")) == 3));
        Assert.Equal(new[] { "problem 1", "problem 2", "problem 3" }, Texts(rig).OrderBy(t => t).Select(t => Regex.Match(t, @"problem \d").Value));
    }

    [RequiresNativeFact]
    public void TheLimitCanBeSwitchedOff()
    {
        var settings = Quick;
        settings["ErrorRateLimit"] = "false";
        using var rig = new EngineRig(settings, productionFunctions: true);
        rig.Rez(EngineRig.Slua(Loops));

        Assert.True(SchedulerHarness.WaitFor(() => Texts(rig).Count(t => t.Contains("same problem")) == 12, 20000), "every one is told");
    }
}
