/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using SLua.ScriptEngine.Bindings;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Event queues as Second Life describes them (https://wiki.secondlife.com/wiki/Event: at most 64 events wait, new
/// ones are discarded until a slot is free, and a state change clears the queue), at the scheduler's own level.
/// Halcyon, and Phlox after it, let on_rez, state_entry, state_exit and timer past the limit, because losing one of
/// them leaves a script half started or its timer dead.
/// </summary>
[Collection("native-library")]
public class EventQueueTests
{
    private const string Listener = "LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"data\", d) end)";

    private const string StateChanger =
        "default { state_entry() { } touch_start(integer n) { state other; } dataserver(key q, string d) { llOwnerSay(\"old\" + d); } }\n" +
        "state other { state_entry() { llOwnerSay(\"entered\"); } dataserver(key q, string d) { llOwnerSay(\"new\" + d); } }";

    [RequiresNativeFact]
    public void EventsAreDeliveredInTheOrderTheyWerePosted()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener);
        h.Pump();
        for (int i = 1; i <= 6; i++) h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str(i.ToString()));
        h.Pump();

        Assert.Equal(new[] { "data:1", "data:2", "data:3", "data:4", "data:5", "data:6" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void AQueueHoldsSixtyFourEventsAndDiscardsTheRest()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener + " ossl.Sleep(5.0)");
        h.Pump();
        for (int i = 1; i <= 100; i++) h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str(i.ToString()));
        h.Pump();
        h.Advance(5.5);
        for (int i = 0; i < 70; i++) h.Advance(0.01);

        string[] marks = h.MarksOf(s);
        Assert.Equal(64, marks.Length);
        Assert.Equal("data:1", marks[0]);
        Assert.Equal("data:64", marks[63]); // the first 64 stay, the later ones are the ones discarded
        Assert.Equal(36, s.DroppedEvents);
        Assert.Equal(36, h.Scheduler.GetStats().DroppedFull);
    }

    [RequiresNativeFact]
    public void OnRezAndTimerPassAFullQueue()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(Listener + " LLEvents:on(\"on_rez\", function(p) ossl.Mark(\"rez\", p) end)" +
                                          " LLEvents:on(\"timer\", function() ossl.Mark(\"timer\", 0) end) ossl.Sleep(5.0)");
        h.Pump();
        for (int i = 1; i <= 64; i++) h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str(i.ToString()));
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("65")); // over the limit: discarded
        h.Scheduler.Post(s, "on_rez", Value.Int(7));
        h.Scheduler.Post(s, "timer");
        h.Pump();
        h.Advance(5.5);
        for (int i = 0; i < 80; i++) h.Advance(0.01);

        string[] marks = h.MarksOf(s);
        Assert.Contains("rez:7", marks);
        Assert.Contains("timer:0", marks);
        Assert.DoesNotContain("data:65", marks);
        Assert.Equal(1, s.DroppedEvents);
    }

    [RequiresNativeFact]
    public void AStateChangeClearsTheEventsWaitingForTheOldState()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(StateChanger, lsl: true);
        h.Pump();
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("2"));
        h.Pump();

        Assert.Equal(new[] { "say:entered" }, h.MarksOf(s));

        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("3"));
        h.Pump();
        Assert.Equal(new[] { "say:entered", "say:new3" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void AStateChangeLeavesATimerAbleToFireAgain()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(
            "default { state_entry() { llSetTimerEvent(1.0); } touch_start(integer n) { state other; } timer() { llOwnerSay(\"t1\"); } }\n" +
            "state other { timer() { llOwnerSay(\"t2\"); } }", lsl: true);
        h.Pump();
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Clock.Now += 1.0; // the tick falls due behind touch_start, which changes state and clears it ...
        h.Pump();
        h.Advance(1.0); // ... and the timer still ticks for the new state
        h.Advance(1.0);

        Assert.DoesNotContain("say:t1", h.MarksOf(s));
        Assert.Contains("say:t2", h.MarksOf(s));
    }
}

/// <summary>
/// The engine's entry points for events (IScriptModule and the pair other engines and the async plugins call), the
/// conversion of their arguments, and events kept for a script that is still being loaded (Halcyon
/// DeferredEventManager: 32 per item, for 60 seconds from the first).
/// </summary>
[Collection("native-library")]
public class EngineEventIntakeTests
{
    private const string Listener = "LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(d) end)";

    private static bool Marked(EngineRig rig, UUID item, int count) =>
        SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length >= count);

    [RequiresNativeFact]
    public void PostScriptEventReachesTheScriptsHandler()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Listener));
        rig.WaitScheduled(item);

        Assert.True(rig.Engine.PostScriptEvent(item, "dataserver", new object[] { "query", "hello" }));
        Assert.True(Marked(rig, item, 1));
        Assert.Equal(new[] { "hello" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void PostScriptEventWithEventParamsReachesTheScript()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Listener));
        rig.WaitScheduled(item);

        Assert.True(rig.Engine.PostScriptEvent(item, new EventParams("dataserver", new object[] { "query", "via params" }, null)));
        Assert.True(Marked(rig, item, 1));
        Assert.Equal(new[] { "via params" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void PostObjectEventReachesEveryScriptOfThePrim()
    {
        using var rig = new EngineRig();
        UUID a = rig.Rez(EngineRig.Slua(Listener));
        UUID b = rig.Rez(EngineRig.Slua(Listener));
        rig.WaitScheduled(a);
        rig.WaitScheduled(b);

        Assert.True(rig.Engine.PostObjectEvent(rig.LocalId, new EventParams("dataserver", new object[] { "query", "all" }, null)));
        Assert.True(Marked(rig, a, 1));
        Assert.True(Marked(rig, b, 1));
        Assert.Equal(new[] { "all" }, rig.MarksOf(a));
        Assert.Equal(new[] { "all" }, rig.MarksOf(b));
    }

    [RequiresNativeFact]
    public void PostObjectEventByItemGoesToTheScriptsOfThatItemsPrim()
    {
        using var rig = new EngineRig();
        UUID a = rig.Rez(EngineRig.Slua(Listener));
        UUID b = rig.Rez(EngineRig.Slua(Listener));
        rig.WaitScheduled(a);
        rig.WaitScheduled(b);

        Assert.True(rig.Engine.PostObjectEvent(a, "dataserver", new object[] { "query", "prim" }));
        Assert.True(Marked(rig, a, 1));
        Assert.True(Marked(rig, b, 1));
    }

    [RequiresNativeFact]
    public void AnEventForAnUnknownScriptPrimOrEventNameIsRefused()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Listener));
        rig.WaitScheduled(item);

        Assert.False(rig.Engine.PostScriptEvent(UUID.Random(), "dataserver", new object[] { "q", "x" }));
        Assert.False(rig.Engine.PostObjectEvent(rig.LocalId + 1000, new EventParams("dataserver", new object[] { "q", "x" }, null)));
        Assert.False(rig.Engine.PostScriptEvent(item, "not_an_event", Array.Empty<object>()));
        Assert.False(rig.Engine.PostScriptEvent(item, null));
    }

    [RequiresNativeFact]
    public void ArgumentsArriveAsTheTypesTheEventDeclares()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Lsl(
            "default { listen(integer c, string n, key id, string m) { llOwnerSay((string)c + \"|\" + n + \"|\" + (string)id + \"|\" + m); }\n" +
            "  at_target(integer t, vector p, vector o) { llOwnerSay(\"at\" + (string)t + (string)p); } }"));
        rig.WaitScheduled(item);
        UUID speaker = UUID.Random();

        // The key argument may arrive as a UUID or as text; the event declares it a key either way.
        Assert.True(rig.Engine.PostScriptEvent(item, "listen", new object[] { 5, "Example Speaker", speaker, "hi" }));
        Assert.True(rig.Engine.PostScriptEvent(item, "listen", new object[] { 6, "Example Speaker", speaker.ToString(), "again" }));
        Assert.True(rig.Engine.PostScriptEvent(item, "at_target", new object[] { 3, new Vector3(1, 2, 3), new Vector3(4, 5, 6) }));

        Assert.True(Marked(rig, item, 3));
        string[] marks = rig.MarksOf(item);
        Assert.Equal($"5|Example Speaker|{speaker}|hi", marks[0]);
        Assert.Equal($"6|Example Speaker|{speaker}|again", marks[1]);
        Assert.StartsWith("at3<1.0", marks[2]);
    }

    // ---- events for a script that is still loading ----

    private sealed class Gate
    {
        public readonly ManualResetEventSlim Open = new(false);
        public byte[] Compile(string text, bool lsl)
        {
            Open.Wait(TimeSpan.FromSeconds(60));
            return Host.Compile(text, lsl);
        }
    }

    [RequiresNativeFact]
    public void EventsForAScriptStillLoadingAreKeptAndDeliveredInOrderWhenItIsReady()
    {
        var gate = new Gate();
        using var rig = new EngineRig(compile: gate.Compile);
        UUID item = rig.Rez(EngineRig.Slua(Listener));

        for (int i = 1; i <= 3; i++)
            Assert.True(rig.Engine.PostScriptEvent(item, "dataserver", new object[] { "query", "n" + i }));
        Assert.Equal(3, rig.Engine.DeferredEventCount(item));
        gate.Open.Set();

        Assert.True(Marked(rig, item, 3));
        Assert.Equal(new[] { "n1", "n2", "n3" }, rig.MarksOf(item));
        Assert.Equal(0, rig.Engine.DeferredEventCount(item));
    }

    [RequiresNativeFact]
    public void OnlyTheFirstThirtyTwoEventsForALoadingScriptAreKept()
    {
        var gate = new Gate();
        using var rig = new EngineRig(compile: gate.Compile);
        UUID item = rig.Rez(EngineRig.Slua(Listener));

        for (int i = 1; i <= 40; i++)
            rig.Engine.PostScriptEvent(item, "dataserver", new object[] { "query", "n" + i });
        Assert.Equal(SLuaEngine.MaxDeferredEventsPerItem, rig.Engine.DeferredEventCount(item));
        gate.Open.Set();

        Assert.True(Marked(rig, item, 32));
        Thread.Sleep(300); // proves no more arrive: a real wait
        string[] marks = rig.MarksOf(item);
        Assert.Equal(32, marks.Length);
        Assert.Equal("n1", marks[0]);
        Assert.Equal("n32", marks[31]);
    }

    [RequiresNativeFact]
    public void DeferredEventsExpireSixtySecondsAfterTheFirst()
    {
        var gate = new Gate();
        var clock = new ManualClock();
        using var rig = new EngineRig(compile: gate.Compile);
        rig.Engine.Clock = clock;
        UUID item = rig.Rez(EngineRig.Slua(Listener));

        rig.Engine.PostScriptEvent(item, "dataserver", new object[] { "query", "early" });
        clock.Now = SLuaEngine.DeferredEventLifetimeSeconds + 1;
        rig.Engine.PostScriptEvent(item, "dataserver", new object[] { "query", "late" });
        Assert.Equal(1, rig.Engine.DeferredEventCount(item)); // the expired one went when the late one came
        gate.Open.Set();

        Assert.True(Marked(rig, item, 1));
        Thread.Sleep(300); // proves "early" does not arrive: a real wait
        Assert.Equal(new[] { "late" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void DeferredEventsAreDroppedWhenTheScriptIsRemovedBeforeItLoads()
    {
        var gate = new Gate();
        using var rig = new EngineRig(compile: gate.Compile);
        UUID item = rig.Rez(EngineRig.Slua(Listener));
        rig.Engine.PostScriptEvent(item, "dataserver", new object[] { "query", "x" });
        Assert.Equal(1, rig.Engine.DeferredEventCount(item));

        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, item);

        Assert.Equal(0, rig.Engine.DeferredEventCount(item));
        Assert.False(rig.Engine.PostScriptEvent(item, "dataserver", new object[] { "query", "y" }));
        gate.Open.Set();
    }
}
