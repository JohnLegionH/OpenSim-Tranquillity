/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    /// <summary>
    /// Runs a client's requests of one kind one at a time, in the order they arrived, on worker threads of the
    /// shared pool rather than on the client's own request queue. At most <see cref="MaxWaiting"/> requests wait
    /// behind the one that runs; when another arrives, the oldest waiting request is dropped, because the newest
    /// is what the user asked for last. Each client has its own lane, so one client's burst delays nobody else.
    /// </summary>
    internal sealed class OneAtATimeRequestLane
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The most requests that wait behind the running one.</summary>
        public const int MaxWaiting = 4;

        private readonly string m_name;
        private readonly object m_lock = new();
        private readonly Queue<Action> m_waiting = new();
        private bool m_running;
        private bool m_stopped;

        public OneAtATimeRequestLane(string name)
        {
            m_name = name;
        }

        /// <summary>Runs the request now if none is running, else queues it behind the running ones.</summary>
        public void Submit(Action request)
        {
            lock (m_lock)
            {
                if (m_stopped)
                    return;

                if (m_running)
                {
                    m_waiting.Enqueue(request);
                    if (m_waiting.Count > MaxWaiting)
                    {
                        m_waiting.Dequeue();
                        m_log.LogDebug("[{0}]: dropped the oldest waiting request, {1} already wait", m_name, MaxWaiting);
                    }
                    return;
                }

                m_running = true;
            }

            Util.FireAndForget(_ => Drain(request), null, m_name, false);
        }

        /// <summary>Forgets the waiting requests and accepts no more. A request that is running finishes.</summary>
        public void Stop()
        {
            lock (m_lock)
            {
                m_stopped = true;
                m_waiting.Clear();
            }
        }

        private void Drain(Action request)
        {
            while (request is not null)
            {
                try
                {
                    request();
                }
                catch (Exception e)
                {
                    m_log.LogWarning(e, "[{0}]: a request failed", m_name);
                }

                lock (m_lock)
                {
                    if (m_waiting.Count > 0)
                    {
                        request = m_waiting.Dequeue();
                    }
                    else
                    {
                        m_running = false;
                        request = null;
                    }
                }
            }
        }
    }
}
