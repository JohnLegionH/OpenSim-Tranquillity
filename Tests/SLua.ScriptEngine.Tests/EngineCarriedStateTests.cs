/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Xml;
using OpenMetaverse;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A script's state travels with its object: the core asks the engine for each script's state when an object is taken, derezzed,
/// detached or crosses (GetXMLState) and offers it back when the object arrives, before the script starts (SetXMLState). The engine
/// writes its own envelope, refuses one that is not its own, holds the state until the script loads and uses it before any row of
/// its database. The script goes on where it was and state_entry does not run again; what arrives first depends on how the state
/// arrived (Second Life wiki: State, CHANGED_REGION, CHANGED_TELEPORT).
/// </summary>
[Collection("native-library")]
public class EngineCarriedStateTests
{
    // n counts the main chunk and every changed event; each mark says what ran.
    private const string Counts =
        "n = (n or 0) + 1\nossl.Mark('start')\n" +
        "LLEvents:on('changed', function(c) n = n + 1; ossl.Mark('changed' .. c .. 'n' .. n) end)\n" +
        "LLEvents:on('on_rez', function(p) ossl.Mark('rez' .. p) end)\n";

    private const int NewRez = 1, PrimCrossing = 2;

    private static string Db(TempFolder dir) => System.IO.Path.Combine(dir.Path, "state.db");

    private static bool Marked(EngineRig rig, UUID item, string mark) => SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark));

    /// <summary>A running counter script whose count has reached 3, ready to be taken.</summary>
    private static UUID RunToThree(EngineRig rig, UUID asset)
    {
        UUID item = rig.Rez(EngineRig.Slua(Counts), item: UUID.Random(), asset: asset, stateSource: NewRez);
        Assert.True(Marked(rig, item, "start"));
        rig.PostProbe(item, 1);
        Assert.True(Marked(rig, item, "changed1n2"));
        rig.PostProbe(item, 2);
        Assert.True(Marked(rig, item, "changed2n3"));
        return item;
    }

    private static XmlElement Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc.DocumentElement;
    }

    private static string Envelope(string engine, UUID item, string asset, string version, string payload) =>
        $"<State Engine=\"{engine}\" UUID=\"{item}\" Asset=\"{asset}\" Version=\"{version}\"><ScriptState>{payload}</ScriptState></State>";

    [RequiresNativeFact]
    public void TheStateOfARunningScriptIsAnEnvelopeThatNamesTheEngineTheItemTheAssetAndTheVersion()
    {
        using var rig = new EngineRig();
        UUID asset = UUID.Random();
        UUID item = RunToThree(rig, asset);

        string xml = rig.Engine.GetXMLState(item);

        Assert.NotEmpty(xml);
        XmlElement state = Parse(xml);
        Assert.Equal("State", state.Name);
        Assert.Equal("SLua", state.GetAttribute("Engine"));
        Assert.Equal(item.ToString(), state.GetAttribute("UUID"));
        Assert.Equal(asset.ToString(), state.GetAttribute("Asset"));
        Assert.Equal("1", state.GetAttribute("Version"));
        byte[] record = Convert.FromBase64String(state["ScriptState"].InnerText);
        Assert.NotNull(ScriptStateRecord.Decode(record, "SLua", out _)); // the payload is this engine's record
    }

    [RequiresNativeFact]
    public void AnItemThatIsNotAScriptHereGivesNoState()
    {
        using var rig = new EngineRig();
        Assert.Equal(string.Empty, rig.Engine.GetXMLState(UUID.Random()));
    }

    [RequiresNativeFact]
    public void AnObjectTakenIntoInventoryAndRezzedAgainCountsOnAndRunsNoStateEntry()
    {
        string xml;
        UUID item;
        UUID asset = UUID.Random();
        using (var rig = new EngineRig())
        {
            item = RunToThree(rig, asset);
            xml = rig.Engine.GetXMLState(item);
            Assert.NotEmpty(xml);
        }

        using var again = new EngineRig();
        Assert.True(again.Engine.SetXMLState(item, xml));
        again.Rez(EngineRig.Slua(Counts), item: item, asset: asset, startParam: 7, postOnRez: true, stateSource: NewRez);
        Assert.True(Marked(again, item, "rez7"), "on_rez arrives with its parameter");
        again.PostProbe(item, 3);
        Assert.True(Marked(again, item, "changed3n4"), "the count went on from 3");
        Assert.DoesNotContain("start", again.MarksOf(item)); // the main chunk, where the script's state_entry work is, did not run again
    }

    [RequiresNativeFact]
    public void ACrossingBringsChangedRegionOnceAndTheCountGoesOn()
    {
        string xml;
        UUID item;
        UUID asset = UUID.Random();
        using (var rig = new EngineRig())
        {
            item = RunToThree(rig, asset);
            xml = rig.Engine.GetXMLState(item);
            Assert.NotEmpty(xml);
        }

        using var there = new EngineRig();
        Assert.True(there.Engine.SetXMLState(item, xml));
        there.Rez(EngineRig.Slua(Counts), item: item, asset: asset, stateSource: PrimCrossing);
        Assert.True(Marked(there, item, "changed256n4"), "CHANGED_REGION (0x100) arrives, and the count went on from 3");
        Assert.Single(there.MarksOf(item), m => m.StartsWith("changed256")); // once
        Assert.DoesNotContain("start", there.MarksOf(item));
    }

    [RequiresNativeFact]
    public void TheCarriedStateIsUsedBeforeTheRowOfTheEnginesDatabase()
    {
        using var dir = new TempFolder();
        UUID asset = UUID.Random();
        UUID item;
        string carried;
        using (var rig = new EngineRig())
        {
            item = RunToThree(rig, asset);
            carried = rig.Engine.GetXMLState(item); // count 3
            Assert.NotEmpty(carried);
        }
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(store: store))
            {
                rig.Rez(EngineRig.Slua(Counts), item: item, asset: asset, stateSource: NewRez);
                Assert.True(Marked(rig, item, "start")); // count 1 in the row
            }
            Assert.True(store.Flush());
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(store: store))
        {
            Assert.True(rig.Engine.SetXMLState(item, carried));
            rig.Rez(EngineRig.Slua(Counts), item: item, asset: asset, stateSource: NewRez);
            rig.WaitScheduled(item);
            rig.PostProbe(item, 9);
            Assert.True(Marked(rig, item, "changed9n4"), "the count of the carried state, not the row's");
        }
    }

    [RequiresNativeFact]
    public void StateCarriedForAnotherAssetIsDroppedAndTheEditedScriptStartsFresh()
    {
        string xml;
        UUID item;
        using (var rig = new EngineRig())
        {
            item = RunToThree(rig, UUID.Random());
            xml = rig.Engine.GetXMLState(item);
            Assert.NotEmpty(xml);
        }

        using var again = new EngineRig();
        Assert.True(again.Engine.SetXMLState(item, xml)); // taken: the asset is only compared when the script loads
        again.Rez(EngineRig.Slua(Counts), item: item, asset: UUID.Random(), stateSource: NewRez);
        Assert.True(Marked(again, item, "start"), "an edited script has a new asset and starts fresh");
    }

    [RequiresNativeFact]
    public void CarriedStateIsUsedOnceAndAResetScriptStartsFresh()
    {
        string xml;
        UUID item;
        UUID asset = UUID.Random();
        using (var rig = new EngineRig())
        {
            item = RunToThree(rig, asset);
            xml = rig.Engine.GetXMLState(item);
            Assert.NotEmpty(xml);
        }

        using var again = new EngineRig();
        Assert.True(again.Engine.SetXMLState(item, xml));
        again.Rez(EngineRig.Slua(Counts), item: item, asset: asset, stateSource: NewRez);
        again.WaitScheduled(item);
        again.PostProbe(item, 5);
        Assert.True(Marked(again, item, "changed5n4"));
        again.Rez(EngineRig.Slua(Counts), item: item, asset: asset, stateSource: NewRez); // the item is loaded again, as an edit does
        Assert.True(SchedulerHarness.WaitFor(() => again.MarksOf(item).Contains("start")), "the carried state was not kept for a second load");
    }

    [RequiresNativeFact]
    public void AnEnvelopeThatIsNotOurOwnIsRefusedSoTheCoreOffersItToTheNextEngine()
    {
        using var rig = new EngineRig();
        UUID asset = UUID.Random();
        UUID item = RunToThree(rig, asset);
        string good = rig.Engine.GetXMLState(item);
        Assert.NotEmpty(good);
        string payload = Parse(good)["ScriptState"].InnerText;
        UUID other = UUID.Random();

        Assert.True(rig.Engine.SetXMLState(other, good.Replace(item.ToString(), other.ToString()))); // the same envelope for another item is taken

        UUID target = UUID.Random();
        Assert.False(rig.Engine.SetXMLState(target, Envelope("YEngine", target, asset.ToString(), "1", payload)), "another engine's");
        Assert.False(rig.Engine.SetXMLState(target, Envelope("SLua", target, asset.ToString(), "2", payload)), "another envelope version");
        Assert.False(rig.Engine.SetXMLState(target, Envelope("SLua", target, "not-a-uuid", "1", payload)), "no asset");
        Assert.False(rig.Engine.SetXMLState(target, Envelope("SLua", target, asset.ToString(), "1", "!!!not base64!!!")), "bytes that are not base64");
        Assert.False(rig.Engine.SetXMLState(target, Envelope("SLua", target, asset.ToString(), "1", Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }))), "bytes that are no record");
        Assert.False(rig.Engine.SetXMLState(target, "<State"), "XML that does not parse");
        Assert.False(rig.Engine.SetXMLState(target, string.Empty), "nothing");
    }

    [RequiresNativeFact]
    public void ARecordAnotherEngineWroteInsideOurEnvelopeIsRefused()
    {
        using var rig = new EngineRig();
        UUID asset = UUID.Random();
        UUID item = RunToThree(rig, asset);
        Assert.NotEmpty(rig.Engine.GetXMLState(item));
        UUID target = UUID.Random();
        byte[] foreign = new ScriptStateRecord { Engine = "Other" }.Encode();

        Assert.False(rig.Engine.SetXMLState(target, Envelope("SLua", target, asset.ToString(), "1", Convert.ToBase64String(foreign))));
    }

    [RequiresNativeFact]
    public void AnEnvelopeAboveTheSizeCapIsRefusedBeforeItIsDecoded()
    {
        using var rig = new EngineRig();
        UUID asset = UUID.Random();
        UUID item = RunToThree(rig, asset);
        string good = rig.Engine.GetXMLState(item);
        Assert.NotEmpty(good);
        UUID target = UUID.Random();
        Assert.True(rig.Engine.SetXMLState(target, good.Replace(item.ToString(), target.ToString()))); // within the cap

        // Not XML at all, and longer than the cap: a decode would fail for another reason, so the refusal's reason is the length.
        string huge = new string('x', SLuaEngine.MaxCarriedStateXmlChars + 1);
        Assert.False(rig.Engine.SetXMLState(target, huge));
        Assert.Contains(rig.Log.Warnings, w => w.Contains(target.ToString()) && w.Contains("above"));
    }

    [RequiresNativeFact]
    public void AScriptHeldBecauseItsRowCouldNotBeReadCarriesNoState()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        using (var first = new EngineRig(store: store))
        {
            first.Rez(EngineRig.Slua(Counts), item: item, asset: asset, stateSource: NewRez);
            Assert.True(Marked(first, item, "start"));
            Assert.NotEmpty(first.Engine.GetXMLState(item)); // a script that runs carries its state
        }
        Assert.True(store.Flush());
        store.FailReadFor = id => id == item;
        using var rig = new EngineRig(new Dictionary<string, string> { ["StateLoadRetries"] = "0", ["StateLoadRetryDelayMs"] = "1" }, store: store);
        rig.Rez(EngineRig.Slua(Counts), item: item, asset: asset, stateSource: NewRez);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.HeldReasonOf(item) != null), "the script is held");

        Assert.Equal(string.Empty, rig.Engine.GetXMLState(item)); // its state is unknown, and the row stays the only copy
    }

    [RequiresNativeFact]
    public void TheScriptsOfAnObjectAreCapturedTogetherInOnePass()
    {
        using var rig = new EngineRig(new Dictionary<string, string> { ["WorkerThreads"] = "2" });
        var items = new List<UUID>();
        for (int i = 0; i < 3; i++)
        {
            UUID item = rig.Rez(EngineRig.Slua(Counts), asset: UUID.Random(), stateSource: NewRez);
            Assert.True(Marked(rig, item, "start"));
            items.Add(item);
        }

        foreach (UUID item in items)
            Assert.NotEmpty(rig.Engine.GetXMLState(item)); // the core asks one script at a time

        Assert.Equal(1, rig.Engine.CarriedCapturePasses);
    }

    [RequiresNativeFact]
    public void AScriptAFaultStoppedIsCarriedStoppedAndArrivesQuietly()
    {
        string xml;
        UUID item;
        UUID asset = UUID.Random();
        const string Faulting = "LLEvents:on('changed', function(c) error('boom') end)\nossl.Mark('start')\n";
        using (var rig = new EngineRig())
        {
            item = rig.Rez(EngineRig.Slua(Faulting), asset: asset, stateSource: NewRez);
            Assert.True(Marked(rig, item, "start"));
            rig.PostProbe(item, 1);
            Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item).Killed), "the script is stopped by its fault");
            xml = rig.Engine.GetXMLState(item);
            Assert.NotEmpty(xml);
        }

        using var there = new EngineRig();
        Assert.True(there.Engine.SetXMLState(item, xml));
        there.Rez(EngineRig.Slua(Faulting), item: item, asset: asset, running: false, stateSource: PrimCrossing);
        SchedulerScript s = there.WaitScheduled(item);
        Assert.True(SchedulerHarness.WaitFor(() => s.Killed), "it arrives stopped");
        Assert.Contains("boom", s.FaultMessage);
        Assert.DoesNotContain("start", there.MarksOf(item)); // it did not run
        Assert.Empty(there.Messages.Shouts); // the owner was told when it first happened
        Assert.Single(there.Log.Lines, l => l.Contains(item.ToString()) && l.Contains("boom")); // one line in the log
    }
}
