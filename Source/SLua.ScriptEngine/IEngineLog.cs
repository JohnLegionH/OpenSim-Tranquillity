/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace SLua.ScriptEngine
{
    /// <summary>Where the engine writes its log lines; a test substitutes a recorder.</summary>
    internal interface IEngineLog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    /// <summary>The region log, under the engine's own tag.</summary>
    internal sealed class RegionLog : IEngineLog
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string Tag = "[" + EngineIdentity.EngineName + "]: ";

        public void Info(string message) => m_log.LogInformation("{Message}", Tag + message);
        public void Warn(string message) => m_log.LogWarning("{Message}", Tag + message);
        public void Error(string message) => m_log.LogError("{Message}", Tag + message);
    }
}
