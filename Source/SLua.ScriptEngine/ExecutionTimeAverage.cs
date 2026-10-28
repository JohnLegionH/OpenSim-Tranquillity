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
    /// A script's recent execution time per run window, in milliseconds, as the viewer's Top Scripts window shows it: a decaying
    /// average that keeps nine tenths of the old figure and adds one tenth of each new window, so it follows what the script did
    /// lately without a list of windows. The other script engines of the region keep the same figure the same way.
    /// </summary>
    internal sealed class ExecutionTimeAverage
    {
        private long m_bits;

        /// <summary>The figure in milliseconds; 0 until a window has been added. May be read from any thread.</summary>
        public double Milliseconds => BitConverter.Int64BitsToDouble(Interlocked.Read(ref m_bits));

        /// <summary>Adds one run window. Called by the one worker thread that runs the script.</summary>
        public void Add(double windowMilliseconds)
        {
            double next = Milliseconds * 0.9 + windowMilliseconds * 0.1;
            Interlocked.Exchange(ref m_bits, BitConverter.DoubleToInt64Bits(next));
        }
    }
}
