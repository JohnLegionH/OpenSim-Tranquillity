/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Rewrite;
using OpenSim.Framework;
using OpenSim.MeshAsset;
using SysQuaternion = System.Numerics.Quaternion;
using SysVector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>What was built for one merged part, and how far its mesh is from the parts it replaces.</summary>
public sealed class MergedPartReport
{
    public Guid Id { get; set; }
    public Guid MeshId { get; set; }
    public Guid ManifestAssetId { get; set; }
    public string Name { get; set; }
    public string Linkset { get; set; }
    public int Members { get; set; }
    public int Faces { get; set; }
    public int Vertices { get; set; }
    public int Triangles { get; set; }
    public int PlanarFaces { get; set; }
    public int BakedFaces { get; set; }
    public int UnbakedFaces { get; set; }
    public bool TookTheRoot { get; set; }
    public long MeshBytes { get; set; }
    /// <summary>Corners compared against the original parts, and the largest difference found, in metres, in texture units and in normal components.</summary>
    public int Corners { get; set; }
    public double WorstPositionMeters { get; set; }
    public double WorstUv { get; set; }
    public double WorstNormal { get; set; }
    /// <summary>The same, measured on the mesh as written and read back (the asset stores positions and coordinates in 16 bits over the mesh's range).</summary>
    public double WorstPositionMetersDecoded { get; set; }
    public double WorstUvDecoded { get; set; }
    public double WorstNormalDecoded { get; set; }
    /// <summary>The most a texture coordinate of the part can have moved when the mesh asset stores it (half a step of 65,535 over the range of its face), in texels of the face's texture; and the most that was measured after reading the asset back.</summary>
    public double WorstUvTexels { get; set; }
    public double MeasuredUvTexels { get; set; }
    /// <summary>Faces whose legacy material maps were baked with the diffuse, the new materials the part names, and the most any map's coordinates moved from where the viewer draws them (in texels of the map's own texture, measured on the mesh as written).</summary>
    public int MapFaces { get; set; }
    public int NewMaterials { get; set; }
    /// <summary>Merged faces that carry a PBR material or override, and merged faces that carry a media entry.</summary>
    public int PbrFaces { get; set; }
    public int MediaFaces { get; set; }
    public double MeasuredMapTexels { get; set; }
    /// <summary>The lower levels of detail the mesh carries, medium first, with what was checked about each.</summary>
    public List<LodLevel> Levels { get; set; } = new List<LodLevel>();
    /// <summary>The hulls in the part's hull list (0 when its collision is the triangle mesh only) and, then, why: the first member that is not exactly convex.</summary>
    public int PhysicsHulls { get; set; }
    public string PhysicsReason { get; set; } = "";
}

/// <summary>A part that holds planar-mapped faces: which faces, and whether the part went into a merged part or stayed.</summary>
public sealed class PlanarPart
{
    public string Linkset { get; set; }
    public string Name { get; set; }
    public int LinkNum { get; set; }
    public List<int> Faces { get; } = new List<int>();
    /// <summary>The merged part it went into (empty when it stayed).</summary>
    public string MergedInto { get; set; } = "";
    /// <summary>Why it stayed (the codes of the rules that keep it); empty when it merged.</summary>
    public string KeptBy { get; set; } = "";
}

/// <summary>How the faces of the convertible prims in the archive are mapped (default or planar), and which parts hold the planar ones.</summary>
public sealed class MappingCensus
{
    public int DefaultFaces { get; set; }
    public int PlanarFaces { get; set; }
    public int OtherFaces { get; set; }
    public List<PlanarPart> PlanarParts { get; } = new List<PlanarPart>();
}

public sealed class MergeWriteResult
{
    public MergePlan Plan { get; set; }
    public string Output { get; set; }
    public RewriteSummary Rewrite { get; set; }
    public List<MergedPartReport> Parts { get; } = new List<MergedPartReport>();
    public MappingCensus Mapping { get; set; } = new MappingCensus();
    public int MeshesAdded => Parts.Count;
    public long SourceBytes { get; set; }
    /// <summary>The most a lower level of detail may differ from the full mesh, in screen pixels at the distance the viewer switches to it.</summary>
    public double LodPixelLimit { get; set; }
    public long OutputBytes { get; set; }
    public double Seconds { get; set; }

    /// <summary>The report: what was merged and how far each merged mesh is from the parts it replaces.</summary>
    public string ToText()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Merge of " + Plan.Archive + " written to " + Output);
        sb.AppendLine("  parts before " + Plan.PartsBefore + ", after " + Plan.PartsAfter + "; merged parts " + Parts.Count + " hold " + Parts.Sum(p => p.Members)
            + " parts, " + Plan.KeptParts + " parts stay as they are");
        sb.AppendLine("  meshes added " + Parts.Count + ", archive " + SourceBytes + " -> " + OutputBytes + " bytes, " + Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
        sb.AppendLine("  faces baked " + Parts.Sum(p => p.BakedFaces) + " (planar " + Parts.Sum(p => p.PlanarFaces) + "), faces kept with their own transform " + Parts.Sum(p => p.UnbakedFaces));
        sb.AppendLine("  corners compared with the original parts: " + Parts.Sum(p => (long)p.Corners));
        sb.AppendLine("  worst position difference " + Worst(p => p.WorstPositionMeters) + " m before the mesh is encoded, " + Worst(p => p.WorstPositionMetersDecoded) + " m as written");
        sb.AppendLine("  worst texture coordinate difference " + Worst(p => p.WorstUv) + " before the mesh is encoded, " + Worst(p => p.WorstUvDecoded) + " as written");
        sb.AppendLine("  worst texture coordinate error after the mesh is stored (16 bits over each face's range): " + Worst(p => p.WorstUvTexels, "0.000") + " texels at most, "
            + Worst(p => p.MeasuredUvTexels, "0.000") + " texels measured (limit " + UvPrecision.MaxTexels.ToString(CultureInfo.InvariantCulture) + ")");
        sb.AppendLine("  faces with normal or specular maps baked " + Parts.Sum(p => p.MapFaces) + ", new materials named " + Parts.Sum(p => p.NewMaterials) + ", worst map coordinate difference "
            + Worst(p => p.MeasuredMapTexels, "0.000") + " texels of the map's own texture (limit " + FaceBaking.MapTolerance.ToString(CultureInfo.InvariantCulture) + " for the figures and " + UvPrecision.MaxTexels.ToString(CultureInfo.InvariantCulture) + " for storing them)");
        sb.AppendLine("  merged faces with a PBR material or override " + Parts.Sum(p => p.PbrFaces) + ", with a media entry " + Parts.Sum(p => p.MediaFaces));
        sb.AppendLine("  worst normal difference " + Worst(p => p.WorstNormal) + " before the mesh is encoded, " + Worst(p => p.WorstNormalDecoded) + " as written");
        sb.AppendLine("  face mapping of the convertible prims: " + Mapping.DefaultFaces + " default, " + Mapping.PlanarFaces + " planar, " + Mapping.OtherFaces + " other");
        foreach (PlanarPart pp in Mapping.PlanarParts.Take(200))
            sb.AppendLine("    planar: " + (pp.Linkset ?? "").PadRight(32) + " " + (pp.Name ?? "").PadRight(32) + " link " + pp.LinkNum + " faces " + string.Join(",", pp.Faces)
                + (pp.MergedInto.Length > 0 ? " -> merged into " + pp.MergedInto : " -> stays (" + pp.KeptBy + ")"));
        if (Mapping.PlanarParts.Count > 200)
            sb.AppendLine("    ... and " + (Mapping.PlanarParts.Count - 200) + " more parts with planar faces");
        sb.AppendLine("  levels of detail: each lower level is within " + LodPixelLimit.ToString("0.##", CultureInfo.InvariantCulture) + " pixel of the full mesh at the distance the viewer switches to it (measured: "
            + Worst(p => p.Levels.Count == 0 ? 0 : p.Levels.Max(l => l.ErrorPixels), "0.00") + " pixel at most); triangles medium/low/lowest: "
            + Parts.Sum(p => p.Levels.Count > 0 ? p.Levels[0].Triangles : 0) + "/" + Parts.Sum(p => p.Levels.Count > 1 ? p.Levels[1].Triangles : 0) + "/" + Parts.Sum(p => p.Levels.Count > 2 ? p.Levels[2].Triangles : 0)
            + " of " + Parts.Sum(p => p.Triangles) + " in the full meshes; members left out of a level: " + Parts.Sum(p => p.Levels.Count > 2 ? p.Levels[2].DroppedMembers : 0) + " at the lowest");
        sb.AppendLine("  merged parts:");
        foreach (MergedPartReport p in Parts)
        {
            sb.AppendLine("    " + p.Name.PadRight(48) + p.Members.ToString().PadLeft(4) + " parts, " + p.Faces + " faces, " + p.Vertices + " vertices, " + p.Triangles + " triangles, "
                + p.MeshBytes + " bytes, position " + p.WorstPositionMeters.ToString("0.0e+0", CultureInfo.InvariantCulture) + " m, uv " + p.WorstUv.ToString("0.0e+0", CultureInfo.InvariantCulture)
                + (p.TookTheRoot ? ", took the root" : "") + (p.PlanarFaces > 0 ? ", " + p.PlanarFaces + " planar faces" : ""));
            sb.AppendLine("      levels of detail: " + string.Join(", ", p.Levels.Select(l => (l.Level == 2 ? "medium " : l.Level == 1 ? "low " : "lowest ") + l.Triangles + " triangles at " + l.SwitchDistance.ToString("0", CultureInfo.InvariantCulture) + " m (" + l.ErrorPixels.ToString("0.00", CultureInfo.InvariantCulture) + " pixel"
                + (l.DroppedMembers > 0 ? ", " + l.DroppedMembers + " members left out" : "") + ")")));
        }
        foreach (string warning in Plan.Warnings)
            sb.AppendLine("  WARNING: " + warning);
        return sb.ToString();
    }

    private string Worst(Func<MergedPartReport, double> pick, string format = "0.0e+0") => (Parts.Count == 0 ? 0 : Parts.Max(pick)).ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes the merge: each merged part of the plan becomes one mesh part and the parts it replaces are cut out; the other parts stay as
/// they were (only their link numbers change, because the numbers have to stay contiguous). A merged part is the lowest-numbered
/// member rewritten in place, so it keeps that member's id, owner, permissions and place in the object; it gets a manifest notecard
/// in its inventory. The source is only read. Nothing is written when any mesh fails its validity check.
/// </summary>
/// <summary>What a merged part holds for its faces besides the texture entry: PBR assignments and overrides, and media entries, all by merged face.</summary>
internal sealed class FaceExtrasOut
{
    public List<(byte Face, Guid Id)> PbrEntries = new List<(byte, Guid)>();
    public List<(byte Face, string Data)> PbrOverrides = new List<(byte, string)>();
    public List<MediaEntry> Media = new List<MediaEntry>();
    public int PbrFaces, MediaFaces;
}

public static class MergeArchiveWriter
{
    public const string ManifestName = "Merge manifest";
    private const int NotecardAssetType = 7, MaxNotecardChars = 60000;

    public static MergeWriteResult Write(string sourcePath, string outputPath, MergeOptions options = null, ConvertOptions convert = null)
    {
        Stopwatch sw = Stopwatch.StartNew();
        options ??= new MergeOptions();
        convert ??= new ConvertOptions();
        IMeshEncoder encoder = convert.Encoder ?? new MeshAssetEncoder();
        if (!encoder.ProducesValidMesh)
            throw new ConvertException("merging writes real meshes; the encoder in use does not");

        MergePlan plan = ArchiveMergePlanner.Plan(sourcePath, options, convert, out Dictionary<Guid, ParsedPart> parsed, out List<MergeLinkset> linksets);
        MergeWriteResult result = new MergeWriteResult { Plan = plan, Output = outputPath, SourceBytes = new FileInfo(sourcePath).Length, LodPixelLimit = options.Lod.PixelLimit };
        ArchiveRewriter rewriter = new ArchiveRewriter();
        HashSet<Guid> assets = new HashSet<Guid>();
        ISculptMapSource maps = new ArchiveSculptMapSource(sourcePath, plan.Linksets.SelectMany((l, i) => l.Merged.SelectMany(m => m.Members))
            .Select(m => m.PartId).Where(id => parsed.TryGetValue(id, out ParsedPart q) && q.Shape != null && q.Shape.SculptEntry).Select(id => parsed[id].Shape.SculptTexture.Guid));

        for (int li = 0; li < plan.Linksets.Count; li++)
        {
            LinksetPlan lp = plan.Linksets[li];
            if (lp.Merged.Count == 0)
                continue;
            Dictionary<Guid, MergePart> read = linksets[li].Parts.ToDictionary(p => p.PartId);
            foreach (MergedPartPlan mp in lp.Merged)
                result.Parts.Add(BuildMerged(lp, mp, read, parsed, encoder, options, convert, rewriter, assets, maps));
            foreach (KeptPartPlan kept in lp.Kept.Where(k => k.NewLinkNum != k.LinkNum))
            {
                int number = kept.NewLinkNum;
                rewriter.AddPatch(new PartPatch(kept.PartId, present => present.Contains("LinkNum")
                    ? new List<ElementEdit> { ElementEdit.SetText("LinkNum", number.ToString(CultureInfo.InvariantCulture)) }
                    : new List<ElementEdit>()));
            }
        }

        result.Mapping = Census(plan, linksets);
        result.Rewrite = rewriter.Rewrite(sourcePath, outputPath);
        result.OutputBytes = new FileInfo(outputPath).Length;
        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private static MappingCensus Census(MergePlan plan, List<MergeLinkset> linksets)
    {
        MappingCensus census = new MappingCensus();
        for (int li = 0; li < linksets.Count; li++)
        {
            LinksetPlan lp = plan.Linksets[li];
            foreach (MergePart part in linksets[li].Parts)
            {
                PlanarPart planar = null;
                for (int i = 0; i < part.Faces.Count; i++)
                {
                    MergeFace f = part.Faces[i];
                    if (f.Vertices == 0)
                        continue;
                    if (f.TexGen == 0)
                        census.DefaultFaces++;
                    else if (f.TexGen == FaceBaking.PlanarMapping)
                    {
                        census.PlanarFaces++;
                        planar ??= new PlanarPart { Linkset = lp.Name, Name = part.Name, LinkNum = part.LinkNum };
                        planar.Faces.Add(i);
                    }
                    else
                        census.OtherFaces++;
                }
                if (planar == null)
                    continue;
                MergedPartPlan into = lp.Merged.FirstOrDefault(m => m.Members.Any(x => x.PartId == part.PartId));
                if (into != null)
                    planar.MergedInto = into.Name;
                else
                    planar.KeptBy = string.Join(", ", lp.Kept.Where(k => k.PartId == part.PartId).SelectMany(k => k.Rules).Select(r => r.Code).Distinct());
                census.PlanarParts.Add(planar);
            }
        }
        return census;
    }

    private static MergedPartReport BuildMerged(LinksetPlan lp, MergedPartPlan mp, Dictionary<Guid, MergePart> read, Dictionary<Guid, ParsedPart> parsed,
        IMeshEncoder encoder, MergeOptions options, ConvertOptions convert, ArchiveRewriter rewriter, HashSet<Guid> assets, ISculptMapSource maps)
    {
        List<MergeMember> members = new List<MergeMember>();
        foreach (MemberPlan x in mp.Members)
        {
            ParsedPart pp = parsed[x.PartId];
            MergePart part = read[x.PartId];
            MemberShape shape = MemberGeometry.Build(part.Kind, pp, maps, convert.Clean, out string problem)
                ?? throw new ConvertException("part " + x.PartId + " has no geometry to merge: " + problem);
            List<EncoderFace> faces = shape.Faces;
            PhysicsGeometry physics = shape.Physics;
            if (faces.Count != part.Faces.Count)
                throw new ConvertException("part " + x.PartId + " has " + faces.Count + " faces now and had " + part.Faces.Count + " when the plan was made");
            members.Add(new MergeMember
            {
                PartId = x.PartId, IsRoot = part.IsRoot,
                Scale = new SysVector3(pp.Scale.X, pp.Scale.Y, pp.Scale.Z),
                Offset = part.IsRoot ? SysVector3.Zero : new SysVector3(pp.OffsetPosition.X, pp.OffsetPosition.Y, pp.OffsetPosition.Z),
                Rotation = part.IsRoot ? SysQuaternion.Identity : SysQuaternion.Normalize(new SysQuaternion(pp.RotationOffset.X, pp.RotationOffset.Y, pp.RotationOffset.Z, pp.RotationOffset.W)),
                Faces = faces, FaceInfo = part.Faces, Physics = physics
            });
        }

        // a face that bakes with maps drawn with a transform names a new material with the transforms taken out; a new one is written once
        int newMaterials = 0, mapFaces = 0;
        HashSet<Guid> usedHere = new HashSet<Guid>();
        foreach (MergeMember m in members)
            for (int i = 0; i < m.Faces.Count; i++)
            {
                MergeFace info = m.FaceInfo[i];
                if (m.Faces[i].IsEmpty || !info.Bakeable)
                    continue;
                if (info.NormalBaked || info.SpecularBaked)
                    mapFaces++;
                if (info.BakedMaterialId.Length == 0)
                    continue;
                Guid id = Guid.Parse(info.BakedMaterialId);
                if (usedHere.Add(id))
                    newMaterials++;
                if (info.NewMaterial != null && assets.Add(id))
                    rewriter.AddAsset(id, AssetType.OSMaterial, info.NewMaterial);
            }

        bool carrierIsRoot = members[0].IsRoot;
        ParsedPart carrier = parsed[mp.Members[0].PartId];
        MergedGeometry g = MergeGeometry.Build(mp, members, carrierIsRoot, options);
        if (g.Faces.Count > MeshShape.MaxFaces)
            throw new ConvertException("merged part " + mp.Name + " needs " + g.Faces.Count + " faces; a mesh part has at most " + MeshShape.MaxFaces);
        if (g.Faces.All(f => f.IsEmpty))
            throw new ConvertException("merged part " + mp.Name + " has no triangles");

        List<LodLevel> levels = MergedLods.Build(g, members, options.Lod);
        byte[] bytes = encoder.Encode(g.Faces, new EncodeOptions { Physics = g.Physics, Lods = levels.Select(l => (IReadOnlyList<EncoderFace>)l.Faces).ToList() });
        List<string> problems = MeshValidator.Check(bytes, g.Faces, out _, g.Physics);
        if (problems.Count > 0)
            throw new ConvertException("the mesh of merged part " + mp.Name + " fails its check: " + string.Join("; ", problems));
        Guid meshId = MeshId.FromBytes(bytes);
        // identical meshes (the same row of parts in two objects) are one asset
        if (assets.Add(meshId))
            rewriter.AddAsset(meshId, AssetType.Mesh, bytes);

        byte[] notecardBytes = new UTF8Encoding(false).GetBytes(Notecard(mp.Manifest));
        Guid noteAsset = MeshId.FromBytes(notecardBytes);
        Guid noteItem = MeshId.FromBytes(Encoding.UTF8.GetBytes("oarx-merge-manifest-item " + mp.Id));
        if (assets.Add(noteAsset))
            rewriter.AddAsset(noteAsset, AssetType.Notecard, notecardBytes);

        // the PBR assignments and overrides and the media entries of the merged faces, renumbered to the merged face each source face became
        FaceExtrasOut extras = new FaceExtrasOut();
        for (int slot = 0; slot < g.Faces.Count; slot++)
        {
            (int mi, int fi) = g.Template[slot];
            MergeFace info = members[mi].FaceInfo[fi];
            if (info.Pbr)
            {
                if (info.PbrMaterial.Length > 0)
                    extras.PbrEntries.Add(((byte)slot, Guid.Parse(info.PbrMaterial)));
                if (info.PbrOverride.Length > 0)
                    extras.PbrOverrides.Add(((byte)slot, info.PbrOverride));
                extras.PbrFaces++;
            }
            List<MediaEntry> sourceMedia = MediaFaceData.Read(parsed[members[mi].PartId], members[mi].Faces.Count, out _);
            MediaEntry entry = fi < sourceMedia.Count ? sourceMedia[fi] : null;
            extras.Media.Add(entry);
            if (entry != null)
                extras.MediaFaces++;
        }

        byte[] textureEntryBytes = TextureEntryOf(g, members, parsed).GetBytes();
        string textureEntry = Convert.ToBase64String(textureEntryBytes);
        int faceCount = g.Faces.Count;
        byte[] extra = carrier.ExtraParams;
        int physicsType = carrier.PhysicsShapeType;
        // a part alone in its object is link number 0, as the region writes it
        int linkNum = lp.PartsAfter == 1 ? 0 : mp.NewLinkNum;
        rewriter.AddPatch(new PartPatch(mp.Id, (PartView view) => CarrierEdits(view, mp, g, faceCount, meshId, extra, textureEntry, carrierIsRoot, noteAsset, noteItem, physicsType, linkNum, extras)));
        foreach (MemberPlan x in mp.Members.Skip(1))
            rewriter.RemovePart(x.PartId);

        MergedPartReport report = new MergedPartReport
        {
            Id = mp.Id, MeshId = meshId, ManifestAssetId = noteAsset, Name = mp.Name, Linkset = lp.Name, Members = mp.Members.Count, Faces = faceCount,
            Vertices = g.Faces.Sum(f => f.VertexCount), Triangles = g.Faces.Sum(f => f.TriangleCount), TookTheRoot = carrierIsRoot, MeshBytes = bytes.Length,
            MapFaces = mapFaces, NewMaterials = newMaterials, PbrFaces = extras.PbrFaces, MediaFaces = extras.MediaFaces, Levels = levels
        };
        foreach (MergeMember m in members)
            for (int i = 0; i < m.Faces.Count; i++)
            {
                if (m.Faces[i].IsEmpty)
                    continue;
                if (m.FaceInfo[i].Bakeable)
                {
                    report.BakedFaces++;
                    if (m.FaceInfo[i].TexGen == FaceBaking.PlanarMapping)
                        report.PlanarFaces++;
                }
                else
                    report.UnbakedFaces++;
            }
        MeshCheck.Measure(members, g, bytes, textureEntryBytes, report);
        // the planner keeps every merged face inside the limit; this is the backstop if the mesh built differs from the plan
        if (report.WorstUvTexels > UvPrecision.MaxTexels + 1e-6)
            throw new ConvertException("merged part " + mp.Name + " would show texture coordinates up to " + report.WorstUvTexels.ToString("0.00", CultureInfo.InvariantCulture) + " texels off; the limit is " + UvPrecision.MaxTexels.ToString(CultureInfo.InvariantCulture));
        return report;
    }

    private static string Notecard(string manifest)
    {
        string text = manifest.Length > MaxNotecardChars ? manifest.Substring(0, MaxNotecardChars) + "\n(the list is cut here; the plan file has all of it)\n" : manifest;
        int length = new UTF8Encoding(false).GetByteCount(text);
        return "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length " + length.ToString(CultureInfo.InvariantCulture) + "\n" + text + "}\n";
    }

    private static Primitive.TextureEntry TextureEntryOf(MergedGeometry g, List<MergeMember> members, Dictionary<Guid, ParsedPart> parsed)
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(UUID.Zero);
        for (int slot = 0; slot < g.Template.Count; slot++)
        {
            (int mi, int fi) = g.Template[slot];
            MergeFace info = members[mi].FaceInfo[fi];
            Primitive.TextureEntry source = ArchiveMergePlanner.ReadTextureEntry(parsed[members[mi].PartId]);
            Primitive.TextureEntryFace from = source == null ? null : (source.FaceTextures != null && fi < source.FaceTextures.Length ? source.FaceTextures[fi] : null) ?? source.DefaultTexture;
            Primitive.TextureEntryFace to = te.CreateFace((uint)slot);
            if (from != null)
            {
                to.TextureID = from.TextureID;
                to.RGBA = from.RGBA;
                to.Glow = from.Glow;
                to.Shiny = from.Shiny;
                to.Bump = from.Bump;
                to.Fullbright = from.Fullbright;
                to.MaterialID = from.MaterialID;
                to.MediaFlags = from.MediaFlags;
            }
            // maps drawn from the baked coordinates are drawn without a transform of their own
            if (info.Bakeable && info.BakedMaterialId.Length > 0)
                to.MaterialID = new UUID(info.BakedMaterialId);
            // a baked face is drawn with the identity transform and default mapping: both are in its coordinates now
            if (info.Bakeable)
            {
                to.RepeatU = 1; to.RepeatV = 1; to.OffsetU = 0; to.OffsetV = 0; to.Rotation = 0;
                to.TexMapType = MappingType.Default;
            }
            else if (from != null)
            {
                // the viewer applies no texture transform to a PBR face, so the merged face carries none
                if (info.Pbr)
                {
                    to.RepeatU = 1; to.RepeatV = 1; to.OffsetU = 0; to.OffsetV = 0; to.Rotation = 0;
                }
                else
                {
                    to.RepeatU = from.RepeatU; to.RepeatV = from.RepeatV; to.OffsetU = from.OffsetU; to.OffsetV = from.OffsetV; to.Rotation = from.Rotation;
                }
                to.TexMapType = from.TexMapType;
            }
            if (slot == 0)
                te.DefaultTexture = te.FaceTextures[0];
        }
        // The library writes a face that is plainer than the default face (not marked as media, say) as if it were the default's. When the first
        // face is marked as media, the default is a copy of it that is not, so each marked face is written as its own.
        Primitive.TextureEntryFace first = te.FaceTextures[0];
        if (first != null && first.MediaFlags)
        {
            Primitive.TextureEntryFace plain = new Primitive.TextureEntryFace(null)
            {
                TextureID = first.TextureID, RGBA = first.RGBA, Glow = first.Glow, Shiny = first.Shiny, Bump = first.Bump, Fullbright = first.Fullbright,
                MaterialID = first.MaterialID, RepeatU = first.RepeatU, RepeatV = first.RepeatV, OffsetU = first.OffsetU, OffsetV = first.OffsetV,
                Rotation = first.Rotation, TexMapType = first.TexMapType, MediaFlags = false
            };
            te.DefaultTexture = plain;
        }
        return te;
    }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static IReadOnlyList<ElementEdit> CarrierEdits(PartView view, MergedPartPlan mp, MergedGeometry g, int faceCount, Guid meshId, byte[] extra, string textureEntry,
        bool carrierIsRoot, Guid noteAsset, Guid noteItem, int physicsType, int linkNum, FaceExtrasOut extras)
    {
        List<ElementEdit> edits = new List<ElementEdit>();
        void Need(string path)
        {
            if (!view.Present.Contains(path))
                throw new ConvertException("part " + mp.Id + " has no '" + path + "' element to rewrite");
        }

        edits.Add(ElementEdit.SetTextOrAdd("Name", mp.Name));
        edits.Add(ElementEdit.SetTextOrAdd("Description", mp.Description));
        bool pbr = extras.PbrEntries.Count > 0 || extras.PbrOverrides.Count > 0;
        byte[] extraParams = pbr ? MeshShape.WithRenderMaterials(extra, extras.PbrEntries) : extra;
        edits.AddRange(MeshShape.Edits(faceCount, meshId, extraParams, view.Present));
        if (pbr)
        {
            // a part with no ExtraParams element gets one, with the mesh block the loader would otherwise derive
            if (!view.Present.Contains("Shape/ExtraParams"))
                edits.Add(ElementEdit.SetTextOrAddInShape("Shape/ExtraParams", Convert.ToBase64String(MeshShape.WithSculptBlock(extraParams, meshId, PrimitiveBaseShape.CreateMesh(faceCount, new UUID(meshId)).SculptType))));
            PrimitiveBaseShape holder = new PrimitiveBaseShape
            {
                RenderMaterials = new Primitive.RenderMaterials
                {
                    entries = extras.PbrEntries.Select(e => new Primitive.RenderMaterials.RenderMaterialEntry { te_index = e.Face, id = new UUID(e.Id) }).ToArray(),
                    overrides = extras.PbrOverrides.Select(o => new Primitive.RenderMaterials.RenderMaterialOverrideEntry { te_index = o.Face, data = o.Data }).ToArray()
                }
            };
            edits.Add(ElementEdit.SetTextOrAddInShape("Shape/MatOvrd", Convert.ToBase64String(holder.RenderMaterialsOvrToRawBin())));
        }
        if (extras.Media.Any(e => e != null))
            edits.Add(ElementEdit.SetTextOrAddInShape("Shape/Media", new PrimitiveBaseShape.MediaList(extras.Media).ToXml()));
        Need("Shape/TextureEntry");
        edits.Add(ElementEdit.SetText("Shape/TextureEntry", textureEntry));
        foreach (string prefix in new[] { "Scale", "Shape/Scale" })
            if (view.Present.Contains(prefix + "/X"))
            {
                edits.Add(ElementEdit.SetText(prefix + "/X", F(g.Scale.X)));
                edits.Add(ElementEdit.SetText(prefix + "/Y", F(g.Scale.Y)));
                edits.Add(ElementEdit.SetText(prefix + "/Z", F(g.Scale.Z)));
            }
        if (!carrierIsRoot)
        {
            Need("OffsetPosition/X");
            edits.Add(ElementEdit.SetText("OffsetPosition/X", F(g.Position.X)));
            edits.Add(ElementEdit.SetText("OffsetPosition/Y", F(g.Position.Y)));
            edits.Add(ElementEdit.SetText("OffsetPosition/Z", F(g.Position.Z)));
            if (view.Present.Contains("RotationOffset/X"))
            {
                edits.Add(ElementEdit.SetText("RotationOffset/X", "0"));
                edits.Add(ElementEdit.SetText("RotationOffset/Y", "0"));
                edits.Add(ElementEdit.SetText("RotationOffset/Z", "0"));
                edits.Add(ElementEdit.SetText("RotationOffset/W", "1"));
            }
        }
        if (view.Present.Contains("LinkNum"))
            edits.Add(ElementEdit.SetText("LinkNum", linkNum.ToString(CultureInfo.InvariantCulture)));
        // a mesh part with no shape type of its own would collide as a convex hull; the prim it replaces collided as a prim
        if (physicsType == -1)
            edits.Add(ElementEdit.SetTextOrAdd("PhysicsShapeType", "0"));

        // a part with no inventory element gets one; its other contents (none, for a part that can merge) are not touched
        edits.Add(ElementEdit.SetInnerXmlOrAdd("TaskInventory", NotecardItem(view, mp.Id, noteAsset, noteItem)));
        return edits;
    }

    private static string NotecardItem(PartView view, Guid part, Guid asset, Guid item)
    {
        string Uuid(string path)
        {
            string t = view.Text(path + "/UUID") ?? view.Text(path + "/Guid");
            return Guid.TryParse(t, out Guid g) ? g.ToString() : Guid.Empty.ToString();
        }
        string owner = Uuid("OwnerID"), creator = Uuid("CreatorID"), group = Uuid("GroupID");
        if (!long.TryParse(view.Text("CreationDate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long when))
            when = 0;
        StringBuilder sb = new StringBuilder();
        sb.Append("<TaskInventoryItem>");
        sb.Append("<AssetID><UUID>").Append(asset).Append("</UUID></AssetID>");
        sb.Append("<BasePermissions>2147483647</BasePermissions><CreationDate>").Append(when.ToString(CultureInfo.InvariantCulture)).Append("</CreationDate>");
        sb.Append("<CreatorID><UUID>").Append(creator).Append("</UUID></CreatorID>");
        sb.Append("<Description>Parts this merged part replaced</Description><EveryonePermissions>0</EveryonePermissions><Flags>0</Flags>");
        sb.Append("<GroupID><UUID>").Append(group).Append("</UUID></GroupID>");
        sb.Append("<GroupPermissions>0</GroupPermissions><InvType>").Append(NotecardAssetType).Append("</InvType>");
        sb.Append("<ItemID><UUID>").Append(item).Append("</UUID></ItemID><OldItemID><UUID>").Append(Guid.Empty).Append("</UUID></OldItemID>");
        sb.Append("<LastOwnerID><UUID>").Append(owner).Append("</UUID></LastOwnerID>");
        sb.Append("<Name>").Append(SecurityElement.Escape(ManifestName)).Append("</Name><NextPermissions>581632</NextPermissions>");
        sb.Append("<OwnerID><UUID>").Append(owner).Append("</UUID></OwnerID><CurrentPermissions>2147483647</CurrentPermissions>");
        sb.Append("<ParentID><UUID>").Append(part).Append("</UUID></ParentID><ParentPartID><UUID>").Append(part).Append("</UUID></ParentPartID>");
        sb.Append("<PermsGranter><UUID>").Append(Guid.Empty).Append("</UUID></PermsGranter><PermsMask>0</PermsMask>");
        sb.Append("<Type>").Append(NotecardAssetType).Append("</Type><OwnerChanged>false</OwnerChanged></TaskInventoryItem>");
        return sb.ToString();
    }
}

/// <summary>
/// Compares a merged mesh with the parts it replaces, corner by corner, before it is encoded and after it is read back from the asset bytes.
/// Texture coordinates are compared as the viewer will show them: the mesh's coordinates with the texture entry written for the merged face applied.
/// </summary>
internal static class MeshCheck
{
    public static void Measure(List<MergeMember> members, MergedGeometry g, byte[] bytes, byte[] textureEntryBytes, MergedPartReport report)
    {
        MeshAssetModel model = MeshCodec.Decode(bytes);
        // the texture entry as written: what the viewer will apply to the merged mesh's coordinates, face by face
        Primitive.TextureEntry written = new Primitive.TextureEntry(textureEntryBytes, 0, textureEntryBytes.Length);
        FaceTransform SlotTransform(int slot)
        {
            Primitive.TextureEntryFace face = (slot < written.FaceTextures.Length ? written.FaceTextures[slot] : null) ?? written.DefaultTexture;
            return new FaceTransform(face.RepeatU, face.RepeatV, face.OffsetU, face.OffsetV, face.Rotation);
        }
        // what the viewer draws the original face with: a bakeable face has its mapping and transform applied; any other default-mapped face
        // is drawn with the transform on its texture entry
        float[][][] wantUv = members.Select(m => m.Faces.Select((f, i) =>
        {
            if (f.IsEmpty)
                return Array.Empty<float>();
            MergeFace info = m.FaceInfo[i];
            if (info.Bakeable)
                return FaceUv.Bake(f.Positions, f.Normals, f.Uvs, info.DrawnTransform, info.TexGen == FaceBaking.PlanarMapping, m.Scale);
            float[] shown = (float[])f.Uvs.Clone();
            for (int v = 0; v < shown.Length; v += 2)
                FaceUv.Transform(ref shown[v], ref shown[v + 1], info.DrawnTransform);
            return shown;
        }).ToArray()).ToArray();
        // the same for the maps of a baked face: where the viewer draws each map for the original face (the material's own transform)
        float[][][] MapUv(Func<MergeFace, bool> has, Func<MergeFace, FaceTransform> transform) => members.Select(m => m.Faces.Select((f, i) =>
            f.IsEmpty || !m.FaceInfo[i].Bakeable || !has(m.FaceInfo[i]) ? null
                : FaceUv.Bake(f.Positions, f.Normals, f.Uvs, transform(m.FaceInfo[i]), m.FaceInfo[i].TexGen == FaceBaking.PlanarMapping, m.Scale)).ToArray()).ToArray();
        float[][][] wantNormalUv = MapUv(i => i.NormalBaked, i => i.NormalTransform), wantSpecularUv = MapUv(i => i.SpecularBaked, i => i.SpecularTransform);
        // and what the viewer will draw the merged face with
        float[][] gotUv = g.Faces.Select((f, slot) =>
        {
            FaceTransform t = SlotTransform(slot);
            float[] shown = (float[])f.Uvs.Clone();
            for (int v = 0; v < shown.Length; v += 2)
                FaceUv.Transform(ref shown[v], ref shown[v + 1], t);
            return shown;
        }).ToArray();
        float[][] decUv = model.High.Faces.Select((f, slot) =>
        {
            FaceTransform t = SlotTransform(slot);
            float[] shown = new float[f.NoGeometry ? 0 : f.Uvs.Length * 2];
            for (int v = 0; v < shown.Length / 2; v++)
            {
                float s = f.Uvs[v].X, tt = f.Uvs[v].Y;
                FaceUv.Transform(ref s, ref tt, t);
                shown[v * 2] = s;
                shown[v * 2 + 1] = tt;
            }
            return shown;
        }).ToArray();

        // the most each merged face can be off after the asset stores it, in texels of its texture, from the range of the coordinates as drawn
        for (int slot = 0; slot < g.Faces.Count; slot++)
        {
            (int tm, int tf) = g.Template[slot];
            MergeFace template = members[tm].FaceInfo[tf];
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            for (int v = 0; v < gotUv[slot].Length; v += 2)
            {
                minU = Math.Min(minU, gotUv[slot][v]); maxU = Math.Max(maxU, gotUv[slot][v]);
                minV = Math.Min(minV, gotUv[slot][v + 1]); maxV = Math.Max(maxV, gotUv[slot][v + 1]);
            }
            if (minU <= maxU)
                report.WorstUvTexels = Math.Max(report.WorstUvTexels,
                    UvPrecision.Texels(new UvBounds(minU, maxU, minV, maxV), template.TextureWidth > 0 ? template.TextureWidth : UvPrecision.UnknownResolution,
                        template.TextureHeight > 0 ? template.TextureHeight : UvPrecision.UnknownResolution));
        }

        foreach (FaceRun run in g.Runs)
        {
            MergeMember m = members[run.Member];
            EncoderFace src = m.Faces[run.Face], merged = g.Faces[run.Slot];
            MeshFace dec = model.High.Faces[run.Slot];
            for (int k = 0; k < run.IndexCount; k++)
            {
                int sv = (int)src.Indices[run.SourceFirstIndex + k], mv = (int)merged.Indices[run.FirstIndex + k];
                SysVector3 wantPos = MergeGeometry.ToRoot(m, new SysVector3(src.Positions[sv * 3], src.Positions[sv * 3 + 1], src.Positions[sv * 3 + 2]));
                SysVector3 wantNormal = SysVector3.Normalize(SysVector3.Transform(new SysVector3(src.Normals[sv * 3], src.Normals[sv * 3 + 1], src.Normals[sv * 3 + 2]) / m.Scale, m.Rotation));
                float wu = wantUv[run.Member][run.Face][sv * 2], wv = wantUv[run.Member][run.Face][sv * 2 + 1];

                SysVector3 gotPos = g.Position + new SysVector3(merged.Positions[mv * 3], merged.Positions[mv * 3 + 1], merged.Positions[mv * 3 + 2]) * g.Scale;
                SysVector3 gotNormal = SysVector3.Normalize(new SysVector3(merged.Normals[mv * 3], merged.Normals[mv * 3 + 1], merged.Normals[mv * 3 + 2]) / g.Scale);
                report.WorstPositionMeters = Math.Max(report.WorstPositionMeters, SysVector3.Distance(wantPos, gotPos));
                report.WorstNormal = Math.Max(report.WorstNormal, MaxComponent(wantNormal - gotNormal));
                report.WorstUv = Math.Max(report.WorstUv, Math.Max(Math.Abs(wu - gotUv[run.Slot][mv * 2]), Math.Abs(wv - gotUv[run.Slot][mv * 2 + 1])));

                int dv = dec.Indices[run.FirstIndex + k];
                SysVector3 decPos = g.Position + dec.Positions[dv] * g.Scale;
                SysVector3 decNormal = SysVector3.Normalize(dec.Normals[dv] / g.Scale);
                report.WorstPositionMetersDecoded = Math.Max(report.WorstPositionMetersDecoded, SysVector3.Distance(wantPos, decPos));
                report.WorstNormalDecoded = Math.Max(report.WorstNormalDecoded, MaxComponent(wantNormal - decNormal));
                report.WorstUvDecoded = Math.Max(report.WorstUvDecoded, Math.Max(Math.Abs(wu - decUv[run.Slot][dv * 2]), Math.Abs(wv - decUv[run.Slot][dv * 2 + 1])));
                MergeFace shownFace = m.FaceInfo[run.Face];
                double texelsU = Math.Abs(wu - decUv[run.Slot][dv * 2]) * (shownFace.TextureWidth > 0 ? shownFace.TextureWidth : UvPrecision.UnknownResolution);
                double texelsV = Math.Abs(wv - decUv[run.Slot][dv * 2 + 1]) * (shownFace.TextureHeight > 0 ? shownFace.TextureHeight : UvPrecision.UnknownResolution);
                report.MeasuredUvTexels = Math.Max(report.MeasuredUvTexels, Math.Max(texelsU, texelsV));
                float[] nu = wantNormalUv[run.Member][run.Face], su = wantSpecularUv[run.Member][run.Face];
                // the viewer draws each map with the transform its material states, from the coordinates the merged face carries
                if (nu != null)
                {
                    float ms = decUv[run.Slot][dv * 2], mt = decUv[run.Slot][dv * 2 + 1];
                    FaceUv.Transform(ref ms, ref mt, shownFace.NormalOut);
                    report.MeasuredMapTexels = Math.Max(report.MeasuredMapTexels, Math.Max(Math.Abs(nu[sv * 2] - ms) * Pixels(shownFace.NormalWidth), Math.Abs(nu[sv * 2 + 1] - mt) * Pixels(shownFace.NormalHeight)));
                }
                if (su != null)
                {
                    float ms = decUv[run.Slot][dv * 2], mt = decUv[run.Slot][dv * 2 + 1];
                    FaceUv.Transform(ref ms, ref mt, shownFace.SpecularOut);
                    report.MeasuredMapTexels = Math.Max(report.MeasuredMapTexels, Math.Max(Math.Abs(su[sv * 2] - ms) * Pixels(shownFace.SpecularWidth), Math.Abs(su[sv * 2 + 1] - mt) * Pixels(shownFace.SpecularHeight)));
                }
                report.Corners++;
            }
        }
    }

    private static double Pixels(int size) => size > 0 ? size : UvPrecision.UnknownResolution;

    private static double MaxComponent(SysVector3 v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));
}
