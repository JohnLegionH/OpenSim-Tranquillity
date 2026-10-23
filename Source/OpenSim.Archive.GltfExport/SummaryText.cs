/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.GltfExport;

/// <summary>
/// The plain-text summary of an export, shown on the console and written beside the JSON summary. When the archive was
/// damaged or cut short, the run stopped for low disk space, or a memory or disk cap was reached, those lines come first so they cannot be missed.
/// </summary>
public static class SummaryText
{
    public static string Build(ExportSummary summary)
    {
        StringBuilder sb = new StringBuilder();
        if (!string.IsNullOrEmpty(summary.DiskSpaceStop))
            sb.AppendLine(Sanitize.Text(summary.DiskSpaceStop));
        if (summary.ArchiveIncomplete)
        {
            sb.AppendLine("ARCHIVE INCOMPLETE");
            sb.AppendLine("The archive is damaged or was cut short" + (string.IsNullOrEmpty(summary.StoppedAt) ? string.Empty : " (reading stopped at " + Sanitize.Text(summary.StoppedAt) + ")") + ". Everything that could be read was exported; the rest is missing.");
        }
        foreach (string cap in summary.CapsReached)
            sb.AppendLine(Sanitize.Text(cap));
        foreach (string warning in summary.Warnings)
            sb.AppendLine(Sanitize.Text(warning));

        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "read: {0}; layout: {1}; builds: {2} ({3} with a file); parts: {4} (exported {5}, placeheld {6}, skipped {7}); {8:0.0} s; {9} bytes written",
            summary.ReadStatus, summary.Layout, summary.Builds, summary.BuildsWithAFile, summary.Parts, summary.Exported, summary.Placeheld, summary.Skipped, summary.Seconds, summary.OutputBytes));
        foreach (KeyValuePair<string, int> r in summary.Reasons.OrderByDescending(r => r.Value))
            sb.AppendLine("  " + r.Value.ToString(CultureInfo.InvariantCulture) + " x " + Sanitize.Text(r.Key));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "builds failed: {0}; left as an earlier run wrote them: {1}; archive passes: {2}; peak memory {3:0} MB",
            summary.BuildsFailed, summary.BuildsResumed, summary.ArchivePasses, summary.PeakWorkingSetMegabytes));
        foreach (SummaryBuild failed in summary.BuildList.Where(b => b.Status != "ok").Take(20))
            sb.AppendLine("  failed: " + Sanitize.Text(failed.Name) + " (" + failed.RootPartUuid + "): " + Sanitize.Text(failed.Error));
        sb.AppendLine("exit code " + summary.ExitCode.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
