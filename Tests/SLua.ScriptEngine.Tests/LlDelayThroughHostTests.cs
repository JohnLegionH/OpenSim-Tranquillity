/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A call's forced delay comes from Phlox's own code: LSLSystemAPI asks for it with ScriptSleep, inside the context the
/// engine enters around the call, and the engine hands it to the runtime as the script's delay. The binding table holds no
/// delay figures of its own. For a sample of calls the delay the script sees is the delay Phlox's code asks for.
/// </summary>
[Collection("native-library")]
public class LlDelayThroughHostTests
{
    [Fact]
    public void TheBindingTableHoldsNoDelayFigures()
    {
        string[] names = typeof(BoundFunction).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(m => m.Name).ToArray();

        Assert.DoesNotContain("DelayMs", names);
        Assert.DoesNotContain("DelayArg", names);
    }

    private static void AssertSeconds(double expected, double actual) => Assert.InRange(actual, expected - 0.0005, expected + 0.0005);

    [RequiresNativeFact]
    public void TheDelayOfLlSetPosIsPhloxsDelay()
    {
        using var rig = new EngineApiRig();
        int phlox = rig.PhloxDelayMs(a => { a.llSetPos(new Vector3(1, 1, 1)); return null; });

        double seen = rig.FirstSleep("ll.SetPos(vector(1, 1, 1))\nossl.Count()");

        Assert.Equal(200, phlox);
        AssertSeconds(phlox / 1000.0, seen);
    }

    [RequiresNativeFact]
    public void TheDelayOfLlInstantMessageIsPhloxsDelay()
    {
        using var rig = new EngineApiRig();
        int phlox = rig.PhloxDelayMs(a => { a.llInstantMessage(UUID.Zero.ToString(), "hello"); return null; });

        double seen = rig.FirstSleep("ll.InstantMessage('" + UUID.Zero + "', 'hello')\nossl.Count()");

        Assert.Equal(2000, phlox);
        AssertSeconds(phlox / 1000.0, seen);
    }

    [RequiresNativeFact]
    public void TheDelayOfLlEmailIsPhloxsDelay()
    {
        using var rig = new EngineApiRig();
        int phlox = rig.PhloxDelayMs(a => { a.llEmail("someone@example.org", "subject", "body"); return null; });

        double seen = rig.FirstSleep("ll.Email('someone@example.org', 'subject', 'body')\nossl.Count()");

        Assert.Equal(20000, phlox);
        AssertSeconds(phlox / 1000.0, seen);
    }

    [RequiresNativeFact]
    public void TheDelayOfLlGetNotecardLineIsPhloxsDelayUnderEachNotecardPauseSetting()
    {
        foreach ((string pause, int expectedMs) in new[] { ("sl", 100), ("halcyon", 25) })
        {
            using var rig = new EngineApiRig(("NotecardPause", pause));
            rig.AddNotecard("card");
            int phlox = rig.PhloxDelayMs(a => a.llGetNotecardLine("card", 1));

            double seen = rig.FirstSleep("ll.GetNotecardLine('card', 1)\nossl.Count()");

            Assert.Equal(expectedMs, phlox);
            AssertSeconds(phlox / 1000.0, seen);
        }
    }

    [RequiresNativeFact]
    public void TheChatDelayFollowsTheChatThrottleSettingAsInPhlox()
    {
        foreach ((string throttle, int expectedMs) in new[] { ("true", 15), ("false", 0) })
        {
            using var rig = new EngineApiRig(("ChatThrottle", throttle));
            int phlox = rig.PhloxDelayMs(a => { a.llSay(0, "hello"); return null; });

            double seen = rig.FirstSleep("ll.Say(0, 'hello')\nossl.Count()");

            Assert.Equal(expectedMs, phlox);
            AssertSeconds(phlox / 1000.0, seen);
        }
    }

    [RequiresNativeFact]
    public void ADelayedCallThatReturnsNothingEndsTheTurnWhoseLengthIsPhloxsDelay()
    {
        using var rig = new EngineApiRig();
        SchedulerScript s = rig.H.AddAndStart("ll.SetPos(vector(1, 1, 1))\nossl.Count()");
        rig.H.Pump();

        Assert.Equal(0, rig.H.CountOf(s));
        Assert.Equal(ScriptRunState.Sleeping, s.State);
        rig.H.Advance(0.15);
        Assert.Equal(0, rig.H.CountOf(s));
        rig.H.Advance(0.1);
        Assert.Equal(1, rig.H.CountOf(s));
    }
}
