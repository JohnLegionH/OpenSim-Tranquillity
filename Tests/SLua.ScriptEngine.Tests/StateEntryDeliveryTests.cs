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
/// Whether an SLua script is told state_entry and state_exit. The runtime runs the script's top-level chunk as the equivalent of
/// state_entry (Executor/src/Script.cpp, Script::start: "we abuse state_entry's handler flag to also mean in the main function"),
/// and for SLua it never pends a separate state_entry (mCurrentEvents is 0 unless the script is LSL).
/// </summary>
[Collection("native-library")]
public class StateEntryDeliveryTests
{
    private const string Script =
        "ossl.Mark('chunk')\n" +
        "LLEvents:on('state_entry', function() ossl.Mark('entry') end)\n" +
        "LLEvents:on('state_exit', function() ossl.Mark('exit') end)\n" +
        "LLEvents:on('touch_start', function() ossl.Mark('touch') end)\n" +
        "ossl.Mark('registered')";

    private static string Diag(EngineRig rig, UUID item) =>
        "marks=[" + string.Join(",", rig.MarksOf(item)) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "] shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "]";

    [RequiresNativeFact]
    public void AnSLuaScriptRunsItsTopLevelChunkOnStartAndItsStateEntryHandlerNeverFires()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Script));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("registered")), "never started: " + Diag(rig, item));

        // The handlers do work (a touch is delivered), so the registration was not refused; state_entry is simply never raised.
        rig.Engine.PostObjectEvent(rig.LocalId, new OpenSim.Region.ScriptEngine.Shared.EventParams("touch_start", new object[] { 0 }, null));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("touch")), "touch not delivered: " + Diag(rig, item));
        Thread.Sleep(600);   // a real wait: proves state_entry does NOT arrive

        Assert.Equal(new[] { "chunk", "registered", "touch" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void AnSLuaScriptsStateExitHandlerDoesNotFireWhenTheScriptIsStopped()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(Slua(Script));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("registered")), "never started: " + Diag(rig, item));

        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item).State == ScriptRunState.Stopped));
        Thread.Sleep(600);   // a real wait: proves state_exit does NOT arrive

        Assert.DoesNotContain("exit", rig.MarksOf(item));
        Assert.DoesNotContain("entry", rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void AnLslScriptOnTheSameEngineDoesGetItsStateEntry()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Lsl("default { state_entry() { llOwnerSay(\"entered\"); } }"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("entered")), "LSL state_entry not seen: " + Diag(rig, item));
    }
}
