/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using Luau.ScriptEngine.Bindings;

namespace Luau.ScriptEngine
{
    /// <summary>
    /// Runs scripts on worker threads. Each worker owns one VM, its shared images and its scripts, and is the only
    /// thread that touches them. A worker keeps a round-robin ring of runnable scripts, gives each a run window of
    /// the configured quanta per turn, parks sleeping and waiting scripts until due, and wakes timers.
    /// </summary>
    internal sealed class LuauScheduler : IDisposable
    {
        public LuauScheduler(SchedulerOptions options) => throw new NotImplementedException();

        public int WorkerCount => throw new NotImplementedException();

        /// <summary>Makes a script from a compiled asset on the least-loaded worker. Returns at once; the script is <see cref="ScriptRunState.Created"/> when its worker has made it.</summary>
        public SchedulerScript Add(byte[] asset, ScriptLanguage language) => throw new NotImplementedException();

        /// <summary>Starts a made script: its main function (SLua) or first state (LSL) becomes runnable.</summary>
        public void Start(SchedulerScript script) => throw new NotImplementedException();

        /// <summary>Queues an event for the script; dropped and counted when its queue is full.</summary>
        public void Post(SchedulerScript script, string eventName, params Value[] args) => throw new NotImplementedException();

        /// <summary>Frees the script's native objects on its worker and forgets it.</summary>
        public void Remove(SchedulerScript script) => throw new NotImplementedException();

        /// <summary>Delivers the result of a pausing call; the script runs again.</summary>
        public void CompleteWait(SchedulerScript script, params Value[] result) => throw new NotImplementedException();

        /// <summary>
        /// Does the pending work of every worker once, as of <paramref name="now"/>, on the calling thread. For a
        /// scheduler built with RunThreads off. True when some worker still has runnable scripts.
        /// </summary>
        public bool DoWork(double now) => throw new NotImplementedException();

        public SchedulerStats GetStats() => throw new NotImplementedException();

        public void Dispose() => throw new NotImplementedException();
    }
}
