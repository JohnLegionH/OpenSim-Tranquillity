/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Threading;

namespace OpenSim.Framework
{
    /// <summary>
    /// Measurement aid: carries a map-search id and start time along the thread that runs one search, so that the
    /// log lines written at each stage of the search can be matched up. Measurement builds only.
    /// </summary>
    public static class DiagMapTrace
    {
        private static int s_next;

        [ThreadStatic] private static int t_id;
        [ThreadStatic] private static long t_start;

        public static int NextId() => Interlocked.Increment(ref s_next);

        public static long Now() => Stopwatch.GetTimestamp();

        public static double MsSince(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        public static void Begin(int id, long start)
        {
            t_id = id;
            t_start = start;
        }

        public static void End()
        {
            t_id = 0;
            t_start = 0;
        }

        /// <summary>Id of the search running on this thread, 0 if none.</summary>
        public static int Id => t_id;

        /// <summary>Milliseconds since the search on this thread was enqueued.</summary>
        public static double SinceEnqueueMs => t_id == 0 ? -1 : MsSince(t_start);

        /// <summary>Thread pool state at this moment, for the log.</summary>
        public static string Pool()
        {
            ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
            ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
            ThreadPool.GetMinThreads(out int minWorker, out int minIo);
            return $"threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount} busyWorker={maxWorker - availWorker} minWorker={minWorker} availWorker={availWorker} busyIo={maxIo - availIo}";
        }
    }
}
