/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace SLua.ScriptEngine
{
    /// <summary>
    /// A script's recent execution time per run window, in milliseconds, as the viewer's Top Scripts window shows it: a decaying
    /// average that keeps nine tenths of the old figure and adds one tenth of each new window, so it follows what the script did
    /// lately without a list of windows. The other script engines of the region keep the same figure the same way.
    /// </summary>
    internal sealed class ExecutionTimeAverage
    {
        /// <summary>The figure in milliseconds; 0 until a window has been added.</summary>
        public double Milliseconds => 0;

        public void Add(double windowMilliseconds)
        {
        }
    }
}
