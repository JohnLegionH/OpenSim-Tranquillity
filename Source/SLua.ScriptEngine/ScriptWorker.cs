/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// One worker: a thread, the VM it makes, the shared images in that VM and the scripts made from them. Only the
    /// worker's thread touches those (the library allows one thread at a time in a VM); everything else reaches the
    /// worker by posting a command. The work is done by <see cref="DoWork"/>, which takes the time as an argument so a
    /// test can drive it, and which a thread runs in a loop.
    /// </summary>
    internal sealed class ScriptWorker
    {
        /// <summary>Stack for a worker thread: native handlers run on it.</summary>
        private const int StackBytes = 8 * 1024 * 1024;

        private static readonly int StateCount = Enum.GetValues(typeof(ScriptRunState)).Length;
        private const double SleepTolerance = 0.0005;

        // A plain step the runtime refuses is tried again at once this many times; after that the script waits,
        // starting at the first back-off and doubling up to the longest.
        private const int RefusedFreeRetries = 2;
        private const double RefusedBackoffFirst = 0.05;
        private const double RefusedBackoffLongest = 1.0;
        private static int s_timerEvent = -1;

        private readonly int m_index;
        private readonly SchedulerOptions m_options;
        private readonly IEngineLog m_log;
        private readonly ConcurrentQueue<Action> m_commands = new ConcurrentQueue<Action>();
        private readonly ManualResetEventSlim m_signal = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim m_stopSignal = new ManualResetEventSlim(false);
        private readonly Thread m_thread;
        private volatile bool m_stop;

        // Worker thread only.
        private Vm m_vm;
        private SharedImages m_images;
        private readonly Dictionary<long, SchedulerScript> m_scripts = new Dictionary<long, SchedulerScript>();
        private readonly Queue<SchedulerScript> m_ring = new Queue<SchedulerScript>();

        // Scripts an event has woken, run ahead of the ring in the next pass. A script gets this place at most once per
        // ring cycle, so a flood of events cannot keep jumping the queue. A cycle is as many turns as the ring held when it began.
        private readonly Queue<SchedulerScript> m_front = new Queue<SchedulerScript>();
        private int m_cycleLeft;
        private readonly PriorityQueue<(SchedulerScript Script, long Gen), double> m_sleepers = new PriorityQueue<(SchedulerScript, long), double>();
        private readonly PriorityQueue<(SchedulerScript Script, long Gen), double> m_timers = new PriorityQueue<(SchedulerScript, long), double>();

        // Scripts holding an event back until their llMinEventDelay floor has passed.
        private readonly PriorityQueue<(SchedulerScript Script, long Gen), double> m_floors = new PriorityQueue<(SchedulerScript, long), double>();
        private double m_now;

        // Read from any thread.
        private readonly int[] m_stateCounts = new int[StateCount];
        private int m_scriptCount;
        private long m_passes;
        private long m_steps;
        private long m_wakeups;
        private long m_budgetCutoffs;
        private long m_kills;
        private long m_punishments;
        private long m_refusedCaps;
        private long m_crashes;
        private volatile bool m_failed;
        private long m_droppedWhileStopped;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> m_waivedByFunction = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        /// <summary>Waived overruns of this worker, by the host function the step called last.</summary>
        internal System.Collections.Generic.IReadOnlyDictionary<string, long> WaivedByFunction => new System.Collections.Generic.Dictionary<string, long>(m_waivedByFunction);
        private long m_droppedUnhandled;
        private long m_droppedFull;
        private long m_cycles;

        internal ScriptWorker(int index, SchedulerOptions options)
        {
            m_index = index;
            m_options = options;
            m_log = options.Log;
            if (options.RunThreads)
            {
                m_thread = new Thread(Loop, StackBytes)
                {
                    IsBackground = true,
                    Name = EngineIdentity.EngineName + " worker " + index,
                    Priority = options.ThreadPriority,
                };
                m_thread.Start();
            }
        }

        public int Index => m_index;

        /// <summary>True once the worker has stopped for good after too many failed passes; it takes no more scripts.</summary>
        public bool Failed => m_failed;

        /// <summary>Scripts assigned to this worker and not removed; the scheduler picks the worker with the fewest.</summary>
        public int ScriptCount => Volatile.Read(ref m_scriptCount);

        internal int ReserveScript() => Interlocked.Increment(ref m_scriptCount);

        internal void CountLoading() => Interlocked.Increment(ref m_stateCounts[(int)ScriptRunState.Loading]);

        public WorkerStats GetStats() => new WorkerStats(
            ScriptCount,
            Volatile.Read(ref m_stateCounts[(int)ScriptRunState.Runnable]),
            Volatile.Read(ref m_stateCounts[(int)ScriptRunState.Sleeping]),
            Volatile.Read(ref m_stateCounts[(int)ScriptRunState.Waiting]),
            Interlocked.Read(ref m_passes),
            Interlocked.Read(ref m_steps),
            Interlocked.Read(ref m_wakeups),
            Interlocked.Read(ref m_budgetCutoffs),
            Volatile.Read(ref m_stateCounts[(int)ScriptRunState.Stopped]),
            Volatile.Read(ref m_stateCounts[(int)ScriptRunState.Suspended]),
            Volatile.Read(ref m_stateCounts[(int)ScriptRunState.Faulted]),
            Interlocked.Read(ref m_kills),
            Interlocked.Read(ref m_punishments),
            Interlocked.Read(ref m_refusedCaps),
            Interlocked.Read(ref m_crashes),
            m_failed,
            Interlocked.Read(ref m_droppedWhileStopped),
            Interlocked.Read(ref m_droppedUnhandled),
            Interlocked.Read(ref m_droppedFull),
            Interlocked.Read(ref m_cycles));

        /// <summary>Hands the worker something to do on its own thread, and wakes it.</summary>
        internal void Enqueue(Action command)
        {
            m_commands.Enqueue(command);
            m_signal.Set();
        }

        // ---- the loop ----

        /// <summary>
        /// Runs passes. A pass that throws is a crash: the worker waits the restart delay and carries on, and after
        /// the allowed number of crashes it stops for good and reports once. This is the rule of Phlox's master
        /// scheduler (a restart after one second, at most ten times).
        /// </summary>
        private void Loop()
        {
            int crashes = 0;
            int allowed = Math.Max(1, m_options.MaxWorkerCrashes);
            try
            {
                while (!m_stop)
                {
                    m_signal.Reset();
                    bool more = false;
                    try
                    {
                        m_options.BeforePass?.Invoke(m_index, Interlocked.Read(ref m_passes) + 1);
                        more = DoWork(m_options.Clock.Now);
                    }
                    catch (Exception e)
                    {
                        crashes++;
                        Interlocked.Increment(ref m_crashes);
                        if (crashes >= allowed)
                        {
                            m_failed = true;
                            m_log?.Error($"worker {m_index} failed {crashes} times and is stopped; its scripts no longer run: {e}");
                            try { m_options.WorkerStopped?.Invoke(m_index, crashes, e.Message); }
                            catch (Exception callback) { m_log?.Error("the worker-stopped callback failed: " + callback.Message); }
                            break;
                        }
                        m_log?.Error($"worker {m_index} failed (failure {crashes} of {allowed}), carrying on in {m_options.WorkerRestartDelayMs} ms: {e}");
                        m_stopSignal.Wait(Math.Max(0, m_options.WorkerRestartDelayMs));
                        continue;
                    }
                    if (more)
                        continue;
                    Interlocked.Increment(ref m_wakeups);
                    m_signal.Wait(NextWaitMs());
                }
            }
            finally
            {
                Cleanup();
            }
        }

        /// <summary>Milliseconds until the earliest sleeper or timer is due; infinite when there is none.</summary>
        private int NextWaitMs()
        {
            double due = double.PositiveInfinity;
            while (m_sleepers.TryPeek(out var e, out double d))
            {
                if (e.Script.SleepGen == e.Gen && e.Script.State == ScriptRunState.Sleeping) { due = d; break; }
                m_sleepers.Dequeue();
            }
            while (m_timers.TryPeek(out var e, out double d))
            {
                if (e.Script.TimerGen == e.Gen && e.Script.TimerInterval > 0 && e.Script.State != ScriptRunState.Removed && e.Script.State != ScriptRunState.Faulted)
                {
                    due = Math.Min(due, d);
                    break;
                }
                m_timers.Dequeue();
            }
            while (m_floors.TryPeek(out var e, out double d))
            {
                if (e.Script.FloorGen == e.Gen && e.Script.FloorHeld && e.Script.State != ScriptRunState.Removed && e.Script.State != ScriptRunState.Faulted)
                {
                    due = Math.Min(due, d);
                    break;
                }
                m_floors.Dequeue();
            }
            if (m_anyDirty && m_options.StateSink != null)
                due = Math.Min(due, m_nextSave);
            if (double.IsPositiveInfinity(due))
                return Timeout.Infinite;
            double ms = Math.Ceiling((due - m_options.Clock.Now) * 1000.0);
            return ms <= 0 ? 0 : (int)Math.Min(ms, int.MaxValue / 2);
        }

        /// <summary>Does the pending commands, wakes what is due, and gives each runnable script one turn. True while scripts are still runnable.</summary>
        internal bool DoWork(double now)
        {
            m_now = now;
            Interlocked.Increment(ref m_passes);
            DrainCommands();
            WakeDue(Now());
            SaveDueStates(Now());

            // A pass ends when every script that was runnable at its start has had a turn, or when the time budget
            // is spent. The scripts that did not get their turn are still at the front of the ring, ahead of the
            // ones that did, so the next pass carries on in the same order.
            long budget = (long)(m_options.PassBudgetMs / 1000.0 * Stopwatch.Frequency);
            long passStart = Stopwatch.GetTimestamp();
            int turns = m_front.Count + m_ring.Count;
            for (int i = 0; i < turns; i++)
            {
                if (m_cycleLeft <= 0)
                {
                    Interlocked.Increment(ref m_cycles);
                    m_cycleLeft = m_front.Count + m_ring.Count;
                }
                m_cycleLeft--;
                SchedulerScript s = m_front.Count > 0 ? m_front.Dequeue() : m_ring.Dequeue();
                s.InRing = false;
                if (s.State == ScriptRunState.Runnable)
                {
                    RunTurn(s);
                    Reschedule(s);
                }
                if (i + 1 < turns && Stopwatch.GetTimestamp() - passStart >= budget)
                {
                    Interlocked.Increment(ref m_budgetCutoffs);
                    break;
                }
            }
            return m_ring.Count > 0 || m_front.Count > 0;
        }

        private double Now() => m_options.RunThreads ? m_options.Clock.Now : m_now;

        private void DrainCommands()
        {
            while (m_commands.TryDequeue(out Action command))
            {
                try { command(); }
                catch (Exception e) { m_log?.Error("a scheduler command failed: " + e); }
            }
        }

        private void WakeDue(double now)
        {
            while (m_sleepers.TryPeek(out var e, out double due) && due <= now)
            {
                m_sleepers.Dequeue();
                if (e.Script.SleepGen != e.Gen || e.Script.State != ScriptRunState.Sleeping)
                    continue;
                e.Script.SleepUntil = 0;
                Reschedule(e.Script);
            }
            while (m_floors.TryPeek(out var e, out double due) && due <= now)
            {
                m_floors.Dequeue();
                if (e.Script.FloorGen != e.Gen || !e.Script.FloorHeld)
                    continue;
                e.Script.FloorHeld = false;
                Reschedule(e.Script);
            }
            while (m_timers.TryPeek(out var e, out double due) && due <= now)
            {
                m_timers.Dequeue();
                SchedulerScript s = e.Script;
                if (s.TimerGen != e.Gen || s.TimerInterval <= 0 || s.State == ScriptRunState.Removed || s.State == ScriptRunState.Faulted)
                    continue;
                FireTimer(s, now);
            }
        }

        // ---- commands, run on the worker's thread ----

        internal void Make(SchedulerScript s)
        {
            if (m_stop)
                return;
            try
            {
                MakeNative(s);
                m_scripts[s.Id] = s;
                SetState(s, ScriptRunState.Created);
            }
            catch (Exception e)
            {
                m_log?.Error($"script {s.Id} could not be made: {e.Message}");
                s.Lease?.Dispose();
                s.Lease = null;
                m_scripts[s.Id] = s;
                Fault(s, FaultKind.Unknown, "script could not be made: " + e.Message);
            }
        }

        /// <summary>Makes the script object from the compiled asset on this worker's VM, sharing the asset's image with the others.</summary>
        private void MakeNative(SchedulerScript s)
        {
            m_vm ??= m_options.VmFactory();
            m_images ??= new SharedImages(m_vm);
            s.Lease ??= m_images.Acquire(s.Asset);
            s.Native = s.Lease.NewScript(m_options.MemoryLimitBytes, s.Id);
        }

        internal void StartScript(SchedulerScript s)
        {
            if (s.Native == null || s.Started || s.State == ScriptRunState.Removed || s.State == ScriptRunState.Faulted)
                return;
            try
            {
                if (TryRestore(s))
                    return;
                s.Native.Start();
            }
            catch (Exception e)
            {
                Fault(s, FaultKind.Unknown, "script could not be started: " + e.Message);
                return;
            }
            s.Started = true;
            s.NeedsStep = true;
            MarkDirty(s);
            Reschedule(s);
        }

        /// <summary>The events a full queue still takes: losing one leaves a script half started or its timer dead (Halcyon, as Phlox's RuntimeState does).</summary>
        private static bool PassesFullQueue(int eventIndex) =>
            eventIndex == EventIndexOf("on_rez") || eventIndex == EventIndexOf("state_entry")
            || eventIndex == EventIndexOf("state_exit") || eventIndex == TimerEvent();

        private static readonly Dictionary<string, int> s_eventIndexes = new Dictionary<string, int>();

        private static int EventIndexOf(string name)
        {
            lock (s_eventIndexes)
            {
                if (!s_eventIndexes.TryGetValue(name, out int index))
                    s_eventIndexes[name] = index = Host.EventIndex(name);
                return index;
            }
        }

        private static ulong EventBit(int eventIndex) => eventIndex >= 1 && eventIndex <= 64 ? 1UL << (eventIndex - 1) : 0;

        /// <summary>
        /// True when the script has a handler for the event: an LSL script's current state, from the asset header; for
        /// SLua, unless the runtime has just said it has none (handlers change only while the script runs, so that
        /// finding stands until the script next runs something).
        /// </summary>
        private static bool Handles(SchedulerScript s, int eventIndex)
        {
            ulong bit = EventBit(eventIndex);
            if (bit == 0)
                return true;
            if (s.StateMasks != null)
                return s.CurrentState >= s.StateMasks.Length || (s.StateMasks[s.CurrentState] & bit) != 0;
            return (s.NotHandled & bit) == 0;
        }

        internal void PostEvent(SchedulerScript s, int eventIndex, Value[] args, DetectedRecord[] detected = null)
        {
            if (s.Native == null || s.State == ScriptRunState.Removed)
                return;
            if (!s.Running)
            {
                // Halcyon: killed and disabled scripts no longer respond to outside stimuli.
                s.CountStoppedDrop();
                Interlocked.Increment(ref m_droppedWhileStopped);
                return;
            }
            if (!Handles(s, eventIndex))
            {
                s.CountUnhandledDrop();
                Interlocked.Increment(ref m_droppedUnhandled);
                return;
            }
            if (s.Suspended && (s.DropsEventsWhileSuspended || (s.DropsListensWhileSuspended && eventIndex == ListenEvent())))
            {
                s.CountPausedDrop();
                return;
            }
            if (s.Events.Count >= m_options.MaxEventQueue && !PassesFullQueue(eventIndex))
            {
                s.CountDrop();
                Interlocked.Increment(ref m_droppedFull);
                return;
            }
            s.Events.Enqueue(new QueuedEvent(eventIndex, args, detected));
            MarkDirty(s);
            Reschedule(s, front: s.State == ScriptRunState.Idle);
        }

        internal void CompleteWait(SchedulerScript s, Value[] result)
        {
            if (!s.Waiting || s.State == ScriptRunState.Removed)
                return;
            s.WaitResult = result;
            s.Waiting = false;
            Reschedule(s);
        }

        /// <summary>
        /// The answer of a call on the service lane. Only the script still waiting for that call takes it: an answer for an
        /// earlier call (the script was reset, or the deadline already answered it and the script has moved on) is dropped,
        /// as Phlox drops a return whose sequence number is not the script's current one (ProcessSyscallReturns).
        /// </summary>
        internal void CompleteWait(SchedulerScript s, int seq, Value[] result, double delaySeconds, Exception fault)
        {
            if (!s.Waiting || s.State == ScriptRunState.Removed)
                return;
            if (seq >= 0 && seq != s.WaitSeq)
                return;
            s.WaitResult = result;
            s.WaitDelay = delaySeconds;
            s.WaitFault = fault;
            s.Waiting = false;
            Reschedule(s);
        }

        /// <summary>
        /// The Running flag changed (it is already set on the script). Off: queued events are dropped and the timer
        /// is held with the time it had left, which the start carries on with (Phlox: ProcessEnableDisable keeps
        /// TimerTimeLeft in m_StoppedTimerLeft, StartAfterStop resumes with it); the script keeps its place in its handler. On: it carries on; a script that was never started
        /// starts, and one stopped by a fault starts over.
        /// </summary>
        internal void ApplyRunning(SchedulerScript s, bool running)
        {
            if (s.State == ScriptRunState.Removed || s.State == ScriptRunState.Loading)
                return;
            MarkDirty(s);
            if (!running)
            {
                if (s.Events.Count > 0)
                {
                    s.CountStoppedDrop(s.Events.Count);
                    Interlocked.Add(ref m_droppedWhileStopped, s.Events.Count);
                }
                s.Events.Clear();
                s.TimerQueued = false;
                HoldTimer(s, keepTimeLeft: true); // Phlox keeps the time left across a stop, as across a pause
                Reschedule(s);
                return;
            }
            if (s.Killed || s.Native == null)
            {
                ResetScript(s);
                return;
            }
            if (!s.Started)
            {
                StartScript(s);
                return;
            }
            RearmTimer(s);
            Reschedule(s);
        }

        /// <summary>Suspend or resume (the flag is already set on the script). Events keep queueing while it is suspended.</summary>
        internal void ApplySuspended(SchedulerScript s, bool suspended)
        {
            if (s.State == ScriptRunState.Removed || s.State == ScriptRunState.Loading)
                return;
            if (suspended)
                HoldTimer(s, keepTimeLeft: true);
            else
                RearmTimer(s);
            Reschedule(s);
        }

        /// <summary>A timer that is held keeps its interval and fires no tick; <see cref="RearmTimer"/> counts it again from now.</summary>
        private void HoldTimer(SchedulerScript s, bool keepTimeLeft)
        {
            // A pause keeps the time the timer had left and the resume carries on with it, as Phlox does
            // (PhloxExecutionScheduler.PauseForParcel keeps it, ResumeForParcel restores it). A timer already held keeps what it kept.
            if (s.TimerArmed)
            {
                s.TimerHeldLeft = keepTimeLeft ? Math.Max(0, Math.Min(s.TimerDue - Now(), s.TimerInterval)) : -1;
                s.TimerArmed = false;
            }
            else if (!keepTimeLeft)
                s.TimerHeldLeft = -1;
            s.TimerGen++;
        }

        private void RearmTimer(SchedulerScript s)
        {
            if (s.TimerInterval > 0 && s.Running && !s.Suspended)
            {
                double left = s.TimerHeldLeft >= 0 ? s.TimerHeldLeft : s.TimerInterval;
                s.TimerHeldLeft = -1;
                ArmTimer(s, Now() + left);
            }
        }

        /// <summary>
        /// Starts the script over from a fresh script object: variables, events, timer, sleep, a pending call and any
        /// fault or kill are dropped. A script that was stopped by a fault runs again; one the Running flag stopped
        /// stays stopped until the flag is set. The llMinEventDelay floor stays, as in Phlox.
        /// </summary>
        internal void ResetScript(SchedulerScript s)
        {
            if (s.State == ScriptRunState.Removed || s.State == ScriptRunState.Loading)
                return;
            ClearRunState(s);
            try
            {
                try { s.Native?.Dispose(); }
                finally { s.Native = null; }
                MakeNative(s);
            }
            catch (Exception e)
            {
                m_log?.Error($"script {s.Id} could not be reset: {e.Message}");
                Fault(s, FaultKind.Unknown, "script could not be reset: " + e.Message);
                return;
            }
            m_scripts[s.Id] = s;
            SetState(s, ScriptRunState.Created);
            s.RestoreRecord = null;
            MarkDirty(s);
            if (s.Running)
                StartScript(s);
            else
                Reschedule(s);
            try { m_options.StateChanged?.Invoke(s); } // back in the first state
            catch (Exception e) { m_log?.Error("the state-change callback failed: " + e.Message); }
        }

        /// <summary>The scheduler's side of a script, put back to a new script's: events, timer, sleep, wait, fault and kill (not its native object).</summary>
        private void ClearRunState(SchedulerScript s)
        {
            s.Events.Clear();
            // The floor llMinEventDelay set and the time before which the next handler may not start survive the reset, as in Phlox
            // (RuntimeState.Reset does not touch MinEventDelayMs or NextEventAllowedOn); only the wait for the floor is dropped with the queue.
            s.FloorHeld = false;
            s.FloorGen++;
            s.CurrentDetected = null;
            s.CurrentState = 0;
            s.NotHandled = 0;
            s.TimerQueued = false;
            s.TimerInterval = 0;
            s.TimerArmed = false;
            s.TimerHeldLeft = -1;
            s.TimerGen++;
            s.SleepUntil = 0;
            s.SleepGen++;
            s.Waiting = false;
            s.WaitResult = null;
            s.WaitDelay = 0;
            s.WaitFault = null;
            s.AskedSeconds = 0;
            s.SetHandlers(0, false);
            s.Windows = null;
            s.SluaTimer = 0;
            s.SluaTimerUnconfirmed = false;
            s.NeedsStep = false;
            s.Started = false;
            s.ConsecutivePunishments = 0;
            s.ConsecutiveRefusals = 0;
            s.Fault = FaultKind.None;
            s.FaultMessage = null;
            if (s.Killed)
            {
                s.Killed = false;
                s.Running = true;
            }
        }

        /// <summary>
        /// Tries a set-aside state again. The state is first restored into a spare script object; only when the runtime takes it does the
        /// script give up what it is running now and go on from the state. When the runtime still refuses it, the script is not touched.
        /// <paramref name="beforeAdopt"/> runs when the spare took the state, before the script's own state is dropped (the engine lets go of the
        /// listens and requests of the fresh run there). Worker thread only.
        /// </summary>
        internal void RetryState(SchedulerScript s, ScriptStateRecord record, Action beforeAdopt, Action<RetryOutcome> done)
        {
            RetryOutcome outcome;
            try { outcome = TryRetryState(s, record, beforeAdopt); }
            catch (Exception e)
            {
                m_log?.Error($"script {s.Id}: the set-aside state could not be tried again: {e.Message}");
                outcome = new RetryOutcome(false, "an error while trying it again: " + e.Message);
            }
            try { done?.Invoke(outcome); }
            catch (Exception e) { m_log?.Error("the retry callback failed: " + e.Message); }
        }

        private RetryOutcome TryRetryState(SchedulerScript s, ScriptStateRecord record, Action beforeAdopt)
        {
            if (s.State == ScriptRunState.Removed || s.State == ScriptRunState.Loading || s.Native == null || s.Lease == null)
                return new RetryOutcome(false, "the script is not loaded");
            if (record == null || record.Faulted || record.Native == null || record.Native.Length == 0)
                return new RetryOutcome(false, "the set-aside state holds no runtime state to put back");
            if (s.Waiting || s.Dispatching)
                return new RetryOutcome(false, "the script is in the middle of a call; try again");

            Bindings.Script spare = s.Lease.NewScript(m_options.MemoryLimitBytes, s.Id);
            bool taken;
            string note;
            try
            {
                taken = spare.Restore(record.Native);
                note = taken ? "" : spare.RestoreNote;
            }
            catch (Exception e)
            {
                spare.Dispose();
                return new RetryOutcome(false, "the runtime could not read it (" + e.Message + ")");
            }
            if (!taken)
            {
                spare.Dispose();
                return new RetryOutcome(false, note.Length > 0 ? note : "the runtime refused it");
            }

            try { beforeAdopt?.Invoke(); }
            catch (Exception e) { m_log?.Error($"script {s.Id}: letting go of the fresh run failed: {e.Message}"); }
            ClearRunState(s);
            try { s.Native?.Dispose(); }
            finally { s.Native = spare; }
            s.NativeRestored = true;
            s.RestoreRecord = record;
            m_scripts[s.Id] = s;
            SetState(s, ScriptRunState.Created);
            MarkDirty(s);
            StartScript(s);
            return new RetryOutcome(true, "");
        }

        private static string RestoreNoteOf(SchedulerScript s)
        {
            try { return s.Native.RestoreNote ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>A host function reported a script error: the host is told, and the script pays the error pause.</summary>
        internal void ReportScriptError(SchedulerScript s, string text)
        {
            try { m_options.ScriptError?.Invoke(s, text); }
            catch (Exception e) { m_log?.Error("the script-error callback failed: " + e.Message); }
            if (m_options.ErrorPauseSeconds > 0)
                s.AddSleep(m_options.ErrorPauseSeconds);
        }

        internal void RemoveScript(SchedulerScript s)
        {
            if (s.State == ScriptRunState.Removed)
                return;
            s.TimerGen++;
            s.SleepGen++;
            s.Events.Clear();
            Free(s);
            m_scripts.Remove(s.Id);
            SetState(s, ScriptRunState.Removed);
            Interlocked.Decrement(ref m_scriptCount);
        }

        private static void Free(SchedulerScript s)
        {
            try { s.Native?.Dispose(); }
            finally
            {
                s.Native = null;
                s.Lease?.Dispose();
                s.Lease = null;
            }
        }

        // ---- host function support (called on this worker's thread, inside a step) ----

        /// <summary>llMinEventDelay, on the worker. A floor of 0 or less lifts it, and lets a script that waits at the floor go on at once.</summary>
        internal void SetMinEventDelay(SchedulerScript s, double seconds)
        {
            s.MinEventDelay = seconds > 0 ? seconds : 0;
            if (s.MinEventDelay > 0)
                return;
            s.NextEventAt = 0;
            if (s.FloorHeld)
            {
                s.FloorHeld = false;
                s.FloorGen++;
                Reschedule(s);
            }
        }

        internal void SetTimerFor(SchedulerScript s, double seconds)
        {
            MarkDirty(s);
            RemoveQueuedTimer(s);
            s.TimerHeldLeft = -1; // a new timer replaces the time a pause kept
            if (seconds <= 0)
            {
                s.TimerInterval = 0;
                s.TimerArmed = false;
                s.TimerGen++;
                return;
            }
            s.TimerInterval = Math.Max(seconds, m_options.MinTimerInterval);
            ArmTimer(s, Now() + s.TimerInterval);
        }

        private void ArmTimer(SchedulerScript s, double due)
        {
            s.TimerGen++;
            s.TimerDue = due;
            s.TimerArmed = true;
            m_timers.Enqueue((s, s.TimerGen), due);
        }

        private static int s_listenEvent = -1;

        private static int ListenEvent()
        {
            if (s_listenEvent < 0)
                s_listenEvent = Host.EventIndex("listen");
            return s_listenEvent;
        }

        private static int TimerEvent()
        {
            if (s_timerEvent < 0)
                s_timerEvent = Host.EventIndex("timer");
            return s_timerEvent;
        }

        /// <summary>A new timer replaces the old one and any tick of it still waiting, as Phlox's does.</summary>
        private void RemoveQueuedTimer(SchedulerScript s)
        {
            if (!s.TimerQueued)
                return;
            int timer = TimerEvent();
            int n = s.Events.Count;
            for (int i = 0; i < n; i++)
            {
                QueuedEvent e = s.Events.Dequeue();
                // The event being dispatched is still at the head until the handler finishes (RunTurn takes it out
                // then), so a timer() handler that sets the timer must leave its own tick where it is.
                if (e.EventIndex != timer || (i == 0 && s.Dispatching))
                    s.Events.Enqueue(e);
            }
            s.TimerQueued = false;
        }

        private void FireTimer(SchedulerScript s, double now)
        {
            if (!s.TimerQueued)
            {
                // A tick passes a full queue (only one is ever queued), or the timer would stop for good.
                s.Events.Enqueue(new QueuedEvent(TimerEvent(), Array.Empty<Value>()));
                s.TimerQueued = true;
            }
            // The next tick is one interval from now: a long gap is one tick, not a burst.
            ArmTimer(s, now + s.TimerInterval);
            MarkDirty(s);
            Reschedule(s, front: s.State == ScriptRunState.Idle);
        }

        /// <summary>SLua scripts ask for timers through LLTimers; the library keeps the interval and the host reads it back after a step.</summary>
        private void SyncSluaTimer(SchedulerScript s)
        {
            double wanted = s.Native.TimerInterval;
            if (wanted != 0)
                s.SluaTimerUnconfirmed = false;
            if (wanted == s.SluaTimer)
                return;
            if (wanted == 0 && s.SluaTimerUnconfirmed)
                return; // a restored script's object has not been asked yet; the interval saved with it stands until its timer event has run
            s.SluaTimer = wanted;
            SetTimerFor(s, wanted);
        }

        // ---- running a script ----

        private void RunTurn(SchedulerScript s)
        {
            double quanta = m_options.QuantaMs / 1000.0;
            MarkDirty(s);
            bool dispatching = !s.NeedsStep && s.Events.Count > 0;
            double floorAtStart = s.MinEventDelay;   // a floor set by this very handler counts from the next one
            QueuedEvent ev = default;
            StepResult r;
            long start = Stopwatch.GetTimestamp();
            long runTicks = 0;
            SchedulerScript.Current = s;
            s.Dispatching = dispatching;
            s.WaitRequested = false;
            s.AskedSeconds = 0;
            s.ClearHostCall();
            try
            {
                if (dispatching)
                {
                    ev = s.Events.Peek();
                    s.CurrentDetected = ev.Detected;
                    r = s.Native.Dispatch(ev.EventIndex, ev.Args, quanta);
                }
                else if (m_options.StepOverride?.Invoke(s) is StepResult forced)
                {
                    r = forced;
                }
                else
                {
                    r = s.Native.Step(quanta);
                }
            }
            catch (SluaException e) when (dispatching && e.Code == -8)
            {
                // Something is still staged (a state change): step it first, the event keeps its place.
                s.NeedsStep = true;
                return;
            }
            catch (Exception e)
            {
                m_log?.Error($"script {s.Id} failed inside the runtime: {e.Message}");
                Fault(s, FaultKind.Unknown, "internal error: " + e.Message);
                return;
            }
            finally
            {
                SchedulerScript.Current = null;
                s.Dispatching = false;
                long ticks = runTicks = Stopwatch.GetTimestamp() - start;
                if (dispatching) s.CountDispatch(ticks); else s.CountStep(ticks);
                Interlocked.Increment(ref m_steps);
                try { s.MemoryUsed = s.Native.UsedMemory; }
                catch (Exception) { /* the script is being freed */ }
            }

            if (dispatching && r.Status != StepStatus.Refused)
            {
                if (floorAtStart > 0 && s.MinEventDelay > 0)
                    s.NextEventAt = Now() + floorAtStart;   // a handler started now: the next may not before the floor
                s.Events.Dequeue();
                if (ev.EventIndex == TimerEvent())
                    s.TimerQueued = false;
            }

            if (s.Language == ScriptLanguage.Slua)
            {
                if (dispatching && r.Status == StepStatus.NotRun)
                    s.NotHandled |= EventBit(ev.EventIndex);
                else if (r.Status != StepStatus.NotRun && r.Status != StepStatus.Idle)
                    s.NotHandled = 0;
            }
            if (r.Status == StepStatus.StateChange)
            {
                // SL clears the event queue on a state change (https://wiki.secondlife.com/wiki/Event); the new
                // state's handlers decide what is heard from here. A tick that was waiting goes with it.
                s.CurrentState = r.NewState;
                s.CurrentDetected = null;
                s.Events.Clear();
                s.TimerQueued = false;
                try { m_options.StateChanged?.Invoke(s); }
                catch (Exception e) { m_log?.Error("the state-change callback failed: " + e.Message); }
            }

            switch (r.Status)
            {
                case StepStatus.Fault:
                    string message = "";
                    try { message = s.Native.Fault().Message; } catch (Exception) { }
                    Fault(s, r.Fault, message);
                    return;
                case StepStatus.Preempted:
                case StepStatus.StateChange:
                case StepStatus.Refused:
                    s.NeedsStep = true;
                    break;
                case StepStatus.Ok:
                    // A finished handler from a dispatch leaves nothing behind. After a step there can be more
                    // (an LSL script's first step loads it and the next runs state_entry): step until Idle.
                    s.NeedsStep = !dispatching;
                    break;
                default: // NotRun, Idle
                    s.NeedsStep = false;
                    break;
            }

            // An SLua script registers handlers while it runs; the runtime says when the set changed, and the host is told.
            if (s.Language == ScriptLanguage.Slua && r.HandlersChanged)
                ReadHandlers(s);

            if (r.Status != StepStatus.Idle && r.Status != StepStatus.NotRun)
            {
                s.ExecutionTime.Add(runTicks * 1000.0 / Stopwatch.Frequency);
                RecordWindow(s, runTicks / (double)Stopwatch.Frequency, r.CpuSeconds);
            }

            // The runtime reports, in the step that is running, the delay the script owes after it: what its host calls
            // asked for (AskedSeconds, served below) plus, when the step overran its window, a punishment. What it
            // reports beyond what was asked is the punishment.
            double extra = 0;
            bool punished = false;
            if (r.SleepServed > 0)
            {
                extra = r.SleepServed - s.AskedSeconds;
                if (extra > SleepTolerance && !IsOwnOverrun(s))
                {
                    // The windows' CPU time says the system kept this worker off the processor: not the script's doing.
                    string function = s.LastHostFunction ?? SchedulerScript.NoFunction;
                    s.CountWaivedOverrun(function);
                    m_waivedByFunction.AddOrUpdate(function, 1, (_, n) => n + 1);
                    extra = 0;
                }
                else if (extra > SleepTolerance)
                {
                    s.CountPunishment();
                    Interlocked.Increment(ref m_punishments);
                    punished = true;
                }
                else
                    extra = 0;
            }

            // A script the runtime punishes turn after turn is killed; a turn without a punishment starts the count over.
            s.ConsecutivePunishments = punished ? s.ConsecutivePunishments + 1 : 0;
            int killAfter = m_options.KillAfterPunishments;
            if (killAfter > 0 && s.ConsecutivePunishments >= killAfter)
            {
                Interlocked.Increment(ref m_kills);
                Fault(s, FaultKind.Timeout, $"it used more run time than it is allowed in {s.ConsecutivePunishments} turns in a row");
                return;
            }

            // A plain step the runtime keeps refusing is not repeated every pass: after a few tries the script waits.
            double backoff = 0;
            if (!dispatching)
            {
                if (r.Status == StepStatus.Refused)
                {
                    s.ConsecutiveRefusals++;
                    if (s.ConsecutiveRefusals > RefusedFreeRetries)
                    {
                        backoff = Math.Min(RefusedBackoffFirst * Math.Pow(2, s.ConsecutiveRefusals - RefusedFreeRetries - 1), RefusedBackoffLongest);
                        Interlocked.Increment(ref m_refusedCaps);
                    }
                }
                else
                    s.ConsecutiveRefusals = 0;
            }

            double sleep = Math.Max(s.AskedSeconds + extra, backoff);
            if (sleep > 0)
                s.SleepUntil = Now() + sleep;
            if (s.WaitRequested)
            {
                s.Waiting = true;
                s.WaitResult = null;
            }
            if (s.Language == ScriptLanguage.Slua)
            {
                if (dispatching && ev.EventIndex == TimerEvent())
                    s.SluaTimerUnconfirmed = false;   // the timer event has run, so the library has told the host what the timer is now
                SyncSluaTimer(s);
            }
        }

        /// <summary>Reads the handler set the runtime reports changed and tells the host (worker thread).</summary>
        private void ReadHandlers(SchedulerScript s)
        {
            try
            {
                (ulong mask, bool known) = s.Native.Handlers();
                s.SetHandlers(mask, known);
                m_options.HandlersChanged?.Invoke(s);
            }
            catch (Exception e)
            {
                m_log?.Error($"script {s.Id}: its handler set could not be read: {e.Message}");
            }
        }

        private void RecordWindow(SchedulerScript s, double wallSeconds, double cpuSeconds)
        {
            int windows = m_options.OverrunWindows;
            if (windows <= 0)
                return;
            if (s.Windows == null || s.Windows.Capacity != windows)
                s.Windows = new RunWindowAverage(windows);
            s.Windows.Add(wallSeconds, cpuSeconds);
        }

        /// <summary>
        /// Whether an overrun the runtime reported is the script's own. Run windows are measured in wall time, so a worker
        /// the system kept off the processor overruns too; the script's CPU time over its recent windows tells the two
        /// apart. With fewer windows than <see cref="SchedulerOptions.OverrunWindows"/> there is nothing to average and the
        /// overrun is the script's, as it was before the runtime reported CPU time.
        /// </summary>
        private bool IsOwnOverrun(SchedulerScript s)
        {
            RunWindowAverage w = s.Windows;
            if (w == null || w.Count < m_options.OverrunWindows)
                return true;
            return w.CpuShare >= m_options.OverrunMinCpuShare;
        }

        /// <summary>Puts the script where its situation says: waiting, sleeping, in the ring, or idle.</summary>
        private void Reschedule(SchedulerScript s, bool front = false)
        {
            switch (s.State)
            {
                case ScriptRunState.Removed:
                case ScriptRunState.Faulted:
                case ScriptRunState.Loading:
                    return;
            }
            if (!s.Started)
            {
                SetState(s, ScriptRunState.Created);
                return;
            }
            if (!s.Running)
            {
                SetState(s, ScriptRunState.Stopped);
                return;
            }
            if (s.Suspended)
            {
                SetState(s, ScriptRunState.Suspended);
                return;
            }
            if (s.Waiting)
            {
                SetState(s, ScriptRunState.Waiting);
                return;
            }
            if (s.SleepUntil > 0)
            {
                if (s.State != ScriptRunState.Sleeping)
                {
                    SetState(s, ScriptRunState.Sleeping);
                    s.SleepGen++;
                    m_sleepers.Enqueue((s, s.SleepGen), s.SleepUntil);
                }
                return;
            }
            // llMinEventDelay: a handler is not started before the floor has passed. The events stay queued (none is dropped)
            // and the script waits, out of the ring, for the end of the floor.
            if (!s.NeedsStep && s.Events.Count > 0 && s.MinEventDelay > 0 && Now() < s.NextEventAt)
            {
                SetState(s, ScriptRunState.Idle);
                if (!s.FloorHeld)
                {
                    s.FloorHeld = true;
                    s.FloorGen++;
                    m_floors.Enqueue((s, s.FloorGen), s.NextEventAt);
                }
                return;
            }
            if (s.NeedsStep || s.Events.Count > 0)
            {
                SetState(s, ScriptRunState.Runnable);
                if (!s.InRing)
                {
                    s.InRing = true;
                    if (front && m_options.FrontOfRing && s.LastFrontCycle != Interlocked.Read(ref m_cycles))
                    {
                        s.LastFrontCycle = Interlocked.Read(ref m_cycles);
                        s.CountFrontGrant();
                        m_front.Enqueue(s);
                    }
                    else
                        m_ring.Enqueue(s);
                }
                return;
            }
            SetState(s, ScriptRunState.Idle);
        }

        private void SetState(SchedulerScript s, ScriptRunState state)
        {
            ScriptRunState old = s.State;
            if (old == state)
                return;
            s.State = state;
            Interlocked.Decrement(ref m_stateCounts[(int)old]);
            Interlocked.Increment(ref m_stateCounts[(int)state]);
        }

        private void Fault(SchedulerScript s, FaultKind kind, string message, bool report = true)
        {
            s.Fault = kind;
            s.FaultMessage = message;
            MarkDirty(s);
            s.Killed = true; // stays stopped until reset or its Running flag is set again
            s.Running = false;
            s.Events.Clear();
            s.TimerGen++;
            s.SleepGen++;
            s.Waiting = false;
            SetState(s, ScriptRunState.Faulted);
            try
            {
                // A fault is reported when it happens; a script put back stopped from its saved state is only noted.
                if (report)
                    m_options.ScriptFaulted?.Invoke(s);
                else
                    m_options.ScriptFaultRestored?.Invoke(s);
            }
            catch (Exception e) { m_log?.Error("the fault callback failed: " + e.Message); }
        }

        // ---- saved state ----

        private bool m_anyDirty;
        private double m_nextSave;

        /// <summary>The script changed since its last save; the next timed save takes it. Scripts with no item to save under are not counted.</summary>
        private void MarkDirty(SchedulerScript s)
        {
            if (m_options.StateSink == null || s.ItemId == UUID.Zero)
                return;
            s.StateDirty = true;
            if (!m_anyDirty)
            {
                m_anyDirty = true;
                m_nextSave = Now() + m_options.StateSaveIntervalMs / 1000.0;
            }
        }

        private void SaveDueStates(double now)
        {
            if (m_anyDirty && m_options.StateSink != null && now >= m_nextSave)
                SaveStates(onlyChanged: true);
        }

        /// <summary>
        /// Captures the scripts and hands their records to the sink, which writes them off this thread. The timed save takes only the
        /// scripts that changed; the save at shutdown takes every started script, because the times left in a record are as of the
        /// moment it was written. A script that cannot be captured now (it waits for a call to come back) keeps its change mark and is
        /// tried again at the next save.
        /// </summary>
        private void SaveStates(bool onlyChanged)
        {
            m_anyDirty = false;
            IScriptStateSink sink = m_options.StateSink;
            if (sink == null)
                return;
            foreach (SchedulerScript s in new List<SchedulerScript>(m_scripts.Values))
            {
                if (s.ItemId == UUID.Zero || s.StateHeld != null || s.State == ScriptRunState.Removed || s.State == ScriptRunState.Loading)
                    continue;
                if (onlyChanged && !s.StateDirty)
                    continue;
                try
                {
                    byte[] state = CaptureState(s);
                    if (state == null)
                    {
                        if (s.Started || s.Killed)
                        {
                            s.StateDirty = true;
                            m_anyDirty = true;
                        }
                        continue;
                    }
                    sink.Save(s.ItemId, s.AssetId, state);
                    s.StateDirty = false;
                }
                catch (Exception e)
                {
                    m_log?.Error($"script {s.ItemId}: its state could not be captured: {e.Message}");
                    s.StateDirty = true;
                    m_anyDirty = true;
                }
            }
            if (m_anyDirty)
                m_nextSave = Now() + m_options.StateSaveIntervalMs / 1000.0;
        }

        /// <summary>The script's record as bytes, or null when it cannot be captured now.</summary>
        private byte[] CaptureState(SchedulerScript s)
        {
            if (s.Waiting || s.Dispatching)
                return null;
            if (!s.Killed && (s.Native == null || !s.Started))
                return null;
            double now = Now();
            var record = new ScriptStateRecord
            {
                Engine = m_options.EngineName,
                Language = s.Language,
                Running = s.Running,
                Faulted = s.Killed,
                FaultKind = (int)s.Fault,
                FaultMessage = s.FaultMessage ?? "",
                LslState = s.CurrentState,
                TimerInterval = s.TimerInterval,
                SluaTimer = s.SluaTimer,
                MinEventDelay = s.MinEventDelay,
            };
            if (s.TimerInterval > 0)
                record.TimerLeft = s.TimerArmed ? Math.Max(0, s.TimerDue - now) : (s.TimerHeldLeft >= 0 ? s.TimerHeldLeft : s.TimerInterval);
            if (s.SleepUntil > now)
                record.SleepLeft = s.SleepUntil - now;
            if (s.NextEventAt > now)
                record.NextEventIn = s.NextEventAt - now;
            foreach (QueuedEvent e in s.Events)
                record.Events.Add(new SavedEvent { EventIndex = e.EventIndex, Args = e.Args ?? Array.Empty<Value>(), Detected = e.Detected });
            ActiveListen[] listens = s.ListenSnapshot?.Invoke();
            if (listens != null)
                record.Listens.AddRange(listens);
            try { s.ExtrasFill?.Invoke(record); }
            catch (Exception e) { m_log?.Error($"script {s.ItemId}: its attributes and permission record could not be captured: {e.Message}"); }
            if (!s.Killed)
                record.Native = s.Native.Serialize();
            return record.Encode();
        }

        /// <summary>
        /// The script's record for the object that carries it (a take, a derez, a crossing), taken here between steps; null when the script
        /// cannot be captured now (it waits for a call to come back). Worker thread only, like every capture.
        /// </summary>
        internal byte[] CaptureForCarry(SchedulerScript s)
        {
            if (s.State == ScriptRunState.Removed || s.State == ScriptRunState.Loading || s.StateHeld != null)
                return null;
            try { return CaptureState(s); }
            catch (Exception e)
            {
                m_log?.Error($"script {s.Id}: its state could not be captured for its object: {e.Message}");
                return null;
            }
        }

        /// <summary>Puts the saved state back instead of starting the script. False: nothing was restored and the script is to start fresh.</summary>
        private bool TryRestore(SchedulerScript s)
        {
            ScriptStateRecord record = s.RestoreRecord;
            s.RestoreRecord = null;
            if (record == null)
                return false;
            if (record.Faulted)
            {
                // A fault stopped the script before the save: it stays stopped, with the reason it was given (reset or set running again starts it).
                s.Started = true;
                s.CurrentState = record.LslState;
                Fault(s, (FaultKind)record.FaultKind, record.FaultMessage, report: false);
                return true;
            }

            bool restored;
            try
            {
                restored = s.NativeRestored || s.Native.Restore(record.Native);
            }
            catch (Exception e)
            {
                RejectState(s, "the runtime could not read the saved state (" + e.Message + ")");
                return false;
            }
            finally
            {
                s.NativeRestored = false;
            }
            s.Started = true;
            s.NeedsStep = true;
            if (!restored)
            {
                // The runtime started the script fresh (another build, or a host function the state holds is gone); its old state stays on record.
                string note = RestoreNoteOf(s);
                RejectState(s, "the runtime refused the saved state (" + (note.Length > 0 ? note : "saved by another runtime build or holding a host function this build does not have") + ") and the script started fresh");
                MarkDirty(s);
                Reschedule(s);
                return true;
            }

            double now = Now();
            s.CurrentState = record.LslState;
            s.NotHandled = 0;
            s.Running = record.Running;
            s.SluaTimer = record.SluaTimer;
            s.SluaTimerUnconfirmed = record.SluaTimer > 0;
            s.MinEventDelay = record.MinEventDelay;
            if (record.NextEventIn > 0)
                s.NextEventAt = now + record.NextEventIn;
            if (record.SleepLeft > 0)
                s.SleepUntil = now + record.SleepLeft;
            if (record.TimerInterval > 0)
            {
                s.TimerInterval = record.TimerInterval;
                if (s.Running)
                    ArmTimer(s, now + Math.Max(0, record.TimerLeft));
                else
                    s.TimerHeldLeft = Math.Max(0, record.TimerLeft);
            }
            if (s.Running)
            {
                int timer = TimerEvent();
                foreach (SavedEvent e in record.Events)
                {
                    if (s.Events.Count >= m_options.MaxEventQueue)
                        break;
                    s.Events.Enqueue(new QueuedEvent(e.EventIndex, e.Args, e.Detected));
                    if (e.EventIndex == timer)
                        s.TimerQueued = true;
                }
            }
            try { s.ListenRestore?.Invoke(record.Listens.ToArray()); }
            catch (Exception e) { m_log?.Error($"script {s.ItemId}: its listens could not be put back: {e.Message}"); }
            try { s.ExtrasRestore?.Invoke(record, s.RestoredFromCarried); }
            catch (Exception e) { m_log?.Error($"script {s.ItemId}: its attributes and permission record could not be put back: {e.Message}"); }
            if (s.Language == ScriptLanguage.Slua)
                ReadHandlers(s);
            MarkDirty(s);
            Reschedule(s);
            return true;
        }

        private void RejectState(SchedulerScript s, string reason)
        {
            m_log?.Warn($"script {s.ItemId}: {reason}; the old state is set aside");
            try { m_options.StateSink?.Reject(s.ItemId, reason); }
            catch (Exception e) { m_log?.Error($"script {s.ItemId}: its state could not be set aside: {e.Message}"); }
        }

        // ---- stopping ----

        /// <summary>Stops the thread (or, with no thread, frees everything on the caller's thread).</summary>
        internal void Stop(TimeSpan wait)
        {
            m_stop = true;
            m_signal.Set();
            m_stopSignal.Set();
            if (m_thread == null)
            {
                Cleanup();
                return;
            }
            if (m_thread != Thread.CurrentThread && !m_thread.Join(wait))
                m_log?.Error($"worker {m_index} did not stop within {wait.TotalSeconds:0} s; its virtual machine is left as it is");
        }

        private void Cleanup()
        {
            try { SaveStates(onlyChanged: false); }
            catch (Exception e) { m_log?.Error("the scripts' state could not be saved at shutdown: " + e.Message); }
            foreach (SchedulerScript s in new List<SchedulerScript>(m_scripts.Values))
            {
                try { Free(s); }
                catch (Exception e) { m_log?.Error($"script {s.Id} could not be freed: {e.Message}"); }
                SetState(s, ScriptRunState.Removed);
            }
            m_scripts.Clear();
            m_ring.Clear();
            m_front.Clear();
            m_images = null;
            try { m_vm?.Dispose(); }
            catch (Exception e) { m_log?.Error("the virtual machine could not be freed: " + e.Message); }
            m_vm = null;
        }
    }
}
