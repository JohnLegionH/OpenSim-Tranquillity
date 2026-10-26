/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.ScriptScan;
using OpenSim.Framework;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>Reads an archive and plans the merge of each of its linksets. The archive is only read.</summary>
public static class ArchiveMergePlanner
{
    private const int LslAssetType = 10, LslBytecodeAssetType = 13;

    public static MergePlan Plan(string archivePath, MergeOptions options, ConvertOptions convert = null) =>
        Plan(archivePath, options, convert, out _);

    /// <summary>Plans the merge and also returns the parts as the eligibility pass read them, which the merge itself builds its meshes from.</summary>
    public static MergePlan Plan(string archivePath, MergeOptions options, ConvertOptions convert, out Dictionary<Guid, ParsedPart> parts) =>
        Plan(archivePath, options, convert, out parts, out _);

    /// <summary>As above, and also returns the linksets as the planner read them, in the order of the plan's linksets.</summary>
    internal static MergePlan Plan(string archivePath, MergeOptions options, ConvertOptions convert, out Dictionary<Guid, ParsedPart> parts, out List<MergeLinkset> linksetsRead)
    {
        options ??= new MergeOptions();
        Dictionary<Guid, ParsedPart> parsed = new Dictionary<Guid, ParsedPart>();
        EligibilityResult elig = new ArchiveEligibility(new EligibilityOptions
        {
            AssumeSafeUnscanned = convert?.AssumeSafeUnscanned ?? false,
            PartObserver = pp => parsed.TryAdd(pp.Part.PartId.Guid, pp)
        }).Evaluate(archivePath);
        parts = parsed;
        Dictionary<Guid, LegacyMaterialInfo> materials = ArchiveMaterials.Read(archivePath);
        Dictionary<Guid, bool> translucent = ArchiveTextureAlpha.Read(archivePath, TextureIds(parsed.Values));

        List<MergeLinkset> linksets = new List<MergeLinkset>();
        foreach (IGrouping<(string, Guid), EligibilityRow> build in elig.Parts.GroupBy(r => (r.Entry, r.LinksetId)))
        {
            MergeLinkset ls = new MergeLinkset { Id = build.Key.Item2, Entry = build.Key.Item1 };
            List<EligibilityRow> rows = build.ToList();
            ls.Name = (rows.FirstOrDefault(r => r.IsRoot) ?? rows[0]).Name ?? "";
            foreach (EligibilityRow row in rows)
                ls.Parts.Add(ReadPart(row, parsed.TryGetValue(row.PartId, out ParsedPart pp) ? pp : null, materials, translucent, convert?.Clean ?? true));
            ls.Links = LinkFactsFor(elig.Scan, ls.Id, rows.Select(r => r.PartId).ToHashSet());
            linksets.Add(ls);
        }

        linksetsRead = linksets;
        MergePlan plan = MergePlanner.Plan(linksets, options, Path.GetFileName(archivePath));
        plan.ToolVersion = typeof(ArchiveMergePlanner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ArchiveMergePlanner).Assembly.GetName().Version?.ToString() ?? "unknown";
        if (elig.ReadStatus == "Partial")
            plan.Warnings.Add("The archive was read only in part (" + elig.ReadNote + "); the plan covers what was read.");
        return plan;
    }

    // ---------------------------------------------------------------- parts

    private static MergePart ReadPart(EligibilityRow row, ParsedPart pp, Dictionary<Guid, LegacyMaterialInfo> materials, Dictionary<Guid, bool> translucent, bool clean)
    {
        MergePart p = new MergePart
        {
            PartId = row.PartId, LinkNum = row.LinkNum, IsRoot = row.IsRoot, Name = row.Name ?? "", Kind = row.Kind,
            NotConvertible = row.Action == PartAction.KeepPrim ? row.Code ?? "" : ""
        };
        if (pp == null)
        {
            p.NotConvertible = "NO_PART_DATA";
            return p;
        }

        p.Creator = pp.Part.Creator.Id + (pp.Part.Creator.HasData ? " " + pp.Part.Creator.Data : "");
        PermissionMasks m = pp.Part.Masks;
        p.Masks = string.Join("/", new[] { m.Base, m.Owner, m.Group, m.Everyone, m.NextOwner }.Select(x => x.HasValue ? x.Value.ToString(CultureInfo.InvariantCulture) : "-"));
        p.SurfaceMaterial = pp.SurfaceMaterial;
        p.Scripted = pp.Items.Any(i => i.AssetType == LslAssetType || i.AssetType == LslBytecodeAssetType);
        p.HasContents = pp.Items.Any(i => i.AssetType != LslAssetType && i.AssetType != LslBytecodeAssetType);
        p.Light = pp.Light;
        p.Flexible = pp.Flexible;
        p.SitTarget = pp.SitTarget;
        p.HoverText = pp.HoverText;
        p.Particles = pp.HasParticles;
        p.TextureAnimation = pp.TextureAnimation.Length > 0;
        p.Sound = pp.HasSound;
        p.Physical = pp.PhysicsFlag || pp.HasVehicleParams;
        p.VolumeDetect = pp.VolumeDetect;
        p.Phantom = pp.Phantom;
        p.PhysicsShapeType = pp.PhysicsShapeType;
        SetBox(p, pp, row.IsRoot);

        if (row.Action != PartAction.Convert || row.Kind != "prim" || pp.Shape == null)
            return p;

        List<EncoderFace> faces;
        try
        {
            faces = FaceBuilder.FromPrim(pp.Shape, out _, clean);
            PhysicsGeometry physics = PrimPhysics.Build(pp.Shape, null, null, pp.Scale, out _);
            if (physics == null)
            {
                p.NotConvertible = "PHYSICS_UNAVAILABLE";
                return p;
            }
            p.PhysicsVertices = physics.VertexCount;
        }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IndexOutOfRangeException || e is NullReferenceException)
        {
            p.NotConvertible = "GEOMETRY_FAILED";
            return p;
        }

        FillFaces(p, pp, faces, materials, translucent);
        return p;
    }

    private static void SetBox(MergePart p, ParsedPart pp, bool isRoot)
    {
        Vector3 pos = isRoot ? Vector3.Zero : pp.OffsetPosition;
        Quaternion rot = isRoot ? Quaternion.Identity : pp.RotationOffset;
        Vector3 half = pp.Scale * 0.5f;
        float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
            Vector3 w = Vector3.Transform(corner, Matrix4.CreateFromQuaternion(rot)) + pos;
            float[] c = { w.X, w.Y, w.Z };
            for (int k = 0; k < 3; k++)
            {
                lo[k] = Math.Min(lo[k], c[k]);
                hi[k] = Math.Max(hi[k], c[k]);
            }
        }
        p.BoxMin = lo;
        p.BoxMax = hi;
    }

    private static void FillFaces(MergePart p, ParsedPart pp, List<EncoderFace> faces, Dictionary<Guid, LegacyMaterialInfo> materials, Dictionary<Guid, bool> translucent)
    {
        Primitive.TextureEntry te = ReadTextureEntry(pp);
        HashSet<int> pbrFaces = PbrFaces(pp);

        for (int i = 0; i < faces.Count; i++)
        {
            Primitive.TextureEntryFace tef = te == null ? null : (te.FaceTextures != null && i < te.FaceTextures.Length ? te.FaceTextures[i] : null) ?? te.DefaultTexture;
            MergeFace f = new MergeFace { Vertices = faces[i].VertexCount };
            if (tef != null)
            {
                f.TextureId = tef.TextureID.ToString();
                f.Color = string.Create(CultureInfo.InvariantCulture, $"{tef.RGBA.R:0.###},{tef.RGBA.G:0.###},{tef.RGBA.B:0.###},{tef.RGBA.A:0.###}");
                f.Glow = tef.Glow;
                f.Shiny = (int)tef.Shiny;
                f.Bump = (int)tef.Bump;
                f.Fullbright = tef.Fullbright;
                f.TexGen = (int)tef.TexMapType;
                f.MaterialId = tef.MaterialID == UUID.Zero ? "" : tef.MaterialID.ToString();
                f.Transform = new FaceTransform(tef.RepeatU, tef.RepeatV, tef.OffsetU, tef.OffsetV, tef.Rotation);
                LegacyMaterialInfo material = null;
                if (tef.MaterialID != UUID.Zero)
                    materials.TryGetValue(tef.MaterialID.Guid, out material);
                f.Alpha = FaceAlpha.IsAlpha(tef.RGBA.A, translucent.TryGetValue(tef.TextureID.Guid, out bool t) && t, material);
                f.Pbr = pbrFaces.Contains(i);
                f.Media = tef.MediaFlags;
                f.Bakeable = FaceBaking.Bakeable(f.Transform, f.TexGen, f.MaterialId, material, pbrFaces.Contains(i), tef.MediaFlags, out string why);
                f.UnbakedReason = why;
            }
            p.Faces.Add(f);
        }
    }

    internal static Primitive.TextureEntry ReadTextureEntry(ParsedPart pp)
    {
        if (pp.TextureEntry.Length <= 16)
            return null;
        try { return new Primitive.TextureEntry(pp.TextureEntry, 0, pp.TextureEntry.Length); }
        catch (Exception) { return null; }
    }

    /// <summary>The texture ids the faces of the convertible parts show, for the one pass over the archive that reads their alpha channels.</summary>
    private static HashSet<Guid> TextureIds(IEnumerable<ParsedPart> parts)
    {
        HashSet<Guid> ids = new HashSet<Guid>();
        foreach (ParsedPart pp in parts)
        {
            Primitive.TextureEntry te = ReadTextureEntry(pp);
            if (te == null)
                continue;
            if (te.DefaultTexture != null)
                ids.Add(te.DefaultTexture.TextureID.Guid);
            if (te.FaceTextures != null)
                foreach (Primitive.TextureEntryFace face in te.FaceTextures)
                    if (face != null)
                        ids.Add(face.TextureID.Guid);
        }
        ids.Remove(Guid.Empty);
        return ids;
    }

    private static HashSet<int> PbrFaces(ParsedPart pp)
    {
        HashSet<int> faces = new HashSet<int>();
        if (pp.ExtraParams.Length == 0 && pp.MaterialOverrides.Length == 0)
            return faces;
        try
        {
            PrimitiveBaseShape shape = new PrimitiveBaseShape();
            if (pp.ExtraParams.Length > 0)
                shape.ReadInExtraParamsBytes(pp.ExtraParams);
            if (pp.MaterialOverrides.Length > 0)
                shape.RenderMaterialsOvrFromRawBin(pp.MaterialOverrides);
            if (shape.RenderMaterials?.entries != null)
                foreach (var e in shape.RenderMaterials.entries)
                    faces.Add(e.te_index);
            if (shape.RenderMaterials?.overrides != null)
                foreach (var o in shape.RenderMaterials.overrides)
                    if (!string.IsNullOrEmpty(o.data))
                        faces.Add(o.te_index);
        }
        catch (Exception)
        {
            // damaged material data: the faces are read as ordinary faces
        }
        return faces;
    }

    // ---------------------------------------------------------------- what the scripts say

    private static LinkFacts LinkFactsFor(ScanResult scan, Guid linksetId, HashSet<Guid> partIds)
    {
        LinkFacts facts = new LinkFacts();
        List<string> locks = new List<string>();
        foreach (UnscannedScript u in scan.Unscanned.Where(u => partIds.Contains(u.PartId)))
            locks.Add("script '" + u.Script + "' could not be read (" + u.Reason + ")");
        foreach (ScriptLinkUses s in scan.ScriptLinks.Where(s => s.LinksetId == linksetId))
        {
            if (s.Truncated)
                locks.Add("script '" + s.Script + "' has more findings than the scan keeps");
            foreach (LinkUse u in s.Uses)
            {
                string where = "script '" + s.Script + "' line " + u.Line;
                switch (u.Kind)
                {
                    case LinkUseKind.Literal: facts.Literal.Add(u.Number); break;
                    case LinkUseKind.Root: facts.RootAddressed = true; break;
                    case LinkUseKind.Self: break;
                    case LinkUseKind.Set:
                    case LinkUseKind.AllOthers:
                    case LinkUseKind.AllChildren:
                        if (!u.MessageOnly)
                            locks.Add(where + ": " + u.Function + " addresses the whole linkset");
                        break;
                    case LinkUseKind.Unknown: locks.Add(where + ": " + u.Function + " has a link argument the scan cannot work out"); break;
                    case LinkUseKind.Enumeration: locks.Add(where + ": " + u.Function + " depends on the number of parts or on link numbers"); break;
                    case LinkUseKind.LinkMessageSender: locks.Add(where + ": link_message reads the sender's link number"); break;
                }
            }
        }
        if (locks.Count > 0)
            facts.LockedBy = locks[0] + (locks.Count > 1 ? " (and " + (locks.Count - 1) + " more)" : "");
        return facts;
    }
}

/// <summary>Which of the wanted textures of an archive have an alpha channel that is not fully opaque, read in one pass.</summary>
internal static class ArchiveTextureAlpha
{
    private const string Suffix = "_texture.jp2";

    public static Dictionary<Guid, bool> Read(string archivePath, HashSet<Guid> wanted)
    {
        Dictionary<Guid, bool> result = new Dictionary<Guid, bool>();
        if (wanted.Count == 0)
            return result;
        using FileStream fs = File.OpenRead(archivePath);
        int b0 = fs.ReadByte(), b1 = fs.ReadByte();
        fs.Seek(0, SeekOrigin.Begin);
        Stream tarStream = b0 == 0x1f && b1 == 0x8b ? new GZipStream(new BufferedStream(fs, 1 << 16), CompressionMode.Decompress) : fs;
        TarStreamReader tar = new TarStreamReader(tarStream);
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
        {
            if (!h.IsFile || !h.Name.StartsWith("assets/", StringComparison.Ordinal) || !h.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            string idText = h.Name.Substring("assets/".Length, h.Name.Length - "assets/".Length - Suffix.Length);
            if (Guid.TryParse(idText, out Guid id) && wanted.Contains(id))
                result[id] = TextureAlpha.IsTranslucent(tar.ReadBody());
        }
        return result;
    }
}

/// <summary>The legacy materials an archive holds, by asset id: whether each has normal and specular maps and how they are drawn.</summary>
internal static class ArchiveMaterials
{
    private const string Suffix = "_material.xml";

    public static Dictionary<Guid, LegacyMaterialInfo> Read(string archivePath)
    {
        Dictionary<Guid, LegacyMaterialInfo> result = new Dictionary<Guid, LegacyMaterialInfo>();
        using FileStream fs = File.OpenRead(archivePath);
        int b0 = fs.ReadByte(), b1 = fs.ReadByte();
        fs.Seek(0, SeekOrigin.Begin);
        Stream tarStream = b0 == 0x1f && b1 == 0x8b ? new GZipStream(new BufferedStream(fs, 1 << 16), CompressionMode.Decompress) : fs;
        TarStreamReader tar = new TarStreamReader(tarStream);
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
        {
            if (!h.IsFile || !h.Name.StartsWith("assets/", StringComparison.Ordinal) || !h.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            string idText = h.Name.Substring("assets/".Length, h.Name.Length - "assets/".Length - Suffix.Length);
            if (!Guid.TryParse(idText, out Guid id))
                continue;
            LegacyMaterialInfo info = Parse(tar.ReadBody());
            if (info != null)
                result[id] = info;
        }
        return result;
    }

    /// <summary>Reads the LLSD the region stores for a legacy material; the repeats, offsets and rotations are stored times 10000.</summary>
    public static LegacyMaterialInfo Parse(byte[] xml)
    {
        try
        {
            if (OSDParser.Deserialize(xml) is not OSDMap map)
                return null;
            const float scale = 0.0001f;
            float F(string key) => scale * (float)map[key].AsReal();
            return new LegacyMaterialInfo
            {
                HasNormalMap = map["NormMap"].AsUUID() != UUID.Zero,
                NormalTransform = new FaceTransform(F("NormRepeatX"), F("NormRepeatY"), F("NormOffsetX"), F("NormOffsetY"), F("NormRotation")),
                HasSpecularMap = map["SpecMap"].AsUUID() != UUID.Zero,
                SpecularTransform = new FaceTransform(F("SpecRepeatX"), F("SpecRepeatY"), F("SpecOffsetX"), F("SpecOffsetY"), F("SpecRotation")),
                DiffuseAlphaMode = (int)map["DiffuseAlphaMode"].AsUInteger()
            };
        }
        catch (Exception e) when (e is OSDException || e is FormatException || e is System.Xml.XmlException)
        {
            return null;
        }
    }
}

/// <summary>Writes a merge plan as JSON: one file for the archive and one for each linkset.</summary>
public static class MergePlanFiles
{
    public static string ArchiveJson(MergePlan plan)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", plan.Version);
            w.WriteString("tool", plan.ToolVersion);
            w.WriteString("archive", plan.Archive);
            w.WriteStartArray("warnings");
            foreach (string s in plan.Warnings) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteStartObject("totals");
            w.WriteNumber("linksets", plan.Linksets.Count);
            w.WriteNumber("partsBefore", plan.PartsBefore);
            w.WriteNumber("partsAfter", plan.PartsAfter);
            w.WriteNumber("mergedParts", plan.MergedParts);
            w.WriteNumber("keptParts", plan.KeptParts);
            w.WriteNumber("alphaFaces", plan.AlphaFaces);
            w.WriteNumber("mergedMembers", plan.Linksets.Sum(l => l.Merged.Sum(m => m.Members.Count)));
            w.WriteStartObject("keptByFirstRule");
            foreach (var g in KeptByFirstRule(plan))
                w.WriteNumber(g.Key, g.Value);
            w.WriteEndObject();
            w.WriteStartObject("keptByAnyRule");
            foreach (var g in KeptByAnyRule(plan))
                w.WriteNumber(g.Key, g.Value);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartArray("linksets");
            foreach (LinksetPlan l in plan.Linksets)
            {
                w.WriteStartObject();
                w.WriteString("id", l.Id.ToString());
                w.WriteString("name", l.Name);
                w.WriteString("entry", l.Entry);
                w.WriteString("file", "linksets/" + LinksetFileName(plan, l));
                w.WriteNumber("partsBefore", l.PartsBefore);
                w.WriteNumber("partsAfter", l.PartsAfter);
                w.WriteNumber("mergedParts", l.Merged.Count);
                w.WriteNumber("keptParts", l.Kept.Count);
                w.WriteString("lockedBy", l.LockedBy);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public static string LinksetJson(LinksetPlan plan)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("id", plan.Id.ToString());
            w.WriteString("name", plan.Name);
            w.WriteString("entry", plan.Entry);
            w.WriteNumber("partsBefore", plan.PartsBefore);
            w.WriteNumber("partsAfter", plan.PartsAfter);
            w.WriteString("lockedBy", plan.LockedBy);
            w.WriteNumber("alphaFaces", plan.AlphaFaces);
            w.WriteStartArray("merged");
            foreach (MergedPartPlan m in plan.Merged)
            {
                w.WriteStartObject();
                w.WriteString("id", m.Id.ToString());
                w.WriteNumber("newLinkNum", m.NewLinkNum);
                w.WriteString("name", m.Name);
                w.WriteString("description", m.Description);
                w.WriteBoolean("alpha", m.Alpha);
                w.WriteNumber("extentMeters", m.ExtentMeters);
                w.WriteNumber("physicsVertices", m.PhysicsVertices);
                w.WriteNumber("faces", m.Slots.Count);
                w.WriteStartArray("members");
                foreach (MemberPlan x in m.Members)
                {
                    w.WriteStartObject();
                    w.WriteString("part", x.PartId.ToString());
                    w.WriteNumber("linkNum", x.LinkNum);
                    w.WriteString("name", x.Name);
                    w.WriteString("creator", x.Creator);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("slots");
                foreach (SlotPlan s in m.Slots)
                {
                    w.WriteStartObject();
                    w.WriteNumber("slot", s.Slot);
                    w.WriteString("material", s.MaterialKey);
                    w.WriteBoolean("alpha", s.Alpha);
                    w.WriteBoolean("baked", s.Baked);
                    w.WriteNumber("sourceFaces", s.SourceFaces);
                    w.WriteNumber("vertices", s.Vertices);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("manifest", m.Manifest);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("kept");
            foreach (KeptPartPlan k in plan.Kept)
            {
                w.WriteStartObject();
                w.WriteString("part", k.PartId.ToString());
                w.WriteNumber("linkNum", k.LinkNum);
                w.WriteNumber("newLinkNum", k.NewLinkNum);
                w.WriteString("name", k.Name);
                w.WriteNumber("alphaFaces", k.AlphaFaces);
                w.WriteStartArray("rules");
                foreach (KeepRule r in k.Rules)
                {
                    w.WriteStartObject();
                    w.WriteString("rule", r.Rule);
                    w.WriteString("code", r.Code);
                    w.WriteString("detail", r.Detail);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public static SortedDictionary<string, int> KeptByFirstRule(MergePlan plan)
    {
        SortedDictionary<string, int> d = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (KeptPartPlan k in plan.Linksets.SelectMany(l => l.Kept))
        {
            string key = k.Rules[0].Rule + " " + k.Rules[0].Code;
            d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        return d;
    }

    /// <summary>For each rule and code, how many parts it applies to (a part with several reasons is counted under each).</summary>
    public static SortedDictionary<string, int> KeptByAnyRule(MergePlan plan)
    {
        SortedDictionary<string, int> d = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (KeepRule r in plan.Linksets.SelectMany(l => l.Kept).SelectMany(k => k.Rules))
        {
            string key = r.Rule + " " + r.Code;
            d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        return d;
    }

    public static string ReportText(MergePlan plan)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Merge plan for " + plan.Archive + " (nothing is written but the plan files)");
        sb.AppendLine("  parts before " + plan.PartsBefore + ", after " + plan.PartsAfter + " (" + plan.MergedParts + " merged parts hold "
            + plan.Linksets.Sum(l => l.Merged.Sum(m => m.Members.Count)) + " parts, " + plan.KeptParts + " parts stay as they are)");
        sb.AppendLine("  alpha faces marked " + plan.AlphaFaces + ", locked linksets " + plan.Linksets.Count(l => l.LockedBy.Length > 0) + " of " + plan.Linksets.Count);
        sb.AppendLine("  parts kept, by the first rule that keeps each:");
        foreach (var g in KeptByFirstRule(plan))
            sb.AppendLine("    " + g.Key.PadRight(40) + g.Value);
        sb.AppendLine("  parts kept, by every rule that applies (a part can have several):");
        foreach (var g in KeptByAnyRule(plan))
            sb.AppendLine("    " + g.Key.PadRight(40) + g.Value);
        foreach (string warning in plan.Warnings)
            sb.AppendLine("  WARNING: " + warning);
        sb.AppendLine("  linksets:");
        foreach (LinksetPlan l in plan.Linksets)
            sb.AppendLine("    " + (l.Name ?? "").PadRight(40) + l.PartsBefore.ToString().PadLeft(5) + " -> " + l.PartsAfter.ToString().PadLeft(4)
                + (l.LockedBy.Length > 0 ? "  locked: " + l.LockedBy : ""));
        return sb.ToString();
    }

    private static string LinksetFileName(MergePlan plan, LinksetPlan l)
    {
        string stem = Path.GetFileNameWithoutExtension(l.Entry ?? "linkset");
        StringBuilder sb = new StringBuilder();
        foreach (char c in stem)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        string clipped = sb.Length > 60 ? sb.ToString(0, 60) : sb.ToString();
        return plan.Linksets.IndexOf(l).ToString("0000", CultureInfo.InvariantCulture) + "-" + clipped + "-" + l.Id.ToString("N").Substring(0, 8) + ".json";
    }

    /// <summary>Writes merge-plan.json, merge-plan.txt and linksets/*.json into the folder, which must not hold a plan yet.</summary>
    public static void Write(MergePlan plan, string folder)
    {
        if (File.Exists(Path.Combine(folder, "merge-plan.json")))
            throw new IOException("'" + folder + "' already holds a merge plan");
        Directory.CreateDirectory(Path.Combine(folder, "linksets"));
        foreach (LinksetPlan l in plan.Linksets)
            File.WriteAllText(Path.Combine(folder, "linksets", LinksetFileName(plan, l)), LinksetJson(l));
        File.WriteAllText(Path.Combine(folder, "merge-plan.txt"), ReportText(plan));
        File.WriteAllText(Path.Combine(folder, "merge-plan.json"), ArchiveJson(plan));
    }
}
