/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.ObjectInspect;

/// <summary>One thing that differs between a matched part (or item) in the reference archive and in the archive.</summary>
public sealed class Difference
{
    /// <summary>"link order", "permissions", "who" or "parts".</summary>
    public string Area { get; set; }
    public string Part { get; set; }
    public string Field { get; set; }
    public string Reference { get; set; }
    public string Archive { get; set; }
}

/// <summary>Where one part stands in the reference archive and in the archive.</summary>
public sealed class PartLink
{
    public string Name { get; set; }
    public int? ReferenceLink { get; set; }
    public int? ArchiveLink { get; set; }
    public int? ReferenceFileOrder { get; set; }
    public int? ArchiveFileOrder { get; set; }
    public bool Changed { get; set; }
}

public sealed class ObjectComparison
{
    public string Name { get; set; }
    /// <summary>How the two objects were paired: "name", or "name, part count and shape" when several objects share the name.</summary>
    public string Matched { get; set; }
    public int ReferenceIndex { get; set; }
    public int ArchiveIndex { get; set; }
    public List<PartLink> Parts { get; set; } = new List<PartLink>();
    public List<Difference> Differences { get; set; } = new List<Difference>();
    public bool LinkOrderDiffers => Differences.Any(d => d.Area == "link order");
    public bool PermissionsDiffer => Differences.Any(d => d.Area == "permissions" || d.Area == "who");
}

public sealed class CompareResult
{
    public List<ObjectComparison> Objects { get; set; } = new List<ObjectComparison>();
    public List<string> OnlyInReference { get; set; } = new List<string>();
    public List<string> OnlyInArchive { get; set; } = new List<string>();
    public int Matched => Objects.Count;
    public int ObjectsWithLinkOrderDifferences => Objects.Count(o => o.LinkOrderDiffers);
    public int ObjectsWithPermissionDifferences => Objects.Count(o => o.PermissionsDiffer);
    public bool Identical { get; set; }

    public string ToText(string referenceLabel = null, string archiveLabel = null) => string.Empty;
    public string ToCsv() => string.Empty;
    public string ToJson(string referenceLabel = null, string archiveLabel = null) => string.Empty;
}

/// <summary>Pairs the objects of two reports and lists the link-order and permission differences, part by part.</summary>
public static class ReportCompare
{
    public static CompareResult Compare(ObjectReport reference, ObjectReport archive) => new CompareResult();
}
