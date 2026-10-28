/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Keeps a script that repeats one error message from flooding its owner: the first time a message comes it is told at once; the same
    /// message again is only counted; when the script has gone quiet (the message did not come for the quiet interval) the count of the
    /// repeats is handed back to be told once. A different message is a different entry. Times are seconds on the engine's clock.
    /// </summary>
    internal sealed class ErrorRateLimiter
    {
        private sealed class Entry
        {
            public int Suppressed;
            public double LastSeen;
        }

        private readonly Dictionary<(UUID Item, string Text), Entry> m_entries = new Dictionary<(UUID, string), Entry>();

        /// <summary>True: tell the owner now (a message not seen lately). False: it is a repeat, counted.</summary>
        public bool Offer(UUID item, string text, double now)
        {
            lock (m_entries)
            {
                if (m_entries.TryGetValue((item, text), out Entry entry))
                {
                    entry.Suppressed++;
                    entry.LastSeen = now;
                    return false;
                }
                m_entries[(item, text)] = new Entry { LastSeen = now };
                return true;
            }
        }

        /// <summary>True while some message has repeats waiting to be counted out.</summary>
        public bool HasPending
        {
            get { lock (m_entries) return m_entries.Count > 0; }
        }

        /// <summary>
        /// The messages that have been quiet for <paramref name="quietSeconds"/>: each is forgotten, and those with repeats come back with
        /// their count to be told once.
        /// </summary>
        public List<(UUID Item, string Text, int Count)> TakeQuiet(double now, double quietSeconds)
        {
            var summaries = new List<(UUID, string, int)>();
            lock (m_entries)
            {
                foreach (KeyValuePair<(UUID Item, string Text), Entry> pair in new List<KeyValuePair<(UUID, string), Entry>>(m_entries))
                {
                    if (now - pair.Value.LastSeen < quietSeconds)
                        continue;
                    m_entries.Remove(pair.Key);
                    if (pair.Value.Suppressed > 0)
                        summaries.Add((pair.Key.Item, pair.Key.Text, pair.Value.Suppressed));
                }
            }
            return summaries;
        }

        /// <summary>A script that is gone: its messages are forgotten.</summary>
        public void Forget(UUID item)
        {
            lock (m_entries)
                foreach ((UUID Item, string Text) key in new List<(UUID, string)>(m_entries.Keys))
                    if (key.Item == item)
                        m_entries.Remove(key);
        }
    }
}
