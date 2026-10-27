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
    }

    /// <summary>Reads a script's saved state when it joins the scheduler (stub: always fresh).</summary>
    internal static class StateJoin
    {
        public static StateLoadResult Load(SqliteStateStore store, string engine, UUID item, UUID asset, int retries, int retryDelayMs, Action<int> wait, IEngineLog log) =>
            new StateLoadResult { Kind = StateLoadKind.Fresh };
    }
}
