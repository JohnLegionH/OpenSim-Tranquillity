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
    /// <summary>
    /// Runs scripts on worker threads. Each worker owns one VM, its shared images and its scripts, and is the only
    /// thread that touches them. A worker keeps a round-robin ring of runnable scripts, gives each a run window of
    /// the configured quanta per turn, parks sleeping and waiting scripts until due, and wakes timers.
    /// </summary>
    internal sealed class SLuaScheduler : IDisposable
    {
        private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

        private readonly SchedulerOptions m_options;
        private readonly ScriptWorker[] m_workers;
        private readonly object m_lock = new object();
        private long m_nextId;
        private bool m_disposed;

        public SLuaScheduler(SchedulerOptions options)
        {
            if (options.VmFactory == null)
                throw new ArgumentException("a VM factory is required", nameof(options));
            m_options = options;
            m_workers = new ScriptWorker[Math.Max(1, options.WorkerThreads)];
            for (int i = 0; i < m_workers.Length; i++)
                m_workers[i] = new ScriptWorker(i, options);
        }

        public int WorkerCount => m_workers.Length;

        /// <summary>Makes a script from a compiled asset on the least-loaded worker. Returns at once; the script is <see cref="ScriptRunState.Created"/> when its worker has made it.</summary>
        public SchedulerScript Add(byte[] asset, ScriptLanguage language)
        {
            SchedulerScript script;
            ScriptWorker worker;
            lock (m_lock)
            {
                if (m_disposed)
                    throw new ObjectDisposedException(nameof(SLuaScheduler));
                worker = null;
                foreach (ScriptWorker w in m_workers)
                    if (!w.Failed && (worker == null || w.ScriptCount < worker.ScriptCount))
                        worker = w;
                if (worker == null)
                    throw new InvalidOperationException("no script worker is running");
                worker.ReserveScript();
                script = new SchedulerScript(++m_nextId, worker.Index, asset, language) { Worker = worker };
            }
            worker.CountLoading();
            worker.Enqueue(() => worker.Make(script));
            return script;
        }

        /// <summary>Starts a made script: its main function (SLua) or first state (LSL) becomes runnable.</summary>
        public void Start(SchedulerScript script) => script.Worker.Enqueue(() => script.Worker.StartScript(script));

        /// <summary>Queues an event for the script; dropped and counted when its queue is full.</summary>
        public void Post(SchedulerScript script, string eventName, params Value[] args)
        {
            int index = Host.EventIndex(eventName);
            script.Worker.Enqueue(() => script.Worker.PostEvent(script, index, args));
        }

        /// <summary>Queues an event with the detected objects its handler reads through the Detected functions.</summary>
        public void Post(SchedulerScript script, string eventName, Value[] args, DetectedRecord[] detected)
        {
            int index = Host.EventIndex(eventName);
            script.Worker.Enqueue(() => script.Worker.PostEvent(script, index, args, detected));
        }

        /// <summary>Sets the Running flag. Off: the script is not run, its events are dropped and its timer is held. On: it continues where it stopped; a script stopped by a fault starts over.</summary>
        public void SetRunning(SchedulerScript script, bool running)
        {
            script.Running = running; // visible at once; the worker applies it
            script.Worker.Enqueue(() => script.Worker.ApplyRunning(script, running));
        }

        /// <summary>Holds the script without dropping its events (the console's suspend).</summary>
        public void Suspend(SchedulerScript script)
        {
            script.Suspended = true;
            script.Worker.Enqueue(() => script.Worker.ApplySuspended(script, true));
        }

        /// <summary>Holds the script; with <paramref name="dropEvents"/> the events that reach it meanwhile are dropped, not queued.</summary>
        public void Suspend(SchedulerScript script, bool dropEvents)
        {
            script.DropsEventsWhileSuspended = dropEvents;
            Suspend(script);
        }

        /// <summary>Lets a suspended script go on.</summary>
        public void Resume(SchedulerScript script)
        {
            script.Suspended = false;
            script.Worker.Enqueue(() => script.Worker.ApplySuspended(script, false));
        }

        /// <summary>Starts the script over from a fresh script object: events, timer, sleep and a pending call are dropped, and a fault or kill is cleared.</summary>
        public void Reset(SchedulerScript script) => script.Worker.Enqueue(() => script.Worker.ResetScript(script));

        /// <summary>Frees the script's native objects on its worker and forgets it.</summary>
        public void Remove(SchedulerScript script) => script.Worker.Enqueue(() => script.Worker.RemoveScript(script));

        /// <summary>Delivers the result of a pausing call; the script runs again.</summary>
        public void CompleteWait(SchedulerScript script, params Value[] result) =>
            script.Worker.Enqueue(() => script.Worker.CompleteWait(script, result));

        /// <summary>
        /// Does the pending work of every worker once, as of <paramref name="now"/>, on the calling thread. For a
        /// scheduler built with RunThreads off. True when some worker still has runnable scripts.
        /// </summary>
        public bool DoWork(double now)
        {
            if (m_options.RunThreads)
                throw new InvalidOperationException("this scheduler runs its own threads");
            bool more = false;
            foreach (ScriptWorker w in m_workers)
                more |= w.DoWork(now);
            return more;
        }

        public SchedulerStats GetStats()
        {
            var workers = new WorkerStats[m_workers.Length];
            for (int i = 0; i < workers.Length; i++)
                workers[i] = m_workers[i].GetStats();
            return new SchedulerStats { Workers = workers };
        }

        /// <summary>Stops every worker, waiting up to five seconds for each, and frees the scripts, images and VMs.</summary>
        public void Dispose()
        {
            lock (m_lock)
            {
                if (m_disposed)
                    return;
                m_disposed = true;
            }
            foreach (ScriptWorker w in m_workers)
                w.Stop(StopWait);
        }
    }
}
