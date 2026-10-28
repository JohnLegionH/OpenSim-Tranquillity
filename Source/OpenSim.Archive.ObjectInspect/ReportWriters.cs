/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.ObjectInspect;

/// <summary>The readable report.</summary>
public static class ReportText
{
    public static string Build(ObjectReport report, string archiveLabel = null) => string.Empty;
}

/// <summary>One row per part, per item in a part and per inventory item, for a spreadsheet.</summary>
public static class ReportCsv
{
    public static string Build(ObjectReport report) => string.Empty;
}

/// <summary>The same report as JSON, for tools.</summary>
public static class ReportJson
{
    public static string Build(ObjectReport report, string archiveLabel = null) => string.Empty;
}
