/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Shared;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// What a handler for a detected-objects event (touch_start, sensor and the others the runtime lists as multi-event types)
/// receives. The engine posts the event with a count; the runtime's LLEvents dispatch (VM/src/lllevents.cpp, llevents_handle_event)
/// replaces that count with a read-only table of DetectedEvent objects numbered from 1, so the handler gets ONE argument, a table,
/// and not the count. Each element reads its data through getter methods that call the matching ll.Detected function.
/// </summary>
[Collection("native-library")]
public class EventArgumentShapeTests
{
    private static DetectParams Detected(string name, int face)
    {
        var dp = new DetectParams { Key = UUID.Random(), Name = name };
        dp.SurfaceTouchArgs = new SurfaceTouchEventArgs { FaceIndex = face };
        return dp;
    }

    // The handler records everything about its arguments that a scripter could depend on.
    private static string ShapeHandler(string evt) =>
        "LLEvents:on('" + evt + "', function(...)\n" +
        "  local n = select('#', ...)\n" +
        "  local d = ...\n" +
        "  ossl.Mark('shape:args=' .. n .. ' type=' .. type(d) .. ' len=' .. #d .. ' first=' .. type(d[1]) .. ' third=' .. tostring(d[3]))\n" +
        "  ossl.Mark('items:' .. d[1].index .. ',' .. tostring(d[1].valid) .. ',' .. d[2].index .. ',' .. tostring(d[2].valid))\n" +
        "  ossl.Mark('names:' .. d[1]:getName() .. ',' .. d[2]:getName())\n" +
        "  ossl.Mark('faces:' .. d[1]:getTouchFace() .. ',' .. d[2]:getTouchFace())\n" +
        "end)\n" +
        "ossl.Mark('ready')";

    private static string[] Run(string evt)
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(ShapeHandler(evt)));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("ready")), "never ready: " + string.Join(" | ", rig.Log.Lines));

        var posted = rig.Engine.PostObjectEvent(rig.LocalId,
            new EventParams(evt, new object[] { 2 }, new[] { Detected("First User", 1), Detected("Second User", 4) }));
        Assert.True(posted);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length >= 5),
            "handler did not finish: marks=[" + string.Join(",", rig.MarksOf(item)) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "] shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "]");
        return rig.MarksOf(item).Skip(1).ToArray();
    }

    [RequiresNativeFact]
    public void ATouchStartHandlerGetsOneTableOfDetectedEventsNotACount()
    {
        string[] marks = Run("touch_start");

        Assert.Equal(new[]
        {
            "shape:args=1 type=table len=2 first=userdata third=nil",
            "items:1,true,2,true",
            "names:First User,Second User",
            "faces:1,4",   // a face number is a face, not a position in a list: it is passed through unshifted
        }, marks);
    }

    [RequiresNativeFact]
    public void ASensorHandlerGetsOneTableOfDetectedEventsNotACount()
    {
        string[] marks = Run("sensor");

        Assert.Equal(new[]
        {
            "shape:args=1 type=table len=2 first=userdata third=nil",
            "items:1,true,2,true",
            "names:First User,Second User",
            "faces:1,4",
        }, marks);
    }

    [RequiresNativeFact]
    public void ADetectedEventStopsBeingValidOnceTheHandlersHaveFinished()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(
            "local kept\n" +
            "LLEvents:on('touch_start', function(d) kept = d[1] end)\n" +
            "LLEvents:on('touch_end', function() ossl.Mark('later:' .. tostring(kept.valid)) end)\n" +
            "ossl.Mark('ready')"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("ready")));

        rig.Engine.PostObjectEvent(rig.LocalId, new EventParams("touch_start", new object[] { 1 }, new[] { Detected("First User", 1) }));
        rig.Engine.PostObjectEvent(rig.LocalId, new EventParams("touch_end", new object[] { 1 }, new[] { Detected("First User", 1) }));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length >= 2));
        Assert.Equal("later:false", rig.MarksOf(item)[1]);
    }
}
