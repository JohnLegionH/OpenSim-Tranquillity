/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// What a notecard read asks for, as the trace writes it: the call, and the line (or the count).
    /// </summary>
    internal readonly struct NotecardRead
    {
        public readonly string Call;
        public readonly bool IsCount;
        public readonly int Line;
        public readonly bool IsSegment;
        public readonly int Offset;
        public readonly int Length;

        private NotecardRead(string call, bool isCount, int line, bool isSegment, int offset, int length)
        {
            Call = call; IsCount = isCount; Line = line; IsSegment = isSegment; Offset = offset; Length = length;
        }

        public static NotecardRead Count(string call) => new(call, true, 0, false, 0, 0);
        public static NotecardRead OfLine(string call, int line) => new(call, false, line, false, 0, 0);
        public static NotecardRead Segment(string call, int line, int offset, int length) => new(call, false, line, true, offset, length);

        /// <summary>"count", "line 5" or "line 5 offset 2 length 10".</summary>
        public string What => IsCount ? "count" : IsSegment ? $"line {Line} offset {Offset} length {Length}" : $"line {Line}";
    }

    /// <summary>
    /// The notecard trace ([InWorldz.Phlox] NotecardTrace, off by default; the engine holds no instance while it is off).
    /// One INFO line per step of a notecard request, each carrying the query id: the request; a fetch's start and end;
    /// the post to the prim's scripts; for each receiving script, queued, dropped (with the rule) or cleared; the handler
    /// start. Besides the lines it keeps each script's own requests until the answer is delivered to that script or a
    /// loss is recorded for it, which the engine's pending-query set does not (it forgets an id before the answer is
    /// queued), so the status output can say what a script is still waiting on.
    /// </summary>
    internal sealed class PhloxNotecardTrace
    {
        /// <summary>The tag every trace line starts with.</summary>
        public const string Tag = "[NotecardTrace]";

        // The reasons an answer is not delivered, as the lines say them.
        public const string ReasonNotLoaded = "script not loaded by this engine (removed, failed to compile, or another engine's script)";
        public const string ReasonLoadCancelled = "script's load cancelled while its events were held";
        public const string ReasonDeferredFull = "deferred backlog full (32 events held for a script still loading)";
        public const string ReasonDeferredExpired = "deferred events expired (script not loaded within 60 s)";
        public const string ReasonDisabled = "script disabled (stopped, crashed or paused by the parcel)";
        public const string ReasonNoHandler = "no dataserver handler in the script's current state";
        public const string ReasonQueueFull = "queue full (64 events)";
        public const string ReasonHalfLimit = "half limit (another script's answer, queue holds 32 or more events)";
        public const string ReasonHeldForCrossing = " while the script is held for a crossing";
        public const string ReasonDequeuedNoHandler = "dequeued, but the script's current state has no dataserver handler";
        public const string ReasonNotOwed = "request no longer owed to the asker (cleared earlier by a reset, state change, stop, unload or crossing hold)";
        public const string ReasonPrimGone = "prim deleted before the answer was posted";
        public const string ReasonNotAccepted = "the engine did not accept the event (no scheduler)";

        private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(PhloxNotecardTrace));

        /// <summary>One script's request, kept until its answer is delivered to that script or a loss is recorded.</summary>
        internal sealed class Request
        {
            public UUID Query;
            public UUID Item;
            public string ScriptName;
            public string Card;
            public UUID Asset;
            public string What;
            public ulong AskedOn;
            public ulong FetchStartedOn;
            public bool FetchEndLogged;
        }

        /// <summary>The oldest waiting request of a script, as the status output shows it.</summary>
        internal readonly record struct Waiting(int Count, ulong OldestAgeMs, string Card, string What, UUID Query);

        // Requests the asking scripts still wait on, by query id.
        private readonly ConcurrentDictionary<UUID, Request> m_waiting = new();
        // What a post needs to tag the events it makes (the time of the request), by query id, until the post.
        private readonly ConcurrentDictionary<UUID, (UUID Item, ulong AskedOn)> m_posting = new();
        private readonly ConcurrentDictionary<UUID, string> m_names = new();
        private readonly ConcurrentDictionary<UUID, int> m_lost = new();

        private static ulong Now => InWorldz.Phlox.Util.Clock.Now;

        private string Label(UUID item)
            => m_names.TryGetValue(item, out string name) ? $"\"{name}\" ({item})" : item.ToString();

        /// <summary>Remember a script's name for the lines that name it.</summary>
        public void Name(UUID item, string name)
        {
            if (!string.IsNullOrEmpty(name)) m_names[item] = name;
        }

        // ── 1. the request ─────────────────────────────────────────────────────

        public void Requested(UUID query, UUID item, string scriptName, uint primLocalId, string primName,
            string card, UUID asset, NotecardRead read, bool cached)
        {
            Name(item, scriptName);
            ulong now = Now;
            m_waiting[query] = new Request
            {
                Query = query, Item = item, ScriptName = scriptName, Card = card, Asset = asset, What = read.What, AskedOn = now
            };
            m_posting[query] = (item, now);
            m_log.LogInformation(Tag + ": q={0} request {1} script={2} prim=\"{3}\" ({4}) notecard=\"{5}\" asset={6} {7} source={8}",
                query, read.Call, Label(item), primName, primLocalId, card, asset, read.What, cached ? "cache" : "fetch");
        }

        /// <summary>A read refused before a query id was made: NULL_KEY goes back to the script and no answer will follow.</summary>
        public void Refused(UUID item, string scriptName, uint primLocalId, string primName, string card, NotecardRead read)
        {
            Name(item, scriptName);
            m_lost.AddOrUpdate(item, 1, (_, n) => n + 1);
            m_log.LogInformation(Tag + ": q=none request {0} script={1} prim=\"{2}\" ({3}) notecard=\"{4}\" {5} refused: no such notecard in the prim, and the name is not a notecard asset id; NULL_KEY is returned and no answer will follow",
                read.Call, Label(item), primName, primLocalId, card, read.What);
        }

        // ── 2. the fetch ───────────────────────────────────────────────────────

        public void FetchStarted(UUID query)
        {
            if (m_waiting.TryGetValue(query, out Request r)) r.FetchStartedOn = Now;
            m_log.LogInformation(Tag + ": q={0} fetch start", query);
        }

        /// <summary>The fetch ended with an answer to post; <paramref name="result"/> says what it found ("lines=12").</summary>
        public void FetchEnded(UUID query, string result)
        {
            ulong took = 0;
            if (m_waiting.TryGetValue(query, out Request r))
            {
                took = Now - r.FetchStartedOn;
                r.FetchEndLogged = true;
            }
            m_log.LogInformation(Tag + ": q={0} fetch end took={1} ms result={2}", query, took, result);
        }

        /// <summary>
        /// The fetch ended with nothing to post (null, no data, wrong type, an exception): the end line, then the loss of
        /// the asking script's request, which no answer will ever follow.
        /// </summary>
        public void FetchFailed(UUID query, string result)
        {
            Request r = null;
            ulong took = 0;
            if (m_waiting.TryGetValue(query, out r) && r.FetchStartedOn != 0) took = Now - r.FetchStartedOn;
            m_log.LogInformation(Tag + ": q={0} fetch end took={1} ms result={2}", query, took, result);
            m_posting.TryRemove(query, out _);
            if (m_waiting.TryRemove(query, out r))
            {
                m_lost.AddOrUpdate(r.Item, 1, (_, n) => n + 1);
                m_log.LogInformation(Tag + ": q={0} lost script={1} no answer will be posted: fetch result {2}", query, Label(r.Item), result);
            }
        }

        public bool FetchEndLogged(UUID query) => m_waiting.TryGetValue(query, out Request r) && r.FetchEndLogged;

        // ── 3. the post ────────────────────────────────────────────────────────

        /// <summary>
        /// The answer is being posted: the time of the request, for the events to carry, or false when the query is not one
        /// the trace follows. The post consumes it.
        /// </summary>
        public bool TakeForPost(UUID query, out ulong askedOn)
        {
            if (m_posting.TryRemove(query, out var p)) { askedOn = p.AskedOn; return true; }
            askedOn = 0;
            return false;
        }

        public void Posted(string query, uint primLocalId, int scripts, int otherEngines)
            => m_log.LogInformation(Tag + ": q={0} post prim={1} goes to {2} script(s) of the prim{3}", query, primLocalId, scripts,
                otherEngines == 0 ? "" : $"; {otherEngines} other script engine(s) are offered it too (their queues are not traced)");

        // ── 4. each receiving script ───────────────────────────────────────────

        public void Queued(string query, UUID item, int depth, string note)
            => m_log.LogInformation(Tag + ": q={0} queued script={1} depth={2}{3}", query, Label(item), depth,
                string.IsNullOrEmpty(note) ? "" : " (" + note + ")");

        /// <summary>The answer for this script is not delivered: the rule, and the loss of the script's own request when it is the asker.</summary>
        public void Dropped(string query, UUID item, string reason)
        {
            m_log.LogInformation(Tag + ": q={0} dropped script={1} reason={2}", query, Label(item), reason);
            if (UUID.TryParse(query, out UUID q)) RequestEnds(q, item, true);
        }

        // ── 5. delivery ────────────────────────────────────────────────────────

        public void Delivered(string query, UUID item, ulong askedOn)
        {
            m_log.LogInformation(Tag + ": q={0} deliver script={1} handler starts {2} ms after the request", query, Label(item), Now - askedOn);
            if (UUID.TryParse(query, out UUID q)) RequestEnds(q, item, false);
        }

        private void RequestEnds(UUID query, UUID item, bool lost)
        {
            if (m_waiting.TryGetValue(query, out Request r) && r.Item == item && m_waiting.TryRemove(query, out _))
            {
                m_posting.TryRemove(query, out _);
                if (lost) m_lost.AddOrUpdate(item, 1, (_, n) => n + 1);
            }
        }

        // ── what ends a script's requests ──────────────────────────────────────

        /// <summary>
        /// The script's pending dataserver replies were dropped (reset, state change, stop, unload, crossing hold): each
        /// request still waiting is a recorded loss. <paramref name="forgetScript"/> (an unload) also forgets its post state and loss count.
        /// </summary>
        public void ClearScript(UUID item, string why, bool forgetScript = false)
        {
            foreach (Request r in m_waiting.Values.Where(r => r.Item == item).OrderBy(r => r.AskedOn).ToList())
            {
                if (!m_waiting.TryRemove(r.Query, out _)) continue;
                m_posting.TryRemove(r.Query, out _);
                m_lost.AddOrUpdate(item, 1, (_, n) => n + 1);
                m_log.LogInformation(Tag + ": q={0} lost script={1} request cleared by {2}, asked {3} ms ago: its answer is no longer owed",
                    r.Query, Label(item), why, Now - r.AskedOn);
            }
            if (forgetScript)
            {
                foreach (var p in m_posting.Where(p => p.Value.Item == item).ToList()) m_posting.TryRemove(p.Key, out _);
                m_lost.TryRemove(item, out _);   // the name stays: a late answer for the gone script is still logged by it
            }
        }

        // ── what a script is waiting on ────────────────────────────────────────

        /// <summary>The script's waiting requests: how many, and the oldest.</summary>
        public Waiting WaitingOf(UUID item)
        {
            Request oldest = null;
            int count = 0;
            foreach (Request r in m_waiting.Values)
            {
                if (r.Item != item) continue;
                count++;
                if (oldest == null || r.AskedOn < oldest.AskedOn) oldest = r;
            }
            return oldest == null ? new Waiting(0, 0, null, null, UUID.Zero)
                : new Waiting(count, Now - oldest.AskedOn, oldest.Card, oldest.What, oldest.Query);
        }

        public int LostOf(UUID item) => m_lost.TryGetValue(item, out int n) ? n : 0;

        /// <summary>The lines the script's status output adds.</summary>
        public string[] StatusLines(UUID item)
        {
            Waiting w = WaitingOf(item);
            int lost = LostOf(item);
            var lines = new List<string>
            {
                $"  notecard trace: {w.Count} request(s) waiting for an answer, {lost} lost (see the [NotecardTrace] lines)"
            };
            if (w.Count > 0)
                lines.Add($"    oldest: {w.OldestAgeMs} ms, notecard \"{w.Card}\", {w.What}, query {w.Query}");
            return lines.ToArray();
        }

        /// <summary>
        /// A region stop, or a script restored: each of the script's requests still waiting, one line each. With none, a
        /// restore still says so, because the saved state holds no request (the pending query ids are not saved).
        /// </summary>
        public void LogWaiting(string when, UUID item = default)
        {
            ulong now = Now;
            foreach (Request r in m_waiting.Values
                .Where(r => item == UUID.Zero || r.Item == item).OrderBy(r => r.Item).ThenBy(r => r.AskedOn).ToList())
            {
                m_log.LogInformation(Tag + ": q={0} still waiting at {1} script={2} notecard=\"{3}\" {4} asked {5} ms ago",
                    r.Query, when, Label(r.Item), r.Card, r.What, now - r.AskedOn);
            }
        }

        public void Restored(UUID item, string scriptName, string runState, int queued)
        {
            Name(item, scriptName);
            m_log.LogInformation(Tag + ": restored script={0} state={1} queued={2} events; waiting on {3} request(s) (a request in flight at a stop is not part of the saved state)",
                Label(item), runState, queued, WaitingOf(item).Count);
            LogWaiting("restore", item);
        }

        /// <summary>A line about an answer the scheduler still holds when the region stops.</summary>
        public void Note(string query, UUID item, string text)
            => m_log.LogInformation(Tag + ": q={0} script={1} {2}", query, Label(item), text);

        /// <summary>Every request still waiting, all scripts (tests).</summary>
        internal int WaitingCount => m_waiting.Count;
    }
}
