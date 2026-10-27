/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using InWorldz.Phlox.VM;
using Phlox.ScriptEngine;

namespace SLua.ScriptEngine
{
    /// <summary>The script as LSLSystemAPI sees it, for a script the engine runs: its runtime state and the events it handles.</summary>
    internal sealed class EngineScriptAccess : IScriptRuntimeAccess
    {
        public EngineScriptAccess(SchedulerScript script) { }

        public RuntimeState ScriptState => null;

        public IEnumerable<int> EventTypesHandledIn(int lslState) => Array.Empty<int>();
    }
}
