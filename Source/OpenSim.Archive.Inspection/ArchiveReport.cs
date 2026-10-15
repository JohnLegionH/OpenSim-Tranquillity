/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Archive.Inspection;

/// <summary>Totals for one creator or one owner.</summary>
public class PartyTotals
{
    public UUID Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Home { get; set; } = string.Empty;
    public long Parts { get; set; }
    public long Linksets { get; set; }
    public long Items { get; set; }
    public long BoxedObjects { get; set; }
    /// <summary>Records that came with creator data (creators only).</summary>
    public long WithCreatorData { get; set; }
    public long Records => Parts + Items;
    public bool IsLibrary { get; set; }
    public int? EarliestDate { get; set; }
    public int? LatestDate { get; set; }
}

public class AssetTypeSummary
{
    public int AssetType { get; set; }
    public string TypeName { get; set; }
    public long Count { get; set; }
    public long Bytes { get; set; }
}

/// <summary>An asset reference that has no creator of its own, with who made the things that use it.</summary>
public class AssetUse
{
    public UUID AssetId { get; set; }
    public int? AssetType { get; set; }
    public string TypeName { get; set; }
    public bool Present { get; set; }
    public bool Uncertain { get; set; }
    public long Uses { get; set; }
    public List<string> Kinds { get; set; } = new List<string>();
    public List<AssetUser> Users { get; set; } = new List<AssetUser>();
}

public class AssetUser
{
    public UUID CreatorId { get; set; }
    public string CreatorName { get; set; }
    public long Uses { get; set; }
}

public class LegacySummary
{
    public string Kind { get; set; }
    public long Count { get; set; }
    public long Bytes { get; set; }
}

public class CreatorFlag
{
    public string Code { get; set; }
    /// <summary>"uuid" when the records were grouped by creator id, "name" when grouped by creator name.</summary>
    public string Grouping { get; set; }
    public string Creator { get; set; }
    public UUID? CreatorId { get; set; }
    public long Count { get; set; }
    public long Total { get; set; }
    public double Share { get; set; }
    /// <summary>How many of the records carry what the grouping needs, for example "3,153 of the 3,205 records that carry a name; 3,205 of 20,969 counted records carry one".</summary>
    public string Coverage { get; set; }
}

public class AnalysisMatch
{
    public string Id { get; set; }
    public string Statement { get; set; }
    public Dictionary<string, string> Numbers { get; set; } = new Dictionary<string, string>();
}

public class CreatorAnalysisResult
{
    public long TotalRecords { get; set; }
    public long LibraryRecords { get; set; }
    public long BlankRecords { get; set; }
    /// <summary>Built-in trees and grass (PCode Tree, NewTree, Grass), counted apart like library content and left out of the shares.</summary>
    public long PlantRecords { get; set; }
    public long CountedRecords { get; set; }
    public long NamedRecords { get; set; }
    public bool Fired => Flags.Count > 0;
    public List<CreatorFlag> Flags { get; set; } = new List<CreatorFlag>();
    public List<AnalysisMatch> Matches { get; set; } = new List<AnalysisMatch>();
    /// <summary>Notes that are not flags, such as names being too sparse to group by.</summary>
    public List<string> Notes { get; set; } = new List<string>();
    public string Advisory { get; set; }
}

public class RetentionInfo
{
    public int MaxRetainedRecords { get; set; }
    public bool PartsTruncated { get; set; }
    public bool ItemsTruncated { get; set; }
    public bool LinksetsTruncated { get; set; }
    public bool FoldersTruncated { get; set; }
    public bool AssetsTruncated { get; set; }
    public bool BoxedTruncated { get; set; }
}

public class RunStats
{
    public double WallSeconds { get; set; }
    public long BytesRead { get; set; }
    public long PeakWorkingSetBytes { get; set; }
}

/// <summary>Everything the pass found. Field names are documented in the tool's README.</summary>
public class ArchiveReport
{
    public const string CurrentSchemaVersion = "1";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public InspectionStatus Status { get; set; }
    /// <summary>Entry being read when reading stopped, when it did.</summary>
    public string StoppedAt { get; set; }
    public string StopReason { get; set; }
    public ArchiveInfo Archive { get; set; } = new ArchiveInfo();

    /// <summary>Linksets, parts and prim inventory items found directly in the archive (OAR object files).</summary>
    public long LinksetCount { get; set; }
    public long PartCount { get; set; }
    public long PrimItemCount { get; set; }
    /// <summary>The same, found inside boxed objects (object assets), counted separately.</summary>
    public long BoxedLinksetCount { get; set; }
    public long BoxedPartCount { get; set; }
    public long BoxedItemCount { get; set; }
    public long IarItemCount { get; set; }
    public long FolderCount { get; set; }
    public long BoxedObjectCount { get; set; }
    public long ParcelCount { get; set; }

    public List<PartyTotals> Creators { get; set; } = new List<PartyTotals>();
    public List<PartyTotals> Owners { get; set; } = new List<PartyTotals>();
    public long BlankCreatorRecords { get; set; }

    public List<LinksetRecord> Linksets { get; set; } = new List<LinksetRecord>();
    public List<FolderRecord> Folders { get; set; } = new List<FolderRecord>();
    public List<PartRecord> Parts { get; set; } = new List<PartRecord>();
    public List<ItemRecord> Items { get; set; } = new List<ItemRecord>();
    public List<BoxedObjectRecord> BoxedObjects { get; set; } = new List<BoxedObjectRecord>();

    public List<AssetTypeSummary> AssetsByType { get; set; } = new List<AssetTypeSummary>();
    public List<AssetRecord> Assets { get; set; } = new List<AssetRecord>();
    public long AssetCount { get; set; }
    public long MissingAssetCount { get; set; }
    public List<UUID> MissingAssets { get; set; } = new List<UUID>();
    public long UnreferencedAssetCount { get; set; }
    public List<UUID> UnreferencedAssets { get; set; } = new List<UUID>();
    /// <summary>Every asset id the archive's content names (certain references, and ids found in text).</summary>
    public long ReferencedAssetCount { get; set; }
    public List<AssetUse> AssetsWithoutCreator { get; set; } = new List<AssetUse>();

    public List<ParcelRecord> Parcels { get; set; } = new List<ParcelRecord>();
    public List<RegionSettingsRecord> RegionSettings { get; set; } = new List<RegionSettingsRecord>();
    public List<LegacySummary> LegacyPayloads { get; set; } = new List<LegacySummary>();
    public long ScriptStateBlobs { get; set; }
    public List<ProblemRecord> Problems { get; set; } = new List<ProblemRecord>();
    public long ProblemCount { get; set; }

    public CreatorAnalysisResult CreatorAnalysis { get; set; } = new CreatorAnalysisResult();
    public List<Finding> Findings { get; set; } = new List<Finding>();
    public List<string> LoaderWarnings { get; set; } = new List<string>();
    public string EditNote { get; set; }
    public RetentionInfo Retention { get; set; } = new RetentionInfo();
    public RunStats Stats { get; set; }

    /// <summary>The referenced asset ids (every kind), for comparison with what a saver would gather.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<UUID> ReferencedAssetIds { get; set; } = new HashSet<UUID>();
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<UUID> CertainReferencedAssetIds { get; set; } = new HashSet<UUID>();
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<UUID> PresentAssetIds { get; set; } = new HashSet<UUID>();

    /// <summary>Exit code of the command-line tool for this report: 0 read fully with no Warning, 1 partial, 2 not an archive, 3 read fully with at least one Warning (the creator flags are Warnings).</summary>
    public int ExitCode
    {
        get
        {
            if (Status == InspectionStatus.NotAnArchive) return 2;
            if (Status == InspectionStatus.Partial) return 1;
            return Findings.Any(f => f.Severity == FindingSeverity.Warning) ? 3 : 0;
        }
    }
}
