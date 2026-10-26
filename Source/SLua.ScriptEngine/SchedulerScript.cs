/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>An event waiting for a script: the native event number and its arguments.</summary>
    internal readonly record struct QueuedEvent(int EventIndex, Value[] Args);

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
        internal bool NeedsStep;
        internal bool InRing;
        internal readonly Queue<QueuedEvent> Events = new Queue<QueuedEvent>();
        internal double SleepUntil;
        internal long SleepGen;
        internal long TimerGen;
        internal bool TimerQueued;
        internal bool WaitRequested;
        internal bool Waiting;
        internal Value[] WaitResult;
        internal double AskedSeconds;
        internal double Owed;
        internal double SluaTimer;
        internal ScriptWorker Worker;

        internal void CountStep(long ticks) { Interlocked.Increment(ref m_steps); Interlocked.Add(ref m_runTicks, ticks); }
        internal void CountDispatch(long ticks) { Interlocked.Increment(ref m_dispatches); Interlocked.Add(ref m_runTicks, ticks); }
        internal void CountDrop() => Interlocked.Increment(ref m_droppedEvents);
        internal void CountStoppedDrop(long n = 1) => Interlocked.Add(ref m_droppedWhileStopped, n);
        internal void CountUnhandledDrop() => Interlocked.Increment(ref m_droppedUnhandled);
        internal void CountFrontGrant() => Interlocked.Increment(ref m_frontGrants);

        /// <summary>Detected objects of the event being handled; empty outside an event that carries any.</summary>
        internal int DetectedCount => 0;

        /// <summary>One detected object of the event being handled, or null for an index out of range.</summary>
        internal DetectedRecord DetectedAt(int index) => null;
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

        /// <summary>For a host function: sets or clears (0 or less) this script's timer, as llSetTimerEvent does. The interval is raised to the floor.</summary>
        public void SetTimer(double seconds)
        {
            RequireCurrent();
            Worker.SetTimerFor(this, seconds);
        }

        /// <summary>For a pausing call's start function: the script is parked after this step until <see cref="LuauScheduler.CompleteWait"/>.</summary>
        public void BeginWait()
        {
            RequireCurrent();
            WaitRequested = true;
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
