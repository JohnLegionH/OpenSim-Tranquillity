/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using SLua.ScriptEngine.Bindings;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The compiled asset's header, which carries, for LSL, a mask of the events each state handles (bit n - 1 for the
/// event whose number is n in the runtime's event list).
/// </summary>
[Collection("native-library")]
public class AssetHeaderTests
{
    private static ulong Bit(string eventName) => 1UL << (Host.EventIndex(eventName) - 1);

    [RequiresNativeFact]
    public void AnLslAssetHeaderGivesTheEventsOfEachState()
    {
        RealLibrary.Load();
        byte[] asset = Host.Compile(
            "default { touch_start(integer n) { } timer() { } }\n" +
            "state other { listen(integer c, string n, key id, string m) { } }", lsl: true);

        Assert.True(AssetHeader.TryRead(asset, out bool isLsl, out ulong[] masks));
        Assert.True(isLsl);
        Assert.Equal(2, masks.Length);
        Assert.NotEqual(0UL, masks[0] & Bit("touch_start"));
        Assert.NotEqual(0UL, masks[0] & Bit("timer"));
        Assert.Equal(0UL, masks[0] & Bit("listen"));
        Assert.NotEqual(0UL, masks[1] & Bit("listen"));
        Assert.Equal(0UL, masks[1] & Bit("touch_start"));
    }

    [RequiresNativeFact]
    public void ASluaAssetHeaderHasNoStateMasks()
    {
        RealLibrary.Load();
        byte[] asset = Host.Compile("ossl.Count()", lsl: false);

        Assert.True(AssetHeader.TryRead(asset, out bool isLsl, out ulong[] masks));
        Assert.False(isLsl);
        Assert.Empty(masks);
    }

    [Fact]
    public void BytesThatAreNotAnAssetAreRefused()
    {
        Assert.False(AssetHeader.TryRead(null, out _, out _));
        Assert.False(AssetHeader.TryRead(Array.Empty<byte>(), out _, out _));
        Assert.False(AssetHeader.TryRead(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, out _, out _));
    }
}

/// <summary>Only events a script handles are queued: an LSL script's handlers in its current state, an SLua script's LLEvents handlers.</summary>
[Collection("native-library")]
public class EventMaskTests
{
    [RequiresNativeFact]
    public void AnLslScriptQueuesOnlyTheEventsItsStateHandles()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("default { touch_start(integer n) { llOwnerSay(\"touched\"); } }", lsl: true);
        h.Pump();
        long before = s.Dispatches;

        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("2"));
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Pump();

        Assert.Equal(new[] { "say:touched" }, h.MarksOf(s));
        Assert.Equal(2, s.DroppedUnhandled);
        Assert.Equal(2, h.Scheduler.GetStats().DroppedUnhandled);
        Assert.Equal(before + 1, s.Dispatches); // the runtime was asked to run one event, not three
        Assert.Equal(0, s.DroppedEvents);
    }

    [RequiresNativeFact]
    public void AnLslScriptsMaskFollowsItsStateChanges()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(
            "default { touch_start(integer n) { state other; } }\n" +
            "state other { dataserver(key q, string d) { llOwnerSay(\"d\" + d); } }", lsl: true);
        h.Pump();

        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1")); // the default state does not handle it
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Pump();
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("2"));
        h.Scheduler.Post(s, "touch_start", Value.Int(1)); // the other state does not handle it
        h.Pump();

        Assert.Equal(new[] { "say:d2" }, h.MarksOf(s));
        Assert.Equal(2, s.DroppedUnhandled);
    }

    [RequiresNativeFact]
    public void AnSluaScriptQueuesOnlyTheEventsItRegisteredAHandlerFor()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart("LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"data\", d) end)");
        h.Pump();

        // The first event nobody listens for is found out when the runtime is asked to run it ...
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Pump();
        // ... and while the script has run nothing since, the same kind is not queued again.
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Pump();
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Pump();

        Assert.Equal(new[] { "data:1" }, h.MarksOf(s));
        Assert.Equal(2, s.DroppedUnhandled);
    }

    [RequiresNativeFact]
    public void AnSluaHandlerRegisteredLaterIsHeardFromThenOn()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(
            "LLEvents:on(\"dataserver\", function(q, d) LLEvents:on(\"touch_start\", function(n) ossl.Mark(\"touch\", 0) end) end)");
        h.Pump();
        h.Scheduler.Post(s, "touch_start", Value.Int(1)); // nobody listens yet
        h.Pump();
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1")); // the handler that registers one
        h.Pump();
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Pump();

        Assert.Equal(new[] { "touch:0" }, h.MarksOf(s));
    }

    [RequiresNativeFact]
    public void AnSluaHandlerRemovedLaterStopsBeingQueued()
    {
        using var h = new SchedulerHarness();
        SchedulerScript s = h.AddAndStart(
            "local function touched(n) ossl.Mark(\"touch\", 0) end\n" +
            "LLEvents:on(\"touch_start\", touched)\n" +
            "LLEvents:on(\"dataserver\", function(q, d) LLEvents:off(\"touch_start\", touched) end)");
        h.Pump();
        h.Scheduler.Post(s, "touch_start", Value.Int(1));
        h.Pump();
        h.Scheduler.Post(s, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Pump();
        h.Scheduler.Post(s, "touch_start", Value.Int(1)); // run to find out nobody listens now
        h.Pump();
        h.Scheduler.Post(s, "touch_start", Value.Int(1)); // and now it is not even queued
        h.Pump();

        Assert.Equal(new[] { "touch:0" }, h.MarksOf(s));
        Assert.Equal(1, s.DroppedUnhandled);
    }
}
