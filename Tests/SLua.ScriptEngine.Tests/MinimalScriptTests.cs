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
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The smallest useful SLua script, run on the native VM with the engine's own functions only (no test-only marking functions):
/// it tells its owner it is ready, and tells its owner who touched it. The owner is a connected avatar whose client records the
/// chat it receives, so what is asserted is what a person would read.
/// </summary>
[Collection("native-library")]
public class MinimalScriptTests
{
    /// <summary>The script a guide can show: the text is exactly what the test runs.</summary>
    public const string MinimalScript =
        "LLEvents:on(\"touch_start\", function(detected)\n" +
        "    ll.OwnerSay(\"Touched by \" .. detected[1]:getName())\n" +
        "end)\n" +
        "ll.OwnerSay(\"Ready\")\n";

    [RequiresNativeFact]
    public void TheMinimalScriptPrintsToItsOwnerAndAnswersATouch()
    {
        using var rig = new EngineRig(productionFunctions: true, testFunctions: false);
        ScenePresence owner = SceneHelpers.AddScenePresence(rig.Scene, Owner);
        var heard = new List<string>();
        ((TestClient)owner.ControllingClient).OnReceivedChatMessage += (message, type, pos, fromName, fromAgent, ownerId, source, audible) =>
        {
            lock (heard) heard.Add(message);
        };
        string[] Heard() { lock (heard) return heard.ToArray(); }

        UUID item = rig.Rez(Slua(MinimalScript));
        Assert.True(SchedulerHarness.WaitFor(() => Heard().Contains("Ready")),
            "no 'Ready': heard=[" + string.Join(",", Heard()) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "] shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "]");

        var toucher = new DetectParams { Key = UUID.Random(), Name = "Test User" };
        Assert.True(rig.Engine.PostObjectEvent(rig.LocalId, new EventParams("touch_start", new object[] { 1 }, new[] { toucher })));

        Assert.True(SchedulerHarness.WaitFor(() => Heard().Contains("Touched by Test User")),
            "no touch answer: heard=[" + string.Join(",", Heard()) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "] shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "]");
        Assert.Equal(new[] { "Ready", "Touched by Test User" }, Heard());
    }
}
