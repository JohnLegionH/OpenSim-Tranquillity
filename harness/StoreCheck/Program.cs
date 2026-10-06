/*
 * Copyright (c) Legion Builds
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

// Store checks: run the region, estate and inventory stores of one OpenSim-Tranquillity tree against a
// database server (or SQLite) and fail on anything that went wrong, including a migration step that failed.
// Migration.Update logs a failing step at Debug and carries on, so a clean start proves nothing: every log
// line the stores write is captured, and a "[MIGRATIONS]: Cmd was" line or any Error line is a failure.
//
// Modes (one process per mode; the upgrade check runs "baseline" with the baseline tree's build and then
// "upgrade" with the target tree's build against the same database):
//   fresh    : empty database; every store migrates from its first step; checks version, newest step,
//              expected columns; then the round trips named by --checks.
//   baseline : empty database; the stores create the schema and write a fixed data set; writes a snapshot
//              (raw rows and what the stores load back) to --snap.
//   upgrade  : opens the baseline's database with this tree's stores; same migration checks; the baseline's
//              rows must be unchanged and must load exactly as the baseline loaded them; then the round trips.

using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;

namespace StoreCheck;

public static class Program
{
    public static int Main(string[] args)
    {
        var opts = Options.Parse(args);
        LoggerProvider.LoggerFactory = Capture.Factory;
        var report = new Report(opts);
        try
        {
            var ctx = new Ctx(opts);
            ctx.PrepareDatabase(report);
            switch (opts.Mode)
            {
                case "fresh": Modes.Fresh(ctx, report); break;
                case "baseline": Modes.Baseline(ctx, report); break;
                case "upgrade": Modes.Upgrade(ctx, report); break;
                default: throw new ArgumentException("unknown --mode " + opts.Mode);
            }
        }
        catch (Exception e)
        {
            report.Fail("harness", "unhandled exception: " + e);
        }
        return report.Finish();
    }
}

// ---------------------------------------------------------------------------------------------------------
// Options, report, log capture
// ---------------------------------------------------------------------------------------------------------

public sealed class Options
{
    public string Mode, Db, Admin, Database, SqliteDir, Snap, Label;
    public List<KnownFault> Known = new();
    public HashSet<string> Checks = new(StringComparer.OrdinalIgnoreCase);
    public List<(string store, string table, string column)> ExpectColumns = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string v = i + 1 < args.Length ? args[i + 1] : "";
            switch (a)
            {
                case "--mode": o.Mode = v; i++; break;
                case "--db": o.Db = v; i++; break;
                case "--admin": o.Admin = v; i++; break;
                case "--database": o.Database = v; i++; break;
                case "--sqlite-dir": o.SqliteDir = v; i++; break;
                case "--snap": o.Snap = v; i++; break;
                case "--label": o.Label = v; i++; break;
                case "--known":
                    // targets.json: "known_develop_faults": [{ "db": "pgsql", "contains": "...", "note": "..." }]
                    using (var doc = JsonDocument.Parse(File.ReadAllText(v)))
                    {
                        if (doc.RootElement.TryGetProperty("known_develop_faults", out var arr))
                            foreach (var k in arr.EnumerateArray())
                                o.Known.Add(new KnownFault(k.GetProperty("db").GetString(), k.GetProperty("contains").GetString(), k.GetProperty("note").GetString()));
                    }
                    i++; break;
                case "--checks":
                    foreach (var c in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        o.Checks.Add(c);
                    i++; break;
                case "--expect-column":
                    // Store:table.column
                    var m = Regex.Match(v, @"^(\w+):(\w+)\.(\w+)$");
                    if (!m.Success) throw new ArgumentException("bad --expect-column " + v);
                    o.ExpectColumns.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
                    i++; break;
                default: throw new ArgumentException("unknown argument " + a);
            }
        }
        return o;
    }
}

/// <summary>
/// A fault already present on develop (recorded as a finding). A failure whose text contains Contains, on a
/// database of kind Db ("*" for any), is reported as KNOWN instead of failing the target.
/// </summary>
public sealed record KnownFault(string Db, string Contains, string Note);

public sealed class Report
{
    private readonly Options m_opts;
    private readonly List<string> m_failures = new();
    private readonly List<string> m_passes = new();
    private readonly List<string> m_known = new();

    public Report(Options opts) { m_opts = opts; }

    public void Section(string title) => Console.WriteLine($"\n===== {title} =====");
    public void Info(string s) => Console.WriteLine("  " + s);
    public void Pass(string check, string what) { m_passes.Add($"{check}: {what}"); Console.WriteLine($"  PASS {check}: {what}"); }
    public void Fail(string check, string what)
    {
        KnownFault k = KnownFor(what);
        if (k is not null)
        {
            m_known.Add($"{check}: {k.Note} | {what.Split('\n')[0]}");
            Console.WriteLine($"  KNOWN {check}: {what}\n      (known develop fault: {k.Note})");
            return;
        }
        m_failures.Add($"{check}: {what}");
        Console.WriteLine($"  FAIL {check}: {what}");
    }

    private KnownFault KnownFor(string what) =>
        m_opts.Known.FirstOrDefault(k => (k.Db == "*" || k.Db == m_opts.Db) && what.Contains(k.Contains, StringComparison.Ordinal));

    /// <summary>Fails the check if the stores logged a failed migration command or any Error since the mark.</summary>
    public bool CheckLog(string check, int mark)
    {
        var lines = Capture.Since(mark);
        var bad = lines.Where(l => l.Level >= LogLevel.Error || l.Message.Contains("[MIGRATIONS]: Cmd was")).ToList();
        // A known develop fault is reported line by line, so it never hides another line logged with it.
        foreach (var l in bad.Where(l => KnownFor(l.ToString()) is not null).ToList())
        {
            Fail(check, "log line: " + l);
            bad.Remove(l);
        }
        if (bad.Count == 0)
            return true;
        var sb = new StringBuilder();
        sb.Append($"{bad.Count} failing log line(s):");
        foreach (var l in bad)
            sb.Append("\n      ").Append(l.ToString());
        Fail(check, sb.ToString());
        return false;
    }

    public int Finish()
    {
        string head = $"[{m_opts.Label}] mode={m_opts.Mode} db={m_opts.Db}";
        Console.WriteLine();
        Console.WriteLine($"===== RESULT {head}: {(m_failures.Count == 0 ? "PASS" : "FAIL")} ({m_passes.Count} passed, {m_failures.Count} failed, {m_known.Count} known develop faults) =====");
        foreach (var f in m_failures)
            Console.WriteLine("  FAIL " + f);
        foreach (var f in m_known)
            Console.WriteLine("  KNOWN " + f);
        string summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"### {head}: {(m_failures.Count == 0 ? "PASS" : "FAIL")}");
            sb.AppendLine($"{m_passes.Count} passed, {m_failures.Count} failed, {m_known.Count} known develop faults");
            foreach (var f in m_failures)
                sb.AppendLine("- FAIL " + f.Split('\n')[0]);
            foreach (var f in m_known)
                sb.AppendLine("- KNOWN " + f.Split('\n')[0]);
            File.AppendAllText(summary, sb.ToString());
        }
        return m_failures.Count == 0 ? 0 : 1;
    }
}

public sealed record LogLine(DateTime Time, LogLevel Level, string Category, string Message, Exception Error)
{
    public override string ToString() =>
        $"[{Level}] {Category}: {Message}" + (Error is null ? "" : " | " + Error.GetType().Name + ": " + Error.Message);
}

public static class Capture
{
    private static readonly List<LogLine> s_lines = new();
    public static readonly ILoggerFactory Factory = new CaptureFactory();

    public static int Mark { get { lock (s_lines) return s_lines.Count; } }

    public static List<LogLine> Since(int mark) { lock (s_lines) return s_lines.Skip(mark).ToList(); }

    internal static void Add(LogLine l)
    {
        lock (s_lines) s_lines.Add(l);
        // Echo migration lines and anything above Information: these are the store's own words.
        if (l.Level >= LogLevel.Warning || l.Message.StartsWith("[MIGRATIONS]"))
            Console.WriteLine("    log " + l);
    }

    private sealed class CaptureFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName);
        public void Dispose() { }
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly string m_category;
        public CaptureLogger(string category) { m_category = category; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => Add(new LogLine(DateTime.UtcNow, logLevel, m_category, formatter(state, exception), exception));
    }
}

// ---------------------------------------------------------------------------------------------------------
// Database context: connections, store construction, schema reads, raw dumps
// ---------------------------------------------------------------------------------------------------------

public enum StoreKind { Region, Estate, Inventory }

public sealed class Ctx
{
    public readonly Options Opts;
    public string Db => Opts.Db;

    public Ctx(Options opts) { Opts = opts; }

    public void PrepareDatabase(Report report)
    {
        if (Db == "sqlite")
        {
            Directory.CreateDirectory(Opts.SqliteDir);
            return;
        }
        if (Opts.Mode == "upgrade")
            return; // the baseline created it
        using DbConnection c = OpenAdmin();
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = Db == "mysql" ? $"CREATE DATABASE `{Opts.Database}`" : $"CREATE DATABASE \"{Opts.Database}\"";
        cmd.ExecuteNonQuery();
        report.Info($"created empty database {Opts.Database}");
    }

    private DbConnection OpenAdmin()
    {
        DbConnection c = Db switch
        {
            "mysql" => new MySqlConnection(Opts.Admin),
            "pgsql" => new NpgsqlConnection(Opts.Admin + "Database=postgres;"),
            _ => throw new InvalidOperationException()
        };
        c.Open();
        return c;
    }

    /// <summary>The connection string a store is given. SQLite: one file per store.</summary>
    public string ConnFor(StoreKind kind) => Db switch
    {
        "mysql" => Opts.Admin + $"Database={Opts.Database};",
        "pgsql" => Opts.Admin + $"Database={Opts.Database};",
        "sqlite" => $"Data Source={Path.Combine(Opts.SqliteDir, kind.ToString().ToLowerInvariant() + ".db")};",
        _ => throw new InvalidOperationException()
    };

    public DbConnection OpenRaw(StoreKind kind)
    {
        DbConnection c = Db switch
        {
            "mysql" => new MySqlConnection(ConnFor(kind)),
            "pgsql" => new NpgsqlConnection(ConnFor(kind)),
            "sqlite" => new System.Data.SQLite.SQLiteConnection(ConnFor(kind)),
            _ => throw new InvalidOperationException()
        };
        c.Open();
        return c;
    }

    public static readonly StoreKind[] Kinds = { StoreKind.Region, StoreKind.Estate, StoreKind.Inventory };

    public string MigrationName(StoreKind kind) => kind switch
    {
        StoreKind.Region => "RegionStore",
        StoreKind.Estate => "EstateStore",
        StoreKind.Inventory => Db == "sqlite" ? "XInventoryStore" : "InventoryStore",
        _ => throw new InvalidOperationException()
    };

    public Assembly StoreAssembly => Db switch
    {
        "mysql" => typeof(MySQLSimulationData).Assembly,
        "pgsql" => typeof(PGSQLSimulationData).Assembly,
        "sqlite" => typeof(SQLiteSimulationData).Assembly,
        _ => throw new InvalidOperationException()
    };

    public ISimulationDataStore NewRegionStore()
    {
        ISimulationDataStore s = Db switch
        {
            "mysql" => new MySQLSimulationData(),
            "pgsql" => new PGSQLSimulationData(),
            "sqlite" => new SQLiteSimulationData(),
            _ => throw new InvalidOperationException()
        };
        s.Initialise(ConnFor(StoreKind.Region));
        return s;
    }

    public IEstateDataStore NewEstateStore()
    {
        IEstateDataStore s = Db switch
        {
            "mysql" => new MySQLEstateStore(),
            "pgsql" => new PGSQLEstateStore(),
            "sqlite" => new SQLiteEstateStore(),
            _ => throw new InvalidOperationException()
        };
        s.Initialise(ConnFor(StoreKind.Estate));
        return s;
    }

    public IXInventoryData NewInventoryStore() => Db switch
    {
        "mysql" => new MySQLXInventoryData(ConnFor(StoreKind.Inventory), ""),
        "pgsql" => new PGSQLXInventoryData(ConnFor(StoreKind.Inventory), ""),
        "sqlite" => new SQLiteXInventoryData(ConnFor(StoreKind.Inventory), ""),
        _ => throw new InvalidOperationException()
    };

    public object NewStore(StoreKind kind) => kind switch
    {
        StoreKind.Region => NewRegionStore(),
        StoreKind.Estate => NewEstateStore(),
        StoreKind.Inventory => NewInventoryStore(),
        _ => throw new InvalidOperationException()
    };

    public static void Close(object store)
    {
        try { (store as IDisposable)?.Dispose(); } catch { }
    }

    // ----- schema -----

    public List<string> Tables(DbConnection c)
    {
        string sql = Db switch
        {
            "mysql" => "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'",
            "pgsql" => "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'",
            _ => "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'"
        };
        var list = new List<string>();
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(Convert.ToString(r.GetValue(0)));
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public sealed record Column(string Name, bool Nullable, string Default, string Type);

    public List<Column> Columns(DbConnection c, string table)
    {
        var list = new List<Column>();
        using DbCommand cmd = c.CreateCommand();
        if (Db == "sqlite")
        {
            cmd.CommandText = "PRAGMA table_info(\"" + table + "\")";
            using DbDataReader r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new Column(r.GetString(1), Convert.ToInt32(r.GetValue(3)) == 0,
                    r.IsDBNull(4) ? null : Convert.ToString(r.GetValue(4)), Convert.ToString(r.GetValue(2))));
            return list;
        }
        string schema = Db == "mysql" ? "DATABASE()" : "current_schema()";
        cmd.CommandText = "SELECT column_name, is_nullable, column_default, data_type FROM information_schema.columns " +
            $"WHERE table_schema = {schema} AND lower(table_name) = lower(@t) ORDER BY ordinal_position";
        var p = cmd.CreateParameter();
        p.ParameterName = "@t";
        p.Value = table;
        cmd.Parameters.Add(p);
        using (DbDataReader r = cmd.ExecuteReader())
        {
            while (r.Read())
                list.Add(new Column(Convert.ToString(r.GetValue(0)), Convert.ToString(r.GetValue(1)) == "YES",
                    r.IsDBNull(2) ? null : Convert.ToString(r.GetValue(2)), Convert.ToString(r.GetValue(3))));
        }
        return list;
    }

    public int RecordedVersion(DbConnection c, string name)
    {
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = "select version from migrations where name='" + name + "' order by version desc";
        object v = cmd.ExecuteScalar();
        return v is null || v is DBNull ? 0 : Convert.ToInt32(v);
    }

    /// <summary>Every row of every table, values as invariant strings, rows sorted, keyed by table.</summary>
    public Dictionary<string, Dump> DumpAll()
    {
        var all = new Dictionary<string, Dump>(StringComparer.OrdinalIgnoreCase);
        foreach (StoreKind kind in Db == "sqlite" ? Kinds : new[] { StoreKind.Region })
        {
            using DbConnection c = OpenRaw(kind);
            foreach (string t in Tables(c))
            {
                string key = Db == "sqlite" ? kind + "/" + t : t;
                all[key] = DumpTable(c, t);
            }
        }
        return all;
    }

    private Dump DumpTable(DbConnection c, string table)
    {
        var d = new Dump();
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = Db == "mysql" ? $"SELECT * FROM `{table}`" : $"SELECT * FROM \"{table}\"";
        using DbDataReader r = cmd.ExecuteReader();
        for (int i = 0; i < r.FieldCount; i++)
            d.Columns.Add(r.GetName(i));
        while (r.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = Val(r.GetValue(i));
            d.Rows.Add(row);
        }
        return d;
    }

    public static string Val(object v) => v switch
    {
        null => "NULL",
        DBNull => "NULL",
        byte[] b => "0x" + Convert.ToHexString(b),
        double x => x.ToString("R", CultureInfo.InvariantCulture),
        float x => x.ToString("R", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };
}

public sealed class Dump
{
    public List<string> Columns { get; set; } = new();
    public List<Dictionary<string, string>> Rows { get; set; } = new();
}

// ---------------------------------------------------------------------------------------------------------
// Migration checks
// ---------------------------------------------------------------------------------------------------------

public static class MigrationText
{
    /// <summary>The store's embedded migration steps: version -> script text.</summary>
    public static SortedList<int, string> Steps(Assembly asm, string name)
    {
        string res = asm.GetManifestResourceNames().Where(n => Regex.IsMatch(n, @"\." + Regex.Escape(name) + @"\.migrations$")).LastOrDefault();
        var steps = new SortedList<int, string>();
        if (res is null)
            return steps;
        using var s = new StreamReader(asm.GetManifestResourceStream(res));
        int ver = -1;
        var sb = new StringBuilder();
        foreach (string line in s.ReadToEnd().Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*:VERSION\s+(\d+)");
            if (m.Success)
            {
                if (ver > 0) steps[ver] = sb.ToString();
                ver = int.Parse(m.Groups[1].Value);
                sb.Clear();
                continue;
            }
            sb.AppendLine(line);
        }
        if (ver > 0) steps[ver] = sb.ToString();
        return steps;
    }

    private static string Unquote(string s) => s.Trim().Trim('`', '"', '[', ']');

    private static string LastPart(string s)
    {
        var parts = s.Split('.');
        return Unquote(parts[^1]);
    }

    /// <summary>Tables created and (table, column) pairs added by a step's text.</summary>
    public static (List<string> tables, List<(string table, string column)> columns) Defines(string script)
    {
        var tables = new List<string>();
        var cols = new List<(string, string)>();
        string text = Regex.Replace(script, @"--[^\n]*", "");
        foreach (Match m in Regex.Matches(text, @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([`""\[\]\w\.]+)", RegexOptions.IgnoreCase))
            tables.Add(LastPart(m.Groups[1].Value));
        foreach (string stmt in text.Split(';'))
        {
            var alter = Regex.Match(stmt, @"ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?([`""\[\]\w\.]+)(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!alter.Success) continue;
            string table = LastPart(alter.Groups[1].Value);
            foreach (Match a in Regex.Matches(alter.Groups[2].Value, @"\bADD\s+(?:COLUMN\s+)?(?:IF\s+NOT\s+EXISTS\s+)?([`""\[\]\w]+)", RegexOptions.IgnoreCase))
            {
                string col = Unquote(a.Groups[1].Value);
                if (Regex.IsMatch(col, @"^(CONSTRAINT|INDEX|KEY|UNIQUE|PRIMARY|FOREIGN|FULLTEXT|SPATIAL|CHECK)$", RegexOptions.IgnoreCase))
                    continue;
                cols.Add((table, col));
            }
        }
        return (tables, cols);
    }

    /// <summary>
    /// After a store opened the database: the recorded version is the newest step, the newest step's tables and
    /// columns exist, and every expected column exists and is nullable.
    /// </summary>
    public static void CheckSchema(Ctx ctx, Report report, string check)
    {
        foreach (StoreKind kind in Ctx.Kinds)
        {
            string name = ctx.MigrationName(kind);
            var steps = Steps(ctx.StoreAssembly, name);
            if (steps.Count == 0)
            {
                report.Fail(check, $"{name}: no embedded migration text found");
                continue;
            }
            int newest = steps.Keys[^1];
            using DbConnection c = ctx.OpenRaw(kind);
            int recorded = ctx.RecordedVersion(c, name);
            if (recorded == newest)
                report.Pass(check, $"{name}: recorded version {recorded} = newest step {newest} ({steps.Count} steps in the text)");
            else
                report.Fail(check, $"{name}: recorded version {recorded}, newest step {newest}");

            var tables = ctx.Tables(c);
            var (newTables, newCols) = Defines(steps[newest]);
            foreach (string t in newTables)
            {
                if (tables.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                    report.Pass(check, $"{name} step {newest}: table {t} exists");
                else
                    report.Fail(check, $"{name} step {newest}: table {t} missing");
            }
            foreach (var (t, col) in newCols)
            {
                var cols = ctx.Columns(c, t);
                if (cols.Any(x => string.Equals(x.Name, col, StringComparison.OrdinalIgnoreCase)))
                    report.Pass(check, $"{name} step {newest}: column {t}.{col} exists");
                else
                    report.Fail(check, $"{name} step {newest}: column {t}.{col} missing (table has {cols.Count} columns)");
            }
            if (newTables.Count == 0 && newCols.Count == 0)
                report.Info($"{name} step {newest}: no CREATE TABLE or ADD COLUMN to check in its text");

            foreach (var e in ctx.Opts.ExpectColumns.Where(e => e.store == name))
            {
                var col = ctx.Columns(c, e.table).FirstOrDefault(x => string.Equals(x.Name, e.column, StringComparison.OrdinalIgnoreCase));
                if (col is null)
                    report.Fail(check, $"expected column {e.table}.{e.column} missing");
                else if (!col.Nullable)
                    report.Fail(check, $"expected column {e.table}.{e.column} is NOT NULL (type {col.Type}, default {col.Default ?? "none"})");
                else
                    report.Pass(check, $"expected column {e.table}.{e.column}: type {col.Type}, nullable, default {col.Default ?? "none"}");
            }
        }
    }

    /// <summary>Opens every store once (running its migrations) and fails on any failed migration command.</summary>
    public static void OpenAll(Ctx ctx, Report report, string check)
    {
        foreach (StoreKind kind in Ctx.Kinds)
        {
            int mark = Capture.Mark;
            object store = null;
            try
            {
                store = ctx.NewStore(kind);
                if (report.CheckLog(check, mark))
                    report.Pass(check, $"{ctx.MigrationName(kind)}: opened, no failed migration command and no error logged");
            }
            catch (Exception e)
            {
                report.CheckLog(check, mark);
                report.Fail(check, $"{ctx.MigrationName(kind)}: opening the store threw {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (store is not null) Ctx.Close(store);
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------------------
// Modes
// ---------------------------------------------------------------------------------------------------------

public static class Modes
{
    public static void Fresh(Ctx ctx, Report report)
    {
        report.Section("fresh: migrate an empty database");
        MigrationText.OpenAll(ctx, report, "fresh");
        MigrationText.CheckSchema(ctx, report, "fresh");
        RoundTrips.Run(ctx, report, "fresh");
    }

    public static void Baseline(Ctx ctx, Report report)
    {
        report.Section("baseline: create the schema and write the data set");
        MigrationText.OpenAll(ctx, report, "baseline");
        BaselineData.Write(ctx, report);
        int mark = Capture.Mark;
        var snap = new Snapshot { Raw = ctx.DumpAll(), Loaded = BaselineData.Load(ctx) };
        report.CheckLog("baseline load", mark);
        File.WriteAllText(ctx.Opts.Snap, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        report.Info($"snapshot: {snap.Raw.Count} tables, {snap.Raw.Values.Sum(d => d.Rows.Count)} rows, {snap.Loaded.Count} loaded values -> {ctx.Opts.Snap}");
        foreach (var kv in snap.Raw.Where(kv => kv.Value.Rows.Count > 0))
            report.Info($"  {kv.Key}: {kv.Value.Rows.Count} rows");
    }

    public static void Upgrade(Ctx ctx, Report report)
    {
        var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(ctx.Opts.Snap));

        report.Section("upgrade: open the baseline's database with this tree's stores");
        var before = ctx.DumpAll();
        CompareRaw(report, "upgrade raw (before opening)", snap.Raw, before, strictColumns: true);

        MigrationText.OpenAll(ctx, report, "upgrade");
        MigrationText.CheckSchema(ctx, report, "upgrade");

        report.Section("upgrade: the baseline's rows are unchanged");
        var after = ctx.DumpAll();
        CompareRaw(report, "upgrade rows", snap.Raw, after, strictColumns: false);

        report.Section("upgrade: the baseline's rows load exactly as the baseline loaded them");
        int mark = Capture.Mark;
        var loaded = BaselineData.Load(ctx);
        report.CheckLog("upgrade load", mark);
        int same = 0;
        foreach (var kv in snap.Loaded)
        {
            if (!loaded.TryGetValue(kv.Key, out string now))
                report.Fail("upgrade load", $"{kv.Key}: missing after the upgrade");
            else if (now != kv.Value)
                report.Fail("upgrade load", $"{kv.Key}: differs\n      baseline: {Clip(kv.Value, now)}\n      upgraded: {Clip(now, kv.Value)}");
            else
                same++;
        }
        foreach (var k in loaded.Keys.Except(snap.Loaded.Keys))
            report.Fail("upgrade load", $"{k}: present after the upgrade, not in the baseline");
        report.Pass("upgrade load", $"{same} of {snap.Loaded.Count} loaded values equal to the baseline's");

        RoundTrips.Run(ctx, report, "upgrade");
    }

    private static string Clip(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int start = Math.Max(0, i - 80);
        return (start > 0 ? "..." : "") + a.Substring(start, Math.Min(a.Length - start, 240));
    }

    private static string RowKey(Dictionary<string, string> row, IEnumerable<string> cols) =>
        string.Join("|", cols.Select(c => c + "=" + (row.TryGetValue(c, out var v) ? v : "<absent>")));

    /// <summary>
    /// Rows of every table the baseline had must be the same on the columns the baseline had. New columns are
    /// reported with the values the old rows now hold. The migrations table is reported, not compared.
    /// </summary>
    private static void CompareRaw(Report report, string check, Dictionary<string, Dump> was, Dictionary<string, Dump> now, bool strictColumns)
    {
        int tablesSame = 0, rows = 0;
        foreach (var kv in was)
        {
            string t = kv.Key;
            if (t.EndsWith("migrations", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!now.TryGetValue(t, out Dump n))
            {
                report.Fail(check, $"table {t} is gone");
                continue;
            }
            var common = kv.Value.Columns.Where(c => n.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            var lost = kv.Value.Columns.Except(common).ToList();
            var added = n.Columns.Where(c => !kv.Value.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (lost.Count > 0)
                report.Fail(check, $"table {t}: columns gone: {string.Join(", ", lost)}");
            if (added.Count > 0)
            {
                if (strictColumns)
                    report.Fail(check, $"table {t}: columns appeared: {string.Join(", ", added)}");
                else
                    foreach (string a in added)
                    {
                        var values = n.Rows.GroupBy(r => r.TryGetValue(a, out var v) ? v : "<absent>").Select(g => $"{g.Key} x{g.Count()}");
                        report.Info($"table {t}: new column {a}; values in the {n.Rows.Count} existing rows: {string.Join(", ", values)}");
                    }
            }
            var wasKeys = kv.Value.Rows.Select(r => RowKey(r, common)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var nowKeys = n.Rows.Select(r => RowKey(r, common)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (wasKeys.SequenceEqual(nowKeys))
            {
                tablesSame++;
                rows += wasKeys.Count;
            }
            else
            {
                var gone = wasKeys.Except(nowKeys).Take(3).ToList();
                var appeared = nowKeys.Except(wasKeys).Take(3).ToList();
                report.Fail(check, $"table {t}: rows differ ({wasKeys.Count} before, {nowKeys.Count} after)" +
                    string.Concat(gone.Select(g => "\n      before: " + g)) + string.Concat(appeared.Select(g => "\n      after:  " + g)));
            }
        }
        foreach (var t in now.Keys.Except(was.Keys, StringComparer.OrdinalIgnoreCase))
            report.Info($"table {t}: new table ({now[t].Rows.Count} rows)");
        report.Pass(check, $"{tablesSame} tables, {rows} rows unchanged on the baseline's columns");
    }
}

public sealed class Snapshot
{
    public Dictionary<string, Dump> Raw { get; set; } = new();
    public Dictionary<string, string> Loaded { get; set; } = new();
}

// ---------------------------------------------------------------------------------------------------------
// The baseline data set (fixed ids; written by the baseline tree, read by both)
// ---------------------------------------------------------------------------------------------------------

public static class BaselineData
{
    public static readonly UUID Region = new UUID("6a3c1f6e-0b8e-4d1f-9a54-3c1e2b7d0001");
    public static readonly UUID Owner = new UUID("6a3c1f6e-0b8e-4d1f-9a54-3c1e2b7d0002");
    public static readonly UUID Agent = new UUID("6a3c1f6e-0b8e-4d1f-9a54-3c1e2b7d0003");

    private static UUID Id(int n) => new UUID($"6a3c1f6e-0b8e-4d1f-9a54-3c1e2b7d{n:x4}");

    /// <summary>Objects covering the sit target cases the stores saw before the state was stored.</summary>
    private static IEnumerable<(string name, SceneObjectGroup sog)> Objects()
    {
        SceneObjectGroup Make(int n, string name)
        {
            var part = new SceneObjectPart(Owner, PrimitiveBaseShape.Default, new Vector3(10 + n, 20, 30), Quaternion.Identity, Vector3.Zero)
            { Name = name, UUID = Id(0x100 + n), Scale = new Vector3(1, 1, 1), Description = "desc " + name, Text = "text " + name };
            return new SceneObjectGroup(part);
        }

        var none = Make(1, "no target");
        yield return ("no target", none);

        var offset = Make(2, "offset target");
        offset.RootPart.SitTargetPosition = new Vector3(0, 0, 0.6f);
        offset.RootPart.SitTargetOrientation = Quaternion.CreateFromEulers(0, 0, 1.0f);
        yield return ("offset target", offset);

        var nudged = Make(3, "nudged zero target");
        nudged.RootPart.SitTargetPosition = new Vector3(0, 0, 1e-5f);
        nudged.RootPart.SitTargetOrientation = Quaternion.Identity;
        yield return ("nudged zero target", nudged);

        var activeZero = Make(4, "active zero target");
        activeZero.RootPart.SetSitTarget(true, Vector3.Zero, Quaternion.Identity);
        yield return ("active zero target", activeZero);

        var inactive = Make(5, "inactive offset target");
        inactive.RootPart.SetSitTarget(false, new Vector3(0.5f, 0, 0.25f), Quaternion.Identity);
        yield return ("inactive offset target", inactive);

        var linkset = Make(6, "linkset root");
        var child = new SceneObjectPart(Owner, PrimitiveBaseShape.Default, linkset.AbsolutePosition, Quaternion.Identity, new Vector3(0, 0, 1))
        { Name = "linkset child", UUID = Id(0x107), Scale = new Vector3(0.5f, 0.5f, 0.5f), Description = "child" };
        linkset.AddPart(child);
        yield return ("linkset", linkset);
    }

    private static TaskInventoryItem Item(UUID part) => new TaskInventoryItem
    {
        ItemID = Id(0x200), AssetID = Id(0x201), Name = "notecard", Description = "a notecard",
        Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard, OwnerID = Owner, CreatorID = Owner,
        ParentID = part, ParentPartID = part, CreationDate = 1700000000
    };

    private static void Part(Report report, string check, Action a)
    {
        int mark = Capture.Mark;
        try
        {
            a();
            report.Pass(check, "written");
        }
        catch (Exception e)
        {
            report.Fail(check, "threw " + e);
        }
        report.CheckLog(check, mark);
    }

    public static void Write(Ctx ctx, Report report)
    {
        Part(report, "baseline write region", () => WriteRegion(ctx));
        Part(report, "baseline write estate", () => WriteEstate(ctx));
        Part(report, "baseline write inventory", () => WriteInventory(ctx));
    }

    private static void WriteRegion(Ctx ctx)
    {
        var rs = ctx.NewRegionStore();
        foreach (var (_, sog) in Objects())
        {
            rs.StoreObject(sog, Region);
            if (sog.RootPart.Name == "linkset root")
                rs.StorePrimInventory(sog.RootPart.UUID, new[] { Item(sog.RootPart.UUID) });
        }
        var settings = rs.LoadRegionSettings(Region);
        settings.AgentLimit = 33;
        settings.WaterHeight = 21.5;
        settings.Covenant = Id(0x300);
        rs.StoreRegionSettings(settings);
        var terrain = new TerrainData(256, 256, 256);
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
                terrain[x, y] = 20f + (x * 7 + y * 3) % 50 / 10f;
        rs.StoreTerrain(terrain, Region);
        // The MySQL store writes terrain on a pool thread (StoreTerrain uses Util.FireAndForget): wait for it.
        if (RoundTrips.WaitForTerrain(rs, Region) is null)
            throw new Exception("terrain not readable 30 s after StoreTerrain");
        Ctx.Close(rs);
    }

    private static void WriteEstate(Ctx ctx)
    {
        var es = ctx.NewEstateStore();
        var estate = es.LoadEstateSettings(Region, true);
        estate.EstateName = "Example Estate";
        estate.EstateOwner = Owner;
        estate.AbuseEmail = "abuse@example.org";
        estate.AddEstateManager(Id(0x401));
        estate.AddEstateUser(Id(0x402));
        estate.AddEstateGroup(Id(0x403));
        estate.AddBan(new EstateBan { EstateID = estate.EstateID, BannedUserID = Id(0x404), BanningUserID = Owner, BanTime = 1700000000 });
        es.StoreEstateSettings(estate);
        es.LinkRegion(Region, (int)estate.EstateID);
        Ctx.Close(es);
    }

    private static void WriteInventory(Ctx ctx)
    {
        var inv = ctx.NewInventoryStore();
        inv.StoreFolder(new XInventoryFolder { folderID = Id(0x500), agentID = Agent, parentFolderID = UUID.Zero, folderName = "My Inventory", type = 8, version = 1 });
        inv.StoreFolder(new XInventoryFolder { folderID = Id(0x501), agentID = Agent, parentFolderID = Id(0x500), folderName = "Notecards", type = 7, version = 1 });
        inv.StoreItem(new XInventoryItem
        {
            inventoryID = Id(0x502), avatarID = Agent, parentFolderID = Id(0x501), assetID = Id(0x503), assetType = 7, invType = 7,
            inventoryName = "a note", inventoryDescription = "baseline item", creatorID = Agent.ToString(),
            inventoryBasePermissions = 0x7fffffff, inventoryCurrentPermissions = 0x7fffffff, inventoryNextPermissions = 0x7fffffff,
            inventoryEveryOnePermissions = 0, inventoryGroupPermissions = 0, creationDate = 1700000000, flags = 0
        });
        Ctx.Close(inv);
    }

    /// <summary>What the stores load back for the data set, as comparable strings keyed by a name.</summary>
    public static Dictionary<string, string> Load(Ctx ctx)
    {
        var d = new Dictionary<string, string>();
        var rs = ctx.NewRegionStore();
        foreach (var g in rs.LoadObjects(Region).OrderBy(g => g.UUID.ToString()))
        {
            string xml = SceneObjectSerializer.ToOriginalXmlFormat(g, false);
            d["object " + g.RootPart.Name] = xml;
            foreach (var p in g.Parts.OrderBy(p => p.UUID.ToString()))
                d[$"sit {p.Name}"] = $"set={p.IsSitTargetSet} pos={p.SitTargetPosition} rot={p.SitTargetOrientation}";
        }
        var s = rs.LoadRegionSettings(Region);
        d["regionsettings"] = $"AgentLimit={s.AgentLimit} WaterHeight={s.WaterHeight.ToString("R", CultureInfo.InvariantCulture)} Covenant={s.Covenant}";
        var t = rs.LoadTerrain(Region, 256, 256, 256);
        d["terrain"] = t is null ? "null" : TerrainHash(t);
        Ctx.Close(rs);

        var es = ctx.NewEstateStore();
        var e = es.LoadEstateSettings(Region, false);
        d["estate"] = e is null ? "null" : EstateString(e);
        Ctx.Close(es);

        var inv = ctx.NewInventoryStore();
        d["inventory folders"] = string.Join("\n", inv.GetFolders(new[] { "agentID" }, new[] { Agent.ToString() }).OrderBy(f => f.folderID.ToString()).Select(FolderString));
        d["inventory items"] = string.Join("\n", inv.GetItems(new[] { "avatarID" }, new[] { Agent.ToString() }).OrderBy(i => i.inventoryID.ToString()).Select(ItemString));
        Ctx.Close(inv);
        return d;
    }

    public static string TerrainHash(TerrainData t)
    {
        var sb = new StringBuilder();
        for (int x = 0; x < t.SizeX; x++)
            for (int y = 0; y < t.SizeY; y++)
                sb.Append(t[x, y].ToString("R", CultureInfo.InvariantCulture)).Append(',');
        return $"{t.SizeX}x{t.SizeY} sha256 " + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString())));
    }

    public static string EstateString(EstateSettings e) =>
        $"id={e.EstateID} name={e.EstateName} owner={e.EstateOwner} abuse={e.AbuseEmail} " +
        $"managers=[{string.Join(",", e.EstateManagers.Select(x => x.ToString()).Order())}] " +
        $"access=[{string.Join(",", e.EstateAccess.Select(x => x.ToString()).Order())}] " +
        $"groups=[{string.Join(",", e.EstateGroups.Select(x => x.ToString()).Order())}] " +
        $"bans=[{string.Join(",", e.EstateBans.Select(b => $"{b.BannedUserID}/{b.BanningUserID}/{b.BanTime}").Order())}]";

    public static string FolderString(XInventoryFolder f) =>
        $"{f.folderID} agent={f.agentID} parent={f.parentFolderID} name={f.folderName} type={f.type} version={f.version}";

    public static string ItemString(XInventoryItem i) =>
        $"{i.inventoryID} avatar={i.avatarID} parent={i.parentFolderID} asset={i.assetID} assetType={i.assetType} invType={i.invType} " +
        $"name={i.inventoryName} desc={i.inventoryDescription} creator={i.creatorID} perms={i.inventoryBasePermissions}/{i.inventoryCurrentPermissions}/" +
        $"{i.inventoryNextPermissions}/{i.inventoryEveryOnePermissions}/{i.inventoryGroupPermissions} sale={i.salePrice}/{i.saleType} " +
        $"created={i.creationDate} group={i.groupID}/{i.groupOwned} flags={i.flags}";
}

// ---------------------------------------------------------------------------------------------------------
// Round trips (written and read back through a second store instance, as after a region restart)
// ---------------------------------------------------------------------------------------------------------

public static class RoundTrips
{
    public static void Run(Ctx ctx, Report report, string phase)
    {
        if (ctx.Opts.Checks.Contains("general"))
        {
            Guarded(report, phase + " region round trip", () => Region(ctx, report, phase + " region round trip"));
            Guarded(report, phase + " estate round trip", () => Estate(ctx, report, phase + " estate round trip"));
            Guarded(report, phase + " estate ban list round trip", () => EstateBans(ctx, report, phase + " estate ban list round trip"));
            Guarded(report, phase + " inventory round trip", () => Inventory(ctx, report, phase + " inventory round trip"));
            Guarded(report, phase + " inventory move round trip", () => InventoryMove(ctx, report, phase + " inventory move round trip"));
        }
        if (ctx.Opts.Checks.Contains("sit"))
            Guarded(report, phase + " sit target round trip", () => SitTargets(ctx, report, phase + " sit target round trip"));
    }

    private static void Guarded(Report report, string check, Action a)
    {
        report.Section(check);
        int mark = Capture.Mark;
        try
        {
            a();
        }
        catch (Exception e)
        {
            report.Fail(check, "threw " + e);
        }
        report.CheckLog(check, mark);
    }

    private static void Expect(Report report, string check, string what, object expected, object actual)
    {
        string ex = Ctx.Val(expected), ac = Ctx.Val(actual);
        if (ex == ac) report.Pass(check, $"{what} = {ac}");
        else report.Fail(check, $"{what}: expected {ex}, got {ac}");
    }

    private static void Near(Report report, string check, string what, Vector3 expected, Vector3 actual)
    {
        if (Vector3.Distance(expected, actual) < 1e-5f) report.Pass(check, $"{what} = {actual}");
        else report.Fail(check, $"{what}: expected {expected}, got {actual}");
    }

    private static void Near(Report report, string check, string what, Quaternion expected, Quaternion actual)
    {
        if (Math.Abs(expected.X - actual.X) < 1e-5f && Math.Abs(expected.Y - actual.Y) < 1e-5f &&
            Math.Abs(expected.Z - actual.Z) < 1e-5f && Math.Abs(expected.W - actual.W) < 1e-5f)
            report.Pass(check, $"{what} = {actual}");
        else report.Fail(check, $"{what}: expected {expected}, got {actual}");
    }

    /// <summary>
    /// The MySQL store writes terrain on a pool thread, so a read straight after StoreTerrain can miss it. Poll
    /// until it is there, for up to 30 s.
    /// </summary>
    public static TerrainData WaitForTerrain(ISimulationDataStore store, UUID region)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var t = store.LoadTerrain(region, 256, 256, 256);
            if (t is not null || DateTime.UtcNow > until)
                return t;
            Thread.Sleep(200);
        }
    }

    private static SceneObjectPart Reload(ISimulationDataStore store, UUID region, UUID part)
    {
        foreach (var g in store.LoadObjects(region))
        {
            var p = g.GetPart(part);
            if (p is not null) return p;
        }
        return null;
    }

    private static SceneObjectPart NewPart(UUID id, string name, Vector3 pos, Vector3 offset) =>
        new SceneObjectPart(UUID.Random(), PrimitiveBaseShape.Default, pos, Quaternion.Identity, offset)
        { Name = name, UUID = id, Scale = new Vector3(1, 1, 1) };

    private static void Region(Ctx ctx, Report report, string check)
    {
        UUID region = UUID.Random();
        var root = NewPart(UUID.Random(), "rt root", new Vector3(100, 101, 25), Vector3.Zero);
        root.Description = "round trip root";
        root.Text = "hover text";
        root.Scale = new Vector3(2, 3, 4);
        root.Material = (byte)Material.Metal;
        root.SitTargetPosition = new Vector3(0.1f, 0.2f, 0.3f);
        var g = new SceneObjectGroup(root);
        var child = NewPart(UUID.Random(), "rt child", root.GroupPosition, new Vector3(0, 0, 2));
        child.Description = "round trip child";
        g.AddPart(child);
        var item = new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = UUID.Random(), Name = "rt script", Description = "rt item", Type = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL, OwnerID = root.OwnerID, CreatorID = root.OwnerID, ParentID = root.UUID, ParentPartID = root.UUID,
            CreationDate = 1700000123
        };

        var a = ctx.NewRegionStore();
        a.StoreObject(g, region);
        a.StorePrimInventory(root.UUID, new[] { item });
        var rs = a.LoadRegionSettings(region);
        rs.AgentLimit = 77; rs.ObjectBonus = 1.5; rs.Maturity = 2; rs.BlockFly = true; rs.WaterHeight = 22.25;
        rs.TerrainTexture1 = UUID.Random(); rs.Elevation1NW = 12.5; rs.TerrainRaiseLimit = 50; rs.TerrainLowerLimit = -40;
        rs.Covenant = UUID.Random(); rs.CovenantChangedDateTime = 1700000456;
        a.StoreRegionSettings(rs);
        var terrain = new TerrainData(256, 256, 256);
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
                terrain[x, y] = 10f + (x + 2 * y) % 64 / 4f;
        a.StoreTerrain(terrain, region);
        a.SaveExtra(region, "rt-key", "rt-value");
        Ctx.Close(a);

        var b = ctx.NewRegionStore();
        var lr = Reload(b, region, root.UUID);
        if (lr is null)
        {
            report.Fail(check, "object not loaded back");
        }
        else
        {
            Expect(report, check, "part count", 2, lr.ParentGroup.PrimCount);
            Expect(report, check, "root name", root.Name, lr.Name);
            Expect(report, check, "root description", root.Description, lr.Description);
            Expect(report, check, "root text", root.Text, lr.Text);
            Expect(report, check, "root material", root.Material, lr.Material);
            Near(report, check, "root position", root.GroupPosition, lr.GroupPosition);
            Near(report, check, "root scale", root.Scale, lr.Scale);
            Near(report, check, "root sit target", root.SitTargetPosition, lr.SitTargetPosition);
            var lc = lr.ParentGroup.GetPart(child.UUID);
            if (lc is null) report.Fail(check, "child part not loaded back");
            else
            {
                Expect(report, check, "child name", child.Name, lc.Name);
                Near(report, check, "child offset", child.OffsetPosition, lc.OffsetPosition);
            }
            var items = lr.TaskInventory.Values.ToList();
            Expect(report, check, "task inventory count", 1, items.Count);
            if (items.Count == 1)
            {
                var li = items[0];
                Expect(report, check, "item id", item.ItemID, li.ItemID);
                Expect(report, check, "item name", item.Name, li.Name);
                Expect(report, check, "item description", item.Description, li.Description);
                Expect(report, check, "item asset", item.AssetID, li.AssetID);
                Expect(report, check, "item type", item.Type, li.Type);
                Expect(report, check, "item creation date", item.CreationDate, li.CreationDate);
            }
        }
        var lrs = b.LoadRegionSettings(region);
        Expect(report, check, "settings AgentLimit", rs.AgentLimit, lrs.AgentLimit);
        Expect(report, check, "settings ObjectBonus", rs.ObjectBonus, lrs.ObjectBonus);
        Expect(report, check, "settings Maturity", rs.Maturity, lrs.Maturity);
        Expect(report, check, "settings BlockFly", rs.BlockFly, lrs.BlockFly);
        Expect(report, check, "settings WaterHeight", rs.WaterHeight, lrs.WaterHeight);
        Expect(report, check, "settings TerrainTexture1", rs.TerrainTexture1, lrs.TerrainTexture1);
        Expect(report, check, "settings Elevation1NW", rs.Elevation1NW, lrs.Elevation1NW);
        Expect(report, check, "settings TerrainRaiseLimit", rs.TerrainRaiseLimit, lrs.TerrainRaiseLimit);
        Expect(report, check, "settings TerrainLowerLimit", rs.TerrainLowerLimit, lrs.TerrainLowerLimit);
        Expect(report, check, "settings Covenant", rs.Covenant, lrs.Covenant);
        Expect(report, check, "settings CovenantChangedDateTime", rs.CovenantChangedDateTime, lrs.CovenantChangedDateTime);
        var lt = WaitForTerrain(b, region);
        if (lt is null) report.Fail(check, "terrain not loaded back within 30 s");
        else
        {
            float worst = 0;
            for (int x = 0; x < 256; x++)
                for (int y = 0; y < 256; y++)
                    worst = Math.Max(worst, Math.Abs(lt[x, y] - terrain[x, y]));
            if (worst <= 0.01f) report.Pass(check, $"terrain heights within 0.01 m (worst {worst})");
            else report.Fail(check, $"terrain heights differ by up to {worst} m");
        }
        var extra = b.GetExtra(region);
        Expect(report, check, "extra rt-key", "rt-value", extra is not null && extra.TryGetValue("rt-key", out var ev) ? ev : null);

        b.RemoveObject(g.UUID, region);
        Ctx.Close(b);
        var c = ctx.NewRegionStore();
        Expect(report, check, "object removed (loaded after remove)", false, Reload(c, region, root.UUID) is not null);
        Ctx.Close(c);
    }

    private static void Estate(Ctx ctx, Report report, string check)
    {
        UUID region = UUID.Random();
        UUID manager = UUID.Random(), manager2 = UUID.Random(), user = UUID.Random(), user2 = UUID.Random();
        UUID group = UUID.Random(), group2 = UUID.Random(), banned = UUID.Random(), banned2 = UUID.Random(), owner = UUID.Random();

        var a = ctx.NewEstateStore();
        var es = a.LoadEstateSettings(region, true);
        es.EstateName = "Round Trip Estate";
        es.EstateOwner = owner;
        es.AbuseEmail = "rt@example.org";
        es.AllowVoice = true;
        es.DenyMinors = true;
        es.PricePerMeter = 3;
        es.AddEstateManager(manager); es.AddEstateManager(manager2);
        es.AddEstateUser(user); es.AddEstateUser(user2);
        es.AddEstateGroup(group); es.AddEstateGroup(group2);
        a.StoreEstateSettings(es);
        a.LinkRegion(region, (int)es.EstateID);
        Ctx.Close(a);

        var b = ctx.NewEstateStore();
        var l = b.LoadEstateSettings(region, false);
        if (l is null) { report.Fail(check, "estate not loaded back for its region"); return; }
        Expect(report, check, "estate id", es.EstateID, l.EstateID);
        Expect(report, check, "estate name", es.EstateName, l.EstateName);
        Expect(report, check, "estate owner", es.EstateOwner, l.EstateOwner);
        Expect(report, check, "abuse email", es.AbuseEmail, l.AbuseEmail);
        Expect(report, check, "allow voice", es.AllowVoice, l.AllowVoice);
        Expect(report, check, "deny minors", es.DenyMinors, l.DenyMinors);
        Expect(report, check, "price per meter", es.PricePerMeter, l.PricePerMeter);
        Expect(report, check, "managers", Set(manager, manager2), Set(l.EstateManagers));
        Expect(report, check, "access list", Set(user, user2), Set(l.EstateAccess));
        Expect(report, check, "group list", Set(group, group2), Set(l.EstateGroups));
        var byId = b.LoadEstateSettings((int)es.EstateID);
        Expect(report, check, "load by id: name", es.EstateName, byId?.EstateName);
        Expect(report, check, "regions of the estate", region.ToString(), string.Join(",", b.GetRegions((int)es.EstateID)));

        // Removing one entry from each list must survive a reload too.
        l.RemoveEstateManager(manager2);
        l.RemoveEstateUser(user2);
        l.RemoveEstateGroup(group2);
        b.StoreEstateSettings(l);
        Ctx.Close(b);

        var c = ctx.NewEstateStore();
        var l2 = c.LoadEstateSettings(region, false);
        Expect(report, check, "managers after a removal", Set(manager), Set(l2.EstateManagers));
        Expect(report, check, "access list after a removal", Set(user), Set(l2.EstateAccess));
        Expect(report, check, "group list after a removal", Set(group), Set(l2.EstateGroups));
        Ctx.Close(c);
    }

    private static void EstateBans(Ctx ctx, Report report, string check)
    {
        UUID region = UUID.Random(), owner = UUID.Random(), banned = UUID.Random(), banned2 = UUID.Random();
        var a = ctx.NewEstateStore();
        var es = a.LoadEstateSettings(region, true);
        es.EstateName = "Ban List Estate";
        es.EstateOwner = owner;
        es.AddBan(new EstateBan { EstateID = es.EstateID, BannedUserID = banned, BanningUserID = owner, BanTime = 1700000789 });
        es.AddBan(new EstateBan { EstateID = es.EstateID, BannedUserID = banned2, BanningUserID = owner, BanTime = 1700000790 });
        a.StoreEstateSettings(es);
        a.LinkRegion(region, (int)es.EstateID);
        Ctx.Close(a);

        var b = ctx.NewEstateStore();
        var l = b.LoadEstateSettings(region, false);
        if (l is null) { report.Fail(check, "estate not loaded back for its region"); return; }
        Expect(report, check, "ban list", Set(banned, banned2), Set(l.EstateBans.Select(x => x.BannedUserID).ToArray()));
        Expect(report, check, "ban banning user", owner.ToString(), string.Join(",", l.EstateBans.Select(x => x.BanningUserID.ToString()).Distinct()));
        Expect(report, check, "ban times", "1700000789,1700000790", string.Join(",", l.EstateBans.Select(x => x.BanTime.ToString(CultureInfo.InvariantCulture)).Order()));
        l.RemoveBan(banned2);
        b.StoreEstateSettings(l);
        Ctx.Close(b);

        var c = ctx.NewEstateStore();
        var l2 = c.LoadEstateSettings(region, false);
        Expect(report, check, "ban list after a removal", Set(banned), Set(l2.EstateBans.Select(x => x.BannedUserID).ToArray()));
        Ctx.Close(c);
    }

    private static string Set(params UUID[] ids) => string.Join(",", ids.Select(x => x.ToString()).Order());

    private static void Inventory(Ctx ctx, Report report, string check)
    {
        UUID agent = UUID.Random();
        var root = new XInventoryFolder { folderID = UUID.Random(), agentID = agent, parentFolderID = UUID.Zero, folderName = "My Inventory", type = 8, version = 1 };
        var sub = new XInventoryFolder { folderID = UUID.Random(), agentID = agent, parentFolderID = root.folderID, folderName = "Objects", type = 6, version = 3 };
        var other = new XInventoryFolder { folderID = UUID.Random(), agentID = agent, parentFolderID = root.folderID, folderName = "Other", type = -1, version = 1 };
        var item = new XInventoryItem
        {
            inventoryID = UUID.Random(), avatarID = agent, parentFolderID = sub.folderID, assetID = UUID.Random(), assetType = 6, invType = 6,
            inventoryName = "rt object", inventoryDescription = "round trip", creatorID = agent.ToString(),
            inventoryBasePermissions = 581639, inventoryCurrentPermissions = 581632, inventoryNextPermissions = 532480,
            inventoryEveryOnePermissions = 0, inventoryGroupPermissions = 0, salePrice = 10, saleType = 0,
            creationDate = 1700001000, groupID = UUID.Random(), groupOwned = 0, flags = 0
        };
        var item2 = item.Clone();
        item2.inventoryID = UUID.Random();
        item2.inventoryName = "rt object 2";

        var a = ctx.NewInventoryStore();
        Expect(report, check, "store root folder", true, a.StoreFolder(root));
        Expect(report, check, "store sub folder", true, a.StoreFolder(sub));
        Expect(report, check, "store other folder", true, a.StoreFolder(other));
        Expect(report, check, "store item", true, a.StoreItem(item));
        Expect(report, check, "store item 2", true, a.StoreItem(item2));
        Ctx.Close(a);

        var b = ctx.NewInventoryStore();
        // The stores raise a folder's version when something is stored in it, so the version is checked to be
        // no lower than written and the other fields exactly.
        var loadedFolders = b.GetFolders(new[] { "agentID" }, new[] { agent.ToString() }).OrderBy(f => f.folderID.ToString()).ToList();
        var written = new[] { root, sub, other }.OrderBy(f => f.folderID.ToString()).ToList();
        Expect(report, check, "folders", string.Join(" | ", written.Select(FolderNoVersion)), string.Join(" | ", loadedFolders.Select(FolderNoVersion)));
        foreach (var f in loadedFolders)
        {
            var w = written.FirstOrDefault(x => x.folderID == f.folderID);
            if (w is not null && f.version < w.version)
                report.Fail(check, $"folder {f.folderName}: version {f.version} lower than written {w.version}");
        }
        var items = b.GetItems(new[] { "avatarID" }, new[] { agent.ToString() }).OrderBy(i => i.inventoryID.ToString()).Select(BaselineData.ItemString);
        Expect(report, check, "items", string.Join(" | ", new[] { item, item2 }.OrderBy(i => i.inventoryID.ToString()).Select(BaselineData.ItemString)), string.Join(" | ", items));

        item.inventoryName = "rt object renamed";
        item.parentFolderID = other.folderID;
        Expect(report, check, "update item (rename, new parent)", true, b.StoreItem(item));
        Expect(report, check, "delete item 2", true, b.DeleteItems("inventoryID", item2.inventoryID.ToString()));
        Ctx.Close(b);

        var c = ctx.NewInventoryStore();
        var after = c.GetItems(new[] { "avatarID" }, new[] { agent.ToString() }).Select(BaselineData.ItemString).ToList();
        Expect(report, check, "items after update and delete", BaselineData.ItemString(item), string.Join(" | ", after));
        Ctx.Close(c);
    }

    private static string FolderNoVersion(XInventoryFolder f) =>
        $"{f.folderID} agent={f.agentID} parent={f.parentFolderID} name={f.folderName} type={f.type}";

    private static void InventoryMove(Ctx ctx, Report report, string check)
    {
        UUID agent = UUID.Random();
        var from = new XInventoryFolder { folderID = UUID.Random(), agentID = agent, parentFolderID = UUID.Zero, folderName = "From", type = -1, version = 1 };
        var to = new XInventoryFolder { folderID = UUID.Random(), agentID = agent, parentFolderID = UUID.Zero, folderName = "To", type = -1, version = 1 };
        var item = new XInventoryItem
        {
            inventoryID = UUID.Random(), avatarID = agent, parentFolderID = from.folderID, assetID = UUID.Random(), assetType = 7, invType = 7,
            inventoryName = "moved note", inventoryDescription = "", creatorID = agent.ToString(), creationDate = 1700002000
        };
        var a = ctx.NewInventoryStore();
        a.StoreFolder(from);
        a.StoreFolder(to);
        a.StoreItem(item);
        Expect(report, check, "move item", true, a.MoveItem(item.inventoryID.ToString(), to.folderID.ToString()));
        Ctx.Close(a);

        var b = ctx.NewInventoryStore();
        var l = b.GetItems(new[] { "inventoryID" }, new[] { item.inventoryID.ToString() });
        Expect(report, check, "parent after the move", to.folderID, l.Length == 1 ? l[0].parentFolderID : UUID.Zero);
        Ctx.Close(b);
    }

    private static void SitTargets(Ctx ctx, Report report, string check)
    {
        UUID region = UUID.Random();
        var cases = new List<(string name, SceneObjectPart part, bool active, Vector3 pos, Quaternion rot, string column)>();

        var p1 = NewPart(UUID.Random(), "active at zero offset", new Vector3(50, 50, 25), Vector3.Zero);
        p1.SetSitTarget(true, Vector3.Zero, Quaternion.Identity);
        cases.Add(("active target at a zero offset", p1, true, Vector3.Zero, Quaternion.Identity, "1"));

        var off = new Vector3(0.5f, 0, 0.25f);
        var p2 = NewPart(UUID.Random(), "inactive with offset", new Vector3(52, 50, 25), Vector3.Zero);
        p2.SetSitTarget(false, off, Quaternion.Identity);
        cases.Add(("inactive target with an offset", p2, false, off, Quaternion.Identity, "0"));

        var off3 = new Vector3(0, 0, 0.6f);
        var rot3 = Quaternion.CreateFromEulers(0, 0, 1.0f);
        var p3 = NewPart(UUID.Random(), "ordinary with offset", new Vector3(54, 50, 25), Vector3.Zero);
        p3.SitTargetPosition = off3;
        p3.SitTargetOrientation = rot3;
        cases.Add(("ordinary target with an offset", p3, true, off3, rot3, "NULL"));

        var p4 = NewPart(UUID.Random(), "no target", new Vector3(56, 50, 25), Vector3.Zero);
        cases.Add(("prim with no target", p4, false, Vector3.Zero, Quaternion.Identity, "NULL"));

        var a = ctx.NewRegionStore();
        foreach (var cse in cases)
        {
            Expect(report, check, $"{cse.name}: state before saving", cse.active, cse.part.IsSitTargetSet);
            a.StoreObject(new SceneObjectGroup(cse.part), region);
        }
        Ctx.Close(a);

        var b = ctx.NewRegionStore();
        using DbConnection raw = ctx.OpenRaw(StoreKind.Region);
        foreach (var cse in cases)
        {
            var l = Reload(b, region, cse.part.UUID);
            if (l is null) { report.Fail(check, $"{cse.name}: not loaded back"); continue; }
            Expect(report, check, $"{cse.name}: active after reload", cse.active, l.IsSitTargetSet);
            Expect(report, check, $"{cse.name}: SitTargetActive after reload", cse.active, l.SitTargetActive);
            Near(report, check, $"{cse.name}: offset after reload", cse.pos, l.SitTargetPosition);
            Near(report, check, $"{cse.name}: rotation after reload", cse.rot, l.SitTargetOrientation);
            using DbCommand cmd = raw.CreateCommand();
            string q = ctx.Db == "mysql" ? "`" : "\"";
            cmd.CommandText = $"SELECT {q}SitTargetActive{q} FROM {q}prims{q} WHERE {q}UUID{q} = '{cse.part.UUID}'";
            object v = cmd.ExecuteScalar();
            Expect(report, check, $"{cse.name}: stored SitTargetActive column", cse.column, v is null ? "<no row>" : Ctx.Val(v) == "True" ? "1" : Ctx.Val(v) == "False" ? "0" : Ctx.Val(v));
        }
        Ctx.Close(b);
    }
}
