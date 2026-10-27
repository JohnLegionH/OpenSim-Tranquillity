/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Phlox.ScriptEngine;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Told by the script functions when a script's state has changed outside an event of its own (a permission granted or
    /// ended, a listen switched off), so the state can be saved. The engine does not save script state yet; the count is what a
    /// save will read.
    /// </summary>
    internal sealed class EngineStateTracker : IScriptStateTracker
    {
        /// <summary>How many times a script has reported a change.</summary>
        public long Changes => System.Threading.Interlocked.Read(ref m_changes);

        private long m_changes;

        public void ScriptChanged(IScriptRuntimeAccess script) => System.Threading.Interlocked.Increment(ref m_changes);
    }
}
