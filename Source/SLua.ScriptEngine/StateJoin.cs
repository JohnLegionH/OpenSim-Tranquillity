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
    /// <summary>What the engine does with a script when it looks for its saved state.</summary>
    internal enum StateLoadKind
    {
        /// <summary>There is nothing usable: the script starts fresh.</summary>
        Fresh,

        /// <summary>A record of this engine for this item and asset: the script is restored from it.</summary>
        Restore,

        /// <summary>The store could not be read: the script is held with the reason and its row is left alone.</summary>
        Held,
    }

    internal sealed class StateLoadResult
    {
        public StateLoadKind Kind;
        public ScriptStateRecord Record;
        public string Reason;

        /// <summary>The record came with the script's object (take, rez, crossing) and not from the database.</summary>
        public bool Carried;
    }

    /// <summary>
    /// Looks for a script's saved state when it joins the scheduler. A record of this engine for the item and asset is restored. No
    /// row, or a row another engine wrote, is a fresh start and the row is left as it is. Bytes that are no record are moved to the
    /// rejected table with the reason and the script starts fresh. A store that cannot be read is retried; when the retries are used
    /// up the script is held with the reason and its row untouched, because a script started fresh over a row it could not read
    /// would write its own state over that row at the next save.
    /// </summary>
    internal static class StateJoin
    {
        public static StateLoadResult Load(SqliteStateStore store, string engine, UUID item, UUID asset, int retries, int retryDelayMs, Action<int> wait, IEngineLog log, byte[] carried = null)
        {
            // State that came with the object is used before any row: it is what the script was doing when it left.
            if (carried != null)
            {
                if (ScriptStateRecord.Inspect(carried, engine, out ScriptStateRecord brought, out string why) == ScriptStateRecord.RecordReading.Ok)
                    return new StateLoadResult { Kind = StateLoadKind.Restore, Record = brought, Carried = true };
                log?.Warn($"Script {item}: the state its object brought cannot be used ({why}); the script starts from its saved state, if it has one");
            }
            if (store == null)
                return new StateLoadResult { Kind = StateLoadKind.Fresh };
            byte[] saved = null;
            ScriptStateLoadException failure = null;
            for (int attempt = 0; attempt <= Math.Max(0, retries); attempt++)
            {
                try
                {
                    saved = store.Load(item, asset);
                    failure = null;
                    break;
                }
                catch (ScriptStateLoadException e)
                {
                    failure = e;
                    if (attempt < retries)
                        wait?.Invoke(retryDelayMs);
                }
            }
            if (failure != null)
            {
                string why = $"its saved state could not be read ({failure.InnerException?.Message ?? failure.Message}); the script is held and its saved state is left as it is";
                log?.Error($"Script {item}: {why}");
                return new StateLoadResult { Kind = StateLoadKind.Held, Reason = why };
            }
            if (saved == null)
                return new StateLoadResult { Kind = StateLoadKind.Fresh };

            switch (ScriptStateRecord.Inspect(saved, engine, out ScriptStateRecord record, out string problem))
            {
                case ScriptStateRecord.RecordReading.Ok:
                    return new StateLoadResult { Kind = StateLoadKind.Restore, Record = record };
                case ScriptStateRecord.RecordReading.OtherEngine:
                    log?.Warn($"Script {item}: the saved state is another engine's ({problem}); the script starts fresh and that state is left alone");
                    return new StateLoadResult { Kind = StateLoadKind.Fresh, Reason = problem };
                default:
                    log?.Warn($"Script {item}: the saved state cannot be used ({problem}); it is set aside and the script starts fresh");
                    store.Reject(item, problem);
                    return new StateLoadResult { Kind = StateLoadKind.Fresh, Reason = problem };
            }
        }
    }
}
