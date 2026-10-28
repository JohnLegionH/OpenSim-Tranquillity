/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A script's reset keeps the floor llMinEventDelay set and the time before which its next handler may not start, as the Phlox
/// engine's does (RuntimeState.Reset leaves MinEventDelayMs and NextEventAllowedOn alone). The events posted after the reset
/// wait for the floor like any others.
/// </summary>
[Collection("native-library")]
public class MinEventDelayResetTests
{
    private const double FloorSeconds = 3;

    [RequiresNativeFact]
    public void AResetKeepsTheMinimumEventDelayFloorAndTheTimeTheNextHandlerWaitsFor()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(
            "LLEvents:on('changed', function(c)\n" +
            "  if c == 7 then ll.MinEventDelay(" + FloorSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") ossl.Mark('floor') else ossl.Mark('c' .. c) end\n" +
            "end)\nossl.Mark('ready')\n"));
        void Heard(string mark) => Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark)), "no '" + mark + "': marks=[" + string.Join(",", rig.MarksOf(item)) + "]");
        Heard("ready");
        Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { 7 }));
        Heard("floor");
        Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { 8 }));
        Heard("c8");   // a handler started: the next may not start for the floor

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Count(m => m == "ready") == 2), "main did not run again");
        Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { 9 }));
        Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { 10 }));
        Thread.Sleep(500);   // a real wait: the handler that started before the reset still holds the next one back, so the first event after it is not heard yet
        Assert.DoesNotContain("c9", rig.MarksOf(item));
        Heard("c9");

        Thread.Sleep(500);   // a real wait: the second event waits for the floor, so it is not heard yet
        Assert.DoesNotContain("c10", rig.MarksOf(item));
        Heard("c10");
    }
}
