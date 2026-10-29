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
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// Runs only when SQLITE_STORE_BENCHMARK=1, so the default suite skips it.
/// </summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute(bool fullSize = false)
    {
        if (Environment.GetEnvironmentVariable("SQLITE_STORE_BENCHMARK") != "1")
            Skip = "Benchmark: set SQLITE_STORE_BENCHMARK=1 and SQLITE_STORE_BENCHMARK_DIR to run it.";
        else if (fullSize && Environment.GetEnvironmentVariable("SQLITE_STORE_BENCHMARK_FULL") != "1")
            Skip = "Full-size benchmark: also set SQLITE_STORE_BENCHMARK_FULL=1 (minutes of runtime on the unfixed store).";
    }
}

/// <summary>
/// Times how the SQLite region store saves a region's objects to a database file on disk, the way a region's
/// backup does: SceneObjectGroup.ProcessBackup calls StoreObject for the group and then StorePrimInventory for
/// each part whose inventory changed (SceneObjectPartInventory.ProcessInventoryBackup).
///
/// Environment:
///   SQLITE_STORE_BENCHMARK=1        required, otherwise every test here is skipped
///   SQLITE_STORE_BENCHMARK_DIR      folder for the database files and the result file (required)
///   SQLITE_STORE_BENCHMARK_LABEL    a name for the code under test, written to the result lines (default "run")
///   SQLITE_STORE_BENCHMARK_CONN_EXTRA  added to the connection string, e.g. "Journal Mode=Wal;Synchronous=Normal;"
///   SQLITE_STORE_BENCHMARK_FULL=1   also run the full-size cases; without it only the sample runs, and its result
///                                   lines extrapolate the sample's cost per linkset to the full sizes
///
/// It reports the time per call, the totals, and how many database commits each call made (counted with the
/// connection's commit hook). A file database is used because an in-memory database has no disk sync to pay for.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
[Trait("Category", "Benchmark")]
public class SQLiteRegionStoreBenchmark : OpenSimTestCase
{
    private readonly ITestOutputHelper m_output;
    private readonly List<SQLiteSimulationData> m_stores = new();

    static SQLiteRegionStoreBenchmark()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    public SQLiteRegionStoreBenchmark(ITestOutputHelper output)
    {
        m_output = output;
    }

    public override void Dispose()
    {
        foreach (SQLiteSimulationData store in m_stores)
        {
            try { store.Dispose(); } catch { /* best effort */ }
        }
        m_stores.Clear();
        base.Dispose();
    }

    private sealed class Population
    {
        public List<SceneObjectGroup> Groups = new();
        public int Prims;
        public List<(UUID PrimId, List<TaskInventoryItem> Items)> Inventories = new();
        public int Items;
    }

    /// <summary>Linksets of three or four parts that add up to <paramref name="prims"/>, with task inventory.</summary>
    private static Population Build(int linksets, int prims, int items)
    {
        Population pop = new();
        UUID owner = UUID.Random();
        int baseParts = prims / linksets;
        int withExtra = prims - baseParts * linksets;
        int tail = 1;
        int itemsLeft = items;
        for (int g = 0; g < linksets; g++)
        {
            int parts = baseParts + (g < withExtra ? 1 : 0);
            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(parts, owner, "Obj" + g + "_", tail);
            tail += parts;
            pop.Groups.Add(sog);
            pop.Prims += parts;
            foreach (SceneObjectPart part in sog.Parts)
            {
                if (itemsLeft <= 0)
                    break;
                List<TaskInventoryItem> list = new();
                list.Add(new TaskInventoryItem
                {
                    ItemID = UUID.Random(),
                    ParentPartID = part.UUID,
                    ParentID = part.UUID,
                    AssetID = UUID.Random(),
                    Name = "script " + itemsLeft,
                    Description = "",
                    OwnerID = owner,
                    CreatorID = owner,
                    InvType = (int)InventoryType.LSL,
                    Type = (int)AssetType.LSLText,
                    CreationDate = 1700000000,
                });
                itemsLeft--;
                pop.Items++;
                pop.Inventories.Add((part.UUID, list));
            }
        }
        return pop;
    }

    private static SQLiteConnection ConnectionOf(SQLiteSimulationData store)
    {
        FieldInfo f = typeof(SQLiteSimulationData).GetField("m_conn", BindingFlags.Instance | BindingFlags.NonPublic);
        return (SQLiteConnection)f.GetValue(store);
    }

    private sealed class Result
    {
        public double StoreObjectMs, StoreInventoryMs, TotalMs;
        public long StoreObjectCommits, StoreInventoryCommits, TotalCommits;
        public int Objects, Inventories;
    }

    /// <summary>One backup pass over the population, the order ProcessBackup uses.</summary>
    private static Result Pass(SQLiteSimulationData store, Population pop, UUID region, bool inBatch = false)
    {
        SQLiteConnection conn = ConnectionOf(store);
        long commits = 0;
        SQLiteCommitHandler hook = (_, _) => commits++;
        conn.Commit += hook;
        Result r = new() { Objects = pop.Groups.Count, Inventories = pop.Inventories.Count };
        Dictionary<UUID, List<TaskInventoryItem>> byPrim = new();
        foreach (var inv in pop.Inventories)
            byPrim[inv.PrimId] = inv.Items;
        Stopwatch total = Stopwatch.StartNew();
        try
        {
            if (inBatch)
                store.BeginBackupBatch();
            foreach (SceneObjectGroup group in pop.Groups)
            {
                long c0 = commits;
                Stopwatch sw = Stopwatch.StartNew();
                store.StoreObject(group, region);
                r.StoreObjectMs += sw.Elapsed.TotalMilliseconds;
                r.StoreObjectCommits += commits - c0;

                foreach (SceneObjectPart part in group.Parts)
                {
                    if (!byPrim.TryGetValue(part.UUID, out List<TaskInventoryItem> items))
                        continue;
                    c0 = commits;
                    sw.Restart();
                    store.StorePrimInventory(part.UUID, items);
                    r.StoreInventoryMs += sw.Elapsed.TotalMilliseconds;
                    r.StoreInventoryCommits += commits - c0;
                }
            }
            if (inBatch)
                store.EndBackupBatch();
        }
        finally
        {
            conn.Commit -= hook;
        }
        r.TotalMs = total.Elapsed.TotalMilliseconds;
        r.TotalCommits = commits;
        return r;
    }

    private void Report(string caseName, string pass, Result r, Population pop, bool extrapolate = false)
    {
        string label = Environment.GetEnvironmentVariable("SQLITE_STORE_BENCHMARK_LABEL") ?? "run";
        StringBuilder sb = new();
        sb.Append($"[{label}] {caseName} {pass}: {pop.Groups.Count} linksets, {pop.Prims} prims, {pop.Items} items; ");
        sb.Append($"total {r.TotalMs:F0} ms ({r.TotalCommits} commits); ");
        sb.Append($"StoreObject {r.StoreObjectMs:F0} ms ({r.StoreObjectCommits} commits, {r.StoreObjectMs / r.Objects:F2} ms per object); ");
        sb.Append($"StorePrimInventory {r.StoreInventoryMs:F0} ms ({r.StoreInventoryCommits} commits, {(r.Inventories == 0 ? 0 : r.StoreInventoryMs / r.Inventories):F2} ms per call)");
        if (extrapolate)
        {
            // Linear in the object count: the cost per linkset (its prims and its inventory) times the full size.
            double perLinkset = r.TotalMs / r.Objects;
            sb.Append($"; per linkset {perLinkset:F1} ms, extrapolated: 287 linksets {perLinkset * 287 / 1000:F0} s, 1200 linksets {perLinkset * 1200 / 1000:F0} s");
        }
        string line = sb.ToString();
        m_output.WriteLine(line);
        File.AppendAllText(Path.Combine(Dir(), "benchmark-results.txt"), line + Environment.NewLine);
    }

    private static string Dir()
    {
        string dir = Environment.GetEnvironmentVariable("SQLITE_STORE_BENCHMARK_DIR");
        Assert.False(string.IsNullOrEmpty(dir), "SQLITE_STORE_BENCHMARK_DIR is not set");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private SQLiteSimulationData NewFileStore(string name)
    {
        string path = Path.Combine(Dir(), name + "-" + Guid.NewGuid().ToString("N") + ".db");
        // For example "Journal Mode=Wal;Synchronous=Normal;", to time those connection string options.
        string extra = Environment.GetEnvironmentVariable("SQLITE_STORE_BENCHMARK_CONN_EXTRA") ?? "";
        SQLiteSimulationData store = new SQLiteSimulationData("Data Source=" + path.Replace('\\', '/') + ";" + extra);
        m_stores.Add(store);
        return store;
    }

    private void RunCase(string caseName, int linksets, int prims, int items, bool extrapolate = false)
    {
        Population pop = Build(linksets, prims, items);
        UUID region = UUID.Random();

        // First save of every object: all rows are new.
        SQLiteSimulationData store = NewFileStore(caseName);
        Report(caseName, "first save", Pass(store, pop, region), pop, extrapolate);

        // The same objects saved again, nothing changed in them: shows whether unchanged objects cost anything.
        Report(caseName, "save again, nothing changed", Pass(store, pop, region), pop, extrapolate);

        // The same first save as a backup pass: all of it between BeginBackupBatch and EndBackupBatch.
        SQLiteSimulationData batchStore = NewFileStore(caseName + "-batch");
        Report(caseName, "first save in one backup batch", Pass(batchStore, pop, region, inBatch: true), pop, extrapolate);

        // Experiment, not store behaviour: the whole first save inside one outer transaction on the store's
        // connection. The gap to the first save is the cost of committing statement by statement.
        SQLiteSimulationData outerStore = NewFileStore(caseName + "-outer");
        SQLiteConnection conn = ConnectionOf(outerStore);
        using (SQLiteTransaction tx = conn.BeginTransaction())
        {
            Result outer = Pass(outerStore, pop, region);
            Stopwatch sw = Stopwatch.StartNew();
            tx.Commit();
            outer.TotalMs += sw.Elapsed.TotalMilliseconds;
            Report(caseName, "first save inside one outer transaction", outer, pop, extrapolate);
        }
    }

    /// <summary>A sample of the small case, with the same shape (about 3.3 prims per linkset, one item per 5 prims).</summary>
    [BenchmarkFact]
    public void Sample_60Linksets_200Prims_37Items()
    {
        RunCase("sample", 60, 200, 37, extrapolate: true);
    }

    /// <summary>A region of 287 linksets and 958 prims, with task inventory.</summary>
    [BenchmarkFact(fullSize: true)]
    public void SmallRegion_287Linksets_958Prims_178Items()
    {
        RunCase("small", 287, 958, 178);
    }

    [BenchmarkFact(fullSize: true)]
    public void LargeRegion_1200Linksets_4000Prims_740Items()
    {
        RunCase("large", 1200, 4000, 740);
    }
}
