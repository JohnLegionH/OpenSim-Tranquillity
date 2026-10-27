/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using InWorldz.Phlox.Glue;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>One call handed to the service lane.</summary>
    internal sealed class LaneCall
    {
        public SchedulerScript Script;
        public SyscallContext Context;
        public string Function;

        /// <summary>The call itself, run on a lane thread inside <see cref="Context"/>; its values are what the script receives.</summary>
        public Func<Value[]> Body;

        /// <summary>What the script receives if the deadline passes first.</summary>
        public Value[] Failure;
    }

    /// <summary>The place the long-running and service-reaching calls of scripts run, off the script workers.</summary>
    internal sealed class ServiceLane : IDisposable
    {
        public ServiceLane(SLuaScheduler scheduler, string deferral = "auto", int threads = 4, int timeoutMs = 35000, int idleMs = 30000) { }

        /// <summary>False when the operator turned deferral off ("never"): service calls then run on the script worker.</summary>
        public bool Defers => true;

        /// <summary>Lane threads alive right now.</summary>
        public int ThreadCount => 0;

        public void Submit(LaneCall call) { }

        public void Dispose() { }
    }
}
