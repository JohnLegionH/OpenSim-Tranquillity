/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Rewrite;
using OpenSim.Framework;
using PermissionMask = OpenSim.Framework.PermissionMask;
using OpenSim.Archive.ScriptScan;

namespace OpenSim.Archive.Eligibility;

/// <summary>
/// Decides, for every part of an OAR, whether the converter turns it into a mesh part, keeps it as it is, or skips it, and
/// for a converted part which face count scripts will see afterwards. The archive is only read. The script scan is textual,
/// so "convert" means no problem was seen, not that there is none.
/// </summary>
public sealed class ArchiveEligibility
{
    private const int PCodePrim = 9;
    private const int SculptTypeMesh = 5;
    private const int SculptTypeGltf = 6;
    private const uint ExportBit = (uint)PermissionMask.Export;

    private readonly EligibilityOptions m_options;

    public ArchiveEligibility(EligibilityOptions options = null)
    {
        m_options = options ?? new EligibilityOptions();
    }

    /// <summary>What the object pass keeps about one part.</summary>
    private sealed class PartFacts
    {
        public string Entry;
        public Guid PartId;
        public ShapeData Shape;
        public int? PCode;
        public bool IsPlant;
        public bool Physics;
        public bool HasVehicle;
        public int VehicleType;
        public bool Flexible;
        public PermissionMasks Masks;
        public CreatorRef Creator;
        public UUID OwnerId;
    }

    private sealed class AssetIds : ArchiveCheckBase
    {
        public readonly HashSet<UUID> Ids = new HashSet<UUID>();
        public override string Id => "eligibility-assets";

        public override void OnRecord(ArchiveRecord record)
        {
            if (record is AssetRecord a)
                Ids.Add(a.AssetId);
        }
    }

    public EligibilityResult Evaluate(string path)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<PartFacts> facts = new List<PartFacts>();
        AssetIds assets = new AssetIds();
        ScanOptions scanOptions = new ScanOptions { AssumeSafeUnscanned = m_options.AssumeSafeUnscanned };
        scanOptions.ExtraChecks.Add(assets);
        scanOptions.ObjectObserver = (obj, entry, regionDir) =>
        {
            foreach (ParsedGroup group in obj.Groups)
                foreach (ParsedPart p in group.Parts)
                {
                    m_options.PartObserver?.Invoke(p);
                    facts.Add(new PartFacts
                    {
                        Entry = entry,
                        PartId = p.Part.PartId.Guid,
                        Shape = p.Shape,
                        PCode = p.Part.PCode,
                        IsPlant = p.Part.IsBuiltInPlant,
                        Physics = p.PhysicsFlag,
                        HasVehicle = p.HasVehicleParams,
                        VehicleType = p.VehicleType,
                        Flexible = p.Flexible,
                        Masks = p.Part.Masks,
                        Creator = p.Part.Creator,
                        OwnerId = p.Part.OwnerId
                    });
                }
        };

        ScanResult scan = new ArchiveScriptScanner(scanOptions).Scan(path);

        // An inventory archive keeps its objects boxed as assets; the converter reads region archives only for now.
        if (scan.ArchiveKind != "oar")
            throw new ScanException("'" + scan.Archive + "' is not a region archive (OAR); only region archives can be assessed");

        Dictionary<(string, Guid), PartFacts> byKey = new Dictionary<(string, Guid), PartFacts>();
        Dictionary<Guid, PartFacts> byId = new Dictionary<Guid, PartFacts>();
        foreach (PartFacts f in facts)
        {
            byKey.TryAdd((f.Entry, f.PartId), f);
            byId.TryAdd(f.PartId, f);
        }

        EligibilityResult result = new EligibilityResult
        {
            Archive = scan.Archive,
            ArchiveKind = scan.ArchiveKind,
            ReadStatus = scan.ReadStatus,
            ReadNote = scan.ReadNote,
            AssumeSafeUnscanned = m_options.AssumeSafeUnscanned,
            ScriptItems = scan.ScriptItems,
            ScriptItemsWithoutSource = scan.Unscanned.Count,
            NestedObjectParts = scan.NestedObjectParts,
            ToolVersion = typeof(ArchiveEligibility).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(ArchiveEligibility).Assembly.GetName().Version?.ToString() ?? "unknown"
        };

        int placeholder = 0;
        foreach (PartRow row in scan.Parts)
        {
            if (!byKey.TryGetValue((row.Entry, row.PartId), out PartFacts f) && !byId.TryGetValue(row.PartId, out f))
                continue;
            EligibilityRow er = Decide(row, f, assets.Ids);
            if (er.Action == PartAction.Convert)
                er.PlaceholderAsset = PlaceholderId(++placeholder);
            result.Parts.Add(er);
        }

        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    /// <summary>
    /// The stand-in for a mesh asset id in a draft plan. The prefix is not a random version-4 id, so a later step can tell
    /// a stand-in from a real id and replace it.
    /// </summary>
    public static readonly string PlaceholderPrefix = StandInIds.Prefix;

    public static Guid PlaceholderId(int ordinal) => StandInIds.Make(ordinal);

    public static bool IsPlaceholder(Guid id) => StandInIds.Is(id);

    private EligibilityRow Decide(PartRow scanRow, PartFacts f, HashSet<UUID> assetIds)
    {
        EligibilityRow er = new EligibilityRow
        {
            PartId = scanRow.PartId,
            LinksetId = scanRow.LinksetId,
            Build = scanRow.Build,
            Entry = scanRow.Entry,
            LinkNum = scanRow.LinkNum,
            IsRoot = scanRow.IsRoot,
            Name = scanRow.Name,
            FaceCountDependent = scanRow.FaceCountDependent,
            ScanDecision = PartDecisionNames.Text(scanRow.Decision)
        };

        void Add(PartAction action, string code, string detail)
        {
            er.Reasons.Add(new EligibilityReason { Code = code, Detail = detail });
            // The first rule that applies decides; the later ones are listed so the report shows everything that stands in the way.
            if (er.Code == null)
            {
                er.Code = code;
                er.Action = action;
            }
        }

        ShapeData shape = f.Shape;
        if (shape == null)
        {
            er.Kind = "other";
            Add(PartAction.Skip, ReasonCodes.NoShape, "the part has no shape element");
            return er;
        }

        bool isPrimCode = f.PCode == PCodePrim;
        bool isMesh = shape.SculptEntry && (shape.SculptType & 7) == SculptTypeMesh;
        bool isGltf = shape.SculptEntry && (shape.SculptType & 7) == SculptTypeGltf;
        bool isSculpt = shape.SculptEntry && !isMesh && !isGltf;
        er.Kind = f.IsPlant ? "plant" : !isPrimCode ? "other" : isMesh || isGltf ? "mesh" : isSculpt ? "sculpt" : "prim";

        // Face counts, where the part is a prim or a sculpt.
        if (isPrimCode && !isMesh && !isGltf)
        {
            FaceCounts counts = FaceAgreement.Measure(shape);
            er.ServerFaces = counts.Server;
            er.ViewerFaces = counts.Viewer;
        }

        if (isMesh || isGltf)
            Add(PartAction.Skip, ReasonCodes.AlreadyMesh, isGltf ? "the part is a glTF model" : "the part is already a mesh");
        if (f.IsPlant)
            Add(PartAction.Skip, ReasonCodes.Plant, "built-in tree or grass");
        else if (!isPrimCode)
            Add(PartAction.Skip, ReasonCodes.OtherPCode, "the part's PCode is " + (f.PCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ", not an ordinary prim");
        if (er.Action == PartAction.Skip)
            return er;

        string exportNote = ExportDenied(f);
        if (exportNote != null)
            Add(PartAction.KeepPrim, ReasonCodes.ExportDenied, exportNote);

        if (f.Physics)
            Add(PartAction.KeepPrim, ReasonCodes.Physical, "the physics flag is set");
        if (f.HasVehicle)
            Add(PartAction.KeepPrim, ReasonCodes.Physical, "the part carries vehicle parameters (type " + f.VehicleType.ToString(CultureInfo.InvariantCulture) + ")");

        if (f.Flexible)
            Add(PartAction.KeepPrim, ReasonCodes.Flexi, "the part is flexible");

        // A script that turns physics on later would leave a physical convex-hull mesh (the stored flag above only covers a part that is already physical).
        PartReason physicsCall = scanRow.Reasons.FirstOrDefault(r => r.Level == "warning" && r.Code == "PHYSICS_CALL" && r.Detail != "PRIM_PHYSICS_SHAPE_TYPE");
        if (physicsCall != null)
            Add(PartAction.KeepPrim, ReasonCodes.PhysicalScript, "a script turns physics on at run time"
                + (string.IsNullOrEmpty(physicsCall.Detail) ? "" : " (" + Sanitize.Text(physicsCall.Detail) + ")")
                + (string.IsNullOrEmpty(physicsCall.Script) ? "" : " in script '" + Sanitize.Text(physicsCall.Script) + "'"));

        if (er.ViewerFaces > MeshShape.MaxFaces)
            Add(PartAction.KeepPrim, ReasonCodes.FacesGt8, "the viewer gives the shape " + er.ViewerFaces + " faces and a mesh has at most " + MeshShape.MaxFaces);

        if (PartDecisionNames.IsKept(scanRow.Decision))
        {
            bool unsafeHit = scanRow.Decision == PartDecision.KeepPrimUnsafe;
            string level = unsafeHit ? "unsafe" : "uncertain";
            foreach (string code in scanRow.Reasons.Where(r => r.Level == level).Select(r => r.Code).Distinct())
                er.ScanCodes.Add(code);
            PartReason first = scanRow.Reasons.FirstOrDefault(r => r.Level == level);
            string detail = "script scan: " + string.Join(", ", er.ScanCodes);
            if (first != null && !string.IsNullOrEmpty(first.Script))
                detail += " (script '" + Sanitize.Text(first.Script) + "'" + (first.Line > 0 ? " line " + first.Line.ToString(CultureInfo.InvariantCulture) : "") + ")";
            Add(PartAction.KeepPrim, unsafeHit ? ReasonCodes.ScriptUnsafe : ReasonCodes.ScriptUncertain, detail);
        }

        if (isPrimCode && !isSculpt && er.ServerFaces != er.ViewerFaces && FaceRuleCases.Explain(shape) == null)
            Add(PartAction.KeepPrim, ReasonCodes.FaceRuleUnverified, "the server counts " + er.ServerFaces + " sides and the viewer " + er.ViewerFaces + ", and the difference is not one the viewer source has been checked for");

        if (isPrimCode && !isSculpt && UvRules.Unverified(shape) is string uvReason)
            Add(PartAction.KeepPrim, ReasonCodes.UvUnverified, uvReason);

        if (isPrimCode && er.ServerFaces != er.ViewerFaces)
        {
            string detail = "the server counts " + er.ServerFaces + " sides and the viewer " + er.ViewerFaces;
            if (scanRow.FaceCountDependent)
                Add(PartAction.KeepPrim, ReasonCodes.FaceCountMismatch, detail + ", and a script asks the face count or addresses faces by number");
            else
                er.Warnings.Add(new EligibilityReason { Code = ReasonCodes.SideCountChanges, Detail = detail + "; llGetNumberOfSides will return " + er.ViewerFaces + " after conversion" });
        }

        foreach (PartReason w in scanRow.Reasons.Where(r => r.Level == "warning"))
            if (!er.Warnings.Any(x => x.Code == w.Code))
                er.Warnings.Add(new EligibilityReason { Code = w.Code, Detail = "script scan" + (string.IsNullOrEmpty(w.Script) ? "" : " (script '" + Sanitize.Text(w.Script) + "')") + (string.IsNullOrEmpty(w.Detail) ? "" : ": " + w.Detail) });

        // A cut, hollow or open sculpt converts only where the faces are built exactly as the viewer builds them: a circle
        // profile on a circular path. Any other combination would need a face list nobody has checked.
        if (isSculpt && SculptHasProfileFaces(shape) && !SculptGeometry.Described(PrimParams.FromShape(shape)))
            Add(PartAction.KeepPrim, ReasonCodes.SculptShapeUnverified, "the sculpt has a profile cut, a hollow or an open path and is not a circle profile on a circular path");

        if (isSculpt && shape.SculptTexture != UUID.Zero && !assetIds.Contains(shape.SculptTexture))
            Add(PartAction.KeepPrim, ReasonCodes.SculptMapMissing, "the sculpt map is not in the archive");
        else if (isSculpt && shape.SculptTexture == UUID.Zero)
            Add(PartAction.KeepPrim, ReasonCodes.SculptMapMissing, "the sculpt names no map");

        if (er.Code == null)
        {
            er.Action = PartAction.Convert;
            // A plain sculpt is one face, to the server and to the viewer; a cut, hollow or open one has the viewer's list.
            er.TargetFaces = er.ViewerFaces;
        }
        return er;
    }

    // A profile cut, a hollow or a path cut: the cases that give a sculpt faces beyond its one outer face.
    private static bool SculptHasProfileFaces(ShapeData shape)
    {
        PrimParams p = PrimParams.FromShape(shape);
        return p.ProfileHollow > 0f || p.ProfileBegin > 0f || p.ProfileEnd < 1f || p.PathBegin > 0f || p.PathEnd < 1f;
    }

    /// <summary>
    /// The creator's export right. A part held by someone other than its creator may be exported only when the owner or
    /// everyone mask carries the export bit; the same rule the inspector uses for its export finding. A part with no export
    /// information in its base mask, or whose creator is unknown, is not refused.
    /// </summary>
    private static string ExportDenied(PartFacts f)
    {
        CreatorRef creator = f.Creator;
        PermissionMasks m = f.Masks;
        if (creator == null || m == null || creator.IsLibrary || f.OwnerId == UUID.Zero || creator.Id == UUID.Zero || creator.Id == f.OwnerId)
            return null;
        if (!m.Base.HasValue || (m.Base.Value & ExportBit) == 0)
            return null;
        bool allowed = ((m.Owner ?? 0) & ExportBit) != 0 || ((m.Everyone ?? 0) & ExportBit) != 0;
        return allowed ? null : "the creator did not allow export (the export right is in the base mask but in neither the owner nor the everyone mask)";
    }
}
