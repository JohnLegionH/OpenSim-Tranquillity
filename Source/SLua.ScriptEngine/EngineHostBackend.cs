/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// What the engine does when the script functions (LSLSystemAPI) ask the host for something: post an event, set a
    /// timer, reset a script, report whether a script exists. The engine fills in the members it can serve; a member
    /// left null answers as an engine with nothing behind it would (false, no-op, full queue space).
    /// </summary>
    internal sealed class EngineHostBackend
    {
        public Func<UUID, EventParams, bool> PostScriptEvent;
        public Func<uint, EventParams, bool> PostObjectEvent;
        public Func<UUID, (bool Exists, bool Running)> HasScript;
        public Action<UUID, float> SetTimer;
        public Action<UUID, float> SetMinEventDelay;
        public Action<uint, UUID, bool> SetItemRunningFlag;
        public Action<UUID> ResetScript;
        public Action<UUID> RequestParcelCheck;
        public Func<UUID, float> EventQueueFreeSpacePercentage;

        /// <summary>Where a call's return goes when it arrives outside the call's own thread (a sequenced return from the service lane).</summary>
        public Action<UUID, object, int, int> SyscallReturn;
    }
}
