/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using Phlox.ScriptEngine;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// What a script's function calls are made through: the script, and the script functions as ILSLSystemAPI, the
    /// interface Phlox's LSLSystemAPI implements. A generated trampoline for llSay calls <c>context.Api.llSay(...)</c>
    /// and so reaches the implementation Phlox uses. The context holds the interface only, never Phlox's engine or its
    /// interpreter.
    /// </summary>
    internal sealed class ScriptContext
    {
        public ScriptContext(SchedulerScript script, ILSLSystemAPI api, UUID itemId = default, ServiceLane lane = null)
        {
            Script = script;
            Api = api;
            ItemId = itemId;
            Lane = lane;
        }

        /// <summary>The script item the API serves. The API's own delay and result calls are recognised by it (Phlox: SyscallContext.ItemId).</summary>
        public UUID ItemId { get; }

        /// <summary>Where the long-running and service calls of the script run; null runs them on the script worker.</summary>
        public ServiceLane Lane { get; }

        public SchedulerScript Script { get; }

        public ILSLSystemAPI Api { get; }
    }
}
