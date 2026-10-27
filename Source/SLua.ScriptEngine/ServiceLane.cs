/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using InWorldz.Phlox.Glue;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>One call handed to the service lane.</summary>
    internal sealed class LaneCall
    {
        public SchedulerScript Script;
        public SyscallContext Context;
        public string Function;

        /// <summary>The call itself, run on a lane thread inside <see cref="Context"/>; its values are what the script receives.</summary>
        public Func<Value[]> Body;

        /// <summary>What the script receives if the deadline passes first.</summary>
        public Value[] Failure;
    }

    /// <summary>
    /// The place the long-running and service-reaching calls of scripts run, off the script workers, so a slow service stalls
    /// only the script that asked. It follows Phlox's service lane (PhloxExecutionScheduler, "The service lane"): dedicated
    /// threads, at most <c>threads</c> of them, started on demand and gone after <c>idleMs</c> without work, so an idle region
    /// holds none. A call that has not answered by its deadline answers with the function's failure value, and the late answer
    /// is dropped (RunServiceCall, ProcessServiceDeadlines). The answer of a call goes to the scheduler, which hands it to the
    /// script only if the script still waits for that call (<see cref="SLuaScheduler.CompleteWait(SchedulerScript, int, Value[], double, Exception)"/>).
    /// </summary>
    internal sealed class ServiceLane : IDisposable
    {
        private sealed class Entry
        {
            public LaneCall Call;
            public Timer Deadline;
            private int m_done;

            /// <summary>Exactly one of the lane thread and the deadline wins; the loser's answer is dropped.</summary>
            public bool TryFinish() => Interlocked.Exchange(ref m_done, 1) == 0;

            public bool IsFinished => Volatile.Read(ref m_done) != 0;
        }

        private readonly SLuaScheduler m_scheduler;
        private readonly bool m_defers;
        private readonly int m_maxThreads;
        private readonly int m_timeoutMs;
        private readonly int m_idleMs;
        private readonly ConcurrentQueue<Entry> m_queue = new ConcurrentQueue<Entry>();
        private readonly SemaphoreSlim m_signal = new SemaphoreSlim(0);
        private readonly ConcurrentDictionary<Entry, byte> m_outstanding = new ConcurrentDictionary<Entry, byte>();
        private int m_threadCount;
        private int m_threadsIdle;
        private int m_threadSeq;
        private volatile bool m_disposed;

        /// <param name="scheduler">Takes the answers.</param>
        /// <param name="deferral">The operator's setting: "auto", "always" or "never". Only "never" is the lane's business here: service calls then stay on the worker. (Always is the API's: its NeedsService says yes.)</param>
        /// <param name="threads">Most lane threads at once (Phlox: ServiceCallThreads).</param>
        /// <param name="timeoutMs">How long a call may take before its script is answered with the failure value (Phlox: ServiceCallTimeoutMs).</param>
        /// <param name="idleMs">How long a thread waits for work before it goes (Phlox: ServiceThreadIdleMs).</param>
        public ServiceLane(SLuaScheduler scheduler, string deferral = "auto", int threads = 4, int timeoutMs = 35000, int idleMs = 30000)
        {
            m_scheduler = scheduler;
            m_defers = deferral != "never";
            m_maxThreads = Math.Max(1, threads);
            m_timeoutMs = Math.Max(1, timeoutMs);
            m_idleMs = Math.Max(1, idleMs);
        }

        /// <summary>False when the operator turned deferral off ("never"): service calls then run on the script worker.</summary>
        public bool Defers => m_defers;

        /// <summary>The most threads the lane runs at once.</summary>
        public int MaxThreads => m_maxThreads;

        /// <summary>How long a call may take before its script gets the failure value, in milliseconds.</summary>
        public int TimeoutMs => m_timeoutMs;

        /// <summary>How many calls the lane has been given.</summary>
        public long Submitted => 0;

        /// <summary>Lane threads alive right now.</summary>
        public int ThreadCount => Volatile.Read(ref m_threadCount);

        public void Submit(LaneCall call)
        {
            var entry = new Entry { Call = call };
            m_outstanding[entry] = 0;
            entry.Deadline = new Timer(Expire, entry, m_timeoutMs, Timeout.Infinite);
            m_queue.Enqueue(entry);
            EnsureThread();
            m_signal.Release();
        }

        private void EnsureThread()
        {
            if (Volatile.Read(ref m_threadsIdle) > 0) return;
            while (true)
            {
                int current = Volatile.Read(ref m_threadCount);
                if (current >= m_maxThreads) return;
                if (Interlocked.CompareExchange(ref m_threadCount, current + 1, current) != current) continue;
                var thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = EngineIdentity.EngineName + " service #" + Interlocked.Increment(ref m_threadSeq),
                };
                thread.Start();
                return;
            }
        }

        private void Loop()
        {
            try
            {
                while (!m_disposed)
                {
                    Interlocked.Increment(ref m_threadsIdle);
                    bool signalled;
                    try { signalled = m_signal.Wait(m_idleMs); }
                    finally { Interlocked.Decrement(ref m_threadsIdle); }
                    if (!signalled)
                    {
                        if (m_queue.IsEmpty) break; // idle long enough: give the thread back
                        continue;
                    }
                    if (m_queue.TryDequeue(out Entry entry))
                        Run(entry);
                }
            }
            finally
            {
                Interlocked.Decrement(ref m_threadCount);
                // A call can land between the empty check and the decrement; never strand it.
                if (!m_queue.IsEmpty && !m_disposed) EnsureThread();
            }
        }

        private void Run(Entry entry)
        {
            // The deadline already answered the script: the call is not performed after the fact.
            if (entry.IsFinished) return;

            LaneCall call = entry.Call;
            Value[] values = null;
            Exception fault = null;
            call.Context.Enter();
            try { values = call.Body(); }
            catch (Exception e) { fault = e; }
            finally { SyscallContext.Exit(); }

            if (!entry.TryFinish()) return; // answered after its deadline: the late result is dropped
            Done(entry);
            Answer(call, fault == null ? values : null, call.Context.DelayMs / 1000.0, fault);
        }

        private void Done(Entry entry)
        {
            entry.Deadline?.Dispose();
            m_outstanding.TryRemove(entry, out _);
        }

        /// <summary>The scheduler may be gone (the region is closing); an answer to nobody is not an error.</summary>
        private void Answer(LaneCall call, Value[] values, double delaySeconds, Exception fault)
        {
            if (m_disposed) return;
            try { m_scheduler.CompleteWait(call.Script, call.Context.Seq, values, delaySeconds, fault); }
            catch (ObjectDisposedException) { }
        }

        private void Expire(object state)
        {
            var entry = (Entry)state;
            if (!entry.TryFinish()) return;
            Done(entry);
            Answer(entry.Call, entry.Call.Failure, 0, null);
        }

        public void Dispose()
        {
            m_disposed = true;
            foreach (Entry entry in m_outstanding.Keys)
            {
                entry.TryFinish();
                Done(entry);
            }
            m_signal.Release(m_maxThreads);
        }
    }
}
