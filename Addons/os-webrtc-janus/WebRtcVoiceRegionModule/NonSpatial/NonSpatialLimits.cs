/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Ceilings on how much non-spatial session state one process holds and how fast one agent can create it.
 *
 * Without them a single agent could open sessions until the process ran out of memory, and one request
 * could ring every online agent. The numbers are settings in [WebRtcVoice] (see the region module's
 * ReadNonSpatialLimits); the defaults here are finite on purpose.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class NonSpatialLimits
    {
        /// <summary>Sessions of every type this process holds at once.</summary>
        public const int DefaultMaxNonSpatialSessions = 5000;

        /// <summary>Live sessions one agent may have started.</summary>
        public const int DefaultMaxSessionsPerAgent = 20;

        public int MaxNonSpatialSessions { get; init; } = DefaultMaxNonSpatialSessions;
        public int MaxSessionsPerAgent { get; init; } = DefaultMaxSessionsPerAgent;

        public static NonSpatialLimits Default => new NonSpatialLimits();
    }

    /// <summary>
    /// Lets a log line through at most once per key per interval. A refused start repeated in a loop
    /// would otherwise write one Warning per attempt.
    /// </summary>
    public sealed class RefusalLogGate
    {
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

        private readonly TimeSpan _interval;
        private readonly Dictionary<UUID, DateTime> _last = new Dictionary<UUID, DateTime>();

        public RefusalLogGate(TimeSpan? interval = null)
        {
            _interval = interval ?? DefaultInterval;
        }

        /// <summary>True the first time a key is seen and again once the interval has passed since the last true.</summary>
        public bool ShouldLog(UUID key, DateTime nowUtc)
        {
            lock (_last)
            {
                if (_last.TryGetValue(key, out DateTime prior) && nowUtc - prior < _interval)
                    return false;
                _last[key] = nowUtc;
                return true;
            }
        }

        /// <summary>Forget keys whose last line is older than <paramref name="maxAge"/>. Returns how many.</summary>
        public int Prune(DateTime nowUtc, TimeSpan maxAge)
        {
            lock (_last)
            {
                List<UUID> stale = new List<UUID>();
                foreach (KeyValuePair<UUID, DateTime> kv in _last)
                    if (nowUtc - kv.Value > maxAge)
                        stale.Add(kv.Key);
                foreach (UUID k in stale)
                    _last.Remove(k);
                return stale.Count;
            }
        }
    }
}
