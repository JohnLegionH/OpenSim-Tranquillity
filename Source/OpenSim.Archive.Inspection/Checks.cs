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
    /// <summary>Id of the check that produced the finding.</summary>
    public string Source { get; set; }
    /// <summary>How many records the finding is about (the examples may be fewer).</summary>
    public long Count { get; set; }
    /// <summary>The records the finding is about, in file order, up to InspectorOptions.MaxFindingExamples. The text report prints the first 20.</summary>
    public List<FindingExample> Examples { get; set; } = new List<FindingExample>();
    /// <summary>True when there were more records than the list holds.</summary>
    public bool ExamplesTruncated { get; set; }
}

/// <summary>One record a finding points at.</summary>
public class FindingExample
{
    public string Name { get; set; }
    public string Id { get; set; }
    /// <summary>Folder path, entry path or linkset, whichever places the record.</summary>
    public string Where { get; set; }
    public string Note { get; set; }
}

/// <summary>Collects the records behind one finding, counting all of them and keeping a bounded list.</summary>
public sealed class FindingBuilder
{
    private readonly int m_max;
    public long Count { get; private set; }
    public List<FindingExample> Examples { get; } = new List<FindingExample>();
    public bool Truncated { get; private set; }

    public FindingBuilder(int max) { m_max = max; }

    public void Add(string name, string id, string where, string note = null)
    {
        Count++;
        if (Examples.Count < m_max)
            Examples.Add(new FindingExample { Name = Sanitize.Text(name ?? string.Empty), Id = id ?? string.Empty, Where = Sanitize.Text(where ?? string.Empty), Note = Sanitize.Text(note) });
        else
            Truncated = true;
    }

    public bool Any => Count > 0;

    public Finding ToFinding(string source, string code, FindingSeverity severity, string title, string detail, Dictionary<string, string> numbers = null)
    {
        return new Finding
        {
            Source = source,
            Code = code,
            Severity = severity,
            Title = title,
            Detail = detail,
            Count = Count,
            Numbers = numbers ?? new Dictionary<string, string>(),
            Examples = Examples,
            ExamplesTruncated = Truncated
        };
    }
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
    /// <summary>Grouping by creator name only flags when the records that carry a name are at least this share of the counted records.</summary>
    public double NameCoverageShare { get; set; } = 0.50;
}

/// <summary>Bounds the structure checks report against. They are settings, not rules taken from a loader.</summary>
public class StructureSettings
{
    public int MaxFolderDepth { get; set; } = 32;
    public int MaxItemsPerFolder { get; set; } = 10_000;
    public long MaxTotalItems { get; set; } = 250_000;
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
    public StructureSettings Structure { get; set; } = new StructureSettings();
    /// <summary>Examples kept per finding (the text report prints 20; the JSON carries all kept).</summary>
    public int MaxFindingExamples { get; set; } = 100_000;
    /// <summary>Run the file-only checks (structure, permissions, references, author lines, creator data, integrity). Tests of a single check turn this off.</summary>
    public bool StandardChecks { get; set; } = true;
    /// <summary>
    /// Called once for every object file read directly from the archive (not for boxed objects), with the parsed
    /// linksets, the entry name and the region folder, before the records are emitted. The parsed object is
    /// only valid during the call.
    /// </summary>
    public Action<ParsedObject, string, string> ObjectObserver { get; set; }
    /// <summary>
    /// Asks, for every asset file that is not an object or a scanned text type, whether its body is wanted (asset id,
    /// asset type, size in bytes). Wanted bodies up to <see cref="MaxWantedAssetBytes"/> are read and handed to
    /// <see cref="AssetBodyObserver"/>; a wanted body over the limit is handed over as null.
    /// </summary>
    public Func<UUID, int, long, bool> WantAssetBody { get; set; }
    /// <summary>Receives a wanted asset's body: asset id, asset type, archive entry name, body (null when over the limit).</summary>
    public Action<UUID, int, string, byte[]> AssetBodyObserver { get; set; }
    public long MaxWantedAssetBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>Extra checks run in the same pass, after the built-in creator analysis.</summary>
    public List<IArchiveCheck> Checks { get; set; } = new List<IArchiveCheck>();
}
