/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Converter.Merge;

/// <summary>Reads an archive and plans the merge of each of its linksets. The archive is only read.</summary>
public static class ArchiveMergePlanner
{
    public static MergePlan Plan(string archivePath, MergeOptions options, ConvertOptions convert = null)
    {
        throw new NotImplementedException();
    }
}

/// <summary>Writes a merge plan as JSON: one file for the archive and one for each linkset.</summary>
public static class MergePlanFiles
{
    public static string ArchiveJson(MergePlan plan)
    {
        throw new NotImplementedException();
    }

    public static string LinksetJson(LinksetPlan plan)
    {
        throw new NotImplementedException();
    }

    public static string ReportText(MergePlan plan)
    {
        throw new NotImplementedException();
    }

    /// <summary>Writes merge-plan.json, merge-plan.txt and linksets/&lt;entry&gt;.json into the folder, which must not hold any of them yet.</summary>
    public static void Write(MergePlan plan, string folder)
    {
        throw new NotImplementedException();
    }
}
