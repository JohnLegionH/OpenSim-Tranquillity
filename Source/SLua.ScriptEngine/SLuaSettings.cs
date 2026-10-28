/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Threading;
using Nini.Config;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The settings in the engine's configuration section. The section is absent from the shipped defaults, so
    /// a stock install does not load the engine. Every default is the value used when the key is not set; none of
    /// them changes how another engine behaves.
    /// </summary>
    internal sealed class SLuaSettings
    {
        /// <summary>The ceiling for a script's memory limit, in KiB. A larger configured value is replaced by it.</summary>
        public const int MemoryLimitCeilingKiB = 128;

        private static readonly string[] WatchdogModes = { "default", "resident", "threaded" };
        private static readonly string[] LogLevels = { "debug", "info", "warn", "error" };
        private static readonly string[] DeferralModes = { "auto", "always", "never" };
        private static readonly string[] NotecardPauses = { "sl", "halcyon" };

        /// <summary>Loads the engine in the region.</summary>
        public bool Enabled { get; private set; }

        /// <summary>The base folder; the engine appends its own name to get <see cref="StateRoot"/>.</summary>
        public string ScriptEnginesPath { get; private set; } = "ScriptEngines";

        /// <summary>Worker threads per region, each with its own VM.</summary>
        public int WorkerThreads { get; private set; } = 1;

        public ThreadPriority SchedulerThreadPriority { get; private set; } = ThreadPriority.Lowest;

        /// <summary>Run window per script step, in milliseconds.</summary>
        public double QuantaMs { get; private set; } = 2.0;

        /// <summary>"default", "resident" or "threaded".</summary>
        public string Watchdog { get; private set; } = "default";

        /// <summary>Per-script memory limit in KiB; never above <see cref="MemoryLimitCeilingKiB"/>.</summary>
        public int MemoryLimitKiB { get; private set; } = MemoryLimitCeilingKiB;

        /// <summary>Floor for llSetTimerEvent in seconds; same key and default as the other engines.</summary>
        public double MinTimerInterval { get; private set; } = 0.1;

        public int MaxEventQueue { get; private set; } = 64;

        /// <summary>A script with no engine header and no saved state is taken by this engine as LSL (ClaimHeaderlessScripts).</summary>
        public bool ClaimHeaderlessScripts { get; private set; }

        /// <summary>An event that wakes an idle script puts it at the front of its worker's ring (once per ring cycle).</summary>
        public bool EventFrontOfRing { get; private set; } = true;

        /// <summary>Consecutive punished turns after which a script is killed.</summary>
        public int KillAfterPunishments { get; private set; } = 20;
        public int StateSaveIntervalMs { get; private set; } = 2500;

        /// <summary>Whether the engine keeps its scripts' state across a restart (SaveScriptState). Off, a restart starts every script fresh and no database is made.</summary>
        public bool SaveScriptState { get; private set; } = true;

        /// <summary>A script that repeats the same error message is told to its owner once, then as a count after a quiet interval (ErrorRateLimit).</summary>
        public bool ErrorRateLimit { get; private set; } = true;

        /// <summary>How long a script must stop repeating an error before the count of the repeats is told, in seconds (ErrorQuietSeconds).</summary>
        public double ErrorQuietSeconds { get; private set; } = 10.0;

        /// <summary>How many times a failed read of a script's saved state is tried again before the script is held (StateLoadRetries).</summary>
        public int StateLoadRetries { get; private set; } = 3;

        /// <summary>How long the engine waits between those tries, in milliseconds (StateLoadRetryDelayMs).</summary>
        public int StateLoadRetryDelayMs { get; private set; } = 500;
        public bool CompileCache { get; private set; } = true;

        /// <summary>Compile the script API's code on a background thread when the engine starts, once per process.</summary>
        public bool WarmUp { get; private set; } = true;

        /// <summary>"auto", "always" or "never".</summary>
        public string ServiceCallDeferral { get; private set; } = "auto";

        public int ServiceCallThreads { get; private set; } = 4;
        public int ServiceCallTimeoutMs { get; private set; } = 35000;

        /// <summary>Lowest native log level forwarded to the region log: "debug", "info", "warn" or "error".</summary>
        public string NativeLogLevel { get; private set; } = "warn";

        public bool ResetThrottle { get; private set; } = true;
        public bool ChatThrottle { get; private set; } = true;
        public bool BotThrottle { get; private set; } = true;
        public bool PhysicsThrottle { get; private set; } = true;
        public bool LinkMessageThrottle { get; private set; } = true;
        public bool NotecardThrottle { get; private set; } = true;
        public bool NotecardCache { get; private set; } = true;
        public bool FormatStringThrottle { get; private set; } = true;
        public bool HttpInFlightThrottle { get; private set; } = true;

        /// <summary>"sl" or "halcyon".</summary>
        public string NotecardPause { get; private set; } = "sl";

        /// <summary>Own key, else [YEngine] AllowGodFunctions, else false.</summary>
        public bool AllowGodFunctions { get; private set; }

        /// <summary>A folder holding the native library, or the library file itself. Null: use SLUA_SHIM_PATH, then the package's runtimes folder.</summary>
        public string NativeLibraryPath { get; private set; }

        /// <summary>The engine's own folder under <see cref="ScriptEnginesPath"/>: the compile cache and state database live here.</summary>
        public string StateRoot { get; private set; } = Path.Combine("ScriptEngines", EngineIdentity.EngineName);

        public static SLuaSettings Read(IConfigSource source, IEngineLog log)
        {
            var s = new SLuaSettings();
            IConfig c = source?.Configs[EngineIdentity.ConfigSection];
            IConfig yengine = source?.Configs["YEngine"];
            s.AllowGodFunctions = yengine?.GetBoolean("AllowGodFunctions", false) ?? false;
            if (c == null)
                return s;

            s.Enabled = c.GetBoolean("Enabled", false);
            string path = c.GetString("ScriptEnginesPath", s.ScriptEnginesPath);
            s.ScriptEnginesPath = string.IsNullOrWhiteSpace(path) ? "ScriptEngines" : path;
            s.StateRoot = Path.Combine(s.ScriptEnginesPath, EngineIdentity.EngineName);

            s.WorkerThreads = Math.Max(1, c.GetInt("WorkerThreads", s.WorkerThreads));
            s.SchedulerThreadPriority = ReadPriority(c, s.SchedulerThreadPriority, log);
            s.QuantaMs = Positive(c, "QuantaMs", s.QuantaMs, log);
            s.Watchdog = ReadChoice(c, "Watchdog", s.Watchdog, WatchdogModes, log);
            s.MemoryLimitKiB = ReadMemoryLimit(c, log);
            s.MinTimerInterval = Math.Max(0.0, c.GetDouble("MinTimerInterval", s.MinTimerInterval));
            s.MaxEventQueue = Math.Max(1, c.GetInt("MaxEventQueue", s.MaxEventQueue));
            s.EventFrontOfRing = c.GetBoolean("EventFrontOfRing", s.EventFrontOfRing);
            s.ClaimHeaderlessScripts = c.GetBoolean("ClaimHeaderlessScripts", s.ClaimHeaderlessScripts);
            s.KillAfterPunishments = Math.Max(1, c.GetInt("KillAfterPunishments", s.KillAfterPunishments));
            s.StateSaveIntervalMs = Math.Max(1, c.GetInt("StateSaveIntervalMs", s.StateSaveIntervalMs));
            s.SaveScriptState = c.GetBoolean("SaveScriptState", s.SaveScriptState);
            s.ErrorRateLimit = c.GetBoolean("ErrorRateLimit", s.ErrorRateLimit);
            s.ErrorQuietSeconds = Math.Max(0.05, c.GetDouble("ErrorQuietSeconds", s.ErrorQuietSeconds));
            s.StateLoadRetries = Math.Max(0, c.GetInt("StateLoadRetries", s.StateLoadRetries));
            s.StateLoadRetryDelayMs = Math.Max(0, c.GetInt("StateLoadRetryDelayMs", s.StateLoadRetryDelayMs));
            s.CompileCache = c.GetBoolean("CompileCache", s.CompileCache);
            s.WarmUp = c.GetBoolean("WarmUp", s.WarmUp);
            s.ServiceCallDeferral = ReadChoice(c, "ServiceCallDeferral", s.ServiceCallDeferral, DeferralModes, log);
            s.ServiceCallThreads = Math.Max(1, c.GetInt("ServiceCallThreads", s.ServiceCallThreads));
            s.ServiceCallTimeoutMs = Math.Max(1, c.GetInt("ServiceCallTimeoutMs", s.ServiceCallTimeoutMs));
            s.NativeLogLevel = ReadChoice(c, "NativeLogLevel", s.NativeLogLevel, LogLevels, log);

            s.ResetThrottle = c.GetBoolean("ResetThrottle", s.ResetThrottle);
            s.ChatThrottle = c.GetBoolean("ChatThrottle", s.ChatThrottle);
            s.BotThrottle = c.GetBoolean("BotThrottle", s.BotThrottle);
            s.PhysicsThrottle = c.GetBoolean("PhysicsThrottle", s.PhysicsThrottle);
            s.LinkMessageThrottle = c.GetBoolean("LinkMessageThrottle", s.LinkMessageThrottle);
            s.NotecardThrottle = c.GetBoolean("NotecardThrottle", s.NotecardThrottle);
            s.NotecardCache = c.GetBoolean("NotecardCache", s.NotecardCache);
            s.FormatStringThrottle = c.GetBoolean("FormatStringThrottle", s.FormatStringThrottle);
            s.HttpInFlightThrottle = c.GetBoolean("HttpInFlightThrottle", s.HttpInFlightThrottle);
            s.NotecardPause = ReadChoice(c, "NotecardPause", s.NotecardPause, NotecardPauses, log);

            s.AllowGodFunctions = c.GetBoolean("AllowGodFunctions", s.AllowGodFunctions);

            string lib = c.GetString("NativeLibraryPath", "");
            s.NativeLibraryPath = string.IsNullOrWhiteSpace(lib) ? null : lib;
            return s;
        }

        private static string ReadChoice(IConfig c, string key, string fallback, string[] allowed, IEngineLog log)
        {
            string value = c.GetString(key, fallback).Trim().ToLowerInvariant();
            if (Array.IndexOf(allowed, value) >= 0)
                return value;
            log.Warn($"[{EngineIdentity.ConfigSection}] {key} = '{c.GetString(key, fallback)}' is not one of {string.Join(", ", allowed)}; using '{fallback}'");
            return fallback;
        }

        private static double Positive(IConfig c, string key, double fallback, IEngineLog log)
        {
            double value = c.GetDouble(key, fallback);
            if (value > 0.0)
                return value;
            log.Warn($"[{EngineIdentity.ConfigSection}] {key} = {value} must be above zero; using {fallback}");
            return fallback;
        }

        private static ThreadPriority ReadPriority(IConfig c, ThreadPriority fallback, IEngineLog log)
        {
            string text = c.GetString("SchedulerThreadPriority", "");
            if (string.IsNullOrWhiteSpace(text))
                return fallback;
            if (Enum.TryParse(text.Trim(), true, out ThreadPriority priority) && Enum.IsDefined(typeof(ThreadPriority), priority))
                return priority;
            log.Warn($"[{EngineIdentity.ConfigSection}] SchedulerThreadPriority = '{text}' is not a ThreadPriority name; using {fallback}");
            return fallback;
        }

        private static int ReadMemoryLimit(IConfig c, IEngineLog log)
        {
            int value = c.GetInt("MemoryLimitKiB", MemoryLimitCeilingKiB);
            if (value > MemoryLimitCeilingKiB)
            {
                log.Warn($"[{EngineIdentity.ConfigSection}] MemoryLimitKiB = {value} is above the {MemoryLimitCeilingKiB} KiB ceiling; using {MemoryLimitCeilingKiB}");
                return MemoryLimitCeilingKiB;
            }
            return Math.Max(1, value);
        }
    }
}
