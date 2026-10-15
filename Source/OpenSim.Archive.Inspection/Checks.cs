/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Archive.Inspection;

public enum FindingSeverity { Info, Notice, Warning }

/// <summary>One thing a check wants the reader to know, with the numbers behind it.</summary>
public class Finding
{
    public string Code { get; set; }
    public FindingSeverity Severity { get; set; }
    public string Title { get; set; }
    public string Detail { get; set; }
    public Dictionary<string, string> Numbers { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// A check sees every parsed record once, in file order, as the single pass produces them, plus the body of
/// every text-type asset within the size cap. It keeps whatever state it needs, so memory stays bounded.
/// Records are PartRecord, ItemRecord, LinksetRecord, FolderRecord, ParcelRecord, RegionSettingsRecord,
/// AssetRecord, ReferenceRecord, BoxedObjectRecord, LegacyPayloadRecord and ProblemRecord. Checks must not
/// keep a reference to a record after OnRecord returns unless they copy what they need.
/// </summary>
public interface IArchiveCheck
{
    string Id { get; }

    /// <summary>Called once, when the control file has been read (it may come late in the file) or when the pass ends without one.</summary>
    void Begin(ArchiveInfo archive);

    void OnRecord(ArchiveRecord record);

    void OnTextAsset(TextAssetBody body);

    /// <summary>Called once at the end of the pass with the finished report; the check adds its findings.</summary>
    void Complete(ArchiveReport report, IList<Finding> findings);
}

/// <summary>A check that does nothing by default, so a new check overrides only what it uses.</summary>
public abstract class ArchiveCheckBase : IArchiveCheck
{
    public abstract string Id { get; }
    public virtual void Begin(ArchiveInfo archive) { }
    public virtual void OnRecord(ArchiveRecord record) { }
    public virtual void OnTextAsset(TextAssetBody body) { }
    public virtual void Complete(ArchiveReport report, IList<Finding> findings) { }
}

public class AnalysisSettings
{
    /// <summary>Records (parts and items, library and blank creators left out) needed before a flag can fire.</summary>
    public int MinRecords { get; set; } = 20;
    /// <summary>Share of the counted records one creator needs for DOMINANT_CREATOR.</summary>
    public double DominantShare { get; set; } = 0.90;
    /// <summary>Share of records on which creator, owner and last owner are the same account.</summary>
    public double SameAccountShare { get; set; } = 0.95;
    /// <summary>Share of dated records that must fall in one window.</summary>
    public double WindowShare { get; set; } = 0.80;
    public int WindowSeconds { get; set; } = 24 * 3600;
    /// <summary>Share of counted records that carry creator data for the one-builder match.</summary>
    public double CreatorDataShare { get; set; } = 0.95;
}

public class InspectorOptions
{
    public long MaxObjectBodyBytes { get; set; } = 64L * 1024 * 1024;
    public long MaxTextBodyBytes { get; set; } = 4L * 1024 * 1024;
    public long MaxMetadataBodyBytes { get; set; } = 16L * 1024 * 1024;
    public long MaxTotalUncompressedBytes { get; set; } = 64L * 1024 * 1024 * 1024;
    public double MaxCompressionRatio { get; set; } = 200;
    /// <summary>The ratio is only judged once this many uncompressed bytes have been produced.</summary>
    public long RatioCheckMinBytes { get; set; } = 64L * 1024 * 1024;
    public long MaxEntries { get; set; } = 5_000_000;
    public int MaxBoxedDepth { get; set; } = 8;
    /// <summary>Records kept in the report per kind; counts and totals always cover everything. Use int.MaxValue for no limit.</summary>
    public int MaxRetainedRecords { get; set; } = 100_000;
    /// <summary>Referrers kept per referenced asset.</summary>
    public int MaxReferrersPerAsset { get; set; } = 8;
    public int MaxProblems { get; set; } = 200;
    public AnalysisSettings Analysis { get; set; } = new AnalysisSettings();
    /// <summary>Extra checks run in the same pass, after the built-in creator analysis.</summary>
    public List<IArchiveCheck> Checks { get; set; } = new List<IArchiveCheck>();
}
