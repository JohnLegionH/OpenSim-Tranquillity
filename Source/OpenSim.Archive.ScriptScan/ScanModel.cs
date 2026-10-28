/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.ScriptScan;

/// <summary>What the scan decided for one part.</summary>
public enum PartDecision
{
    Convertible,
    ConvertibleWithWarnings,
    KeepPrimUnsafe,
    KeepPrimUncertain
}

public static class PartDecisionNames
{
    public static string Text(PartDecision d)
    {
        switch (d)
        {
            case PartDecision.Convertible: return "CONVERTIBLE";
            case PartDecision.ConvertibleWithWarnings: return "CONVERTIBLE-WITH-WARNINGS";
            case PartDecision.KeepPrimUnsafe: return "KEEP-PRIM (UNSAFE)";
            default: return "KEEP-PRIM (UNCERTAIN)";
        }
    }

    public static bool IsKept(PartDecision d) => d == PartDecision.KeepPrimUnsafe || d == PartDecision.KeepPrimUncertain;
}

/// <summary>One reason behind a part's decision, with the script and line that cause it.</summary>
public sealed class PartReason
{
    public string Code { get; set; }
    /// <summary>"unsafe", "uncertain", "warning" or "note".</summary>
    public string Level { get; set; }
    /// <summary>Name of the script item in the part's contents.</summary>
    public string Script { get; set; }
    public Guid ScriptAssetId { get; set; }
    public string HostPartName { get; set; }
    public Guid HostPartId { get; set; }
    public int HostLinkNum { get; set; }
    /// <summary>1-based line in the script, or 0 when the reason is about the whole script (missing source).</summary>
    public int Line { get; set; }
    public string Function { get; set; }
    public string Detail { get; set; }
    /// <summary>How many times the script does the same thing.</summary>
    public int Count { get; set; } = 1;
    /// <summary>How the call reaches this part: "this part", "link 3", "LINK_SET", "unknown link" ...</summary>
    public string Reach { get; set; }
    /// <summary>True when the call is read exactly (literals and constants); false when an argument could not be worked out.</summary>
    public bool Literal { get; set; }
}

public sealed class PartRow
{
    public Guid PartId { get; set; }
    public Guid LinksetId { get; set; }
    public string Build { get; set; }
    public string Entry { get; set; }
    public string RegionDir { get; set; }
    /// <summary>The object asset the part sits in, for an inventory archive; empty for an OAR.</summary>
    public Guid BoxedAssetId { get; set; }
    public int LinkNum { get; set; }
    public bool IsRoot { get; set; }
    public string Name { get; set; }
    public PartDecision Decision { get; set; }
    /// <summary>True when a script asks how many faces the part has or addresses its faces by number: the converted part must keep its face count.</summary>
    public bool FaceCountDependent { get; set; }
    public List<PartReason> Reasons { get; } = new List<PartReason>();
    /// <summary>Reasons left out of <see cref="Reasons"/> because the list is capped.</summary>
    public int ReasonsOmitted { get; set; }
}

/// <summary>How one script addresses the parts of its linkset (see <see cref="LslLinkUses"/>).</summary>
public sealed class ScriptLinkUses
{
    public Guid LinksetId { get; set; }
    public string Entry { get; set; }
    public Guid HostPartId { get; set; }
    public string Script { get; set; }
    /// <summary>True when the scan stopped at its cap of findings for this script, so the uses may be incomplete.</summary>
    public bool Truncated { get; set; }
    /// <summary>True when the script is SLua, which the scan does not read: <see cref="Uses"/> is then empty because nothing was read.</summary>
    public bool IsLua { get; set; }
    public List<LinkUse> Uses { get; } = new List<LinkUse>();
}

public sealed class UnscannedScript
{
    public string Script { get; set; }
    public Guid AssetId { get; set; }
    public string Part { get; set; }
    public Guid PartId { get; set; }
    public string Build { get; set; }
    public string Reason { get; set; }
}

public sealed class ScanOptions
{
    /// <summary>Treat a script that cannot be read as harmless (off by default: such a script keeps its whole build as prims).</summary>
    public bool AssumeSafeUnscanned { get; set; }
    /// <summary>Most reasons listed for one part; the decision always uses all of them.</summary>
    public int MaxReasonsPerPart { get; set; } = 40;
    /// <summary>Called with each archive-level object as the inspector reads it, so a caller can collect part fields in the same pass (see InspectorOptions.ObjectObserver).</summary>
    public Action<OpenSim.Archive.Inspection.ParsedObject, string, string> ObjectObserver { get; set; }
    /// <summary>More checks to run in the same pass over the archive.</summary>
    public List<OpenSim.Archive.Inspection.IArchiveCheck> ExtraChecks { get; } = new List<OpenSim.Archive.Inspection.IArchiveCheck>();
}

public sealed class ScanException : Exception
{
    public ScanException(string message, Exception inner = null) : base(message, inner) { }
}

public sealed class ScanResult
{
    public string ToolVersion { get; set; }
    public string Archive { get; set; }
    public string ArchiveKind { get; set; }
    /// <summary>"Complete" or "Partial" (the archive ended early or had unreadable parts).</summary>
    public string ReadStatus { get; set; }
    public string ReadNote { get; set; }
    public bool AssumeSafeUnscanned { get; set; }
    public List<PartRow> Parts { get; } = new List<PartRow>();
    public int ScriptItems { get; set; }
    /// <summary>Distinct script assets that were read.</summary>
    public int ScriptAssetsRead { get; set; }
    public List<UnscannedScript> Unscanned { get; } = new List<UnscannedScript>();
    /// <summary>For each script that was read, where it addresses other parts of its linkset.</summary>
    public List<ScriptLinkUses> ScriptLinks { get; } = new List<ScriptLinkUses>();
    /// <summary>Parts inside objects stored in prim contents, which this scan does not assess.</summary>
    public int NestedObjectParts { get; set; }
    public int NestedObjectScripts { get; set; }
    /// <summary>For each reason code, the number of parts that have it.</summary>
    public SortedDictionary<string, int> ReasonCounts { get; } = new SortedDictionary<string, int>(StringComparer.Ordinal);
    public double Seconds { get; set; }

    public int Count(PartDecision d) => Parts.Count(p => p.Decision == d);
    public bool AnyKept => Parts.Any(p => PartDecisionNames.IsKept(p.Decision));
}
