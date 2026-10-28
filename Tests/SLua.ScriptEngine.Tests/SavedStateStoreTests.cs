/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.VM;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Where a script's saved state lives (the engine's own database, apart from every other engine's) and who owns a row. The
/// shape is Phlox's StateManager's: one database file in write-ahead mode, a busy timeout, every write done by one writer, and a
/// row that cannot be used moved aside with its reason and never overwritten. A read error is not "no state".
/// </summary>
public class SavedStateStoreTests
{
    private static string DbIn(TempFolder dir) => System.IO.Path.Combine(dir.Path, "state", "script_state.db");

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public void AStateSavedForAnItemAndAssetComesBack()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(DbIn(dir), "SLua");
        UUID item = UUID.Random(), asset = UUID.Random();

        store.Save(item, asset, Bytes("one"));
        Assert.True(store.Flush());

        Assert.True(store.HasSavedState(item, asset));
        Assert.Equal(Bytes("one"), store.Load(item, asset));
    }

    [Fact]
    public void ANewerSaveReplacesTheOlderOne()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(DbIn(dir), "SLua");
        UUID item = UUID.Random(), asset = UUID.Random();

        store.Save(item, asset, Bytes("one"));
        store.Save(item, asset, Bytes("two"));
        Assert.True(store.Flush());

        Assert.Equal(Bytes("two"), store.Load(item, asset));
    }

    [Fact]
    public void AnEditedScriptHasANewAssetAndNoSavedStateWhileTheOldRowStays()
    {
        using var dir = new TempFolder();
        using var store = new SqliteStateStore(DbIn(dir), "SLua");
        UUID item = UUID.Random(), asset = UUID.Random(), edited = UUID.Random();
        store.Save(item, asset, Bytes("old script"));
        Assert.True(store.Flush());

        Assert.False(store.HasSavedState(item, edited));
        Assert.Null(store.Load(item, edited));
        Assert.True(store.HasSavedState(item, asset));
        Assert.Equal(Bytes("old script"), store.Load(item, asset));
    }

    [Fact]
    public void StateSurvivesClosingAndReopeningTheDatabase()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using (var store = new SqliteStateStore(DbIn(dir), "SLua"))
        {
            store.Save(item, asset, Bytes("kept"));
            Assert.True(store.Flush());
        }

        using var again = new SqliteStateStore(DbIn(dir), "SLua");
        Assert.True(again.HasSavedState(item, asset));
        Assert.Equal(Bytes("kept"), again.Load(item, asset));
    }

    [Fact]
    public void DisposingTheStoreWritesWhatWasQueued()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        var store = new SqliteStateStore(DbIn(dir), "SLua");
        store.Save(item, asset, Bytes("queued"));
        store.Dispose();

        using var again = new SqliteStateStore(DbIn(dir), "SLua");
        Assert.Equal(Bytes("queued"), again.Load(item, asset));
    }

    [Fact]
    public void ARowAnotherEngineWroteIsNotOursAndIsNeverOverwrittenByOurSave()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var other = new SqliteStateStore(DbIn(dir), "Other");
        using var ours = new SqliteStateStore(DbIn(dir), "SLua");
        other.Save(item, asset, Bytes("theirs"));
        Assert.True(other.Flush());

        Assert.False(ours.HasSavedState(item, asset));
        Assert.Null(ours.Load(item, asset));

        ours.Save(item, asset, Bytes("ours"));
        Assert.True(ours.Flush());
        Assert.Equal(Bytes("ours"), ours.Load(item, asset));
        Assert.Equal(Bytes("theirs"), other.Load(item, asset));
    }

    [Fact]
    public void AReadErrorKeepsOwnershipAndIsNotTakenForNoState()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(DbIn(dir), "SLua");
        store.Save(item, asset, Bytes("precious"));
        Assert.True(store.Flush());

        store.FailReadFor = id => id == item;

        Assert.True(store.HasSavedState(item, asset)); // the region must not hand the script to another engine
        var e = Assert.Throws<ScriptStateLoadException>(() => store.Load(item, asset));
        Assert.Equal(item, e.ItemId);

        store.FailReadFor = null;
        Assert.Equal(Bytes("precious"), store.Load(item, asset)); // the row was not touched
    }

    [Fact]
    public void ARejectedRowIsTakenOutOfUseAndKeptWithItsReason()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(DbIn(dir), "SLua");
        store.Save(item, asset, Bytes("unusable"));
        Assert.True(store.Flush());

        store.Reject(item, "saved by another runtime build");
        Assert.True(store.Flush());

        Assert.False(store.HasSavedState(item, asset));
        Assert.Null(store.Load(item, asset));
        Assert.Equal("saved by another runtime build", store.RejectedReason(item));
        Assert.Equal(Bytes("unusable"), store.RejectedState(item));
    }

    [Fact]
    public void DeletingRemovesOurRowOnly()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var other = new SqliteStateStore(DbIn(dir), "Other");
        using var ours = new SqliteStateStore(DbIn(dir), "SLua");
        ours.Save(item, asset, Bytes("ours"));
        other.Save(item, asset, Bytes("theirs"));
        Assert.True(ours.Flush());
        Assert.True(other.Flush());

        ours.Delete(item);
        Assert.True(ours.Flush());

        Assert.False(ours.HasSavedState(item, asset));
        Assert.Equal(Bytes("theirs"), other.Load(item, asset));
    }

    [Fact]
    public void ManyThreadsAndTwoStoresWriteTheSameFileWithoutLosingARow()
    {
        using var dir = new TempFolder();
        using var a = new SqliteStateStore(DbIn(dir), "SLua");
        using var b = new SqliteStateStore(DbIn(dir), "Other");
        var items = Enumerable.Range(0, 120).Select(_ => (Item: UUID.Random(), Asset: UUID.Random())).ToArray();

        System.Threading.Tasks.Parallel.For(0, items.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            a.Save(items[i].Item, items[i].Asset, Bytes("a" + i));
            b.Save(items[i].Item, items[i].Asset, Bytes("b" + i));
        });
        Assert.True(a.Flush());
        Assert.True(b.Flush());

        for (int i = 0; i < items.Length; i++)
        {
            Assert.Equal(Bytes("a" + i), a.Load(items[i].Item, items[i].Asset));
            Assert.Equal(Bytes("b" + i), b.Load(items[i].Item, items[i].Asset));
        }
        Assert.Equal(items.Length, a.WritesDone);
    }
}
