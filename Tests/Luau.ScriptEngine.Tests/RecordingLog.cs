/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace Luau.ScriptEngine.Tests;

/// <summary>Records the engine's log lines so a test can count and read them.</summary>
internal sealed class RecordingLog : IEngineLog
{
    private readonly List<string> m_lines = new();

    public IReadOnlyList<string> Lines { get { lock (m_lines) return m_lines.ToArray(); } }
    public IEnumerable<string> Warnings => Lines.Where(l => l.StartsWith("WARN "));
    public IEnumerable<string> Errors => Lines.Where(l => l.StartsWith("ERROR "));

    private void Add(string level, string message) { lock (m_lines) m_lines.Add(level + " " + message); }
    public void Info(string message) => Add("INFO", message);
    public void Warn(string message) => Add("WARN", message);
    public void Error(string message) => Add("ERROR", message);
}
