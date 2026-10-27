/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using InWorldz.Phlox.Glue;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>The ll functions bound to ILSLSystemAPI. The table itself (<c>All</c>) is generated; see tools/slua-bindings.</summary>
    internal static partial class LlBindings
    {
        /// <summary>Registers every bound function in the VM's "ll" table. With <paramref name="serviceLane"/>, the async and service functions run on the context's service lane.</summary>
        public static void Register(Vm vm, Func<SchedulerScript, ScriptContext> contextOf, bool serviceLane = false)
        {
            foreach (BoundFunction f in All)
            {
                BoundFunction function = f;
                vm.Register("ll", function.Name, (tag, args) =>
                {
                    ScriptContext context = contextOf(SchedulerScript.Current);

                    // The call runs inside a SyscallContext for its script, which is where LSLSystemAPI puts the delay it
                    // asks for (its ScriptSleep adds to the context of the call it is running in; Phlox does the same for a
                    // call that runs off its script's thread). The API's delay is then the runtime's: the script is held for
                    // it. A call that returns no values also ends the script's turn; one that returns values keeps them and
                    // the delay takes effect at the script's next interrupt, and two such delays add up.
                    var call = new SyscallContext(context.ItemId, SyscallContext.NextSeq());
                    Value[] result;
                    call.Enter();
                    try { result = function.Call(context, args); }
                    finally { SyscallContext.Exit(); }

                    if (call.DelayMs > 0) context.Script.AddSleep(call.DelayMs / 1000.0);
                    return result;
                });
            }
        }
    }
}
