/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The wall time and CPU time of a script's last run windows. The runtime measures a window in wall time, so a worker
    /// the system kept off the processor looks like a script that ran over. The CPU time of one window cannot say which:
    /// Windows counts it in ticks of about 15 ms, so a 2 ms window reads 0 or a whole tick. Summed over many windows the
    /// ticks add up, and the share of wall time the script used as CPU is a fair figure. Worker thread only.
    /// </summary>
    internal sealed class RunWindowAverage
    {
        private readonly double[] m_wall;
        private readonly double[] m_cpu;
        private int m_next;
        private int m_count;
        private double m_wallSum;
        private double m_cpuSum;

        public RunWindowAverage(int capacity)
        {
            m_wall = new double[capacity];
            m_cpu = new double[capacity];
        }

        /// <summary>Windows held, up to the capacity.</summary>
        public int Count => m_count;

        public int Capacity => m_wall.Length;

        public void Add(double wallSeconds, double cpuSeconds)
        {
            if (m_count == m_wall.Length)
            {
                m_wallSum -= m_wall[m_next];
                m_cpuSum -= m_cpu[m_next];
            }
            else
                m_count++;
            m_wall[m_next] = wallSeconds;
            m_cpu[m_next] = cpuSeconds;
            m_wallSum += wallSeconds;
            m_cpuSum += cpuSeconds;
            m_next = (m_next + 1) % m_wall.Length;
        }

        /// <summary>CPU time over wall time across the windows held; 1 when no wall time was recorded.</summary>
        public double CpuShare => m_wallSum > 0 ? m_cpuSum / m_wallSum : 1;
    }
}
