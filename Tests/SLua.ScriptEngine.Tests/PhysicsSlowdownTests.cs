/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Physics slowdowns, as Phlox's PhySleep does them (itself Halcyon's): a function that changes a physical property delays
/// the calling script by the region's physics frame time, averaged over the last ten frames, when that is over 30 ms. The
/// setting is <c>PhysicsThrottle</c>, on by default; off, the delay is never added.
/// </summary>
[Collection("native-library")]
public class PhysicsSlowdownTests
{
    private const string Slows =
        "LLEvents:on(\"changed\", function(c) ossl.PhySleep() ossl.Mark(\"done\") end)";

    private static void Frames(Scene scene, float update, float sync, int count = 10)
    {
        typeof(Scene).GetField("physicsMS", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scene, update);
        typeof(Scene).GetField("physicsMS2", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scene, sync);
        for (int i = 0; i < count; i++)
            scene.EventManager.TriggerOnFrame();
    }

    /// <summary>The seconds of delay the script asked for from one PhySleep call.</summary>
    private static double Asked(EngineRig rig, UUID item)
    {
        rig.PostProbe(item, 1);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("done")), "the handler ran");
        return Assert.Single(rig.PhySleeps);
    }

    [RequiresNativeFact]
    public void AFunctionThatChangesPhysicsDelaysTheScriptByTheAverageFrameTimeWhenItIsOverThirtyMilliseconds()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Slows), stateSource: (int)StateSource.NewRez);
        rig.WaitScheduled(item);
        Frames(rig.Scene, 38f, 2f); // 40 ms a frame

        Assert.Equal(0.040, Asked(rig, item), 3);
    }

    [RequiresNativeFact]
    public void AtThirtyMillisecondsOrLessThereIsNoDelay()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Slows), stateSource: (int)StateSource.NewRez);
        rig.WaitScheduled(item);
        Frames(rig.Scene, 28f, 2f); // exactly 30 ms

        Assert.Equal(0.0, Asked(rig, item));
    }

    [RequiresNativeFact]
    public void WithThePhysicsThrottleOffThereIsNoDelayHoweverSlowThePhysics()
    {
        using var rig = new EngineRig(new Dictionary<string, string> { ["PhysicsThrottle"] = "false" });
        UUID item = rig.Rez(EngineRig.Slua(Slows), stateSource: (int)StateSource.NewRez);
        rig.WaitScheduled(item);
        Frames(rig.Scene, 80f, 20f);

        Assert.Equal(0.0, Asked(rig, item));
    }

    [RequiresNativeFact]
    public void TheDelayFollowsTheAverageOfTheLastTenFramesNotTheLastOne()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Slows), stateSource: (int)StateSource.NewRez);
        rig.WaitScheduled(item);
        Frames(rig.Scene, 58f, 2f, 5); // five frames of 60 ms
        Frames(rig.Scene, 8f, 2f, 5);  // then five of 10 ms: the average is 35 ms

        Assert.Equal(0.035, Asked(rig, item), 3);
    }

    [RequiresNativeFact]
    public void ACallOutsideAScriptsRunDoesNothing()
    {
        using var rig = new EngineRig();
        Frames(rig.Scene, 80f, 20f);

        rig.Engine.PhySleep(); // no script is running on this thread: no failure
    }
}
