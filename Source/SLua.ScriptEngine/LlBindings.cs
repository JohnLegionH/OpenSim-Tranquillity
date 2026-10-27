/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>The ll functions bound to ILSLSystemAPI. The table itself (<c>All</c>) is generated; see tools/slua-bindings.</summary>
    internal static partial class LlBindings
    {
        /// <summary>Registers every bound function in the VM's "ll" table.</summary>
        public static void Register(Vm vm, Func<SchedulerScript, ScriptContext> contextOf)
        {
            foreach (BoundFunction f in All)
            {
                BoundFunction function = f;
                vm.Register("ll", function.Name, (tag, args) => function.Call(contextOf(SchedulerScript.Current), args));
            }
        }
    }
}
