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
    /// <summary>The os, iw and bot functions bound to ILSLSystemAPI. The table (<c>All</c>) and the host list are generated; see tools/slua-bindings.</summary>
    internal static partial class OsBindings
    {
        /// <summary>The functions' signatures as the library's LSL compiler is told them, one line per signature.</summary>
        internal static string HostList => string.Empty;

        /// <summary>Gives the library the host list, once, before the first compile.</summary>
        internal static void ApplyHostList()
        {
        }

        /// <summary>The signature of <paramref name="function"/> that a call with these arguments reaches.</summary>
        internal static BoundFunction Pick(OsFunction function, ReadOnlySpan<Value> args) => null;

        /// <summary>Registers every os, iw and bot function in the VM's ossl, iw and bot tables.</summary>
        public static void Register(Vm vm, Func<SchedulerScript, ScriptContext> contextOf, bool serviceLane = false, bool unavailable = false)
        {
        }
    }
}
