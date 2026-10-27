/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.ArchiveInspector;

/// <summary>Writes a --report file with LF line ends on every platform, so the same run writes the same bytes on Windows and on Linux (text built with AppendLine and indented JSON follow the platform's line end otherwise).</summary>
internal static class ReportFile
{
    public static void Write(string path, string text) => File.WriteAllText(path, text.Replace("\r\n", "\n"));
}
