/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>A script that catches an error with pcall and then calls an ll function goes on running, in the main chunk and in an event handler.</summary>
[Collection("native-library")]
public class CaughtErrorThenCallTests
{
    private static readonly UUID Speaker = new("a11ce000-0000-4000-8000-000000000001");

    private static string Diag(EngineRig rig, UUID item) =>
        "marks=[" + string.Join(",", rig.MarksOf(item)) + "] state=" + (rig.Engine.SchedulerScriptFor(item)?.FaultMessage ?? "none") + " shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "]";

    [RequiresNativeFact]
    public void ACallAfterAnErrorCaughtInAnEventHandlerReturnsAndTheScriptStaysUp()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(EngineRig.Slua(
            "LLEvents:on('listen', function(ch, name, id, msg)\n" +
            "  local ok = pcall(function() error('boom') end)\n" +
            "  ll.RegionSayTo('" + UUID.Zero + "', 0, 'x')\n" +
            "  ossl.Mark('after ' .. msg)\n" +
            "end)\nll.Listen(5, '', '', '')\nossl.Mark('ready')"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("ready")), Diag(rig, item));

        rig.Scene.SimChat("one", ChatTypeEnum.Say, 5, rig.Object.AbsolutePosition + new Vector3(1, 0, 0), "Test Speaker", Speaker, false);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("after one")), Diag(rig, item));
        rig.Scene.SimChat("two", ChatTypeEnum.Say, 5, rig.Object.AbsolutePosition + new Vector3(1, 0, 0), "Test Speaker", Speaker, false);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("after two")), "the script stopped after its first event: " + Diag(rig, item));
    }
}
