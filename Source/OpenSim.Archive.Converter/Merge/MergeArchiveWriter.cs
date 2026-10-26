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
        sb.AppendLine("  worst normal difference " + Worst(p => p.WorstNormal) + " before the mesh is encoded, " + Worst(p => p.WorstNormalDecoded) + " as written");
        sb.AppendLine("  merged parts:");
        foreach (MergedPartReport p in Parts)
            sb.AppendLine("    " + p.Name.PadRight(48) + p.Members.ToString().PadLeft(4) + " parts, " + p.Faces + " faces, " + p.Vertices + " vertices, " + p.Triangles + " triangles, "
                + p.MeshBytes + " bytes, position " + p.WorstPositionMeters.ToString("0.0e+0", CultureInfo.InvariantCulture) + " m, uv " + p.WorstUv.ToString("0.0e+0", CultureInfo.InvariantCulture)
                + (p.TookTheRoot ? ", took the root" : "") + (p.PlanarFaces > 0 ? ", " + p.PlanarFaces + " planar faces" : ""));
        foreach (string warning in Plan.Warnings)
            sb.AppendLine("  WARNING: " + warning);
        return sb.ToString();
    }

    private string Worst(Func<MergedPartReport, double> pick) => (Parts.Count == 0 ? 0 : Parts.Max(pick)).ToString("0.0e+0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes the merge: each merged part of the plan becomes one mesh part and the parts it replaces are cut out; the other parts stay as
/// they were (only their link numbers change, because the numbers have to stay contiguous). A merged part is the lowest-numbered
/// member rewritten in place, so it keeps that member's id, owner, permissions and place in the object; it gets a manifest notecard
/// in its inventory. The source is only read. Nothing is written when any mesh fails its validity check.
/// </summary>
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
        MergeWriteResult result = new MergeWriteResult { Plan = plan, Output = outputPath, SourceBytes = new FileInfo(sourcePath).Length };
        ArchiveRewriter rewriter = new ArchiveRewriter();
        HashSet<Guid> assets = new HashSet<Guid>();

        for (int li = 0; li < plan.Linksets.Count; li++)
        {
            LinksetPlan lp = plan.Linksets[li];
            if (lp.Merged.Count == 0)
                continue;
            Dictionary<Guid, MergePart> read = linksets[li].Parts.ToDictionary(p => p.PartId);
            foreach (MergedPartPlan mp in lp.Merged)
                result.Parts.Add(BuildMerged(lp, mp, read, parsed, encoder, options, convert, rewriter, assets));
            foreach (KeptPartPlan kept in lp.Kept.Where(k => k.NewLinkNum != k.LinkNum))
            {
                int number = kept.NewLinkNum;
                rewriter.AddPatch(new PartPatch(kept.PartId, present => present.Contains("LinkNum")
                    ? new List<ElementEdit> { ElementEdit.SetText("LinkNum", number.ToString(CultureInfo.InvariantCulture)) }
                    : new List<ElementEdit>()));
            }
        }

        result.Rewrite = rewriter.Rewrite(sourcePath, outputPath);
        result.OutputBytes = new FileInfo(outputPath).Length;
        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private static MergedPartReport BuildMerged(LinksetPlan lp, MergedPartPlan mp, Dictionary<Guid, MergePart> read, Dictionary<Guid, ParsedPart> parsed,
        IMeshEncoder encoder, MergeOptions options, ConvertOptions convert, ArchiveRewriter rewriter, HashSet<Guid> assets)
    {
        List<MergeMember> members = new List<MergeMember>();
        foreach (MemberPlan x in mp.Members)
        {
            ParsedPart pp = parsed[x.PartId];
            MergePart part = read[x.PartId];
            List<EncoderFace> faces = FaceBuilder.FromPrim(pp.Shape, out _, convert.Clean);
            PhysicsGeometry physics = PrimPhysics.Build(pp.Shape, null, null, pp.Scale, out string problem)
                ?? throw new ConvertException("part " + x.PartId + " has no collision shape to merge: " + problem);
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

        bool carrierIsRoot = members[0].IsRoot;
        ParsedPart carrier = parsed[mp.Members[0].PartId];
        MergedGeometry g = MergeGeometry.Build(mp, members, carrierIsRoot, options);
        if (g.Faces.Count > MeshShape.MaxFaces)
            throw new ConvertException("merged part " + mp.Name + " needs " + g.Faces.Count + " faces; a mesh part has at most " + MeshShape.MaxFaces);
        if (g.Faces.All(f => f.IsEmpty))
            throw new ConvertException("merged part " + mp.Name + " has no triangles");

        byte[] bytes = encoder.Encode(g.Faces, new EncodeOptions { Physics = g.Physics });
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

        string textureEntry = Convert.ToBase64String(TextureEntryOf(g, members, parsed).GetBytes());
        int faceCount = g.Faces.Count;
        byte[] extra = carrier.ExtraParams;
        int physicsType = carrier.PhysicsShapeType;
        rewriter.AddPatch(new PartPatch(mp.Id, (PartView view) => CarrierEdits(view, mp, g, faceCount, meshId, extra, textureEntry, carrierIsRoot, noteAsset, noteItem, physicsType)));
        foreach (MemberPlan x in mp.Members.Skip(1))
            rewriter.RemovePart(x.PartId);

        MergedPartReport report = new MergedPartReport
        {
            Id = mp.Id, MeshId = meshId, ManifestAssetId = noteAsset, Name = mp.Name, Linkset = lp.Name, Members = mp.Members.Count, Faces = faceCount,
            Vertices = g.Faces.Sum(f => f.VertexCount), Triangles = g.Faces.Sum(f => f.TriangleCount), TookTheRoot = carrierIsRoot, MeshBytes = bytes.Length
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
        MeshCheck.Measure(members, g, bytes, report);
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
            }
            // a baked face is drawn with the identity transform and default mapping: both are in its coordinates now
            if (info.Bakeable)
            {
                to.RepeatU = 1; to.RepeatV = 1; to.OffsetU = 0; to.OffsetV = 0; to.Rotation = 0;
                to.TexMapType = MappingType.Default;
            }
            else if (from != null)
            {
                to.RepeatU = from.RepeatU; to.RepeatV = from.RepeatV; to.OffsetU = from.OffsetU; to.OffsetV = from.OffsetV; to.Rotation = from.Rotation;
                to.TexMapType = from.TexMapType;
            }
            if (slot == 0)
                te.DefaultTexture = te.FaceTextures[0];
        }
        return te;
    }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static IReadOnlyList<ElementEdit> CarrierEdits(PartView view, MergedPartPlan mp, MergedGeometry g, int faceCount, Guid meshId, byte[] extra, string textureEntry,
        bool carrierIsRoot, Guid noteAsset, Guid noteItem, int physicsType)
    {
        List<ElementEdit> edits = new List<ElementEdit>();
        void Need(string path)
        {
            if (!view.Present.Contains(path))
                throw new ConvertException("part " + mp.Id + " has no '" + path + "' element to rewrite");
        }

        edits.Add(ElementEdit.SetTextOrAdd("Name", mp.Name));
        edits.Add(ElementEdit.SetTextOrAdd("Description", mp.Description));
        edits.AddRange(MeshShape.Edits(faceCount, meshId, extra, view.Present));
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
            edits.Add(ElementEdit.SetText("LinkNum", mp.NewLinkNum.ToString(CultureInfo.InvariantCulture)));
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

/// <summary>Compares a merged mesh with the parts it replaces, corner by corner, before it is encoded and after it is read back from the asset bytes.</summary>
internal static class MeshCheck
{
    public static void Measure(List<MergeMember> members, MergedGeometry g, byte[] bytes, MergedPartReport report)
    {
        MeshAssetModel model = MeshCodec.Decode(bytes);
        // the coordinates each face should have: the viewer's, worked out from the original face on the original part
        float[][][] wantUv = members.Select(m => m.Faces.Select((f, i) =>
            f.IsEmpty ? Array.Empty<float>() : m.FaceInfo[i].Bakeable ? FaceUv.Bake(f.Positions, f.Normals, f.Uvs, m.FaceInfo[i].Transform, m.FaceInfo[i].TexGen == FaceBaking.PlanarMapping, m.Scale) : f.Uvs).ToArray()).ToArray();

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
                report.WorstUv = Math.Max(report.WorstUv, Math.Max(Math.Abs(wu - merged.Uvs[mv * 2]), Math.Abs(wv - merged.Uvs[mv * 2 + 1])));

                int dv = dec.Indices[run.FirstIndex + k];
                SysVector3 decPos = g.Position + dec.Positions[dv] * g.Scale;
                SysVector3 decNormal = SysVector3.Normalize(dec.Normals[dv] / g.Scale);
                report.WorstPositionMetersDecoded = Math.Max(report.WorstPositionMetersDecoded, SysVector3.Distance(wantPos, decPos));
                report.WorstNormalDecoded = Math.Max(report.WorstNormalDecoded, MaxComponent(wantNormal - decNormal));
                report.WorstUvDecoded = Math.Max(report.WorstUvDecoded, Math.Max(Math.Abs(wu - dec.Uvs[dv].X), Math.Abs(wv - dec.Uvs[dv].Y)));
                report.Corners++;
            }
        }
    }

    private static double MaxComponent(SysVector3 v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));
}
