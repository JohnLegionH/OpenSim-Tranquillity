/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Threading;
using Nini.Config;

namespace Luau.ScriptEngine
{
    /// <summary>The settings in the engine's configuration section. The section is absent from the shipped defaults.</summary>
    internal sealed class LuauSettings
    {
        public bool Enabled { get; private set; }
        public string ScriptEnginesPath { get; private set; }
        public int WorkerThreads { get; private set; }
        public ThreadPriority SchedulerThreadPriority { get; private set; }
        public double QuantaMs { get; private set; }
        public string Watchdog { get; private set; }
        public int MemoryLimitKiB { get; private set; }
        public double MinTimerInterval { get; private set; }
        public int MaxEventQueue { get; private set; }
        public int StateSaveIntervalMs { get; private set; }
        public bool CompileCache { get; private set; }
        public string ServiceCallDeferral { get; private set; }
        public int ServiceCallThreads { get; private set; }
        public int ServiceCallTimeoutMs { get; private set; }
        public string NativeLogLevel { get; private set; }
        public bool ResetThrottle { get; private set; }
        public bool ChatThrottle { get; private set; }
        public bool BotThrottle { get; private set; }
        public bool PhysicsThrottle { get; private set; }
        public bool LinkMessageThrottle { get; private set; }
        public bool NotecardThrottle { get; private set; }
        public bool NotecardCache { get; private set; }
        public bool FormatStringThrottle { get; private set; }
        public bool HttpInFlightThrottle { get; private set; }
        public string NotecardPause { get; private set; }
        public bool AllowGodFunctions { get; private set; }
        public string NativeLibraryPath { get; private set; }

        /// <summary>The engine's own folder under <see cref="ScriptEnginesPath"/>: the compile cache and state database live here.</summary>
        public string StateRoot { get; private set; }

        public static LuauSettings Read(IConfigSource source, IEngineLog log) => new LuauSettings();
    }
}
