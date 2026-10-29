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

        /// <summary>Holds the script; with <paramref name="dropListens"/> a listen that reaches it meanwhile is dropped, as Phlox drops it for a script held only by its object crossing.</summary>
        public void Suspend(SchedulerScript script, bool dropEvents, bool dropListens)
        {
            script.DropsListensWhileSuspended = dropListens;
            Suspend(script, dropEvents);
        }

        /// <summary>Waived overruns of all scripts by the host function the step called last, for the status command.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, long> WaivedOverrunsByFunction()
        {
            var total = new System.Collections.Generic.Dictionary<string, long>();
            foreach (ScriptWorker w in m_workers)
                foreach (var kv in w.WaivedByFunction)
                    total[kv.Key] = total.TryGetValue(kv.Key, out long n) ? n + kv.Value : kv.Value;
            return total;
        }

        /// <summary>Lets a suspended script go on.</summary>
        public void Resume(SchedulerScript script)
        {
            script.Suspended = false;
            script.Worker.Enqueue(() => script.Worker.ApplySuspended(script, false));
        }

        /// <summary>Starts the script over from a fresh script object: events, timer, sleep and a pending call are dropped, and a fault or kill is cleared.</summary>
        /// <summary>llMinEventDelay for a script from a thread that is not its worker's: the floor is set on the worker.</summary>
        public void SetMinEventDelay(SchedulerScript script, double seconds) => script.Worker.Enqueue(() => script.Worker.SetMinEventDelay(script, seconds));

        public void Reset(SchedulerScript script) => script.Worker.Enqueue(() => script.Worker.ResetScript(script));

        /// <summary>Tries a set-aside state again on the script's worker; <paramref name="done"/> gets the outcome.</summary>
        internal void RetryState(SchedulerScript script, ScriptStateRecord record, Action<RetryOutcome> done, Action beforeAdopt = null) => done(new RetryOutcome(false, ""));

        /// <summary>Frees the script's native objects on its worker and forgets it.</summary>
        public void Remove(SchedulerScript script) => script.Worker.Enqueue(() => script.Worker.RemoveScript(script));

        /// <summary>Delivers the result of a pausing call; the script runs again.</summary>
        public void CompleteWait(SchedulerScript script, params Value[] result) =>
            script.Worker.Enqueue(() => script.Worker.CompleteWait(script, result));

        /// <summary>Delivers the answer of a call on the service lane: only if the script is still waiting for that call.</summary>
        internal void CompleteWait(SchedulerScript script, int seq, Value[] result, double delaySeconds, Exception fault) =>
            script.Worker.Enqueue(() => script.Worker.CompleteWait(script, seq, result, delaySeconds, fault));

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

        /// <summary>
        /// Captures the scripts' records for the object that carries them, all in one pass: each worker takes its own scripts between steps
        /// and the caller waits up to <paramref name="timeoutMs"/> for all of them. A script that could not be captured now has no entry.
        /// With no worker threads (tests) the capture is done on the calling thread.
        /// </summary>
        internal System.Collections.Generic.Dictionary<long, byte[]> CaptureForCarry(System.Collections.Generic.IReadOnlyList<SchedulerScript> scripts, int timeoutMs, out bool timedOut)
        {
            var results = new System.Collections.Concurrent.ConcurrentDictionary<long, byte[]>();
            timedOut = false;
            var byWorker = new System.Collections.Generic.Dictionary<ScriptWorker, System.Collections.Generic.List<SchedulerScript>>();
            foreach (SchedulerScript s in scripts)
            {
                if (s.Worker == null)
                    continue;
                if (!byWorker.TryGetValue(s.Worker, out var list))
                    byWorker[s.Worker] = list = new System.Collections.Generic.List<SchedulerScript>();
                list.Add(s);
            }
            var done = new System.Threading.CountdownEvent(Math.Max(1, byWorker.Count));
            if (byWorker.Count == 0)
                done.Signal();
            foreach (var pair in byWorker)
            {
                ScriptWorker worker = pair.Key;
                var group = pair.Value;
                Action capture = () =>
                {
                    try
                    {
                        foreach (SchedulerScript s in group)
                        {
                            byte[] record = worker.CaptureForCarry(s);
                            if (record != null)
                                results[s.Id] = record;
                        }
                    }
                    finally { done.Signal(); }
                };
                if (m_options.RunThreads)
                    worker.Enqueue(capture);
                else
                    capture();
            }
            if (!done.Wait(timeoutMs))
                timedOut = true;
            return new System.Collections.Generic.Dictionary<long, byte[]>(results);
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
