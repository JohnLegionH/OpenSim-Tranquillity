/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLua.ScriptEngine.Bindings;
using OpenMetaverse;

namespace SLua.ScriptEngine
{
    /// <summary>One request to load a script this engine owns.</summary>
    internal sealed class LoadRequest
    {
        public uint LocalID;
        public UUID ItemID;

        /// <summary>The asset the item had when the request was made; used to fetch the text when <see cref="Source"/> is empty.</summary>
        public UUID AssetID;

        /// <summary>The script text from the rez event; empty when the loader must fetch it.</summary>
        public string Source;
    }

    /// <summary>Fetches a script's text from the asset service.</summary>
    internal interface IScriptTextSource
    {
        /// <summary>Calls <paramref name="done"/> with the asset's text, or null when it is missing or is not script text. May call on any thread.</summary>
        void Fetch(UUID assetId, Action<string> done);
    }

    /// <summary>What the engine asks of a loader; the engine's tests substitute their own.</summary>
    internal interface IScriptLoader : IDisposable
    {
        /// <summary>Queue a load; returns at once. The serial identifies this load for <see cref="WaitForCompileErrors"/>.</summary>
        long Post(LoadRequest request);

        /// <summary>The item is no longer this engine's: forget its load and release its ready script.</summary>
        void Disown(UUID itemID);

        /// <summary>
        /// The compile errors, in the editor's format, of the load this thread last posted for the item (else the
        /// latest), waiting for that load to finish; empty when it compiled. Null when this engine has no load of the item.
        /// </summary>
        List<string> WaitForCompileErrors(UUID itemID, TimeSpan timeout);

        bool IsLoading(UUID itemID);
        bool IsReady(UUID itemID);

        /// <summary>Raised on the loader's thread when a script has compiled and is held ready: its compiled bytes, not a script object.</summary>
        event Action<ReadyScript> ScriptReady;

        /// <summary>Raised when a compile failed and no editor collected its errors in time; the owner is told.</summary>
        event Action<LoadFailure> FailureUnclaimed;
    }

    /// <summary>A compile failure nobody collected.</summary>
    internal sealed class LoadFailure
    {
        public uint LocalID { get; init; }
        public UUID ItemID { get; init; }
        public List<string> Errors { get; init; }
    }

    /// <summary>A compiled script held ready: its compiled bytes. The scheduler worker makes the script object from them when the script starts.</summary>
    internal sealed class ReadyScript
    {
        public UUID ItemID { get; init; }
        public uint LocalID { get; init; }
        public UUID AssetID { get; init; }
        public ScriptLanguage Language { get; init; }
        public long Serial { get; init; }

        /// <summary>The compiled asset: header and bytecode.</summary>
        public byte[] Asset { get; init; }
    }

    /// <summary>
    /// Fetches a script's text, compiles it off the region's threads, and holds the compiled bytes ready. It makes no
    /// script object and no virtual machine: a worker of the scheduler makes the script from the bytes when the script
    /// starts. Compile outcomes are published per load (by serial), so an editor that saved a script is answered for
    /// that save, as the region's other engines do; a failure no editor collects in time is announced for the owner.
    /// </summary>
    internal sealed class ScriptLoader : IScriptLoader
    {
        public const string ErrorWaitTimedOut = "timedout waiting for errors";

        // Outcomes of earlier saves kept per item, for an editor still waiting on one when a newer save has finished.
        private const int OutcomesKept = 8;

        private readonly ScriptCompiler m_compiler;
        private readonly bool m_ownsCompiler;
        private readonly IScriptTextSource m_assets;
        private readonly IEngineLog m_log;

        private readonly BlockingCollection<Action> m_work = new BlockingCollection<Action>();
        private readonly Thread m_thread;

        private readonly object m_lock = new object();
        private readonly Dictionary<UUID, long> m_latest = new Dictionary<UUID, long>();
        private readonly Dictionary<UUID, int> m_inFlight = new Dictionary<UUID, int>();
        private readonly Dictionary<UUID, List<(long Serial, List<string> Errors)>> m_outcomes = new Dictionary<UUID, List<(long Serial, List<string> Errors)>>();
        private readonly Dictionary<UUID, ReadyScript> m_ready = new Dictionary<UUID, ReadyScript>();
        private readonly Dictionary<UUID, long> m_unclaimed = new Dictionary<UUID, long>();
        private long m_serial;
        private volatile bool m_stopped;

        // The serial this thread posted last, so the editor that saved is answered for that save.
        [ThreadStatic] private static (UUID Item, long Serial) t_posted;

        public event Action<ReadyScript> ScriptReady;
        public event Action<LoadFailure> FailureUnclaimed;

        /// <summary>How long a compile failure is held for an editor to collect before the owner is told.</summary>
        public TimeSpan UnclaimedGrace { get; set; } = TimeSpan.FromSeconds(2);

        public ScriptLoader(ScriptCompiler compiler, IScriptTextSource assets, IEngineLog log, bool ownsCompiler = false)
        {
            m_compiler = compiler;
            m_ownsCompiler = ownsCompiler;
            m_assets = assets;
            m_log = log;
            m_thread = new Thread(Run) { IsBackground = true, Name = EngineIdentity.EngineName + " load" };
            m_thread.Start();
        }

        public int LoaderThreadId => m_thread.ManagedThreadId;

        public int ReadyCount
        {
            get { lock (m_lock) return m_ready.Count; }
        }

        public long Post(LoadRequest request)
        {
            long serial;
            lock (m_lock)
            {
                serial = ++m_serial;
                m_latest[request.ItemID] = serial;
                m_inFlight.TryGetValue(request.ItemID, out int n);
                m_inFlight[request.ItemID] = n + 1;
            }
            t_posted = (request.ItemID, serial);
            Enqueue(() => Begin(request, serial));
            return serial;
        }

        public void Disown(UUID itemID)
        {
            lock (m_lock)
            {
                m_latest.Remove(itemID);
                m_inFlight.Remove(itemID);
                m_outcomes.Remove(itemID);
                m_unclaimed.Remove(itemID);
                m_ready.Remove(itemID);
                Monitor.PulseAll(m_lock);
            }
        }

        public bool IsLoading(UUID itemID)
        {
            lock (m_lock) return m_inFlight.TryGetValue(itemID, out int n) && n > 0;
        }

        public bool IsReady(UUID itemID)
        {
            lock (m_lock) return m_ready.ContainsKey(itemID);
        }

        /// <summary>The ready script for the item, or null.</summary>
        public ReadyScript GetReady(UUID itemID)
        {
            lock (m_lock) return m_ready.TryGetValue(itemID, out ReadyScript r) ? r : null;
        }

        public List<string> WaitForCompileErrors(UUID itemID, TimeSpan timeout)
        {
            DateTime until = DateTime.UtcNow + timeout;
            lock (m_lock)
            {
                if (!m_latest.TryGetValue(itemID, out long wanted))
                    return null;
                if (t_posted.Item == itemID && t_posted.Serial <= wanted)
                    wanted = t_posted.Serial;
                t_posted = default;
                while (true)
                {
                    if (!m_latest.ContainsKey(itemID))
                        return null; // disowned while waiting
                    if (OutcomeFor(itemID, wanted) is List<string> errors)
                    {
                        m_unclaimed.Remove(itemID); // the editor has the errors
                        return new List<string>(errors);
                    }
                    TimeSpan left = until - DateTime.UtcNow;
                    if (m_stopped || left <= TimeSpan.Zero)
                        return new List<string> { ErrorWaitTimedOut };
                    Monitor.Wait(m_lock, left);
                }
            }
        }

        /// <summary>The outcome of the wanted load, or of a newer one if the wanted load never reports. Under the lock.</summary>
        private List<string> OutcomeFor(UUID itemID, long wanted)
        {
            if (!m_outcomes.TryGetValue(itemID, out var list))
                return null;
            (long Serial, List<string> Errors)? newer = null;
            foreach (var o in list)
            {
                if (o.Serial == wanted)
                    return o.Errors;
                if (o.Serial > wanted && (newer == null || o.Serial < newer.Value.Serial))
                    newer = o;
            }
            return newer?.Errors;
        }

        public void Dispose()
        {
            if (m_stopped) return;
            m_stopped = true;
            try { m_work.CompleteAdding(); }
            catch (ObjectDisposedException) { }
            lock (m_lock) Monitor.PulseAll(m_lock);
            m_thread.Join(TimeSpan.FromSeconds(30));
            if (m_ownsCompiler)
                m_compiler.Dispose();
        }

        private void Enqueue(Action work)
        {
            try { m_work.Add(work); }
            catch (InvalidOperationException) { /* stopping: the work is dropped */ }
        }

        private void Run()
        {
            try
            {
                foreach (Action work in m_work.GetConsumingEnumerable())
                {
                    try { work(); }
                    catch (Exception e) { m_log?.Error("a load step failed: " + e); }
                }
            }
            finally
            {
                lock (m_lock) m_ready.Clear();
            }
        }

        private bool IsLatest(UUID itemID, long serial)
        {
            lock (m_lock) return m_latest.TryGetValue(itemID, out long latest) && latest == serial;
        }

        private void Begin(LoadRequest request, long serial)
        {
            if (m_stopped) return;
            try
            {
                if (!string.IsNullOrEmpty(request.Source))
                {
                    Compile(request, serial, request.Source);
                    return;
                }
                if (request.AssetID == UUID.Zero)
                {
                    Finish(request, serial, new List<string> { "script item not found" });
                    return;
                }
                m_assets.Fetch(request.AssetID, text => Enqueue(() => Compile(request, serial, text)));
            }
            catch (Exception e)
            {
                Fail(request, serial, e);
            }
        }

        private void Compile(LoadRequest request, long serial, string text)
        {
            if (m_stopped) return;
            try
            {
                if (text == null)
                {
                    Finish(request, serial, new List<string> { "the script's asset is not script text" });
                    return;
                }
                CompileResult result = m_compiler.Compile(text);
                if (!result.Success)
                {
                    Finish(request, serial, new List<string>(result.EditorErrors()));
                    return;
                }
                if (IsLatest(request.ItemID, serial))
                    Install(request, serial, result);
                Finish(request, serial, new List<string>());
            }
            catch (Exception e)
            {
                Fail(request, serial, e);
            }
        }

        /// <summary>Holds the compiled bytes ready and announces them. Loader thread only.</summary>
        private void Install(LoadRequest request, long serial, CompileResult result)
        {
            var ready = new ReadyScript
            {
                ItemID = request.ItemID,
                LocalID = request.LocalID,
                AssetID = request.AssetID,
                Language = result.Language,
                Serial = serial,
                Asset = result.Asset,
            };
            bool kept;
            lock (m_lock)
            {
                kept = m_latest.TryGetValue(request.ItemID, out long latest) && latest == serial;
                if (kept)
                    m_ready[request.ItemID] = ready;
            }
            if (!kept)
                return; // disowned or saved again while the compile ran
            try { ScriptReady?.Invoke(ready); }
            catch (Exception e) { m_log?.Error($"a listener of the ready script {request.ItemID} failed: {e}"); }
        }

        private void Fail(LoadRequest request, long serial, Exception e)
        {
            m_log?.Error($"Script load FAILED for item {request.ItemID}: {e}");
            Finish(request, serial, new List<string> { "script load failed: " + e.Message });
        }

        /// <summary>Records the load's outcome for an editor waiting on it and wakes the waiters.</summary>
        private void Finish(LoadRequest request, long serial, List<string> errors)
        {
            lock (m_lock)
            {
                if (m_inFlight.TryGetValue(request.ItemID, out int n))
                {
                    if (n <= 1) m_inFlight.Remove(request.ItemID);
                    else m_inFlight[request.ItemID] = n - 1;
                }
                if (m_latest.ContainsKey(request.ItemID))
                {
                    if (!m_outcomes.TryGetValue(request.ItemID, out var list))
                        m_outcomes[request.ItemID] = list = new List<(long Serial, List<string> Errors)>();
                    list.Add((serial, errors));
                    if (list.Count > OutcomesKept)
                        list.RemoveAt(0);
                    if (errors.Count > 0 && m_latest[request.ItemID] == serial)
                        m_unclaimed[request.ItemID] = serial;
                    else
                        errors = null; // nothing to announce
                }
                else
                    errors = null;
                Monitor.PulseAll(m_lock);
            }
            if (errors != null)
                AnnounceIfUnclaimed(request, serial, errors);
        }

        /// <summary>After the grace, tells the listener of a failure that no editor collected and that is still the latest of its item.</summary>
        private void AnnounceIfUnclaimed(LoadRequest request, long serial, List<string> errors)
        {
            Task.Delay(UnclaimedGrace).ContinueWith(_ =>
            {
                lock (m_lock)
                {
                    if (m_stopped || !m_unclaimed.TryGetValue(request.ItemID, out long pending) || pending != serial)
                        return;
                    m_unclaimed.Remove(request.ItemID);
                }
                try { FailureUnclaimed?.Invoke(new LoadFailure { LocalID = request.LocalID, ItemID = request.ItemID, Errors = errors }); }
                catch (Exception e) { m_log?.Error($"a listener of the failed script {request.ItemID} failed: {e}"); }
            }, TaskScheduler.Default);
        }
    }
}
