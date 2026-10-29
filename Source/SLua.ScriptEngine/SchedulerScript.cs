/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>An event waiting for a script: the native event number and its arguments.</summary>
    internal readonly record struct QueuedEvent(int EventIndex, Value[] Args, DetectedRecord[] Detected = null);

    /// <summary>What a retry of a set-aside state found: whether the script now goes on from it, and why not when it does not.</summary>
    internal readonly record struct RetryOutcome(bool Restored, string Reason);

    /// <summary>
    /// A script as its worker sees it. The fields that are not marked "worker only" may be read from any thread;
    /// the rest belong to the worker thread that owns the script's VM. A host function running inside a step
    /// reaches its own script through <see cref="Current"/>.
    /// </summary>
    internal sealed class SchedulerScript
    {
        [ThreadStatic] private static SchedulerScript t_current;

        private int m_state;
        private int m_running = 1;
        private int m_suspended;
        private int m_killed;
        private long m_steps;
        private long m_dispatches;
        private long m_runTicks;
        private long m_droppedEvents;
        private long m_punishments;
        private long m_droppedWhileStopped;
        private long m_droppedUnhandled;
        private long m_frontGrants;

        internal SchedulerScript(long id, int workerIndex, byte[] asset, ScriptLanguage language)
        {
            Id = id;
            WorkerIndex = workerIndex;
            Asset = asset;
            Language = language;
            // An LSL asset says which events each state handles; the host keeps only events the current state handles.
            if (language == ScriptLanguage.Lsl && AssetHeader.TryRead(asset, out bool isLsl, out ulong[] masks) && isLsl && masks.Length > 0)
                StateMasks = masks;
        }

        public long Id { get; }
        public int WorkerIndex { get; }
        public ScriptLanguage Language { get; }
        internal byte[] Asset { get; }

        public ScriptRunState State
        {
            get => (ScriptRunState)Volatile.Read(ref m_state);
            internal set => Volatile.Write(ref m_state, (int)value);
        }

        /// <summary>Steps and dispatches the worker has made for the script.</summary>
        public long Steps => Interlocked.Read(ref m_steps);
        public long Dispatches => Interlocked.Read(ref m_dispatches);

        /// <summary>Stopwatch ticks spent inside the script's native calls.</summary>
        public long RunTicks => Interlocked.Read(ref m_runTicks);

        /// <summary>Events dropped because the script's queue was full.</summary>
        public long DroppedEvents => Interlocked.Read(ref m_droppedEvents);

        /// <summary>Events dropped because the script was stopped (its Running flag off, or stopped by a fault), including the ones already queued when it was stopped.</summary>
        public long DroppedWhileStopped => Interlocked.Read(ref m_droppedWhileStopped);

        /// <summary>Events not queued because the script has no handler for them.</summary>
        public long DroppedUnhandled => Interlocked.Read(ref m_droppedUnhandled);

        /// <summary>Times an event that woke the idle script put it at the front of its worker's ring.</summary>
        public long FrontGrants => Interlocked.Read(ref m_frontGrants);

        /// <summary>Times the script was put to sleep by a step that overran (the runtime's punishment), as opposed to a delay it asked for.</summary>
        public long Punishments => Interlocked.Read(ref m_punishments);

        /// <summary>The Running flag: false while the script is stopped, and after a fault or a kill, until reset or set again.</summary>
        public bool Running { get => Volatile.Read(ref m_running) != 0; internal set => Volatile.Write(ref m_running, value ? 1 : 0); }

        /// <summary>True when the script would run if it had work: its Running flag is on, it is not suspended and not stopped by a fault.</summary>
        public bool IsRunning => Running && !Suspended && !Killed && State != ScriptRunState.Removed;

        /// <summary>Held by suspend; events keep queueing.</summary>
        public bool Suspended { get => Volatile.Read(ref m_suspended) != 0; internal set => Volatile.Write(ref m_suspended, value ? 1 : 0); }

        /// <summary>The script was stopped by a fault or a kill and has not been reset or set running since.</summary>
        public bool Killed { get => Volatile.Read(ref m_killed) != 0; internal set => Volatile.Write(ref m_killed, value ? 1 : 0); }

        /// <summary>Consecutive turns the runtime punished; a turn without a punishment sets it back to 0.</summary>
        public int ConsecutivePunishments { get; internal set; }

        /// <summary>Plain steps the runtime refused, in a row.</summary>
        public int ConsecutiveRefusals { get; internal set; }

        public FaultKind Fault { get; internal set; }
        public string FaultMessage { get; internal set; }

        /// <summary>The inventory item and asset the script's saved state is kept under; empty for a script that saves nothing.</summary>
        internal UUID ItemId;
        internal UUID AssetId;

        /// <summary>The saved state to restore when the script starts (set by the engine before the start); null for a fresh start.</summary>
        internal ScriptStateRecord RestoreRecord;

        /// <summary>Why the script was held and not started: its saved state could not be read. A held script saves nothing, so its row stays as it is.</summary>
        internal string StateHeld;

        /// <summary>The script's listens, for the record the scheduler writes; null for a script with none to save.</summary>
        internal Func<ActiveListen[]> ListenSnapshot;

        /// <summary>Given the listens of a restored script before it runs, so the engine can register them again.</summary>
        internal Action<ActiveListen[]> ListenRestore;

        /// <summary>Fills in what the engine keeps of the script outside the worker (its attributes and permission records) when a record is captured.</summary>
        internal Action<ScriptStateRecord> ExtrasFill;

        /// <summary>Given the record of a restored script before it runs; the second argument says the state came with the object, not from the database.</summary>
        internal Action<ScriptStateRecord, bool> ExtrasRestore;

        /// <summary>The record the script was restored from came with its object (take, rez, crossing, teleport) and not from the database.</summary>
        internal bool RestoredFromCarried;

        /// <summary>The timer interval in force, seconds; 0 for none.</summary>
        public double TimerInterval { get; internal set; }

        // ---- worker only ----
        internal ulong[] StateMasks;
        internal int CurrentState;
        internal ulong NotHandled;
        internal long LastFrontCycle = -1;
        internal Script Native;
        internal ImageLease Lease;
        internal bool Started;
        /// <summary>Worker only: the script changed since its state was last handed to the sink.</summary>
        internal bool StateDirty;
        internal bool NeedsStep;
        internal bool InRing;
        internal readonly Queue<QueuedEvent> Events = new Queue<QueuedEvent>();
        internal double SleepUntil;
        internal long SleepGen;
        internal long TimerGen;
        internal bool TimerQueued;
        /// <summary>Worker only: an event is being dispatched, and it is still the head of <see cref="Events"/>.</summary>
        internal bool Dispatching;
        /// <summary>Worker only: when the armed timer is due, and whether one is armed.</summary>
        internal double TimerDue;
        internal bool TimerArmed;
        /// <summary>Worker only: seconds the timer had left when a pause held it, or -1 when none are kept.</summary>
        internal double TimerHeldLeft = -1;
        internal bool WaitRequested;
        internal bool Waiting;
        internal Value[] WaitResult;

        /// <summary>Worker only: the number of the call the script waits for (-1 when the wait has none); an answer for another call is dropped.</summary>
        internal int WaitSeq = -1;

        /// <summary>Worker only: the delay the answer to the waited call carries, in seconds, and the fault it carries, if any.</summary>
        internal double WaitDelay;
        internal Exception WaitFault;
        internal double AskedSeconds;
        internal double SluaTimer;
        internal ScriptWorker Worker;

        /// <summary>Worker only: the floor llMinEventDelay set, in seconds (0: none), and the time the next handler may start.</summary>
        internal double MinEventDelay;
        internal double NextEventAt;

        /// <summary>Worker only: the script waits at the floor for its next handler; the generation tells a wake that is still wanted from an old one.</summary>
        internal bool FloorHeld;
        internal long FloorGen;

        /// <summary>
        /// How many events wait for the script. Read from any thread (the queue figure), so it can be a moment old; the queue
        /// itself is the worker's.
        /// </summary>
        internal int QueuedEvents => Events.Count;

        /// <summary>The bytes the script's runtime heap used after its last step, for the status command and the memory figures.</summary>
        internal int MemoryUsed;

        internal void CountStep(long ticks) { Interlocked.Increment(ref m_steps); Interlocked.Add(ref m_runTicks, ticks); }
        internal void CountDispatch(long ticks) { Interlocked.Increment(ref m_dispatches); Interlocked.Add(ref m_runTicks, ticks); }
        internal void CountDrop() => Interlocked.Increment(ref m_droppedEvents);
        internal void CountStoppedDrop(long n = 1) => Interlocked.Add(ref m_droppedWhileStopped, n);
        internal void CountUnhandledDrop() => Interlocked.Increment(ref m_droppedUnhandled);
        internal void CountFrontGrant() => Interlocked.Increment(ref m_frontGrants);

        /// <summary>
        /// The detected objects of the event being handled, set by the worker when it dispatches the event and kept
        /// until the next dispatch, a state change or a reset; null when the event carried none. Worker thread only,
        /// as host functions run on it.
        /// </summary>
        internal DetectedRecord[] CurrentDetected
        {
            get => m_currentDetected;
            set
            {
                m_currentDetected = value;
                DetectedChanged?.Invoke(value);
            }
        }

        private DetectedRecord[] m_currentDetected;

        /// <summary>Told whenever <see cref="CurrentDetected"/> changes: the script functions (llDetected*) read the same objects through the script's runtime access.</summary>
        internal Action<DetectedRecord[]> DetectedChanged;

        /// <summary>Detected objects of the event being handled; 0 outside an event that carries any.</summary>
        internal int DetectedCount => CurrentDetected?.Length ?? 0;

        /// <summary>One detected object of the event being handled, or null for an index out of range.</summary>
        internal DetectedRecord DetectedAt(int index) =>
            CurrentDetected != null && index >= 0 && index < CurrentDetected.Length ? CurrentDetected[index] : null;
        /// <summary>Overruns the runtime reported that were not punished because the script's CPU time, averaged over its recent run windows, says the system kept its worker off the processor.</summary>
        public long WaivedOverruns => Interlocked.Read(ref m_waivedOverruns);
        private long m_waivedOverruns;
        /// <summary>While the script is suspended, events that reach it are dropped instead of queued (a policy pause).</summary>
        internal bool DropsEventsWhileSuspended { get => Volatile.Read(ref m_dropsWhileSuspended) != 0; set => Volatile.Write(ref m_dropsWhileSuspended, value ? 1 : 0); }
        private int m_dropsWhileSuspended;

        /// <summary>Events dropped because a policy pause held the script.</summary>
        public long DroppedWhilePaused => Interlocked.Read(ref m_droppedWhilePaused);
        private long m_droppedWhilePaused;
        internal void CountPausedDrop() => Interlocked.Increment(ref m_droppedWhilePaused);


        /// <summary>The host function the latest host call of this script ran, as "table.name"; null before its step made one.</summary>
        internal string LastHostFunction { get; private set; }

        /// <summary>Records that a host function ran inside the current step.</summary>
        internal void NoteHostCall(string name) => LastHostFunction = name;

        internal void ClearHostCall() => LastHostFunction = null;

        /// <summary>The key a waived overrun is counted under when its step called no host function.</summary>
        internal const string NoFunction = "(none)";

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> m_waivedByFunction = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        /// <summary>Waived overruns by the host function the step called last (a script blocked in a host call without using CPU is waived too; this says where).</summary>
        public IReadOnlyDictionary<string, long> WaivedByFunction => new Dictionary<string, long>(m_waivedByFunction);

        internal void CountWaivedOverrun(string function)
        {
            Interlocked.Increment(ref m_waivedOverruns);
            m_waivedByFunction.AddOrUpdate(function, 1, (_, n) => n + 1);
        }

        /// <summary>While the script is suspended, a listen that reaches it is dropped instead of queued (its object is crossing); its other events are kept.</summary>
        internal bool DropsListensWhileSuspended { get => Volatile.Read(ref m_dropsListens) != 0; set => Volatile.Write(ref m_dropsListens, value ? 1 : 0); }
        private int m_dropsListens;

        /// <summary>The run windows (wall time and CPU time) the overrun decision averages; worker thread only.</summary>
        internal RunWindowAverage Windows;

        /// <summary>The script's recent execution time per run window in milliseconds, for the region's Top Scripts report.</summary>
        internal readonly ExecutionTimeAverage ExecutionTime = new ExecutionTimeAverage();

        private long m_handlerMask;
        private int m_handlersKnown;

        /// <summary>
        /// The events an SLua script handles now, one bit each as the runtime counts them, and whether the set is complete.
        /// Read from the runtime on the worker after a step that changed it; an LSL script's come from its asset header.
        /// </summary>
        internal ulong HandlerMask => (ulong)Interlocked.Read(ref m_handlerMask);
        internal bool HandlersKnown => Volatile.Read(ref m_handlersKnown) != 0;

        internal void SetHandlers(ulong mask, bool known)
        {
            Interlocked.Exchange(ref m_handlerMask, (long)mask);
            Volatile.Write(ref m_handlersKnown, known ? 1 : 0);
        }

        internal void CountPunishment() => Interlocked.Increment(ref m_punishments);

        /// <summary>The script whose native call is running on this thread, or null.</summary>
        public static SchedulerScript Current
        {
            get => t_current;
            internal set => t_current = value;
        }

        /// <summary>For a host function: adds a forced delay to this script (the call-delay of the Second Life wiki; llSleep).</summary>
        public void AddSleep(double seconds)
        {
            RequireCurrent();
            Native.AddSleep(seconds);
            AskedSeconds += seconds;
        }

        /// <summary>For a host function: reports a script error to the owner and pauses the script for the error pause (the chat pause of an error shout).</summary>
        public void ReportError(string text)
        {
            RequireCurrent();
            Worker.ReportScriptError(this, text);
        }

        /// <summary>
        /// For a host function: llMinEventDelay. The next handler of this script starts no sooner than <paramref name="seconds"/>
        /// after the last one started; 0 or less lifts the floor. Events wait in the queue meanwhile and are not dropped.
        /// </summary>
        public void SetMinEventDelay(double seconds)
        {
            RequireCurrent();
            Worker.SetMinEventDelay(this, seconds);
        }

        /// <summary>For a host function: sets or clears (0 or less) this script's timer, as llSetTimerEvent does. The interval is raised to the floor.</summary>
        public void SetTimer(double seconds)
        {
            RequireCurrent();
            Worker.SetTimerFor(this, seconds);
        }

        /// <summary>For a pausing call's start function: the script is parked after this step until <see cref="SLuaScheduler.CompleteWait"/>.</summary>
        public void BeginWait()
        {
            RequireCurrent();
            WaitRequested = true;
            WaitSeq = -1;
        }

        /// <summary>For a pausing call's start function: as <see cref="BeginWait()"/>, for the call with this sequence number; only that call's answer ends the wait.</summary>
        public void BeginWait(int seq)
        {
            RequireCurrent();
            WaitRequested = true;
            WaitSeq = seq;
        }

        /// <summary>For a pausing call's start function that finished the call itself: the take function returns these values, after the delay (seconds), with no wait in between.</summary>
        public void CompleteInline(Value[] result, double delaySeconds)
        {
            RequireCurrent();
            WaitResult = result;
            WaitDelay = delaySeconds;
            WaitFault = null;
        }

        /// <summary>For a pausing call's take function: what the call was answered with, and clears it.</summary>
        public (Value[] Result, double DelaySeconds, Exception Fault) TakeOutcome()
        {
            RequireCurrent();
            var outcome = (WaitResult ?? Array.Empty<Value>(), WaitDelay, WaitFault);
            WaitResult = null;
            WaitDelay = 0;
            WaitFault = null;
            return outcome;
        }

        /// <summary>For a pausing call's take function: the values the call was completed with, or none.</summary>
        public Value[] TakeResult()
        {
            RequireCurrent();
            Value[] result = WaitResult ?? Array.Empty<Value>();
            WaitResult = null;
            return result;
        }

        private void RequireCurrent()
        {
            if (!ReferenceEquals(t_current, this))
                throw new InvalidOperationException("this call is valid only inside a host function that this script called");
        }
    }
}
