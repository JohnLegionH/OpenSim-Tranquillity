/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
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
    /// The engine's own database of saved script state (stub: it holds nothing).
    /// </summary>
    internal sealed class SqliteStateStore : ISavedStateStore, IDisposable
    {
        public SqliteStateStore(string databaseFile, string engineName, IEngineLog log = null, int busyTimeoutMs = 5000)
        {
        }

        /// <summary>For a test: makes a read of the item's row fail as the database would.</summary>
        public Func<UUID, bool> FailReadFor;

        public bool HasSavedState(UUID itemID, UUID assetID) => false;

        public byte[] Load(UUID itemId, UUID assetId) => null;

        public void Save(UUID itemId, UUID assetId, byte[] state)
        {
        }

        public void Delete(UUID itemID)
        {
        }

        public void Reject(UUID itemId, string reason)
        {
        }

        public string RejectedReason(UUID itemId) => null;

        public byte[] RejectedState(UUID itemId) => null;

        public bool Flush(int timeoutMs = 10000) => true;

        public long WritesDone => 0;

        public void Dispose()
        {
        }
    }
}
