/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Compiles the script API's code once per process, on a background thread, so the first ll call of a script does not pay
    /// for it (a first call can overrun the script's run window, which the runtime punishes with a sleep).
    /// </summary>
    internal static class ApiWarmUp
    {
        /// <summary>What the warm-up does; tests replace it. It gets the engine's log.</summary>
        internal static Action<IEngineLog> Work = DefaultWork;

        /// <summary>Compiles the script API's methods ahead of their first call.</summary>
        internal static void DefaultWork(IEngineLog log)
        {
        }

        /// <summary>How many times the warm-up has been started in this process.</summary>
        internal static int Runs => 0;

        /// <summary>How many methods the default work compiled.</summary>
        internal static int Prepared => 0;

        /// <summary>True once the warm-up has run to its end (or failed).</summary>
        internal static bool IsDone => true;

        /// <summary>Starts the warm-up if this process has not started it yet.</summary>
        internal static void Start(IEngineLog log)
        {
        }

        /// <summary>Blocks while a warm-up that has started has not ended.</summary>
        internal static void WaitDone()
        {
        }

        /// <summary>Puts the process-wide state back to "never started"; for tests.</summary>
        internal static void ResetForTest()
        {
        }
    }
}
