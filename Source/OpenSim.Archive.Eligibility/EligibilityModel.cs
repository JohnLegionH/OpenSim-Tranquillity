/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Eligibility;

/// <summary>What the converter does with one part.</summary>
public enum PartAction
{
    /// <summary>The part becomes a mesh part.</summary>
    Convert,
    /// <summary>The part stays as it is because converting it could break something.</summary>
    KeepPrim,
    /// <summary>The part is not a prim that needs converting (already a mesh, a built-in plant, not a prim).</summary>
    Skip
}

public static class ReasonCodes
{
    public const string AlreadyMesh = "ALREADY_MESH";
    public const string Plant = "PLANT";
    public const string OtherPCode = "OTHER_PCODE";
    public const string NoShape = "NO_SHAPE";
    public const string ExportDenied = "EXPORT_DENIED";
    public const string Physical = "PHYSICAL";
    public const string Flexi = "FLEXI";
    public const string FacesGt8 = "FACES_GT8";
    public const string ScriptUnsafe = "SCRIPT_UNSAFE";
    public const string ScriptUncertain = "SCRIPT_UNCERTAIN";
    public const string FaceCountMismatch = "FACECOUNT_MISMATCH";
    public const string SculptMapMissing = "SCULPT_MAP_MISSING";
    /// <summary>A sculpt with a profile cut, a hollow or an open path whose faces are not built exactly as the viewer builds them (anything but a circle profile on a circular path).</summary>
    public const string SculptShapeUnverified = "SCULPT_SHAPE_UNVERIFIED";
    /// <summary>A script turns physics on at run time (STATUS_PHYSICS, PRIM_PHYSICS or a vehicle call); the part would become a physical convex-hull mesh.</summary>
    public const string PhysicalScript = "PHYSICAL_SCRIPT";
    /// <summary>The server and viewer face counts differ for a reason that is not one of the settled cases (see FaceRuleCases).</summary>
    public const string FaceRuleUnverified = "FACERULE_UNVERIFIED";
    /// <summary>The shape's texture coordinates, as the mesher builds them, are not the viewer's (see UvRules).</summary>
    public const string UvUnverified = "UV_UNVERIFIED";
    /// <summary>A warning, not a reason to keep: the side count changes and no script was seen to depend on it.</summary>
    public const string SideCountChanges = "SIDE_COUNT_CHANGES";
}

public sealed class EligibilityReason
{
    public string Code { get; set; }
    public string Detail { get; set; }
}

public sealed class EligibilityRow
{
    public Guid PartId { get; set; }
    public Guid LinksetId { get; set; }
    public string Build { get; set; }
    public string Entry { get; set; }
    public int LinkNum { get; set; }
    public bool IsRoot { get; set; }
    public string Name { get; set; }
    /// <summary>"prim", "sculpt", "mesh", "plant" or "other".</summary>
    public string Kind { get; set; }
    public PartAction Action { get; set; }
    /// <summary>The reason code of the first rule that applied; for a converted part, empty.</summary>
    public string Code { get; set; }
    /// <summary>Every rule that applied, in rule order. The first is <see cref="Code"/>.</summary>
    public List<EligibilityReason> Reasons { get; } = new List<EligibilityReason>();
    public List<EligibilityReason> Warnings { get; } = new List<EligibilityReason>();
    /// <summary>The server's count (llGetNumberOfSides) before conversion; 0 when not worked out.</summary>
    public int ServerFaces { get; set; }
    /// <summary>The viewer rule's count before conversion; 0 when not worked out.</summary>
    public int ViewerFaces { get; set; }
    /// <summary>For a converted part, the face count scripts see afterwards (the mesh shape fields give exactly this count).</summary>
    public int TargetFaces { get; set; }
    public bool FaceCountDependent { get; set; }
    /// <summary>The script scan's own decision for the part.</summary>
    public string ScanDecision { get; set; }
    /// <summary>The distinct codes of the scan's findings that kept the part.</summary>
    public List<string> ScanCodes { get; } = new List<string>();
    /// <summary>The stand-in id for the part's mesh asset in the draft plan; empty unless converted.</summary>
    public Guid PlaceholderAsset { get; set; }

    public bool IsKept => Action == PartAction.KeepPrim;
}

public sealed class EligibilityOptions
{
    /// <summary>Treat a script with no readable source as harmless. Off by default; the report says loudly when it is on.</summary>
    public bool AssumeSafeUnscanned { get; set; }

    /// <summary>Called once for every part read in the archive pass, so a caller can keep what it needs of the part without a second pass.</summary>
    public Action<ParsedPart> PartObserver { get; set; }
}

public sealed class EligibilityResult
{
    public string ToolVersion { get; set; }
    public string Archive { get; set; }
    public string ArchiveKind { get; set; }
    public string ReadStatus { get; set; }
    public string ReadNote { get; set; }
    public bool AssumeSafeUnscanned { get; set; }
    public List<EligibilityRow> Parts { get; } = new List<EligibilityRow>();
    public int ScriptItems { get; set; }
    public int ScriptItemsWithoutSource { get; set; }
    public int NestedObjectParts { get; set; }
    public double Seconds { get; set; }

    public int Count(PartAction a) => Parts.Count(p => p.Action == a);
    public int Warned => Parts.Count(p => p.Action == PartAction.Convert && p.Warnings.Count > 0);

    /// <summary>For each primary reason code, the number of parts.</summary>
    public SortedDictionary<string, int> ReasonCounts()
    {
        SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (EligibilityRow p in Parts)
            if (p.Action != PartAction.Convert)
                counts[p.Code] = counts.TryGetValue(p.Code, out int n) ? n + 1 : 1;
        return counts;
    }

    /// <summary>For each script-scan finding code behind a SCRIPT_* decision, the number of parts.</summary>
    public SortedDictionary<string, int> ScanCodeCounts()
    {
        SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (EligibilityRow p in Parts)
            foreach (string c in p.ScanCodes)
                counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
        return counts;
    }
}
