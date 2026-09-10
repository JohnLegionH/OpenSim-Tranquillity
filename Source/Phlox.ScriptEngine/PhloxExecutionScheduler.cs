/*
 * Legion Grid — Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon ExecutionScheduler.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 for Legion Grid / OpenSim 0.9.3 .NET 8
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using Microsoft.Extensions.Logging;
using PhloxEventInfo = InWorldz.Phlox.VM.EventInfo;
using SysLinkedList = System.Collections.Generic.LinkedList<InWorldz.Phlox.VM.Interpreter>;
using SysLinkedListNode = System.Collections.Generic.LinkedListNode<InWorldz.Phlox.VM.Interpreter>;

namespace Phlox.ScriptEngine
{
    internal class PhloxExecutionScheduler
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // How many total Tick() calls per DoWork() pass
        private const int INSTRUCTION_FREQUENCY = 48;
        // Ticks per script per pass
        private const int SCRIPT_TIMESLICE = 8;
        // Slow timeslice warning threshold ms
        private const int SLOW_THRESH_MS = 250;
        // Touch repeat interval ms
        private const ulong TOUCH_INTERVAL = 100;
        // Maximum number of queued events per script — prevents listen/timer floods
        private const int MAX_EVENT_QUEUE_DEPTH = 64;

        private readonly WorkArrivedDelegate m_WorkArrived;
        private readonly PhloxEngine m_Engine;
        private readonly IWorldComm m_WorldComm;

        // All scripts regardless of run state
        private readonly System.Collections.Generic.Dictionary<UUID, Interpreter> m_AllScripts = new();
        // Guards m_AllScripts add/remove against the cross-thread stats snapshot
        // (SnapshotScriptStats, called from the estate-request thread).
        private readonly object m_AllScriptsLock = new();

        // Run queue — scripts ready to execute
        private readonly SysLinkedList m_RunQueue = new();
        private SysLinkedListNode m_NextScript;
        private readonly System.Collections.Generic.Dictionary<UUID, SysLinkedListNode> m_RunIndex = new();

        // Sleeping scripts priority queue
        private struct SleepEntry : IComparable<SleepEntry>
        {
            public UUID ItemId;
            public ulong ReadyOn;
            public WakeEvent Event;
            public enum WakeEvent { None, Timer, Touch, MinDelay }

            public int CompareTo(SleepEntry other)
                => ReadyOn < other.ReadyOn ? -1 : ReadyOn > other.ReadyOn ? 1 : 0;
        }

        private readonly C5.IntervalHeap<SleepEntry> m_SleepHeap = new();
        private readonly System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> m_StdSleepHandles = new();
        private readonly System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> m_TimerHandles = new();
        private readonly System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> m_TouchHandles = new();
        /// <summary>PHLOX-7b: one pending llMinEventDelay wake per script.</summary>
        private readonly System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> m_MinDelayHandles = new();

        // Pending events (posted from outside thread)
        private readonly Queue<PendingEvent> m_PendingEvents = new();
        private struct PendingEvent { public UUID ItemId; public PostedEvent Evt; }

        // Enable/disable requests
        private readonly Queue<EnableDisableReq> m_EnableDisableQueue = new();
        private struct EnableDisableReq { public UUID ItemId; public bool Enable; }

        // Transient suspend/resume requests (posted from estate/console threads) and the
        // suspended set itself. The set is scheduler-thread-only (drained-queue pattern, like
        // enable/disable) so it needs no lock. Suspension is deliberately NOT persisted —
        // it lives in scheduler bookkeeping, so a region restart clears it (SL semantics:
        // suspend is a live-operations pause, not a durable state). It is also NOT
        // enable/disable: listens, timers and touch subscriptions stay registered and the
        // user-visible Running flag is untouched; the script just stops receiving timeslices.
        private readonly Queue<SuspendResumeReq> m_SuspendResumeQueue = new();
        private struct SuspendResumeReq { public UUID ItemId; public bool Suspend; }
        private readonly HashSet<UUID> m_Suspended = new();

        // Reset requests
        private readonly Queue<UUID> m_PendingResets = new();

        // Syscall returns
        private readonly Queue<SyscallReturn> m_SyscallReturns = new();
        private struct SyscallReturn { public UUID ItemId; public object RetValue; public int Delay; }

        // Async syscall dispatcher (for long-running syscalls like HTTP, dataserver, etc.).
        // Replaces the vendored SmartThreadPool ("Phlox Async", MinWorkerThreads=0,
        // MaxWorkerThreads=6, IdleTimeout=60s) with the system thread pool, preserving
        // its semantics: strict FIFO start order, at most MAX_ASYNC_WORKERS calls in
        // flight, and no dedicated threads held while idle (drain workers exit when the
        // queue empties, so idle reclamation is immediate rather than 60s).
        private const int MAX_ASYNC_WORKERS = 6;
        private readonly ConcurrentQueue<SyscallShim.LongRunSyscallDelegate> m_AsyncQueue = new();
        private int m_AsyncWorkers;

        // Deferred events for scripts not yet loaded
        private readonly System.Collections.Generic.Dictionary<UUID, List<PostedEvent>> m_DeferredEvents = new();

        private readonly System.Diagnostics.Stopwatch m_SliceWatch = new();

        public PhloxExecutionScheduler(WorkArrivedDelegate workArrived, PhloxEngine engine, IWorldComm worldComm)
        {
            m_WorkArrived = workArrived;
            m_Engine = engine;
            m_WorldComm = worldComm;
        }

        // ── Called by ScriptLoader once a script is ready to run ──────────────

        internal void FinishedLoading(PhloxLoadRequest req, CompiledScript compiled)
        {
            if (m_AllScripts.ContainsKey(req.ItemID))
            {
                m_log.LogDebug("[PhloxExe]: Skipping duplicate OnRezScript for {0} (already loaded)", req.ItemID);
                return;
            }
            if (req.Prim == null)
            {
                m_log.LogError("[PhloxExe]: Prim is null for item {0}", req.ItemID);
                return;
            }

            SyscallShim shim = new SyscallShim(PerformAsyncCall);
            LSLSystemAPI sysApi = new LSLSystemAPI(m_Engine, req.Prim, req.Prim.LocalId, req.ItemID);
            shim.SystemAPI = sysApi;

            Interpreter interp;
            bool freshStart;
            bool holdStateLoadFailed = false;

            try
            {
                InWorldz.Phlox.Serialization.SerializedRuntimeState savedState = null;
                try
                {
                    savedState = m_Engine.StateManager?.LoadState(req.ItemID, compiled.AssetId);
                }
                catch (StateLoadFailedException e)
                {
                    // PHLOX-11: the row may be there and unreadable right now. A fresh start here would
                    // save over it at the next flush. Hold the script instead: loaded, visible in
                    // `phlox status` as StateLoadFailed, never run, never saved. A restart retries.
                    m_log.LogError("[PhloxExe]: Holding {0} DISABLED (state load failed, row kept): {1}", req.ItemID, e.Message);
                    holdStateLoadFailed = true;
                }
                if (savedState != null)
                {
                    try
                    {
                        var restoredRuntimeState = savedState.ToRuntimeState();
                        interp = new Interpreter(compiled, restoredRuntimeState, shim);
                        freshStart = false;
                        m_log.LogDebug("[PhloxExe]: Restored state for {0}", req.ItemID);
                    }
                    catch (Exception e)
                    {
                        m_log.LogWarning("[PhloxExe]: State restore failed for {0}: {1}", req.ItemID, e.Message);
                        interp = new Interpreter(compiled, shim);
                        freshStart = true;
                    }
                }
                else
                {
                    interp = new Interpreter(compiled, shim);
                    freshStart = true;
                }
                interp.ItemId = req.ItemID;
                shim.Interpreter = interp;
                sysApi.Script = interp;
            }
            catch (VMException e)
            {
                m_log.LogError("[PhloxExe]: VM error starting {0}: {1}", req.ItemID, e);
                return;
            }

            interp.OnStateChg += OnStateChange;
            interp.ScriptState.StartParameter = req.StartParam;
            interp.HostLocalId = req.Prim.LocalId;

            lock (m_AllScriptsLock)
                m_AllScripts[interp.ItemId] = interp;
            interp.SetScriptEventFlags();

            if (holdStateLoadFailed)
            {
                interp.ScriptState.LocalDisable |= RuntimeState.LocalDisableFlag.StateLoadFailed;
                interp.ScriptState.RunState = RuntimeState.Status.Waiting;
                return;   // no state_entry, no run queue - and StateManager refuses to save it
            }

            if (freshStart)
            {
                // New script — fire state_entry to run the script's initialization.
                //
                // PHLOX-2d: this MUST be Waiting, not Running. ProcessEventQueue only starts an
                // event when the script is Waiting (:681); anything else is queued (:687), and the
                // queue is drained by TransitionToWait, which runs only when a script that is
                // already on the run queue finishes an event. A fresh script set to Running was
                // therefore never started by anything: its state_entry sat in the queue for ever,
                // with no error and no log line. The restored-state branch below has always set
                // Waiting, which is why scripts restored from state ran and freshly compiled ones
                // did not - the manhole on 1.1.275, and every new script.
                interp.ScriptState.RunState = RuntimeState.Status.Waiting;
                sysApi.OnScriptReset();
                PostEvent(req.ItemID, new PostedEvent
                {
                    EventType = SupportedEventList.Events.STATE_ENTRY,
                    Args = Array.Empty<object>()
                });
            }
           else
            {
                // PHLOX-4: resume where the script stopped, instead of forcing Waiting.
                //
                // The saved state carries RunState, Calls, TopFrame, RunningEvent, EventQueue and a
                // relative NextWakeup, and all of it used to be restored and then thrown away by an
                // unconditional `RunState = Waiting`. Nothing re-armed a sleep and nothing put a
                // running script back on the run queue, so an interrupted handler simply never
                // finished. Worse than never: the stale frame stays on the stack, so the next
                // unrelated event pushes on top of it (RuntimeState.cs:335-336) and the interrupted
                // handler resumes NESTED inside the new event, after it.
                //
                // ScriptUnloaded saves at shutdown, so a script mid-llSleep when the region stopped
                // was saved in exactly the state that never resumed.
                var restoredRunState = interp.ScriptState.RunState;
                switch (restoredRunState)
                {
                    case RuntimeState.Status.Running:
                        // PHLOX-4b: mid-event with time left on the clock. Put it back on the run
                        // queue and it continues from its own TopFrame.
                        AddToRunQueue(interp);
                        break;

                    case RuntimeState.Status.Sleeping:
                        // NextWakeup was already restored relative to now by
                        // SerializedRuntimeState.ToRuntimeState, so it is a tick value on this run's
                        // basis and can be tracked directly.
                        //
                        // PHLOX-4c: this arm was DELETED by PHLOX-4b's edit - the Running block was
                        // replaced by slicing from `case Running` to `case Syscall`, and Sleeping sat
                        // between them. A sleeping script then fell to `default` and was restored
                        // Waiting, which is the exact PHLOX-4 symptom coming back. The dispatch is
                        // on RunState ONLY; LastSyscallIndex is read inside the Syscall arm and
                        // nowhere else, whatever value it holds.
                        TrackSleep(interp, interp.ScriptState.NextWakeup);
                        break;

                    case RuntimeState.Status.Syscall:
                        // PHLOX-4b: a syscall in flight when the region stopped has NO completion
                        // coming - whatever was going to call SysReturn died with the old process. So
                        // the only way back is to supply the return value ourselves, which is what
                        // LastSyscallIndex is persisted for.
                        ResumeFromSyscall(interp, req.ItemID);
                        break;

                    default:
                        interp.ScriptState.RunState = RuntimeState.Status.Waiting;
                        break;
                }

                // A script saved while Waiting can still hold events on its OWN queue
                // (ScriptState.EventQueue). ProcessEventQueue reads only m_PendingEvents, and that
                // queue is drained by TransitionToWait - which runs only for a script already on the
                // run queue. A restored script is on neither, so without this the queued events sit
                // there for ever.
                if (interp.ScriptState.RunState == RuntimeState.Status.Waiting)
                {
                    bool hasQueued;
                    lock (interp.ScriptState.EventQueueLock)
                        hasQueued = interp.ScriptState.EventQueue != null && interp.ScriptState.EventQueue.Count > 0;
                    if (hasQueued)
                        DeliverNextQueuedEvent(interp);
                }

                // Re-register timer if the script had one running
                if (interp.ScriptState.TimerInterval > 0)
                {
                    ulong readyOn = InWorldz.Phlox.Util.Clock.Now + (ulong)interp.ScriptState.TimerInterval;
                    TrackTimer(interp, readyOn, true);
                }

                // Re-register active listens with the world comm system
                if (interp.ScriptState.ActiveListens != null && interp.ScriptState.ActiveListens.Count > 0)
                {
                    foreach (var kvp in interp.ScriptState.ActiveListens)
                    {
                        var listen = kvp.Value;
                        UUID filterKey = UUID.Zero;
                        if (!string.IsNullOrEmpty(listen.Key))
                            UUID.TryParse(listen.Key, out filterKey);
                        m_WorldComm.Listen(
                            req.ItemID,
                            req.Prim.UUID,
                            listen.Channel,
                            listen.Name ?? string.Empty,
                            filterKey,
                            listen.Message ?? string.Empty);
                    }
                }

                bool fromCrossing = req.StateSource == (int)StateSource.PrimCrossing;
                interp.OnScriptInjected(fromCrossing);

                if (req.StateSource == (int)StateSource.RegionStart &&
                    interp.Script.FindEvent(interp.ScriptState.LSLState,
                        (int)SupportedEventList.Events.CHANGED) != null)
                {
                    const int CHANGED_REGION_START = 0x400;
                    PostEvent(req.ItemID, new PostedEvent
                    {
                        EventType = SupportedEventList.Events.CHANGED,
                        Args = new object[] { CHANGED_REGION_START }
                    });
                }

            }

            if (req.StateSource == (int)StateSource.AttachedRez &&
                req.Prim?.ParentGroup?.AttachedAvatar != UUID.Zero &&
                interp.Script.FindEvent(interp.ScriptState.LSLState,
                    (int)SupportedEventList.Events.ATTACH) != null)
            {
                PostEvent(req.ItemID, new PostedEvent
                {
                    EventType = SupportedEventList.Events.ATTACH,
                    Args = new object[] { req.Prim.ParentGroup.AttachedAvatar.ToString() }
                });
            }

            if (req.PostOnRez)
            {
                PostEvent(req.ItemID, new PostedEvent
                {
                    EventType = SupportedEventList.Events.ON_REZ,
                    Args = new object[] { req.StartParam }
                });
            }

            // Inject any events that arrived before the script was loaded
            InjectDeferredEvents(interp);
            
            // Only add to run queue if fresh — restored scripts wait for events
            if (freshStart)
                AddToRunQueue(interp);
            
            m_WorkArrived();
        }

        // ── Event posting ──────────────────────────────────────────────────────

        public void PostEvent(UUID itemId, PostedEvent evt)
        {
            lock (m_PendingEvents)
                m_PendingEvents.Enqueue(new PendingEvent { ItemId = itemId, Evt = evt });
            m_WorkArrived();
        }

        // ── Enable / disable / reset ───────────────────────────────────────────

        public void ChangeEnabledStatus(UUID itemId, bool enable)
        {
            lock (m_EnableDisableQueue)
                m_EnableDisableQueue.Enqueue(new EnableDisableReq { ItemId = itemId, Enable = enable });
            m_WorkArrived();
        }

        public void ResetScript(UUID itemId)
        {
            lock (m_PendingResets)
                m_PendingResets.Enqueue(itemId);
            m_WorkArrived();
        }

        // ── Transient suspend/resume (estate live-ops tool) ────────────────────

        /// <summary>
        /// Request a transient suspend. Returns false if this scheduler doesn't run the
        /// script (lets a multi-engine caller try the next engine). Applied on the
        /// scheduler thread in ProcessSuspendResume.
        /// </summary>
        public bool RequestSuspend(UUID itemId)
        {
            lock (m_AllScriptsLock)
                if (!m_AllScripts.ContainsKey(itemId)) return false;
            lock (m_SuspendResumeQueue)
                m_SuspendResumeQueue.Enqueue(new SuspendResumeReq { ItemId = itemId, Suspend = true });
            m_WorkArrived();
            return true;
        }

        /// <summary>
        /// Request a resume. Unknown/non-suspended scripts are a cheap no-op — callers
        /// like SceneObjectPartInventory.ResumeScripts() invoke this for every script on
        /// every rez/deed, so the known-script check here keeps that path from queueing.
        /// </summary>
        public void RequestResume(UUID itemId)
        {
            lock (m_AllScriptsLock)
                if (!m_AllScripts.ContainsKey(itemId)) return;
            lock (m_SuspendResumeQueue)
                m_SuspendResumeQueue.Enqueue(new SuspendResumeReq { ItemId = itemId, Suspend = false });
            m_WorkArrived();
        }

        public bool ResetNow(UUID itemId)
        {
            Interpreter script;
            if (!m_AllScripts.TryGetValue(itemId, out script)) return false;

            UnregisterFromNotifications(script);
            m_Engine.StateManager?.DeleteState(itemId);
            script.Reset();
            script.SetScriptEventFlags();

            PostEvent(itemId, new PostedEvent
            {
                EventType = SupportedEventList.Events.STATE_ENTRY,
                Args = Array.Empty<object>()
            });

            if (!m_RunIndex.ContainsKey(itemId) && script.ScriptState.Enabled)
                AddToRunQueue(script);

            return true;
        }

        public bool GetScriptRunning(UUID itemId)
        {
            Interpreter script;
            return m_AllScripts.TryGetValue(itemId, out script) && script.ScriptState.Enabled;
        }

        public Interpreter FindScript(UUID itemId)
        {
            m_AllScripts.TryGetValue(itemId, out var s);
            return s;
        }

        // Per-script stats snapshot for the estate Top Scripts report. Taken under the
        // m_AllScripts lock so it is safe to enumerate from the estate-request thread.
        internal struct ScriptStatSnapshot
        {
            public uint HostLocalId;
            public double ExecMs;
            public int MemoryUsed;
            public bool Enabled;
        }

        internal List<ScriptStatSnapshot> SnapshotScriptStats()
        {
            List<ScriptStatSnapshot> list;
            lock (m_AllScriptsLock)
            {
                list = new List<ScriptStatSnapshot>(m_AllScripts.Count);
                foreach (Interpreter interp in m_AllScripts.Values)
                {
                    list.Add(new ScriptStatSnapshot
                    {
                        HostLocalId = interp.HostLocalId,
                        ExecMs = interp.GetExecutionTime(),
                        MemoryUsed = interp.ScriptState.MemInfo.MemoryUsed,
                        Enabled = interp.ScriptState.Enabled
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// PHLOX-2f: a read-only view of one script for the console. Everything the last four
        /// sessions had to reach for with a debugger or infer from silence - what state the script
        /// is in, whether anything is queued for it, and what the region thinks it handles.
        /// </summary>
        internal struct ScriptStatus
        {
            public bool Found;
            public UUID ItemId;
            public uint HostLocalId;
            public string RunState;
            public bool Enabled;
            public bool GeneralEnable;
            public bool Suspended;
            public int QueuedEvents;
            public int LslState;
            public int TimerIntervalMs;
            public ulong EventMask;
            public string PendingSyscall;
            /// <summary>PHLOX-11: why the simulator holds it, if it does (e.g. StateLoadFailed).</summary>
            public string LocalDisable;
        }

        /// <summary>PHLOX-13 test seam: is this script on the run queue right now?</summary>
        internal bool IsOnRunQueue(UUID itemId)
        {
            lock (m_AllScriptsLock) return m_RunIndex.ContainsKey(itemId);
        }

        internal ScriptStatus GetStatus(UUID itemId)
        {
            lock (m_AllScriptsLock)
            {
                if (!m_AllScripts.TryGetValue(itemId, out Interpreter interp))
                    return new ScriptStatus { Found = false, ItemId = itemId };

                var st = interp.ScriptState;
                int queued;
                lock (st.EventQueueLock) queued = st.EventQueue.Count;

                return new ScriptStatus
                {
                    Found = true,
                    ItemId = itemId,
                    HostLocalId = interp.HostLocalId,
                    RunState = st.RunState.ToString(),
                    Enabled = st.Enabled,
                    GeneralEnable = st.GeneralEnable,
                    Suspended = m_Suspended.Contains(itemId),
                    QueuedEvents = queued,
                    LslState = st.LSLState,
                    TimerIntervalMs = st.TimerInterval,
                    EventMask = 0,
                    // PHLOX-2g: which syscall the script is sitting in. One word, and it is the
                    // difference between "RunState=Syscall" telling you nothing and telling you
                    // everything - this is what made the manhole a half-hour trace instead of a
                    // five-minute one.
                    PendingSyscall = st.RunState == RuntimeState.Status.Syscall
                        ? DescribeCurrentSyscall(interp) : null,
                    LocalDisable = st.LocalDisable == RuntimeState.LocalDisableFlag.None ? null : st.LocalDisable.ToString(),
                };
            }
        }

        /// <summary>Every item this scheduler holds, for a whole-object status listing.</summary>
        /// <summary>
        /// The built-in the script is currently inside, by name. The interpreter's instruction
        /// pointer sits just past the syscall opcode, so the operand it carries is the function's
        /// TableIndex; that is looked back up in the table it was emitted from.
        /// </summary>
        private static string DescribeCurrentSyscall(Interpreter interp)
        {
            try
            {
                int idx = interp.ScriptState.LastSyscallIndex;
                if (idx < 0) return "(unknown)";
                foreach (var sig in InWorldz.Phlox.Types.Defaults.AllMethods)
                    if (sig.TableIndex == idx) return sig.FunctionName;
                return "(index " + idx + ")";
            }
            catch { return "(unknown)"; }
        }

        internal List<UUID> AllItemIds()
        {
            lock (m_AllScriptsLock) return new List<UUID>(m_AllScripts.Keys);
        }

        // ── Syscall returns ────────────────────────────────────────────────────

        public void PostSyscallReturn(UUID itemId, object retValue, int delay)
        {
            lock (m_SyscallReturns)
                m_SyscallReturns.Enqueue(new SyscallReturn { ItemId = itemId, RetValue = retValue, Delay = delay });
            m_WorkArrived();
        }

        // ── Timer ──────────────────────────────────────────────────────────────

        public void SetTimer(UUID itemId, float sec)
        {
            Interpreter script;
            if (!m_AllScripts.TryGetValue(itemId, out script)) return;

            script.ScriptState.TimerInterval = (int)(sec * 1000);

            // Remove any existing timer handle
            C5.IPriorityQueueHandle<SleepEntry> existing;
            if (m_TimerHandles.TryGetValue(itemId, out existing))
            {
                m_TimerHandles.Remove(itemId);
                m_SleepHeap.Delete(existing);
                script.ScriptState.RemovePendingTimerEvent();
            }

            if (script.ScriptState.TimerInterval > 0)
            {
                ulong readyOn = InWorldz.Phlox.Util.Clock.Now + (ulong)script.ScriptState.TimerInterval;
                TrackTimer(script, readyOn, false);
            }
        }

        // ── Unload ────────────────────────────────────────────────────────────

        internal void DoUnload(UUID itemId)
        {
            Interpreter script;
            if (!m_AllScripts.TryGetValue(itemId, out script)) return;

            RemoveFromRunQueue(itemId);
            m_Suspended.Remove(itemId);
            UnregisterFromNotifications(script);
           script.OnUnload(ScriptUnloadReason.Unloaded, RuntimeState.LocalDisableFlag.None);
            m_Engine.StateManager?.ScriptUnloaded(script);
            lock (m_AllScriptsLock)
                m_AllScripts.Remove(itemId);
        }

        // ── Main work loop ─────────────────────────────────────────────────────

        /// <summary>PHLOX-10. The managed thread id of whoever last drove DoWork - the script thread.
        /// A region-side wait on a script event must never block this thread.</summary>
        public int WorkerThreadId { get; private set; } = -1;

        public WorkStatus DoWork()
        {
            WorkerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            CheckSleepingScripts();
            ProcessEventQueue();
            ProcessEnableDisable();
            ProcessSuspendResume();
            ProcessResets();
            ProcessSyscallReturns();

            bool hadRunnable = m_NextScript != null;
            DoTimeslices();

            return new WorkStatus
            {
                WorkWasDone = hadRunnable,
                WorkIsPending = HasWork(),
                NextWakeUpTime = m_SleepHeap.Count > 0 ? m_SleepHeap.FindMin().ReadyOn : ulong.MaxValue
            };
        }

        internal void Stop() { }

        // ── Private helpers ────────────────────────────────────────────────────

        private bool HasWork()
        {
            if (m_RunQueue.Count > 0) return true;
            lock (m_PendingEvents) if (m_PendingEvents.Count > 0) return true;
            lock (m_EnableDisableQueue) if (m_EnableDisableQueue.Count > 0) return true;
            lock (m_SuspendResumeQueue) if (m_SuspendResumeQueue.Count > 0) return true;
            lock (m_PendingResets) if (m_PendingResets.Count > 0) return true;
            lock (m_SyscallReturns) if (m_SyscallReturns.Count > 0) return true;
            return false;
        }

        private void DoTimeslices()
        {
            int iterations = 0;
            while (m_NextScript != null && iterations < INSTRUCTION_FREQUENCY)
            {
                var followingScript = m_NextScript.Next;
                var currentNode = m_NextScript;
                int ticks = 0;
                bool terminated = false;

                m_SliceWatch.Restart();
                while (ticks < SCRIPT_TIMESLICE)
                {
                    try { m_NextScript.Value.Tick(); }
                    catch (Exception e)
                    {
                        // PHLOX-13: TerminateWithError marks the script Killed, but this path broke out of
                        // the timeslice WITHOUT the Killed arm of CheckRunstateChange, so the node stayed on
                        // the run queue and the next pass ticked the dead script again on a torn operand
                        // stack - the "Unable to cast" and "Stack empty" stops that followed every OSSL
                        // denial. Off the queue here, once.
                        TerminateWithError(m_NextScript.Value, e);
                        m_RunIndex.Remove(m_NextScript.Value.ItemId);
                        m_RunQueue.Remove(m_NextScript);
                        terminated = true;
                    }

                    iterations++;
                    ticks++;

                    if (terminated || CheckRunstateChange()) break;
                }
                m_SliceWatch.Stop();

                // Guard against null after termination/removal
			if (!terminated && m_NextScript != null && m_NextScript == currentNode)
                {
                    m_NextScript.Value.AddExecutionTime(m_SliceWatch.Elapsed.TotalMilliseconds);

                    if (m_SliceWatch.Elapsed.TotalMilliseconds >= SLOW_THRESH_MS)
                        m_log.LogWarning("[PhloxExe]: Slow timeslice for {0} ({1:F1}ms)",
                            m_NextScript.Value.Script.AssetId, m_SliceWatch.Elapsed.TotalMilliseconds);
                }

                m_NextScript = followingScript ?? m_RunQueue.First;
            }
        }

        private bool CheckRunstateChange()
        {
            if (m_NextScript.Value.ScriptState.RunState == RuntimeState.Status.Running) return false;

            switch (m_NextScript.Value.ScriptState.RunState)
            {
                case RuntimeState.Status.Sleeping:
                    var sleeper = m_NextScript.Value;
                    RemoveFromRunQueue(sleeper.ItemId);
                    TrackSleep(sleeper, sleeper.ScriptState.NextWakeup);
                    break;

                case RuntimeState.Status.Waiting:
                    TransitionToWait();
                    break;

                case RuntimeState.Status.Killed:
                    m_RunIndex.Remove(m_NextScript.Value.ItemId);
                    m_RunQueue.Remove(m_NextScript);
                    break;

                case RuntimeState.Status.Syscall:
                    m_RunQueue.Remove(m_NextScript);
                    m_RunIndex.Remove(m_NextScript.Value.ItemId);
                    break;
            }
            return true;
        }

        private void TransitionToWait()
        {
            Interpreter script = m_NextScript.Value;
            script.ScriptState.RunningEvent?.SignalCompleted();   // PHLOX-10
            script.ScriptState.RunningEvent = null;

            while (true)
            {
                PostedEvent nextEvt;
                lock (script.ScriptState.EventQueueLock)
                {
                    if (script.ScriptState.EventQueue.Count == 0) break;
                    nextEvt = script.ScriptState.EventQueue.Dequeue();
                }
                PhloxEventInfo info = FindEventHandler(nextEvt, script);
                if (info != null)
                {
                    // PHLOX-7b: the floor applies to a queued start as much as a fresh one. Put
                    // the event back at the front, arm the wake, and let the script go idle.
                    if (MinDelayHolds(script))
                    {
                        lock (script.ScriptState.EventQueueLock)
                            script.ScriptState.EventQueue.InsertFirst(nextEvt);
                        TrackMinDelayWake(script);
                        break;
                    }
                    try
                    {
                        ArmMinDelay(script);
                        script.ScriptState.DoEvent(info, nextEvt, nextEvt.Args);
                        CheckAndResetTimer(script, info);
                        return; // stay on run queue
                    }
                    catch (VMException e)
                    {
                        TerminateWithError(script, e);
                        m_RunIndex.Remove(m_NextScript.Value.ItemId);
                        m_RunQueue.Remove(m_NextScript);
                        return;
                    }
                }
            }

			m_Engine.StateManager?.ScriptChanged(script);
            m_RunIndex.Remove(m_NextScript.Value.ItemId);
            m_RunQueue.Remove(m_NextScript);
        }

        private void CheckSleepingScripts()
        {
            ulong now = InWorldz.Phlox.Util.Clock.Now;
            while (m_SleepHeap.Count > 0)
            {
                SleepEntry s = m_SleepHeap.FindMin();
                if (now < s.ReadyOn) break;

                m_SleepHeap.DeleteMin();

                switch (s.Event)
                {
                    case SleepEntry.WakeEvent.None: m_StdSleepHandles.Remove(s.ItemId); break;
                    case SleepEntry.WakeEvent.Timer: m_TimerHandles.Remove(s.ItemId); break;
                    case SleepEntry.WakeEvent.Touch: m_TouchHandles.Remove(s.ItemId); break;
                    case SleepEntry.WakeEvent.MinDelay: m_MinDelayHandles.Remove(s.ItemId); break;
                }

                Interpreter script;
                if (!m_AllScripts.TryGetValue(s.ItemId, out script)) continue;
                if (!script.ScriptState.Enabled) continue;

                switch (s.Event)
                {
                    case SleepEntry.WakeEvent.None:
                        AddToRunQueue(script);
                        break;
                    case SleepEntry.WakeEvent.Timer:
                        PostEvent(s.ItemId, new PostedEvent
                        {
                            EventType = SupportedEventList.Events.TIMER,
                            Args = Array.Empty<object>()
                        });
                        break;
                    case SleepEntry.WakeEvent.MinDelay:
                        // PHLOX-7b: the floor has elapsed; if the script is idle with events it was
                        // made to hold, start the next one now.
                        if (script.ScriptState.RunState == RuntimeState.Status.Waiting)
                        {
                            bool queued;
                            lock (script.ScriptState.EventQueueLock)
                                queued = script.ScriptState.EventQueue != null && script.ScriptState.EventQueue.Count > 0;
                            if (queued) DeliverNextQueuedEvent(script);
                        }
                        break;
                    case SleepEntry.WakeEvent.Touch:
                        if (script.ScriptState.TouchActive)
                            PostEvent(s.ItemId, new PostedEvent
                            {
                                EventType = SupportedEventList.Events.TOUCH,
                                Args = new object[] { 1 },
                                DetectVars = script.ScriptState.CurrentTouchDetectVars
                            });
                        break;
                }
            }
        }

        private void ProcessEventQueue()
        {
            List<PendingEvent> events;
            lock (m_PendingEvents)
            {
                if (m_PendingEvents.Count == 0) return;
                events = new List<PendingEvent>(m_PendingEvents);
                m_PendingEvents.Clear();
            }

            foreach (var pe in events)
            {
                Interpreter script;
                if (!m_AllScripts.TryGetValue(pe.ItemId, out script))
                {
                    pe.Evt.SignalCompleted();   // PHLOX-10: a waiter must not wait for a script that is not here
                    AddDeferredEvent(pe.ItemId, pe.Evt);
                    continue;
                }

                if (!script.ScriptState.Enabled && pe.Evt.EventType != SupportedEventList.Events.STATE_ENTRY)
                {
                    pe.Evt.SignalCompleted();
                    continue;
                }

                PhloxEventInfo info = FindEventHandler(pe.Evt, script);
                if (info == null) { pe.Evt.SignalCompleted(); continue; }

                // Flood protection: drop events if the script's queue is full
                int queueDepth = script.ScriptState.EventQueue.Count;
                if (queueDepth >= MAX_EVENT_QUEUE_DEPTH)
                {
                    m_log.LogWarning("[PhloxExe]: Event queue full ({0} events) for script {1}, dropping {2} event",
                        queueDepth, pe.ItemId, pe.Evt.EventType);
                    pe.Evt.SignalCompleted();
                    continue;
                }

                // Suspended: accumulate instead of delivering (SL semantics — the event
                // queue keeps filling, bounded by the depth cap above, and drains on
                // resume). Note a fired llSetTimerEvent timer only re-arms when its TIMER
                // event is DELIVERED (CheckAndResetTimer), so a suspended repeating timer
                // accumulates exactly one pending TIMER event — no flood.
                if (m_Suspended.Contains(pe.ItemId))
                {
                    pe.Evt.SignalCompleted();   // PHLOX-10: it will run on resume, but nobody waits that long
                    script.ScriptState.QueueEvent(pe.Evt);
                    continue;
                }

                if (script.ScriptState.RunState == RuntimeState.Status.Waiting)
                {
                    // PHLOX-7b: llMinEventDelay - a floor between handler STARTS. wiki: events
                    // inside the window are queued and processed after it, not dropped (YEngine
                    // drops some; the wiki is the parity rule here). Hold it and arm a wake.
                    if (MinDelayHolds(script))
                    {
                        script.ScriptState.QueueEvent(pe.Evt);
                        TrackMinDelayWake(script);
                    }
                    else
                        StartEvent(pe.Evt, script, info);
                }
                else
                {
                    script.ScriptState.QueueEvent(pe.Evt);
                }
            }
        }

        private void ProcessEnableDisable()
        {
            // Drain the entire queue in one pass — the previous one-per-pass
            // behaviour caused multi-script prims to take many DoWork cycles
            // before all their scripts entered the run queue, which compounded
            // with master-scheduler wakeup gaps to produce minute-long delays
            // between script state_entry events.
            List<EnableDisableReq> batch;
            lock (m_EnableDisableQueue)
            {
                if (m_EnableDisableQueue.Count == 0) return;
                batch = new List<EnableDisableReq>(m_EnableDisableQueue);
                m_EnableDisableQueue.Clear();
            }

            foreach (var req in batch)
            {
                Interpreter script;
                if (!m_AllScripts.TryGetValue(req.ItemId, out script)) continue;

                if (req.Enable)
                {
                    script.ScriptState.GeneralEnable = true;
                    if (!m_RunIndex.ContainsKey(req.ItemId))
                        AddToRunQueue(script);
                }
                else
                {
                    script.ScriptState.GeneralEnable = false;
                    RemoveFromRunQueue(req.ItemId);
                    UnregisterFromNotifications(script);
                }
                script.SetScriptEventFlags();
            }
        }

        private void ProcessSuspendResume()
        {
            List<SuspendResumeReq> batch;
            lock (m_SuspendResumeQueue)
            {
                if (m_SuspendResumeQueue.Count == 0) return;
                batch = new List<SuspendResumeReq>(m_SuspendResumeQueue);
                m_SuspendResumeQueue.Clear();
            }

            foreach (var req in batch)
            {
                Interpreter script;
                if (!m_AllScripts.TryGetValue(req.ItemId, out script)) continue;

                if (req.Suspend)
                {
                    if (!m_Suspended.Add(req.ItemId)) continue;
                    // Park a runnable script: pull it from the run queue but leave
                    // RunState=Running — "runnable but not queued" is the parked marker
                    // the resume path re-queues. Sleep-heap entries, timers, touch and
                    // worldcomm listens all stay registered (the point of TRANSIENT
                    // suspend); their wake paths funnel through AddToRunQueue, which
                    // parks instead of queueing while suspended.
                    RemoveFromRunQueue(req.ItemId);
                }
                else
                {
                    if (!m_Suspended.Remove(req.ItemId)) continue;
                    if (!script.ScriptState.Enabled) continue; // disabled while suspended — enable path owns re-queueing

                    if (script.ScriptState.RunState == RuntimeState.Status.Running)
                    {
                        // Was mid-timeslice at suspend, or a sleep wake / syscall return
                        // parked it while suspended — continue execution where it left off.
                        AddToRunQueue(script);
                    }
                    else if (script.ScriptState.RunState == RuntimeState.Status.Waiting)
                    {
                        // Deliver the first event that accumulated during suspension (the
                        // rest drain normally via TransitionToWait once it runs).
                        DeliverNextQueuedEvent(script);
                    }
                    // Sleeping/Syscall: nothing to do — their normal completion paths
                    // re-queue through the (no longer gated) AddToRunQueue.
                }
            }
        }

        // Kick a Waiting script whose event queue filled while it was suspended. Mirrors
        // the TransitionToWait drain (dequeue -> find handler -> DoEvent -> re-arm timer)
        // but from outside the run queue, so it re-queues on success.
        private void DeliverNextQueuedEvent(Interpreter script)
        {
            while (true)
            {
                PostedEvent nextEvt;
                lock (script.ScriptState.EventQueueLock)
                {
                    if (script.ScriptState.EventQueue.Count == 0) return;
                    nextEvt = script.ScriptState.EventQueue.Dequeue();
                }
                PhloxEventInfo info = FindEventHandler(nextEvt, script);
                if (info == null) continue;
                try
                {
                    script.ScriptState.DoEvent(info, nextEvt, nextEvt.Args);
                    CheckAndResetTimer(script, info);
                    AddToRunQueue(script);
                }
                catch (VMException e)
                {
                    TerminateWithError(script, e);
                }
                return;
            }
        }

        private void ProcessResets()
        {
            // Drain the entire queue in one pass (see ProcessEnableDisable note).
            List<UUID> batch;
            lock (m_PendingResets)
            {
                if (m_PendingResets.Count == 0) return;
                batch = new List<UUID>(m_PendingResets);
                m_PendingResets.Clear();
            }
            foreach (var id in batch)
                ResetNow(id);
        }

        private void ProcessSyscallReturns()
        {
            List<SyscallReturn> returns;
            lock (m_SyscallReturns)
            {
                if (m_SyscallReturns.Count == 0) return;
                returns = new List<SyscallReturn>(m_SyscallReturns);
                m_SyscallReturns.Clear();
            }

            foreach (var ret in returns)
            {
                Interpreter script;
                if (!m_AllScripts.TryGetValue(ret.ItemId, out script)) continue;
                if (script.ScriptState.RunState != RuntimeState.Status.Syscall) continue;

                if (ret.RetValue != null)
                    script.ScriptState.Operands.Push(ret.RetValue);

                if (ret.Delay == 0)
                {
                    script.ScriptState.RunState = RuntimeState.Status.Running;
                    AddToRunQueue(script);
                }
                else
                {
                    script.ScriptState.RunState = RuntimeState.Status.Sleeping;
                    script.ScriptState.NextWakeup = InWorldz.Phlox.Util.Clock.Now + (ulong)ret.Delay;
                    TrackSleep(script, script.ScriptState.NextWakeup);
                }
            }
        }

        private void OnStateChange(Interpreter script, int newState)
        {
            UnregisterFromNotifications(script);
            script.ScriptState.StateChangePrep();

            lock (m_PendingEvents)
            {
                PostEvent(script.ItemId, new PostedEvent
                {
                    EventType = SupportedEventList.Events.STATE_EXIT,
                    Args = Array.Empty<object>()
                });
                PostEvent(script.ItemId, new PostedEvent
                {
                    EventType = SupportedEventList.Events.STATE_ENTRY,
                    Args = Array.Empty<object>(),
                    TransitionToState = newState
                });
            }
        }

		private PhloxEventInfo FindEventHandler(PostedEvent evt, Interpreter script)
		{
			if (evt.TransitionToState != PostedEvent.NO_TRANSITION)
			{
				script.ScriptState.LSLState = evt.TransitionToState;
				script.SetScriptEventFlags();
			}
			int state = script.ScriptState.LSLState;
			if (state < 0 || script.Script.StateEvents == null || state >= script.Script.StateEvents.Length)
				return null;
			return script.Script.FindEvent(state, (int)evt.EventType);
		}

        private void StartEvent(PostedEvent evt, Interpreter script, PhloxEventInfo info)
        {
            try
            {
                ArmMinDelay(script);
                script.ScriptState.SampleMemoryPeak();   // PHLOX-7b: event boundary
                script.ScriptState.DoEvent(info, evt, evt.Args);
                CheckAndResetTimer(script, info);
                AddToRunQueue(script);
            }
            catch (VMException e)
            {
                TerminateWithError(script, e);
            }
        }

        // ── PHLOX-7b: llMinEventDelay ───────────────────────────────────────────

        /// <summary>True while this script's floor has not elapsed since its last handler start.</summary>
        private static bool MinDelayHolds(Interpreter script)
            => script.ScriptState.MinEventDelayMs > 0
               && InWorldz.Phlox.Util.Clock.Now < script.ScriptState.NextEventAllowedOn;

        /// <summary>A handler is starting now: the next may not start before now + floor.</summary>
        private static void ArmMinDelay(Interpreter script)
        {
            if (script.ScriptState.MinEventDelayMs > 0)
                script.ScriptState.NextEventAllowedOn = InWorldz.Phlox.Util.Clock.Now + (ulong)script.ScriptState.MinEventDelayMs;
        }

        /// <summary>Wake at the end of the floor so the held event is delivered without a poke.</summary>
        private void TrackMinDelayWake(Interpreter script)
        {
            if (m_MinDelayHandles.ContainsKey(script.ItemId)) return;
            var entry = new SleepEntry { ItemId = script.ItemId, ReadyOn = script.ScriptState.NextEventAllowedOn, Event = SleepEntry.WakeEvent.MinDelay };
            C5.IPriorityQueueHandle<SleepEntry> h = null;
            m_SleepHeap.Add(ref h, entry);
            m_MinDelayHandles[script.ItemId] = h;
            m_WorkArrived?.Invoke();
        }

        /// <summary>llMinEventDelay(delay): the floor, applied from the next handler start on.</summary>
        public void SetMinEventDelay(UUID itemId, float seconds)
        {
            if (!m_AllScripts.TryGetValue(itemId, out Interpreter script)) return;
            int ms = seconds <= 0f ? 0 : (int)(seconds * 1000f);
            script.ScriptState.MinEventDelayMs = ms;
            if (ms == 0) script.ScriptState.NextEventAllowedOn = 0;
        }

        private void CheckAndResetTimer(Interpreter script, PhloxEventInfo info)
        {
            if (info.EventType == (int)SupportedEventList.Events.TIMER &&
                script.ScriptState.TimerInterval > 0 &&
                !m_TimerHandles.ContainsKey(script.ItemId))
            {
                ulong readyOn = InWorldz.Phlox.Util.Clock.Now + (ulong)script.ScriptState.TimerInterval;
                TrackTimer(script, readyOn, false);
            }
        }

        private void AddToRunQueue(Interpreter script)
        {
            // Suspended scripts never enter the run queue (removal at suspend + this gate
            // keeps DoTimeslices' hot path free of per-slice flag checks and keeps
            // HasWork() honest — a run queue holding only suspended scripts would make
            // the master scheduler busy-spin). Park as RunState=Running-but-not-queued:
            // that is the marker ProcessSuspendResume re-queues on resume, so sleep wakes
            // and syscall returns that land during suspension aren't lost.
            if (m_Suspended.Contains(script.ItemId))
            {
                script.ScriptState.RunState = RuntimeState.Status.Running;
                return;
            }

            if (m_RunIndex.ContainsKey(script.ItemId)) return;

            var node = m_RunQueue.AddLast(script);
            m_RunIndex[script.ItemId] = node;
            script.ScriptState.RunState = RuntimeState.Status.Running;

            if (m_NextScript == null)
                m_NextScript = node;
        }

        private void RemoveFromRunQueue(UUID itemId)
        {
            if (m_NextScript != null && m_NextScript.Value.ItemId == itemId)
            {
                m_NextScript = m_NextScript.Next ?? m_RunQueue.First;
                if (m_NextScript != null && m_NextScript.Value.ItemId == itemId)
                    m_NextScript = null;
            }

            SysLinkedListNode node;
            if (m_RunIndex.TryGetValue(itemId, out node))
            {
                m_RunIndex.Remove(itemId);
                m_RunQueue.Remove(node);
            }
        }

        private void UnregisterFromNotifications(Interpreter script)
        {
            C5.IPriorityQueueHandle<SleepEntry> h;
            if (m_StdSleepHandles.TryGetValue(script.ItemId, out h))
            {
                m_StdSleepHandles.Remove(script.ItemId);
                m_SleepHeap.Delete(h);
            }
            if (m_TimerHandles.TryGetValue(script.ItemId, out h))
            {
                m_TimerHandles.Remove(script.ItemId);
                m_SleepHeap.Delete(h);
            }
            if (m_TouchHandles.TryGetValue(script.ItemId, out h))
            {
                m_TouchHandles.Remove(script.ItemId);
                m_SleepHeap.Delete(h);
            }

            m_WorldComm.DeleteListener(script.ItemId);
        }

        /// <summary>
        /// PHLOX-4b. Bring a script back that was captured mid-syscall.
        /// <para>
        /// The interrupted call cannot be re-issued and its completion will never arrive, so the
        /// function's return value is pushed here and the script continues from the instruction after
        /// the call. A Void function pushes nothing, which is exactly what its caller expects.
        /// </para>
        /// <para>
        /// A state saved before <c>LastSyscallIndex</c> existed has -1 and cannot be resumed - there is
        /// no way to know what value to push - so it falls back to the old behaviour and says so.
        /// </para>
        /// </summary>
        private void ResumeFromSyscall(Interpreter interp, UUID itemId)
        {
            int index = interp.ScriptState.LastSyscallIndex;
            // FunctionSig is a struct, so "not found" needs its own flag.
            InWorldz.Phlox.Types.FunctionSig sig = default;
            bool found = false;
            if (index >= 0)
            {
                foreach (var m in InWorldz.Phlox.Types.Defaults.AllMethods)
                {
                    if (m.TableIndex == index) { sig = m; found = true; break; }
                }
            }

            if (!found)
            {
                m_log.LogWarning(
                    "[PhloxExe]: {Item} was saved mid-syscall with no recorded function (index {Index}); restored as Waiting - the interrupted call does not resume",
                    itemId, index);
                interp.ScriptState.RunState = RuntimeState.Status.Waiting;
                return;
            }

            if (sig.ReturnType != InWorldz.Phlox.Types.VarType.Void)
                interp.ScriptState.Operands.Push(DefaultValueFor(sig.ReturnType));

            m_log.LogInformation(
                "[PhloxExe]: {Item} was saved inside {Function}; resuming with that call's default return value",
                itemId, sig.FunctionName);

            AddToRunQueue(interp);
        }

        /// <summary>PHLOX-4b: the value an interrupted call of this return type contributes.</summary>
        private static object DefaultValueFor(InWorldz.Phlox.Types.VarType type)
        {
            switch (type)
            {
                case InWorldz.Phlox.Types.VarType.Integer: return 0;
                case InWorldz.Phlox.Types.VarType.Float:   return 0.0f;
                case InWorldz.Phlox.Types.VarType.Vector:  return OpenMetaverse.Vector3.Zero;
                case InWorldz.Phlox.Types.VarType.Rotation:return OpenMetaverse.Quaternion.Identity;
                case InWorldz.Phlox.Types.VarType.List:    return new InWorldz.Phlox.Types.LSLList(new System.Collections.Generic.List<object>());
                case InWorldz.Phlox.Types.VarType.Key:     return OpenMetaverse.UUID.Zero.ToString();
                case InWorldz.Phlox.Types.VarType.String:  return string.Empty;
                default:                                   return null;
            }
        }
        private void TrackSleep(Interpreter script, ulong readyOn)
        {
            C5.IPriorityQueueHandle<SleepEntry> h = null;
            m_SleepHeap.Add(ref h, new SleepEntry
            {
                ItemId = script.ItemId,
                ReadyOn = readyOn,
                Event = SleepEntry.WakeEvent.None
            });
            m_StdSleepHandles[script.ItemId] = h;
            script.ScriptState.NextWakeup = readyOn;
        }

        private void TrackTimer(Interpreter script, ulong readyOn, bool fromRestore)
        {
            C5.IPriorityQueueHandle<SleepEntry> h = null;
            m_SleepHeap.Add(ref h, new SleepEntry
            {
                ItemId = script.ItemId,
                ReadyOn = readyOn,
                Event = SleepEntry.WakeEvent.Timer
            });
            m_TimerHandles[script.ItemId] = h;
            if (!fromRestore)
                script.ScriptState.TimerLastScheduledOn = InWorldz.Phlox.Util.Clock.Now;
            script.ScriptState.RemovePendingTimerEvent();
        }

        private void TerminateWithError(Interpreter script, Exception e)
        {
            script.ScriptState.RunningEvent?.SignalCompleted();   // PHLOX-10
            script.ScriptState.RunState = RuntimeState.Status.Killed;
            script.ScriptState.LastSyscallIndex = -1;   // PHLOX-13: not parked in anything any more
            m_log.LogError("[PhloxExe]: Script {0} asset {1} terminated: {2}",
                script.ItemId, script.Script.AssetId, e);
            try
            {
                script.ShoutError($"Script {script.Script.AssetId} stopped: {e.Message}");
            }
            catch { /* ignore errors during error reporting */ }
        }

        private void PerformAsyncCall(SyscallShim.LongRunSyscallDelegate call)
        {
            m_AsyncQueue.Enqueue(call);
            EnsureAsyncWorker();
        }

        private void EnsureAsyncWorker()
        {
            // Spawn a drain worker unless the concurrency cap is already saturated.
            // CAS loop so a burst of posts can never exceed MAX_ASYNC_WORKERS.
            while (!m_AsyncQueue.IsEmpty)
            {
                int current = Volatile.Read(ref m_AsyncWorkers);
                if (current >= MAX_ASYNC_WORKERS)
                    return;
                if (Interlocked.CompareExchange(ref m_AsyncWorkers, current + 1, current) == current)
                {
                    Task.Run(DrainAsyncQueue);
                    return;
                }
            }
        }

        private void DrainAsyncQueue()
        {
            try
            {
                while (m_AsyncQueue.TryDequeue(out SyscallShim.LongRunSyscallDelegate call))
                {
                    try { call(); }
                    catch (Exception e)
                    {
                        m_log.LogError("[PhloxExe]: Async syscall exception: {0}", e);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref m_AsyncWorkers);
                // An item may land between the failed TryDequeue and the decrement;
                // re-kick so it cannot strand in the queue with zero workers.
                EnsureAsyncWorker();
            }
        }

        private void InjectDeferredEvents(Interpreter script)
        {
            List<PostedEvent> deferred;
            if (!m_DeferredEvents.TryGetValue(script.ItemId, out deferred)) return;
            m_DeferredEvents.Remove(script.ItemId);
            foreach (var evt in deferred)
                PostEvent(script.ItemId, evt);
        }

        private void AddDeferredEvent(UUID itemId, PostedEvent evt)
        {
            if (!m_DeferredEvents.TryGetValue(itemId, out var list))
            {
                list = new List<PostedEvent>();
                m_DeferredEvents[itemId] = list;
            }
            list.Add(evt);
        }
    }
}
