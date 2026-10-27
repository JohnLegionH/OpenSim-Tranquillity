/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Link messages, the minimum event delay and the event queue figure, on the real LSLSystemAPI. A link message reaches the
/// target link's scripts whatever engine runs them: this engine's through its own posting and every other engine's through the
/// offer the script function makes to each script engine of the region. llMessageLinked pauses its caller for 50 ms when a
/// receiving script's queue is 80% full (Halcyon, as Phlox does through the same member); the figure comes from the engine's
/// own queue, 64 events as in Second Life. llMinEventDelay holds the next handler back; events wait in the queue and are not
/// dropped (SL wiki LlMinEventDelay).
/// </summary>
[Collection("native-library")]
public class LinkMessageAndQueueTests
{
    private const string NullKey = "00000000-0000-0000-0000-000000000000";

    private static string Diag(EngineRig rig, UUID item) =>
        "marks=[" + string.Join(",", rig.MarksOf(item)) + "] shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "]";

    private static void Heard(EngineRig rig, UUID item, string mark) =>
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark)), "no '" + mark + "': " + Diag(rig, item));

    private const string LinkListener = "LLEvents:on('link_message', function(sender, num, str, id) ossl.Mark('lm=' .. sender .. '|' .. num .. '|' .. str .. '|' .. tostring(id)) end)\nossl.Mark('ready')\n";

    [Fact]
    public void LinkMessagesTheQueueFigureAndTheMinimumDelayRunOnTheRealApi()
    {
        Assert.Contains("llMessageLinked", LlServed.Served);
        Assert.Contains("llMinEventDelay", LlServed.Served);
        Assert.DoesNotContain(LlServed.NotServed, r => r.Contains("SetMinEventDelay"));
        // llHTTPRequest still needs the asynchronous command plugins; the queue figure is no longer what it waits on
        Assert.DoesNotContain(LlServed.NotServed, r => r.StartsWith("llHTTPRequest:") && r.Contains("GetEventQueueFreeSpacePercentage"));
    }

    // ---- link messages ----

    [RequiresNativeFact]
    public void AnSLuaScriptSendsALinkMessageToAnSLuaScriptAndAnLslScriptOfItsLinkset()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID slua = rig.Rez(Slua(LinkListener));
        UUID lsl = rig.Rez(Lsl("default { state_entry() { llOwnerSay(\"ready\"); } link_message(integer s, integer n, string m, key id) { llOwnerSay(\"lm=\" + (string)s + \"|\" + (string)n + \"|\" + m + \"|\" + (string)id); } }"));
        Heard(rig, slua, "ready");
        Heard(rig, lsl, "ready");

        rig.Rez(Slua("ll.MessageLinked(-1, 7, 'hi', '" + Owner + "')"));

        Heard(rig, slua, "lm=0|7|hi|" + Owner);
        Heard(rig, lsl, "lm=0|7|hi|" + Owner);
    }

    [RequiresNativeFact]
    public void AnLslScriptRunByTheEngineSendsALinkMessageToAnSLuaScript()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID slua = rig.Rez(Slua(LinkListener));
        Heard(rig, slua, "ready");

        rig.Rez(Lsl("default { state_entry() { llMessageLinked(LINK_SET, 9, \"from lsl\", NULL_KEY); } }"));

        Heard(rig, slua, "lm=0|9|from lsl|" + NullKey);
    }

    [RequiresNativeFact]
    public void ALinkMessageIsOfferedToEachOtherEngineForTheScriptsOfTheTargetLinks()
    {
        using var rig = new EngineRig(productionFunctions: true);
        var other = new FakeOtherEngine();
        rig.Scene.StackModuleInterface<IScriptModule>(other);
        UUID receiver = rig.Rez(Slua(LinkListener));
        Heard(rig, receiver, "ready");

        UUID sender = rig.Rez(Slua("ll.MessageLinked(-1, 7, 'hi', '" + Owner + "')"));

        Assert.True(SchedulerHarness.WaitFor(() => other.ScriptPosts.Any(p => p.Item == receiver)), "the other engine was offered nothing for the receiver");
        var offered = other.ScriptPosts.Where(p => p.Item == receiver).ToArray();
        Assert.Single(offered);
        Assert.Equal("link_message", offered[0].Params.EventName);
        Assert.Equal(new object[] { 0, 7, "hi", Owner.ToString() }, offered[0].Params.Params);
        Assert.Contains(other.ScriptPosts, p => p.Item == sender);   // every script item of the link is listed; an engine takes the ones it runs
    }

    [RequiresNativeFact]
    public void ALinkMessageAnotherEngineOffersReachesAnSLuaHandlerAndAnLslHandler()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID slua = rig.Rez(Slua(LinkListener));
        UUID lsl = rig.Rez(Lsl("default { state_entry() { llOwnerSay(\"ready\"); } link_message(integer s, integer n, string m, key id) { llOwnerSay(\"lm=\" + (string)s + \"|\" + (string)n + \"|\" + m + \"|\" + (string)id); } }"));
        Heard(rig, slua, "ready");
        Heard(rig, lsl, "ready");

        // what the Phlox script function offers: plain values, to each script item of the target links
        IScriptEngine engine = (object)rig.Engine as IScriptEngine;
        Assert.NotNull(engine);
        foreach (UUID item in new[] { slua, lsl })
            Assert.True(engine.PostScriptEvent(item, new EventParams("link_message", new object[] { 1, 7, "from elsewhere", Owner.ToString() }, new DetectParams[0])));

        Heard(rig, slua, "lm=1|7|from elsewhere|" + Owner);
        Heard(rig, lsl, "lm=1|7|from elsewhere|" + Owner);
    }

    [RequiresNativeFact]
    public void ASenderIsSlowedBy50MillisecondsPerCallWhileAReceiversQueueIsEightyPercentFull()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID receiver = rig.Rez(Slua("LLEvents:on('changed', function(c) ossl.Mark('in') ll.Sleep(30) end)\nossl.Mark('ready')\n"));
        Heard(rig, receiver, "ready");
        for (int i = 0; i < 61; i++)
            Assert.True(rig.Engine.PostScriptEvent(receiver, "changed", new object[] { 1 }));
        Heard(rig, receiver, "in");
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.HostServices.GetEventQueueFreeSpacePercentage(receiver) <= 0.2f), "the receiver's queue never filled");

        UUID sender = rig.Rez(Slua("local t0 = ll.GetTime()\nfor i = 1, 10 do ll.MessageLinked(-4, i, 'x', '') end\nossl.Mark('elapsed=' .. (ll.GetTime() - t0))"));

        string elapsed = null;
        Assert.True(SchedulerHarness.WaitFor(() => (elapsed = rig.MarksOf(sender).FirstOrDefault(m => m.StartsWith("elapsed="))) != null), "the sender never finished: " + Diag(rig, sender));
        // ten calls at 50 ms each; a sleep is never shorter than asked, so the lower bound holds under load
        Assert.True(double.Parse(elapsed.Substring("elapsed=".Length), CultureInfo.InvariantCulture) >= 0.45, elapsed);
    }

    // ---- the event queue figure ----

    [RequiresNativeFact]
    public void TheQueueFigureIsOneForAScriptTheEngineDoesNotRunAndForAnIdleScript()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID idle = rig.Rez(Slua("ossl.Mark('ready')\n"));
        Heard(rig, idle, "ready");

        Assert.Equal(1.0f, rig.Engine.HostServices.GetEventQueueFreeSpacePercentage(UUID.Random()));
        Assert.Equal(1.0f, rig.Engine.HostServices.GetEventQueueFreeSpacePercentage(idle));
    }

    [RequiresNativeFact]
    public void TheQueueFigureFallsAsEventsWaitAndIsZeroWhenTheQueueOfSixtyFourIsFull()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID busy = rig.Rez(Slua("LLEvents:on('changed', function(c) ossl.Mark('in') ll.Sleep(30) end)\nossl.Mark('ready')\n"));
        Heard(rig, busy, "ready");
        Assert.True(rig.Engine.PostScriptEvent(busy, "changed", new object[] { 1 }));
        Heard(rig, busy, "in");   // the first event is being handled; none of the others has been
        for (int i = 0; i < 32; i++)
            Assert.True(rig.Engine.PostScriptEvent(busy, "changed", new object[] { 1 }));

        // 32 of 64 wait: half the queue is free
        Assert.True(SchedulerHarness.WaitFor(() => Math.Abs(rig.Engine.HostServices.GetEventQueueFreeSpacePercentage(busy) - 0.5f) < 0.001f),
            "free space " + rig.Engine.HostServices.GetEventQueueFreeSpacePercentage(busy));

        for (int i = 0; i < 100; i++)
            rig.Engine.PostScriptEvent(busy, "changed", new object[] { 1 });

        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.HostServices.GetEventQueueFreeSpacePercentage(busy) == 0.0f),
            "free space " + rig.Engine.HostServices.GetEventQueueFreeSpacePercentage(busy));
    }

    // ---- the minimum event delay ----

    private const string TimedChanged =
        "local function stamp(c) ossl.Mark(string.format('c%d=%.3f', c, ll.GetTime())) end\n" +
        "LLEvents:on('changed', function(c) if c == 100 then ll.MinEventDelay(0) end stamp(c) end)\n" +
        "ll.MinEventDelay(0.6)\nossl.Mark('ready')\n";

    private static double At(EngineRig rig, UUID item, int c)
    {
        string mark = null;
        Assert.True(SchedulerHarness.WaitFor(() => (mark = rig.MarksOf(item).FirstOrDefault(m => m.StartsWith("c" + c + "="))) != null), "no c" + c + ": " + Diag(rig, item));
        return double.Parse(mark.Substring(mark.IndexOf('=') + 1), CultureInfo.InvariantCulture);
    }

    [RequiresNativeFact]
    public void AMinimumEventDelaySpacesHandlerStartsAndQueuesTheEventsInsteadOfDroppingThem()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(TimedChanged));
        Heard(rig, item, "ready");

        for (int c = 1; c <= 3; c++)
            Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { c }));

        double t1 = At(rig, item, 1), t2 = At(rig, item, 2), t3 = At(rig, item, 3);
        // all three are handled, each at least the floor after the one before (a little under it for the clock's grain)
        Assert.True(t2 - t1 >= 0.55, "c2 after c1: " + (t2 - t1));
        Assert.True(t3 - t2 >= 0.55, "c3 after c2: " + (t3 - t2));
    }

    [RequiresNativeFact]
    public void AMinimumEventDelayOfZeroLiftsTheFloor()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(TimedChanged));
        Heard(rig, item, "ready");
        Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { 100 }));   // lifts the floor in its own handler
        At(rig, item, 100);

        for (int c = 1; c <= 3; c++)
            Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { c }));

        double t1 = At(rig, item, 1), t3 = At(rig, item, 3);
        // under the 0.6 s floor two gaps would take 1.2 s
        Assert.True(t3 - t1 < 1.0, "c3 after c1: " + (t3 - t1));
    }

    [RequiresNativeFact]
    public void AnLslScriptSetsTheMinimumEventDelayToo()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Lsl("default { state_entry() { llMinEventDelay(0.6); llOwnerSay(\"ready\"); } changed(integer c) { llOwnerSay(\"c\" + (string)c + \"=\" + (string)llGetTime()); } }"));
        Heard(rig, item, "ready");

        for (int c = 1; c <= 2; c++)
            Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { c }));

        double t1 = At(rig, item, 1), t2 = At(rig, item, 2);
        Assert.True(t2 - t1 >= 0.55, "c2 after c1: " + (t2 - t1));
    }
}
