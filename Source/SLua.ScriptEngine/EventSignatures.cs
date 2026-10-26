/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;

namespace SLua.ScriptEngine
{
    /// <summary>The parameter types of every event the runtime knows, by event name.</summary>
    internal static class EventSignatures
    {
        internal static readonly IReadOnlyDictionary<string, string[]> Parameters = new Dictionary<string, string[]>();
    }
}
