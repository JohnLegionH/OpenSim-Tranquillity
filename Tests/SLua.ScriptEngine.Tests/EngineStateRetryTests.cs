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
/// The "state retry" console command: a script whose saved state was set aside (the runtime refused it) started fresh; the command tries
/// the set-aside state again under the running build, puts it back if it now loads, and otherwise leaves it set aside and says why.
/// </summary>
[Collection("native-library")]
public class EngineStateRetryTests
{
    private const string CountsEvents = "n = (n or 0) + 1\nossl.Mark('start')\nLLEvents:on('changed', function(c) if c < 1000 then n = n + 1; ossl.Mark('n' .. n) end end)\n";

    private static string Db(TempFolder dir) => System.IO.Path.Combine(dir.Path, "state.db");

    private static bool Marked(EngineRig rig, UUID item, string mark) => SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark));

    /// <summary>A script that has counted to 2 and been saved, then had its state set aside, as a refusal does.</summary>
    private static void SaveThenSetAside(SqliteStateStore store, UUID item, UUID asset, string reason, Action<ScriptStateRecord> damage = null)
    {
        using (var rig = new EngineRig(store: store))
        {
            rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
            Assert.True(Marked(rig, item, "start"));
            rig.PostProbe(item, 1);
            Assert.True(Marked(rig, item, "n2"));
        }
        Assert.True(store.Flush());
        if (damage != null)
        {
            ScriptStateRecord saved = ScriptStateRecord.Decode(store.Load(item, asset), "SLua", out string why);
            Assert.True(saved != null, why);
            damage(saved);
            store.Save(item, asset, saved.Encode());
            Assert.True(store.Flush());
        }
        store.Reject(item, reason);
        Assert.True(store.Flush());
    }

    [RequiresNativeFact]
    public void TheRetryCommandPutsASetAsideStateBackAndTheScriptGoesOnFromItsCount()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        SaveThenSetAside(store, item, asset, "the runtime refused the saved state (test)");

        using var rig = new EngineRig(store: store);
        rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
        Assert.True(Marked(rig, item, "start")); // no row: it started fresh
        rig.WaitScheduled(item);

        string[] lines = rig.Engine.RetryStateLines(item.ToString());
        Assert.Contains(lines, l => l.Contains(item.ToString()) && l.Contains("restored"));
        rig.PostProbe(item, 1);
        Assert.True(Marked(rig, item, "n3"), "the count did not go on from 2: " + string.Join(",", rig.MarksOf(item))); // the old state's n was 2

        Assert.True(store.Flush());
        Assert.Contains(rig.Engine.RetryStateLines(""), l => l.Contains("No set-aside"));   // it is not offered again
    }

    [RequiresNativeFact]
    public void TheRetryCommandLeavesAStateThatStillCannotLoadSetAsideAndTheScriptRunning()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        SaveThenSetAside(store, item, asset, "the runtime refused the saved state (test)", saved => saved.Native = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

        using var rig = new EngineRig(store: store);
        rig.Rez(EngineRig.Slua(CountsEvents), item: item, asset: asset);
        Assert.True(Marked(rig, item, "start"));
        rig.WaitScheduled(item);

        string[] lines = rig.Engine.RetryStateLines("");
        Assert.Contains(lines, l => l.Contains(item.ToString()) && l.Contains("still set aside"));
        rig.PostProbe(item, 1);
        Assert.True(Marked(rig, item, "n2")); // the fresh script went on from its own count
        Assert.True(store.Flush());
        Assert.NotNull(store.PendingRejected(item)); // so it can be tried again after the next build
    }

    [RequiresNativeFact]
    public void TheRetryCommandSaysSoWhenNoScriptMatches()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        using var rig = new EngineRig(store: store);
        Assert.Contains(rig.Engine.RetryStateLines("no such script"), l => l.Contains("No script matches"));
    }
}
