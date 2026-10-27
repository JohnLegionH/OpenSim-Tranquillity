/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using Phlox.ScriptEngine;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The script as LSLSystemAPI sees it, for a script the engine runs: a runtime state of its own for the data the API
    /// keeps (attributes, enable flags, experience grant), and the events the script handles, read from the handler set the
    /// runtime reports.
    /// </summary>
    internal sealed class EngineScriptAccess : IScriptRuntimeAccess
    {
        private static readonly object s_lock = new object();
        private static (int Event, ulong Bit)[] s_events;

        private readonly SchedulerScript m_script;

        public EngineScriptAccess(SchedulerScript script)
        {
            m_script = script;
            ScriptState = new RuntimeState();
        }

        public RuntimeState ScriptState { get; }

        /// <summary>The Phlox event numbers of the events the script handles; an LSL script's handler set is per state.</summary>
        public IEnumerable<int> EventTypesHandledIn(int lslState)
        {
            ulong[] perState = m_script.StateMasks;
            ulong mask = perState != null && lslState >= 0 && lslState < perState.Length ? perState[lslState] : m_script.HandlerMask;
            if (mask == 0)
                yield break;
            foreach ((int evt, ulong bit) in EventBits())
                if ((mask & bit) != 0)
                    yield return evt;
        }

        /// <summary>Each Phlox event with the runtime's bit for the event of the same name (bit n - 1 for event number n).</summary>
        private static (int Event, ulong Bit)[] EventBits()
        {
            lock (s_lock)
            {
                if (s_events != null)
                    return s_events;
                var list = new List<(int, ulong)>();
                foreach (SupportedEventList.Events e in Enum.GetValues(typeof(SupportedEventList.Events)))
                {
                    int index;
                    try { index = Host.EventIndex(e.ToString().ToLowerInvariant()); }
                    catch (SluaException) { continue; }
                    if (index >= 1 && index <= 64)
                        list.Add(((int)e, 1UL << (index - 1)));
                }
                return s_events = list.ToArray();
            }
        }
    }
}
