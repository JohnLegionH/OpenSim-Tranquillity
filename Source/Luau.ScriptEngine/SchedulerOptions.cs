/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Diagnostics;
using System.Threading;
using Luau.ScriptEngine.Bindings;

namespace Luau.ScriptEngine
{
    /// <summary>The scheduler's clock, in seconds from an arbitrary start. Tests replace it to move time by hand.</summary>
    internal interface ISchedulerClock
    {
        double Now { get; }
    }

    /// <summary>The real clock: a monotonic stopwatch.</summary>
    internal sealed class MonotonicClock : ISchedulerClock
    {
        private readonly Stopwatch m_watch = Stopwatch.StartNew();

        public double Now => m_watch.Elapsed.TotalSeconds;
    }

    /// <summary>Where a script is in the scheduler.</summary>
    internal enum ScriptRunState
    {
        /// <summary>Being made on its worker.</summary>
        Loading = 0,

        /// <summary>Made and not started.</summary>
        Created,

        /// <summary>In its worker's ring: it has a handler in flight or an event waiting.</summary>
        Runnable,

        /// <summary>Not run until a time: a forced delay, llSleep or the runtime's punishment.</summary>
        Sleeping,

        /// <summary>Inside a pausing call; not run until the call's result is delivered.</summary>
        Waiting,

        /// <summary>Started, nothing to run: costs the worker nothing until an event or timer arrives.</summary>
        Idle,

        /// <summary>Ended by a fault; kept so its fault can be read.</summary>
        Faulted,

        /// <summary>Taken out of the scheduler; its native objects are freed.</summary>
        Removed,

        /// <summary>Its Running flag is off: not run, events are dropped, timers are held. Resuming continues where it stopped.</summary>
        Stopped,

        /// <summary>Held by suspend: not run, events keep queueing. Resuming continues where it stopped.</summary>
        Suspended,
    }

    /// <summary>What the scheduler is built from. Defaults are the settings defaults.</summary>
    internal sealed class SchedulerOptions
    {
        /// <summary>Worker threads, each with its own VM, images and scripts.</summary>
        public int WorkerThreads { get; set; } = 1;

        /// <summary>Run window for one step of one script, in milliseconds.</summary>
        public double QuantaMs { get; set; } = 2.0;

        /// <summary>Most wall time one pass over the ring may take before the worker looks at its queues again, in milliseconds.</summary>
        public double PassBudgetMs { get; set; } = 20.0;

        /// <summary>Smallest timer interval, in seconds.</summary>
        public double MinTimerInterval { get; set; } = 0.1;

        /// <summary>Events queued per script; more are dropped and counted.</summary>
        public int MaxEventQueue { get; set; } = 64;

        public ThreadPriority ThreadPriority { get; set; } = ThreadPriority.Lowest;

        /// <summary>Memory limit given to each script, in bytes; 0 is the library's default.</summary>
        public int MemoryLimitBytes { get; set; }

        /// <summary>Makes a worker's VM with its host functions registered. Called on the worker's thread, once, when its first script is made.</summary>
        public Func<Vm> VmFactory { get; set; }

        public ISchedulerClock Clock { get; set; } = new MonotonicClock();

        /// <summary>False: no threads are started and the caller pumps <see cref="LuauScheduler.DoWork"/> (tests).</summary>
        public bool RunThreads { get; set; } = true;

        public IEngineLog Log { get; set; }

        /// <summary>Called on the worker's thread when a script ends in a fault or is killed.</summary>
        public Action<SchedulerScript> ScriptFaulted { get; set; }

        /// <summary>Consecutive punished turns after which a script is killed.</summary>
        public int KillAfterPunishments { get; set; }

        /// <summary>Delay a script's own error report adds to the script, in seconds (the 15 ms chat pause).</summary>
        public double ErrorPauseSeconds { get; set; }

        /// <summary>Called on the worker's thread when a host function reports a script error (<see cref="SchedulerScript.ReportError"/>).</summary>
        public Action<SchedulerScript, string> ScriptError { get; set; }

        /// <summary>Wait before a worker whose pass failed carries on, in milliseconds.</summary>
        public int WorkerRestartDelayMs { get; set; }

        /// <summary>Failed passes after which a worker stops for good.</summary>
        public int MaxWorkerCrashes { get; set; }

        /// <summary>Called once, on the worker's thread, when a worker has stopped for good after too many failed passes: its index, the failures, the last one's message.</summary>
        public Action<int, int, string> WorkerStopped { get; set; }

        /// <summary>Tests only: called on the worker's thread at the start of each pass with the worker's index and the pass number; it may throw to fail the pass.</summary>
        public Action<int, long> BeforePass { get; set; }

        /// <summary>Tests only: returns the result of a plain step in place of the runtime's, or null to run the step.</summary>
        public Func<SchedulerScript, StepResult?> StepOverride { get; set; }
    }

    /// <summary>A reading of one worker's counters.</summary>
    internal readonly record struct WorkerStats(
        int Scripts,
        int Runnable,
        int Sleeping,
        int Waiting,
        long Passes,
        long Steps,
        long Wakeups,
        long BudgetCutoffs,
        int Stopped = 0,
        int Suspended = 0,
        int Faulted = 0,
        long Kills = 0,
        long Punishments = 0,
        long RefusedCaps = 0,
        long Crashes = 0,
        bool Failed = false);

    /// <summary>A reading of the whole scheduler's counters.</summary>
    internal sealed class SchedulerStats
    {
        public WorkerStats[] Workers { get; init; }

        public int Scripts => Sum(w => w.Scripts);
        public int Stopped => Sum(w => w.Stopped);
        public int Suspended => Sum(w => w.Suspended);
        public int Faulted => Sum(w => w.Faulted);
        public long Kills => Sum(w => w.Kills);
        public long Punishments => Sum(w => w.Punishments);
        public long RefusedCaps => Sum(w => w.RefusedCaps);
        public long Crashes => Sum(w => w.Crashes);
        public int FailedWorkers => Sum(w => w.Failed ? 1 : 0);

        private int Sum(Func<WorkerStats, int> f) { int n = 0; foreach (WorkerStats w in Workers) n += f(w); return n; }
        private long Sum(Func<WorkerStats, long> f) { long n = 0; foreach (WorkerStats w in Workers) n += f(w); return n; }
    }
}
