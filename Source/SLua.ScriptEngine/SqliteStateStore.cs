/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using OpenMetaverse;

namespace SLua.ScriptEngine
{
    /// <summary>The state of a script could not be read from the store; the script is held and its saved state is left as it is.</summary>
    internal sealed class ScriptStateLoadException : Exception
    {
        public ScriptStateLoadException(UUID itemId, Exception inner) : base($"the saved state of script {itemId} could not be read: {inner?.Message}", inner) { ItemId = itemId; }

        public UUID ItemId { get; }
    }

    /// <summary>
    /// The engine's own database of saved script state: one SQLite file in write-ahead mode, kept apart from every other engine's
    /// (a script that moves to another engine keeps its row here untouched), with a row per item and engine. The design follows what
    /// the Phlox engine's state manager learned in a running world: a busy timeout on every connection, because several regions
    /// restore from one file at once and a contended open must wait; one writer for the whole process, because two writers meet as
    /// "database is locked"; writes done off the script threads, which never wait on the database; and a read error is not
    /// "no state", because a script started fresh over a row it could not read destroys that row at its next save.
    /// A row that cannot be used is moved to a second table with its reason, never overwritten.
    /// </summary>
    internal sealed class SqliteStateStore : ISavedStateStore, IDisposable
    {
        // Every write of every store in the process takes this lock, so there is one writer at a time.
        private static readonly object s_writerLock = new object();

        private readonly string m_engine;
        private readonly IEngineLog m_log;
        private readonly SQLiteConnection m_writer;
        private readonly SQLiteConnection m_reader;
        private readonly object m_readLock = new object();
        private readonly BlockingCollection<Write> m_queue = new BlockingCollection<Write>();
        private readonly Thread m_thread;

        // What is queued and not yet written, newest per item, so a read right after a save sees it.
        private readonly ConcurrentDictionary<UUID, (UUID Asset, byte[] State)> m_pending = new ConcurrentDictionary<UUID, (UUID, byte[])>();
        private long m_writesDone;
        private bool m_disposed;

        private enum WriteKind { Save, Delete, Reject, Marker }

        private sealed class Write
        {
            public WriteKind Kind;
            public UUID ItemId;
            public UUID AssetId;
            public byte[] State;
            public string Reason;
            public ManualResetEventSlim Done;
        }

        public SqliteStateStore(string databaseFile, string engineName, IEngineLog log = null, int busyTimeoutMs = 5000)
        {
            m_engine = engineName;
            m_log = log;
            string dir = Path.GetDirectoryName(databaseFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string connection = $"Data Source={databaseFile};Version=3;Pooling=False;Default Timeout={Math.Max(1, busyTimeoutMs / 1000)}";
            m_writer = new SQLiteConnection(connection);
            m_writer.Open();
            lock (s_writerLock)
            {
                Execute(m_writer, "PRAGMA journal_mode=WAL");
                Execute(m_writer, "PRAGMA synchronous=NORMAL");
                Execute(m_writer, "CREATE TABLE IF NOT EXISTS script_state (item_id TEXT NOT NULL, engine TEXT NOT NULL, asset_id TEXT NOT NULL, " +
                    "state_data BLOB NOT NULL, saved_at INTEGER NOT NULL, PRIMARY KEY (item_id, engine))");
                Execute(m_writer, "CREATE TABLE IF NOT EXISTS script_state_rejected (id INTEGER PRIMARY KEY AUTOINCREMENT, item_id TEXT NOT NULL, engine TEXT NOT NULL, " +
                    "asset_id TEXT NOT NULL, state_data BLOB NOT NULL, reason TEXT NOT NULL, rejected_at INTEGER NOT NULL)");
            }
            m_reader = new SQLiteConnection(connection);
            m_reader.Open();
            m_thread = new Thread(WriterLoop) { IsBackground = true, Name = "SLua state writer" };
            m_thread.Start();
        }

        /// <summary>For a test: makes a read of the item's row fail as the database would.</summary>
        public Func<UUID, bool> FailReadFor;

        /// <summary>How many writes the writer has done.</summary>
        public long WritesDone => Interlocked.Read(ref m_writesDone);

        /// <summary>
        /// Whether this engine has saved state for the item and asset, without using it. An error reading the store keeps ownership
        /// (answers yes, and logs): a script whose row cannot be read must not be handed to another engine.
        /// </summary>
        public bool HasSavedState(UUID itemID, UUID assetID)
        {
            if (m_pending.TryGetValue(itemID, out var pending))
                return pending.Asset == assetID;
            try
            {
                if (FailReadFor?.Invoke(itemID) == true)
                    throw new SQLiteException(SQLiteErrorCode.IoErr, "read failed (test)");
                lock (m_readLock)
                {
                    using SQLiteCommand c = m_reader.CreateCommand();
                    c.CommandText = "SELECT 1 FROM script_state WHERE item_id = @i AND engine = @e AND asset_id = @a";
                    c.Parameters.AddWithValue("@i", itemID.ToString());
                    c.Parameters.AddWithValue("@e", m_engine);
                    c.Parameters.AddWithValue("@a", assetID.ToString());
                    return c.ExecuteScalar() != null;
                }
            }
            catch (Exception e)
            {
                m_log?.Error($"the saved state of script {itemID} could not be checked, so the script keeps its owner: {e.Message}");
                return true;
            }
        }

        /// <summary>The state saved for the item and asset, or null when this engine has none. Throws <see cref="ScriptStateLoadException"/> when the store cannot be read.</summary>
        public byte[] Load(UUID itemId, UUID assetId)
        {
            if (m_pending.TryGetValue(itemId, out var pending))
                return pending.Asset == assetId ? pending.State : null;
            try
            {
                if (FailReadFor?.Invoke(itemId) == true)
                    throw new SQLiteException(SQLiteErrorCode.IoErr, "read failed (test)");
                lock (m_readLock)
                {
                    using SQLiteCommand c = m_reader.CreateCommand();
                    c.CommandText = "SELECT state_data FROM script_state WHERE item_id = @i AND engine = @e AND asset_id = @a";
                    c.Parameters.AddWithValue("@i", itemId.ToString());
                    c.Parameters.AddWithValue("@e", m_engine);
                    c.Parameters.AddWithValue("@a", assetId.ToString());
                    return c.ExecuteScalar() as byte[];
                }
            }
            catch (Exception e)
            {
                throw new ScriptStateLoadException(itemId, e);
            }
        }

        /// <summary>Queues the state for writing; the caller does not wait for the database.</summary>
        public void Save(UUID itemId, UUID assetId, byte[] state)
        {
            m_pending[itemId] = (assetId, state);
            Enqueue(new Write { Kind = WriteKind.Save, ItemId = itemId, AssetId = assetId, State = state });
        }

        /// <summary>Queues the removal of this engine's row for the item (the script was deleted).</summary>
        public void Delete(UUID itemID)
        {
            m_pending.TryRemove(itemID, out _);
            Enqueue(new Write { Kind = WriteKind.Delete, ItemId = itemID });
        }

        /// <summary>Queues moving this engine's row for the item to the rejected table with the reason; the script starts fresh and the old state stays on record.</summary>
        public void Reject(UUID itemId, string reason)
        {
            m_pending.TryRemove(itemId, out _);
            Enqueue(new Write { Kind = WriteKind.Reject, ItemId = itemId, Reason = reason ?? "" });
        }

        /// <summary>The reason of the item's latest rejected row, or null.</summary>
        public string RejectedReason(UUID itemId) => ReadRejected(itemId, "reason") as string;

        /// <summary>The state of the item's latest rejected row, or null.</summary>
        public byte[] RejectedState(UUID itemId) => ReadRejected(itemId, "state_data") as byte[];

        private object ReadRejected(UUID itemId, string column)
        {
            lock (m_readLock)
            {
                using SQLiteCommand c = m_reader.CreateCommand();
                c.CommandText = $"SELECT {column} FROM script_state_rejected WHERE item_id = @i AND engine = @e ORDER BY id DESC LIMIT 1";
                c.Parameters.AddWithValue("@i", itemId.ToString());
                c.Parameters.AddWithValue("@e", m_engine);
                return c.ExecuteScalar();
            }
        }

        /// <summary>Waits until everything queued so far is written.</summary>
        public bool Flush(int timeoutMs = 10000)
        {
            if (m_disposed)
                return true;
            using var done = new ManualResetEventSlim(false);
            Enqueue(new Write { Kind = WriteKind.Marker, Done = done });
            return done.Wait(timeoutMs);
        }

        private void Enqueue(Write write)
        {
            try { m_queue.Add(write); }
            catch (InvalidOperationException) { write.Done?.Set(); } // the store is closed
        }

        private void WriterLoop()
        {
            foreach (Write write in m_queue.GetConsumingEnumerable())
            {
                try
                {
                    if (write.Kind != WriteKind.Marker)
                    {
                        lock (s_writerLock)
                            Apply(write);
                        Interlocked.Increment(ref m_writesDone);
                    }
                }
                catch (Exception e)
                {
                    m_log?.Error($"the saved state of script {write.ItemId} could not be written ({write.Kind}): {e.Message}");
                }
                finally
                {
                    if (write.Kind == WriteKind.Save && m_pending.TryGetValue(write.ItemId, out var p) && ReferenceEquals(p.State, write.State))
                        m_pending.TryRemove(new KeyValuePair<UUID, (UUID, byte[])>(write.ItemId, p));
                    write.Done?.Set();
                }
            }
        }

        private void Apply(Write write)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using SQLiteTransaction tx = m_writer.BeginTransaction();
            switch (write.Kind)
            {
                case WriteKind.Save:
                    Run(tx, "INSERT OR REPLACE INTO script_state (item_id, engine, asset_id, state_data, saved_at) VALUES (@i, @e, @a, @d, @t)",
                        ("@i", write.ItemId.ToString()), ("@e", m_engine), ("@a", write.AssetId.ToString()), ("@d", write.State), ("@t", now));
                    break;
                case WriteKind.Delete:
                    Run(tx, "DELETE FROM script_state WHERE item_id = @i AND engine = @e", ("@i", write.ItemId.ToString()), ("@e", m_engine));
                    break;
                case WriteKind.Reject:
                    Run(tx, "INSERT INTO script_state_rejected (item_id, engine, asset_id, state_data, reason, rejected_at) " +
                        "SELECT item_id, engine, asset_id, state_data, @r, @t FROM script_state WHERE item_id = @i AND engine = @e",
                        ("@i", write.ItemId.ToString()), ("@e", m_engine), ("@r", write.Reason), ("@t", now));
                    Run(tx, "DELETE FROM script_state WHERE item_id = @i AND engine = @e", ("@i", write.ItemId.ToString()), ("@e", m_engine));
                    break;
            }
            tx.Commit();
        }

        private void Run(SQLiteTransaction tx, string sql, params (string Name, object Value)[] parameters)
        {
            using SQLiteCommand c = m_writer.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            foreach ((string name, object value) in parameters)
                c.Parameters.AddWithValue(name, value);
            c.ExecuteNonQuery();
        }

        private static void Execute(SQLiteConnection connection, string sql)
        {
            using SQLiteCommand c = connection.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        /// <summary>Writes what is queued, stops the writer and closes the database.</summary>
        public void Dispose()
        {
            if (m_disposed)
                return;
            m_disposed = true;
            m_queue.CompleteAdding();
            if (!m_thread.Join(TimeSpan.FromSeconds(30)))
                m_log?.Error("the state writer did not finish within 30 s; the rest of its queue is not written");
            m_writer.Dispose();
            m_reader.Dispose();
            m_queue.Dispose();
        }
    }
}
