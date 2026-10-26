/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The detected objects of an event travel with it through the script's queue and are what the handler reads through
/// the Detected functions: per event, not per script, and gone when the handler has gone to the next event.
/// </summary>
[Collection("native-library")]
public class DetectedDataTests
{
    private const string Handler =
        "LLEvents:on(\"touch_start\", function(n) ossl.Mark(\"touch\", ossl.DetectedCount() .. \":\" .. ossl.DetectedName(0) .. \":\" .. ossl.DetectedFace(0)) end)\n" +
        "LLEvents:on(\"moving_start\", function() ossl.Mark(\"moving\", ossl.DetectedCount() .. \":\" .. ossl.DetectedName(0)) end)";

    private static DetectParams Toucher(string name, int face = 1)
    {
        var dp = new DetectParams { Key = UUID.Random(), Name = name };
        dp.SurfaceTouchArgs = new SurfaceTouchEventArgs { FaceIndex = face };
        return dp;
    }

    private static EventParams Touch(params DetectParams[] detected) => new("touch_start", new object[] { detected.Length }, detected);

    private static bool Marked(EngineRig rig, UUID item, int count) =>
        SchedulerHarness.WaitFor(() => rig.MarksOf(item).Length >= count);

    [RequiresNativeFact]
    public void TheHandlerReadsTheDetectedObjectOfItsEvent()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Handler));
        rig.WaitScheduled(item);

        Assert.True(rig.Engine.PostObjectEvent(rig.LocalId, Touch(Toucher("Test User", face: 4))));

        Assert.True(Marked(rig, item, 1));
        Assert.Equal(new[] { "touch:1:Test User:4" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void EventsWaitingInTheQueueEachKeepTheirOwnDetectedData()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Handler));
        rig.WaitScheduled(item);
        Assert.True(rig.Engine.SuspendScript(item));

        rig.Engine.PostObjectEvent(rig.LocalId, Touch(Toucher("First User")));
        rig.Engine.PostObjectEvent(rig.LocalId, Touch(Toucher("Second User")));
        rig.Engine.PostObjectEvent(rig.LocalId, Touch(Toucher("Third User")));
        Assert.True(rig.Engine.ResumeScript(item));

        Assert.True(Marked(rig, item, 3));
        Assert.Equal(new[] { "touch:1:First User:1", "touch:1:Second User:1", "touch:1:Third User:1" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void AnEventWithNoDetectedObjectsSeesNoneAfterOneThatHadSome()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Handler));
        rig.WaitScheduled(item);

        rig.Engine.PostObjectEvent(rig.LocalId, Touch(Toucher("Test User")));
        Assert.True(Marked(rig, item, 1));
        rig.Engine.PostObjectEvent(rig.LocalId, new EventParams("moving_start", Array.Empty<object>(), null));

        Assert.True(Marked(rig, item, 2));
        Assert.Equal("moving:0:none", rig.MarksOf(item)[1]);
    }

    private sealed class Gate
    {
        public readonly ManualResetEventSlim Open = new();

        public byte[] Compile(string text, bool lsl)
        {
            Open.Wait(TimeSpan.FromSeconds(60));
            return Host.Compile(text, lsl);
        }
    }

    [RequiresNativeFact]
    public void AnEventDeferredWhileTheScriptLoadsKeepsItsDetectedData()
    {
        var gate = new Gate();
        using var rig = new EngineRig(compile: gate.Compile);
        UUID item = rig.Rez(EngineRig.Slua(Handler));

        Assert.True(rig.Engine.PostObjectEvent(rig.LocalId, Touch(Toucher("Early User", face: 2))));
        Assert.Equal(1, rig.Engine.DeferredEventCount(item));
        gate.Open.Set();

        Assert.True(Marked(rig, item, 1));
        Assert.Equal(new[] { "touch:1:Early User:2" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void ATouchRaisedOnTheRegionReachesTheScriptWithTheTouchersData()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Handler));
        rig.WaitScheduled(item);
        ScenePresence toucher = SceneHelpers.AddScenePresence(rig.Scene, UUID.Random());

        rig.Scene.EventManager.TriggerObjectGrab(rig.LocalId, 0, Vector3.Zero, toucher.ControllingClient, new SurfaceTouchEventArgs { FaceIndex = 3 });

        Assert.True(Marked(rig, item, 1));
        Assert.Equal(new[] { "touch:1:" + toucher.Firstname + " " + toucher.Lastname + ":3" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void AMovingEventRaisedOnTheRegionReachesTheScript()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Handler));
        rig.WaitScheduled(item);

        rig.Scene.EventManager.TriggerMovingStartEvent(rig.LocalId);

        Assert.True(Marked(rig, item, 1));
        Assert.Equal(new[] { "moving:0:none" }, rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void TheEngineFollowsTheRegionsEventsFromAddRegionUntilRemoveRegion()
    {
        var rig = new EngineRig();
        SceneHooksA hooks = rig.Engine.Hooks;
        Assert.NotNull(hooks);
        Assert.True(hooks.IsSubscribed);

        rig.Engine.RemoveRegion(rig.Scene);

        Assert.False(hooks.IsSubscribed);
        Assert.Null(rig.Engine.Hooks);
    }
}
