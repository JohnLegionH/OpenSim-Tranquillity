/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The engine in a region that restarts: the engine owns the database of its scripts' state, a script that joins with a record of
/// its own is restored from it, a script whose record cannot be read is held with the reason, and the listens a script had come back.
/// A restart is a second engine (and region) over the same database file.
/// </summary>
[Collection("native-library")]
public class EngineStateRestartTests
{
    private const string CountsEvents = "n = (n or 0) + 1\nossl.Mark('start')\nLLEvents:on('changed', function(c) if c < 1000 then n = n + 1; ossl.Mark('n' .. n) end end)\n";

    private static string Db(TempFolder dir) => System.IO.Path.Combine(dir.Path, "state.db");

    private static bool Marked(EngineRig rig, UUID item, string mark) => SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark));

    [RequiresNativeFact]
    public void ARestartedEngineBringsAScriptsGlobalsBackAndDoesNotRunItsMainChunkAgain()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(store: store))
            {
                rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
                Assert.True(Marked(rig, item, "start"));
                rig.PostProbe(item, 1); // the first changed event: n becomes 2
                Assert.True(Marked(rig, item, "n2"));
            } // the region stops: the scripts are saved
            Assert.True(store.Flush());
            Assert.True(store.HasSavedState(item, asset));
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(store: store))
        {
            Assert.True(rig.Engine.HasSavedScriptState(item, asset)); // the region's question is answered from the engine's own database
            rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
            rig.WaitScheduled(item);
            rig.PostProbe(item, 1);
            Assert.True(Marked(rig, item, "n3")); // the count went on from 2
            Assert.DoesNotContain("start", rig.MarksOf(item)); // the main chunk did not run again
        }
    }

    [RequiresNativeFact]
    public void AnEditedScriptHasANewAssetAndStartsFresh()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random();
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(store: store))
            {
                rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: UUID.Random());
                Assert.True(Marked(rig, item, "start"));
            }
            Assert.True(store.Flush());
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(store: store))
        {
            rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: UUID.Random());
            Assert.True(Marked(rig, item, "start"));
        }
    }

    [RequiresNativeFact]
    public void AScriptWhoseSavedStateCannotBeReadIsHeldWithTheReasonAndItsRowIsNeverWrittenOver()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        byte[] before;
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(store: store))
            {
                rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
                Assert.True(Marked(rig, item, "start"));
            }
            Assert.True(store.Flush());
            before = store.Load(item, asset);
            Assert.NotNull(before);
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            store.FailReadFor = id => id == item;
            using (var rig = new EngineRig(new Dictionary<string, string> { ["StateLoadRetries"] = "1", ["StateLoadRetryDelayMs"] = "1" }, store: store))
            {
                rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
                Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.HeldReasonOf(item) != null), "the script is held");
                Assert.Contains("saved state", rig.Engine.HeldReasonOf(item));
                Assert.Contains(rig.Log.Errors, e => e.Contains(item.ToString()));
                Assert.Empty(rig.MarksOf(item)); // it did not start fresh
                Assert.True(rig.Engine.HasSavedScriptState(item, asset)); // and it stays this engine's
            } // the region stops: a held script is not saved
            long writes = store.WritesDone;
            Assert.True(store.Flush());
            Assert.Equal(writes, store.WritesDone);
            store.FailReadFor = null;
            Assert.Equal(before, store.Load(item, asset)); // the row is as it was
            Assert.Null(store.RejectedReason(item));
        }
    }

    [RequiresNativeFact]
    public void ARecordAnotherEngineWroteStartsTheScriptFreshAndIsLeftAlone()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        store.Save(item, asset, new ScriptStateRecord { Engine = "Other" }.Encode());
        Assert.True(store.Flush());

        using (var rig = new EngineRig(store: store))
        {
            rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
            Assert.True(Marked(rig, item, "start"));
        }

        Assert.Null(store.RejectedReason(item));
    }

    [RequiresNativeFact]
    public void SavingCanBeSwitchedOff()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        using (var rig = new EngineRig(new Dictionary<string, string> { ["SaveScriptState"] = "false" }, store: store))
        {
            rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
            Assert.True(Marked(rig, item, "start"));
        }
        Assert.True(store.Flush());
        Assert.Equal(0, store.WritesDone);
    }

    [RequiresNativeFact]
    public void AScriptsListensComeBackAfterARestart()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        const string listener = "default { state_entry() { llOwnerSay(\"entry\"); llListen(77, \"\", NULL_KEY, \"\"); } }";
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(productionFunctions: true, store: store))
            {
                rig.Rez(EngineRig.Lsl(listener), item: item, asset: asset);
                Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.Listens.ListenCount == 1), "the script listens");
            }
            Assert.True(store.Flush());
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(productionFunctions: true, store: store))
        {
            rig.Rez(EngineRig.Lsl(listener), item: item, asset: asset);
            Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.Listens.ListenCount == 1), "the listen is registered again");
            Assert.Equal(1, rig.Engine.Listens.ListenCount); // once
            Assert.DoesNotContain("entry", rig.MarksOf(item)); // state_entry did not run again
        }
    }
}
