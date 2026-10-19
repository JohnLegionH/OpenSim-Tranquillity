/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Keeps this region's seats alive in the grid session service. On a timer it sends the agents that
 * are still in the region; the service releases a seat nobody has renewed for its lease, which is
 * how the seats of a region that crashed or lost its connection come back.
 */
using System;
using System.Collections.Generic;
using System.Threading;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class GridSeatRenewer : IDisposable
    {
        public const int DefaultRenewSeconds = 60;

        private readonly GridNonSpatialSessions _sessions;
        private readonly Func<IReadOnlyList<UUID>> _presentAgents;
        private readonly Action<string, Exception> _error;
        private readonly TimeSpan _interval;
        private readonly object _timerLock = new object();
        private Timer _timer;
        private int _reentry;

        public GridSeatRenewer(GridNonSpatialSessions sessions, Func<IReadOnlyList<UUID>> presentAgents,
                               Action<string, Exception> error, TimeSpan? interval = null)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _presentAgents = presentAgents ?? throw new ArgumentNullException(nameof(presentAgents));
            _error = error;
            _interval = interval ?? TimeSpan.FromSeconds(DefaultRenewSeconds);
        }

        public void Start()
        {
            lock (_timerLock)
                if (_timer == null)
                    _timer = new Timer(_ => RunOnce(), null, _interval, _interval);
        }

        public void Stop()
        {
            Timer t;
            lock (_timerLock)
            {
                t = _timer;
                _timer = null;
            }
            t?.Dispose();
        }

        public void Dispose() => Stop();

        /// <summary>One renewal. Returns false when it did not reach the service. Never throws.</summary>
        public bool RunOnce()
        {
            if (Interlocked.Exchange(ref _reentry, 1) != 0)
                return true;
            try
            {
                IReadOnlyList<UUID> agents = _presentAgents();
                return agents == null || agents.Count == 0 || _sessions.RenewSeats(agents);
            }
            catch (Exception e)
            {
                try { _error?.Invoke("grid seat renewal failed", e); }
                catch { /* the log sink must not break the timer */ }
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _reentry, 0);
            }
        }
    }
}
