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
/// A script's saved row goes with the script: it is deleted when the script is deleted, and deleting an object deletes the rows of all
/// its scripts. These are rows in the engine's own database of script state. A region that shuts down deletes nothing.
/// </summary>
[Collection("native-library")]
public class EngineRowDeletionTests
{
    private const string Quiet = "ossl.Mark('start')\n";

    private static string Db(TempFolder dir) => System.IO.Path.Combine(dir.Path, "state.db");

    private static Dictionary<string, string> Fast => new() { ["StateSaveIntervalMs"] = "50" };

    /// <summary>The script ran and its row is in the database (the timed save wrote it).</summary>
    private static bool HasRow(SqliteStateStore store, UUID item, UUID asset)
    {
        store.Flush();
        return store.HasSavedState(item, asset);
    }

    private static UUID RezSaved(EngineRig rig, SqliteStateStore store, UUID asset, Action<UUID> sawStart = null, OpenSim.Region.Framework.Scenes.SceneObjectPart part = null)
    {
        UUID item = rig.Rez(EngineRig.Slua(Quiet), asset: asset, part: part);
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("start")));
        Assert.True(SchedulerHarness.WaitFor(() => HasRow(store, item, asset)), "the script's row was written");
        return item;
    }

    [RequiresNativeFact]
    public void ADeletedScriptTakesItsRowWithIt()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        using var rig = new EngineRig(Fast, store: store);
        UUID asset = UUID.Random();
        UUID item = RezSaved(rig, store, asset);

        rig.Object.RootPart.Inventory.RemoveInventoryItem(item); // the item leaves its prim, and the region stops its script

        Assert.True(SchedulerHarness.WaitFor(() => !HasRow(store, item, asset)), "the row is gone");
    }

    [RequiresNativeFact]
    public void DeletingTheObjectDeletesTheRowsOfAllItsScripts()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        using var rig = new EngineRig(Fast, store: store);
        var rows = new List<(UUID Item, UUID Asset)>();
        for (int i = 0; i < 3; i++)
        {
            UUID asset = UUID.Random();
            rows.Add((RezSaved(rig, store, asset), asset));
        }
        UUID kept = UUID.Random(), keptAsset = UUID.Random();
        store.Save(kept, keptAsset, new ScriptStateRecord { Engine = "SLua" }.Encode()); // a row of a script of another object
        Assert.True(store.Flush());

        rig.Scene.DeleteSceneObject(rig.Object, false);

        foreach ((UUID item, UUID asset) in rows)
            Assert.True(SchedulerHarness.WaitFor(() => !HasRow(store, item, asset)), "the row of " + item + " is gone");
        Assert.True(store.HasSavedState(kept, keptAsset)); // and only those
    }

    [RequiresNativeFact]
    public void ARegionShuttingDownDeletesNoRow()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        using var rig = new EngineRig(Fast, store: store);
        UUID asset = UUID.Random();
        UUID item = RezSaved(rig, store, asset);

        rig.Scene.EventManager.TriggerShutdown();
        rig.Object.RootPart.Inventory.RemoveInventoryItem(item); // the region takes its scripts out on the way down

        Assert.True(store.Flush());
        Assert.True(store.HasSavedState(item, asset), "the row is what the restart restores from");
    }

    [RequiresNativeFact]
    public void AScriptThatLeavesForAnotherEngineKeepsItsRow()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        using var rig = new EngineRig(Fast, store: store);
        UUID asset = UUID.Random();
        UUID item = RezSaved(rig, store, asset);

        // The owner edits the first line to name Phlox: the engine lets go of the item, as OwnershipTests shows, and the row stays.
        rig.Scene.EventManager.TriggerRezScript(rig.LocalId, item, "//InWorldz.Phlox:\ndefault {}", 0, false, "InWorldz.Phlox", 0);

        Assert.True(store.Flush());
        Assert.True(store.HasSavedState(item, asset));
    }

    [RequiresNativeFact]
    public void SwitchingScriptsOffKeepsTheRowsAndAfterwardsDeletingTheObjectOrAScriptStillDeletesThem()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(Db(dir), "SLua");
        using var rig = new EngineRig(Fast, store: store);
        UUID asset = UUID.Random(), other = UUID.Random();
        UUID item = RezSaved(rig, store, asset);
        UUID second = RezSaved(rig, store, other);

        rig.Scene.ScriptsEnabled = false; // the console's "scripts stop": every script is removed, the items stay in their prims

        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item) == null), "the script was taken out");
        Assert.True(store.Flush());
        Assert.True(store.HasSavedState(item, asset), "the row stays when scripts are only switched off");
        Assert.True(store.HasSavedState(second, other));

        rig.Scene.DeleteSceneObject(rig.Object, false); // and the object deleted afterwards takes its scripts' rows

        Assert.True(SchedulerHarness.WaitFor(() => !HasRow(store, item, asset)), "the object's delete removes the row");
        Assert.True(SchedulerHarness.WaitFor(() => !HasRow(store, second, other)));
    }
}
