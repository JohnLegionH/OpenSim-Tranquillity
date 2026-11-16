/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.RegularExpressions;
using OpenMetaverse;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The engine's status command: per region and per script, the run state, why a script is held or paused, the policy and waiver counts,
/// the events dropped while a script is stopped, the queue, the memory, the minimum event delay and the saved-state store's figures. A
/// filter names an object, a script or an item. The output stays short however many scripts there are.
/// </summary>
[Collection("native-library")]
public class EngineStatusTests
{
    private const string Quiet = "ossl.Mark('start')\n";

    private static string Text(IEnumerable<string> lines) => string.Join("\n", lines);

    private static string LineOf(EngineRig rig, UUID item, string filter = "")
    {
        IReadOnlyList<string> lines = rig.Engine.StatusLines(filter);
        int at = -1;
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Contains(item.ToString()))
                at = i;
        Assert.True(at >= 0, "no line for the script in: " + Text(lines));
        // the script's line and the detail line under it
        return lines[at] + "\n" + (at + 1 < lines.Count ? lines[at + 1] : "");
    }

    [RequiresNativeFact]
    public void TheHeaderNamesTheEngineTheScriptCountsTheDropsThePolicyAndTheWaiverFigures()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Quiet));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("start")));

        string text = Text(rig.Engine.StatusLines(""));

        Assert.Contains("SLua", text);
        Assert.Contains("scripts 1", text);
        Assert.Contains("events dropped", text);
        Assert.Contains("waived overruns", text);
        Assert.Contains("parcel paused 0", text);
        Assert.Contains("estate paused 0", text);
    }

    [RequiresNativeFact]
    public void ARunningScriptIsShownRunningWithItsMemory()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua(Quiet));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("start")));

        string line = LineOf(rig, item);

        Assert.Contains("running", line);
        Match mem = Regex.Match(line, @"mem (\d+) B");
        Assert.True(mem.Success, line);
        Assert.True(int.Parse(mem.Groups[1].Value) > 0, "the script's heap is not empty: " + line);
    }

    [RequiresNativeFact]
    public void AScriptAFaultStoppedIsShownWithItsReason()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua("LLEvents:on('changed', function(c) error('boom') end)\nossl.Mark('start')\n"), stateSource: 1);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("start")));
        rig.PostProbe(item, 1);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item).Killed));

        string line = LineOf(rig, item);

        Assert.Contains("FAULTED", line);
        Assert.Contains("boom", line);
        Assert.Contains("faulted 1", Text(rig.Engine.StatusLines("")));
    }

    [RequiresNativeFact]
    public void AScriptWithAMinimumEventDelayShowsIt()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(EngineRig.Slua("ll.MinEventDelay(0.5)\nossl.Mark('start')\n"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("start")));

        string line = LineOf(rig, item);

        Assert.Contains("min event delay 0.5 s", line);
    }

    [RequiresNativeFact]
    public void ASuspendedScriptShowsWhyItIsPausedAndHowManyEventsWait()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua("LLEvents:on('changed', function(c) end)\nossl.Mark('start')\n"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("start")));
        Assert.True(rig.Engine.SuspendScript(item));
        for (int i = 0; i < 3; i++)
            rig.PostProbe(item, 10 + i);

        string line = LineOf(rig, item);

        Assert.Contains("paused: Core", line);
        Assert.Contains("queued 3", line);
    }

    [RequiresNativeFact]
    public void EventsDroppedWhileAScriptIsStoppedAreCounted()
    {
        using var rig = new EngineRig();
        UUID item = rig.Rez(EngineRig.Slua("LLEvents:on('changed', function(c) end)\nossl.Mark('start')\n"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("start")));
        rig.Scene.EventManager.TriggerStopScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => !rig.Engine.SchedulerScriptFor(item).Running));
        rig.PostProbe(item, 1);
        rig.PostProbe(item, 2);

        Assert.True(SchedulerHarness.WaitFor(() => LineOf(rig, item).Contains("dropped while stopped 2")), LineOf(rig, item));
        Assert.Contains("stopped", LineOf(rig, item));
    }

    [RequiresNativeFact]
    public void AScriptHeldBecauseItsSavedStateCouldNotBeReadShowsTheReason()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(System.IO.Path.Combine(dir.Path, "state.db"), "SLua");
        using (var first = new EngineRig(store: store))
        {
            first.Rez(EngineRig.Slua(Quiet), item: item, asset: asset);
            Assert.True(SchedulerHarness.WaitFor(() => first.MarksOf(item).Contains("start")));
        }
        Assert.True(store.Flush());
        store.FailReadFor = id => id == item;
        using var rig = new EngineRig(new Dictionary<string, string> { ["StateLoadRetries"] = "0", ["StateLoadRetryDelayMs"] = "1" }, store: store);
        rig.Rez(EngineRig.Slua(Quiet), item: item, asset: asset);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.HeldReasonOf(item) != null));

        string text = Text(rig.Engine.StatusLines(""));

        Assert.Contains("HELD:", text);
        Assert.Contains("saved state could not be read", text);
        Assert.Contains("state writes", text); // and the store's figures are in the header
    }

    [RequiresNativeFact]
    public void AFilterNamesAnObjectAScriptOrAnItem()
    {
        using var rig = new EngineRig();
        UUID inRoot = rig.Rez(EngineRig.Slua(Quiet));
        SceneObjectGroupHelper other = SceneObjectGroupHelper.Add(rig, "Elsewhere Box");
        UUID inOther = rig.Rez(EngineRig.Slua(Quiet), part: other.Group.RootPart);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(inRoot).Contains("start") && rig.MarksOf(inOther).Contains("start")));

        string byObject = Text(rig.Engine.StatusLines("elsewhere"));
        Assert.Contains(inOther.ToString(), byObject);
        Assert.DoesNotContain(inRoot.ToString(), byObject);

        string byItem = Text(rig.Engine.StatusLines(inRoot.ToString()));
        Assert.Contains(inRoot.ToString(), byItem);
        Assert.DoesNotContain(inOther.ToString(), byItem);

        string byScript = Text(rig.Engine.StatusLines(rig.ItemOf(inRoot).Name));
        Assert.Contains(inRoot.ToString(), byScript);

        Assert.Contains("No script matches", Text(rig.Engine.StatusLines("nothing like this")));
    }

    [RequiresNativeFact]
    public void TheOutputStaysShortWhateverTheNumberOfScripts()
    {
        using var rig = new EngineRig();
        for (int i = 0; i < 60; i++)
            rig.Rez(EngineRig.Slua(Quiet));
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.StatusLines("").Any(l => l.Contains("scripts 60"))));

        IReadOnlyList<string> lines = rig.Engine.StatusLines("");

        Assert.True(lines.Count <= 100, "lines: " + lines.Count);
        Assert.Contains(lines, l => l.Contains("more") && l.Contains("filter"));
    }

    /// <summary>Another object in the test scene.</summary>
    private sealed class SceneObjectGroupHelper
    {
        public OpenSim.Region.Framework.Scenes.SceneObjectGroup Group;

        public static SceneObjectGroupHelper Add(EngineRig rig, string name) =>
            new SceneObjectGroupHelper { Group = rig.AddObject(name, rig.Object.AbsolutePosition + new Vector3(3, 0, 0), EngineRig.Owner) };
    }
}
