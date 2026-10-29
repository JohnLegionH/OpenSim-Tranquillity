/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The SQLite region store saves one object, and one prim's inventory, in a single transaction. Without one,
/// every statement is its own commit and each commit waits for the disk, so saving a region with many changed
/// objects (a backup, a shutdown) takes minutes. A failure part-way through an object must roll the whole object
/// back and leave the previously saved state and the store's in-memory copy of it intact.
///
/// SQLite runs against a throwaway in-memory database per test (shared cache, kept alive by an anchor connection,
/// so that a second store instance reads what the first one wrote, as a region restart does). No files are written.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
public class SQLiteRegionStoreTransactionTests : OpenSimTestCase
{
    private static readonly UUID s_region = new UUID("6b0e2f4d-93a1-4c57-8e2b-d4f71a09c3e5");
    private static readonly UUID s_owner = new UUID("a3c95e18-27d4-4f6b-b081-5e9d3c7a2f46");

    private readonly List<IDisposable> m_disposables = new();
    private readonly List<SQLiteSimulationData> m_stores = new();

    static SQLiteRegionStoreTransactionTests()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    public override void Dispose()
    {
        foreach (SQLiteSimulationData store in m_stores)
        {
            try { store.Dispose(); } catch { /* best effort */ }
        }
        m_stores.Clear();
        for (int i = m_disposables.Count - 1; i >= 0; i--)
        {
            try { m_disposables[i].Dispose(); } catch { /* best effort */ }
        }
        m_disposables.Clear();
        base.Dispose();
    }

    private string NewMemoryDatabase()
    {
        string conn = "FullUri=file:regiontx_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        SQLiteConnection anchor = new SQLiteConnection(conn);
        anchor.Open();
        m_disposables.Add(anchor);
        return conn;
    }

    private SQLiteConnection OpenConnection(string conn)
    {
        SQLiteConnection c = new SQLiteConnection(conn);
        c.Open();
        m_disposables.Add(c);
        return c;
    }

    private SQLiteSimulationData NewStore(string conn)
    {
        SQLiteSimulationData store = new SQLiteSimulationData(conn);
        m_stores.Add(store);
        return store;
    }

    private static SQLiteConnection ConnectionOf(SQLiteSimulationData store)
    {
        FieldInfo f = typeof(SQLiteSimulationData).GetField("m_conn", BindingFlags.Instance | BindingFlags.NonPublic);
        return (SQLiteConnection)f.GetValue(store);
    }

    /// <summary>Counts the commits the store's own connection makes while the returned object is live.</summary>
    private sealed class CommitCounter : IDisposable
    {
        private readonly SQLiteConnection m_conn;
        private readonly SQLiteCommitHandler m_hook;
        public int Count;

        public CommitCounter(SQLiteSimulationData store)
        {
            m_conn = ConnectionOf(store);
            m_hook = (_, _) => Count++;
            m_conn.Commit += m_hook;
        }

        public void Dispose() => m_conn.Commit -= m_hook;
    }

    /// <summary>An object of <paramref name="parts"/> parts with fixed ids and values, so stored rows are repeatable.</summary>
    private static SceneObjectGroup NewObject(int index, int parts, string name)
    {
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(parts, s_owner, name + "_", 0x1000 + index * 16);
        foreach (SceneObjectPart part in sog.Parts)
        {
            part.CreationDate = 1700000000;
            part.PseudoCRC = 12345; // a part starts with the clock's ticks
            part.Description = "description of " + part.Name;
        }
        return sog;
    }

    private static List<TaskInventoryItem> NewItems(SceneObjectPart part, int count)
    {
        List<TaskInventoryItem> items = new();
        for (int i = 0; i < count; i++)
        {
            items.Add(new TaskInventoryItem
            {
                ItemID = new UUID(new Guid(MD5.HashData(Encoding.ASCII.GetBytes(part.UUID + "/" + i)))),
                ParentPartID = part.UUID,
                ParentID = part.UUID,
                AssetID = new UUID($"00000000-0000-0000-0002-{i:X12}"),
                Name = "item " + i,
                Description = "item description " + i,
                OwnerID = s_owner,
                CreatorID = s_owner,
                InvType = (int)InventoryType.LSL,
                Type = (int)AssetType.LSLText,
                CreationDate = 1700000000,
            });
        }
        return items;
    }

    private static void Exec(SQLiteConnection c, string sql)
    {
        using SQLiteCommand cmd = new SQLiteCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(SQLiteConnection c, string sql)
    {
        using SQLiteCommand cmd = new SQLiteCommand(sql, c);
        return cmd.ExecuteScalar();
    }

    /// <summary>Every column of every row of a table, as text, in key order.</summary>
    private static string Dump(SQLiteConnection c, string table, string orderBy)
    {
        StringBuilder sb = new();
        using SQLiteCommand cmd = new SQLiteCommand("select * from " + table + " order by " + orderBy, c);
        using SQLiteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            for (int i = 0; i < r.FieldCount; i++)
            {
                sb.Append(r.GetName(i)).Append('=');
                object v = r.GetValue(i);
                sb.Append(v switch
                {
                    null => "null",
                    DBNull => "null",
                    byte[] b => Convert.ToHexString(b),
                    float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    _ => v.ToString(),
                });
                sb.Append('|');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Fingerprint(SQLiteConnection c)
    {
        string all = Dump(c, "prims", "UUID") + "#" + Dump(c, "primshapes", "UUID") + "#" + Dump(c, "primitems", "itemID");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all)));
    }

    private static void SaveLikeABackup(SQLiteSimulationData store, SceneObjectGroup sog, int itemsPerPart)
    {
        store.StoreObject(sog, s_region);
        if (itemsPerPart == 0)
            return;
        foreach (SceneObjectPart part in sog.Parts)
            store.StorePrimInventory(part.UUID, NewItems(part, itemsPerPart));
    }

    [Fact]
    public void StoringAnObjectIsOneCommit()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        SceneObjectGroup sog = NewObject(1, 4, "First");

        using CommitCounter commits = new CommitCounter(store);
        store.StoreObject(sog, s_region);

        Assert.Equal(1, commits.Count);
        Assert.Equal(4L, Scalar(OpenConnection(conn), "select count(*) from prims"));
        Assert.Equal(4L, Scalar(OpenConnection(conn), "select count(*) from primshapes"));
    }

    [Fact]
    public void StoringTheSameObjectAgainIsOneCommit()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        SceneObjectGroup sog = NewObject(1, 4, "First");
        store.StoreObject(sog, s_region);
        sog.RootPart.Name = "Renamed";
        sog.Parts[2].Description = "changed";

        using CommitCounter commits = new CommitCounter(store);
        store.StoreObject(sog, s_region);

        Assert.Equal(1, commits.Count);
        Assert.Equal("Renamed", Scalar(OpenConnection(conn), "select Name from prims where UUID = '" + sog.RootPart.UUID + "'"));
    }

    [Fact]
    public void StoringAPrimsInventoryIsOneCommit()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        SceneObjectGroup sog = NewObject(1, 2, "First");
        store.StoreObject(sog, s_region);
        store.StorePrimInventory(sog.RootPart.UUID, NewItems(sog.RootPart, 5));

        using CommitCounter commits = new CommitCounter(store);
        store.StorePrimInventory(sog.RootPart.UUID, NewItems(sog.RootPart, 3));

        Assert.Equal(1, commits.Count);
        Assert.Equal(3L, Scalar(OpenConnection(conn), "select count(*) from primitems where primID = '" + sog.RootPart.UUID + "'"));
    }

    [Fact]
    public void RemovingAnObjectIsOneCommit()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        SceneObjectGroup sog = NewObject(1, 3, "First");
        SaveLikeABackup(store, sog, 2);
        Assert.Equal(6L, Scalar(OpenConnection(conn), "select count(*) from primitems"));

        using CommitCounter commits = new CommitCounter(store);
        store.RemoveObject(sog.UUID, s_region);

        Assert.Equal(1, commits.Count);
        Assert.Equal(0L, Scalar(OpenConnection(conn), "select count(*) from prims"));
        Assert.Equal(0L, Scalar(OpenConnection(conn), "select count(*) from primshapes"));
        Assert.Equal(0L, Scalar(OpenConnection(conn), "select count(*) from primitems"));
    }

    [Fact]
    public void ARegionSaveStoresTheSameRowsAsBefore()
    {
        // The expected fingerprint was taken from the store as it was before the transaction change, with the
        // same objects, so a change in what is stored (not only in how) fails here.
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        for (int i = 0; i < 12; i++)
        {
            SceneObjectGroup sog = NewObject(i, 1 + i % 4, "Obj" + i);
            SaveLikeABackup(store, sog, i % 3);
            if (i % 4 == 0)
            {
                sog.RootPart.Name = "Changed" + i;
                store.StoreObject(sog, s_region);
            }
        }
        store.RemoveObject(NewObject(5, 1 + 5 % 4, "Obj5").UUID, s_region);

        SQLiteConnection reader = OpenConnection(conn);
        string fingerprint = Fingerprint(reader);
        Assert.True(ExpectedRegionSaveFingerprint == fingerprint, "stored rows fingerprint " + fingerprint);

        // And the objects read back from a second store are the ones saved.
        List<SceneObjectGroup> loaded = NewStore(conn).LoadObjects(s_region);
        Assert.Equal(11, loaded.Count);
        Assert.Equal(Convert.ToInt64(Scalar(reader, "select count(*) from prims")), loaded.Sum(g => g.Parts.Length));
        Assert.Equal(Convert.ToInt64(Scalar(reader, "select count(*) from primitems")),
            loaded.Sum(g => g.Parts.Sum(p => p.Inventory.GetInventoryList().Count)));
    }

    // SHA-256 of every column of every prims, primshapes and primitems row, taken from the store before it saved in
    // transactions.
    private const string ExpectedRegionSaveFingerprint = "8A825D954B2A24DFBCF4E329CDF75F7545B1F57443648F82BE15299E1FCB141D";

    [Fact]
    public void AFailureMidObjectLeavesThePreviousSavedStateAndTheStoreKeepsWorking()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        SceneObjectGroup sog = NewObject(1, 3, "Before");
        SaveLikeABackup(store, sog, 1);
        SQLiteConnection db = OpenConnection(conn);
        string before = Fingerprint(db);

        // The prims rows update first, then the shape rows: the shape update of the last part fails.
        UUID failing = sog.Parts[2].UUID;
        Exec(db, "create trigger fail_shape before update on primshapes when new.UUID = '" + failing +
                 "' begin select raise(abort, 'injected failure'); end");
        foreach (SceneObjectPart part in sog.Parts)
            part.Name = "After";
        Assert.ThrowsAny<Exception>(() => store.StoreObject(sog, s_region));

        Assert.Equal(before, Fingerprint(db));
        Assert.Equal(3L, Scalar(db, "select count(*) from prims where Name like 'Before%'"));
        Assert.Equal(0L, Scalar(db, "select count(*) from prims where Name = 'After'"));

        // The failure is gone: the same store saves the object, and a restart reads the new state.
        Exec(db, "drop trigger fail_shape");
        store.StoreObject(sog, s_region);
        Assert.Equal(3L, Scalar(db, "select count(*) from prims where Name = 'After'"));
        SceneObjectGroup reloaded = NewStore(conn).LoadObjects(s_region).Single();
        Assert.All(reloaded.Parts, p => Assert.Equal("After", p.Name));
        Assert.All(reloaded.Parts, p => Assert.Equal(1, p.Inventory.GetInventoryList().Count));
    }

    [Fact]
    public void AFailureWhileAddingANewObjectStoresNoPartOfIt()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        SceneObjectGroup kept = NewObject(1, 2, "Kept");
        store.StoreObject(kept, s_region);
        SQLiteConnection db = OpenConnection(conn);
        string before = Fingerprint(db);

        SceneObjectGroup added = NewObject(2, 3, "Added");
        Exec(db, "create trigger fail_shape before insert on primshapes when new.UUID = '" + added.Parts[2].UUID +
                 "' begin select raise(abort, 'injected failure'); end");
        Assert.ThrowsAny<Exception>(() => store.StoreObject(added, s_region));

        Assert.Equal(before, Fingerprint(db));
        Assert.Equal(2L, Scalar(db, "select count(*) from prims"));

        Exec(db, "drop trigger fail_shape");
        store.StoreObject(added, s_region);
        Assert.Equal(5L, Scalar(db, "select count(*) from prims"));
        Assert.Equal(5L, Scalar(db, "select count(*) from primshapes"));
    }

    [Fact]
    public void AFailureWhileStoringInventoryKeepsThePreviousItems()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        SceneObjectGroup sog = NewObject(1, 1, "Scripted");
        store.StoreObject(sog, s_region);
        store.StorePrimInventory(sog.RootPart.UUID, NewItems(sog.RootPart, 4));
        SQLiteConnection db = OpenConnection(conn);
        string before = Fingerprint(db);

        // The old items are removed first; the third replacement item fails to insert.
        List<TaskInventoryItem> replacement = NewItems(sog.RootPart, 6);
        replacement.ForEach(i => i.Name = "replacement");
        Exec(db, "create trigger fail_item before insert on primitems when new.assetID = '" + replacement[2].AssetID +
                 "' and new.name = 'replacement' begin select raise(abort, 'injected failure'); end");
        Assert.ThrowsAny<Exception>(() => store.StorePrimInventory(sog.RootPart.UUID, replacement));

        Assert.Equal(before, Fingerprint(db));
        Assert.Equal(4L, Scalar(db, "select count(*) from primitems"));

        Exec(db, "drop trigger fail_item");
        store.StorePrimInventory(sog.RootPart.UUID, replacement);
        Assert.Equal(6L, Scalar(db, "select count(*) from primitems where name = 'replacement'"));
    }

    [Fact]
    public void ReplacingTerrainIsOneCommitAndAFailureKeepsTheOldTerrain()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        double[,] first = new double[256, 256];
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
                first[x, y] = 21.0;
        store.StoreTerrain(first, s_region);
        SQLiteConnection db = OpenConnection(conn);
        string before = Dump(db, "terrain", "RegionUUID");
        Assert.NotEqual("", before);

        double[,] second = new double[256, 256];
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
                second[x, y] = 30.0;
        using (CommitCounter commits = new CommitCounter(store))
        {
            store.StoreTerrain(second, s_region);
            Assert.Equal(1, commits.Count);
        }

        string stored = Dump(db, "terrain", "RegionUUID");
        Assert.NotEqual(before, stored);
        Exec(db, "create trigger fail_terrain before insert on terrain begin select raise(abort, 'injected failure'); end");
        Assert.ThrowsAny<Exception>(() => store.StoreTerrain(first, s_region));

        Assert.Equal(stored, Dump(db, "terrain", "RegionUUID"));
    }
}
