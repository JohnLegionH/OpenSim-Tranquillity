/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>Repeats, offset and rotation of one face's texture, as the texture entry stores them.</summary>
public readonly record struct FaceTransform(float RepeatU, float RepeatV, float OffsetU, float OffsetV, float Rotation)
{
    public static readonly FaceTransform Identity = new FaceTransform(1, 1, 0, 0, 0);

    public string Key => string.Create(CultureInfo.InvariantCulture, $"{RepeatU:0.####},{RepeatV:0.####},{OffsetU:0.####},{OffsetV:0.####},{Rotation:0.####}");

    public bool SameAs(FaceTransform o) =>
        MathF.Abs(RepeatU - o.RepeatU) < 1e-4f && MathF.Abs(RepeatV - o.RepeatV) < 1e-4f && MathF.Abs(OffsetU - o.OffsetU) < 1e-4f
        && MathF.Abs(OffsetV - o.OffsetV) < 1e-4f && MathF.Abs(Rotation - o.Rotation) < 1e-4f;
}

/// <summary>The normal and specular maps of a legacy material, with the transform each is drawn with.</summary>
public sealed class LegacyMaterialInfo
{
    public bool HasNormalMap { get; set; }
    public FaceTransform NormalTransform { get; set; } = FaceTransform.Identity;
    public bool HasSpecularMap { get; set; }
    public FaceTransform SpecularTransform { get; set; } = FaceTransform.Identity;
    /// <summary>0 none, 1 blend, 2 mask, 3 emissive.</summary>
    public int DiffuseAlphaMode { get; set; }
    public Guid NormalMapId { get; set; }
    public Guid SpecularMapId { get; set; }
    /// <summary>True when a map the material has is drawn with a transform other than the identity.</summary>
    public bool MapsTransformed =>
        (HasNormalMap && !NormalTransform.SameAs(FaceTransform.Identity)) || (HasSpecularMap && !SpecularTransform.SameAs(FaceTransform.Identity));
    /// <summary>
    /// The same material with the repeats, offset and rotation of both maps taken out (identity), as the region encodes a legacy material,
    /// and the id the region gives that encoding (the MD5 of its bytes). Empty when the material was not read from an archive.
    /// </summary>
    public Guid IdentityId { get; set; }
    public byte[] IdentityBytes { get; set; }
}

/// <summary>One face of a part as the merge planner needs it: what makes two faces share a material slot, and how many vertices it has.</summary>
public sealed class MergeFace
{
    public string TextureId { get; set; } = "";
    public string Color { get; set; } = "1,1,1,1";
    public float Glow { get; set; }
    public int Shiny { get; set; }
    public int Bump { get; set; }
    public bool Fullbright { get; set; }
    public int TexGen { get; set; }
    public string MaterialId { get; set; } = "";
    /// <summary>
    /// For a baked face whose legacy material has maps drawn with a transform: the material the merged face names instead (the same material
    /// with the maps' transforms taken out, because the face's coordinates now carry them). Empty when the face keeps its own material.
    /// </summary>
    public string BakedMaterialId { get; set; } = "";
    /// <summary>The bytes of the new material when the archive does not hold it yet (written once); null otherwise.</summary>
    public byte[] NewMaterial { get; set; }
    /// <summary>The transform of each map the material has and the face bakes, with the size in pixels of the map's texture (0 when unknown), for checking the merged face against the maps.</summary>
    public bool NormalBaked { get; set; }
    public bool SpecularBaked { get; set; }
    public FaceTransform NormalTransform { get; set; } = FaceTransform.Identity;
    public FaceTransform SpecularTransform { get; set; } = FaceTransform.Identity;
    public int NormalWidth { get; set; }
    public int NormalHeight { get; set; }
    public int SpecularWidth { get; set; }
    public int SpecularHeight { get; set; }
    public FaceTransform Transform { get; set; } = FaceTransform.Identity;
    /// <summary>True when the face's transform can be baked into the merged mesh's texture coordinates.</summary>
    public bool Bakeable { get; set; } = true;
    /// <summary>Why the face is not bakeable (empty when it is).</summary>
    public string UnbakedReason { get; set; } = "";
    /// <summary>True when the face is drawn alpha-blended (a colour alpha below 1, or a legacy material that blends).</summary>
    public bool Alpha { get; set; }
    /// <summary>True when the face is drawn with a PBR material (held in its part's extra parameters, which a merge does not carry).</summary>
    public bool Pbr { get; set; }
    /// <summary>True when the face shows media (held in its part's media data, which a merge does not carry).</summary>
    public bool Media { get; set; }
    public int Vertices { get; set; }
    /// <summary>The size of the face's texture in pixels (0 when the archive does not hold it).</summary>
    public int TextureWidth { get; set; }
    public int TextureHeight { get; set; }
    /// <summary>The range of the texture coordinates the face carries in a merged mesh (baked when the face is baked).</summary>
    public UvBounds Uv { get; set; }
    /// <summary>True when the coordinates of the face as drawn already run so wide that a mesh asset cannot store them to half a texel; the part stays a prim.</summary>
    public bool UvTooWide { get; set; }
    /// <summary>0 for the first group of faces of a material whose coordinates fit one mesh face, then 1, 2 ... (set when the merged part is planned).</summary>
    public int UvClass { get; set; }

    /// <summary>
    /// What a material slot of a merged part is keyed by. The transform is part of the key only for a face that cannot be baked;
    /// a baked face carries its mapping, repeats, offset and rotation in its texture coordinates.
    /// </summary>
    public string SlotKey =>
        string.Create(CultureInfo.InvariantCulture, $"{TextureId}|{Color}|g{Glow:0.###}|s{Shiny}|b{Bump}|f{(Fullbright ? 1 : 0)}|m{(Bakeable && BakedMaterialId.Length > 0 ? BakedMaterialId : MaterialId)}|a{(Alpha ? 1 : 0)}")
        + (UvClass > 0 ? "|u" + UvClass.ToString(CultureInfo.InvariantCulture) : "")
        + (Bakeable ? "" : "|t" + TexGen.ToString(CultureInfo.InvariantCulture) + "|xf:" + Transform.Key);
}

/// <summary>One part of a linkset with everything the planner reads about it.</summary>
public sealed class MergePart
{
    public Guid PartId { get; set; }
    public int LinkNum { get; set; }
    public bool IsRoot { get; set; }
    public string Name { get; set; } = "";
    public string Creator { get; set; } = "";
    public string Masks { get; set; } = "";
    public int SurfaceMaterial { get; set; }
    /// <summary>"prim", "sculpt", "mesh" or "other".</summary>
    public string Kind { get; set; } = "prim";
    /// <summary>The eligibility code that keeps the part as it is (empty when the converter converts it).</summary>
    public string NotConvertible { get; set; } = "";
    public bool Scripted { get; set; }
    public bool HasContents { get; set; }
    public bool Light { get; set; }
    public bool Flexible { get; set; }
    public bool SitTarget { get; set; }
    public bool HoverText { get; set; }
    /// <summary>Per-part fields a merged part does not carry: set when the value differs from the default.</summary>
    public bool SitText { get; set; }
    public bool TouchText { get; set; }
    public bool ClickAction { get; set; }
    public bool SaleType { get; set; }
    public bool SalePrice { get; set; }
    public bool HoverTextColor { get; set; }
    public bool PayPrice { get; set; }
    public bool Particles { get; set; }
    /// <summary>True when the part has a texture animation, which moves the textures of all its faces.</summary>
    public bool TextureAnimation { get; set; }
    public bool Sound { get; set; }
    public bool Physical { get; set; }
    public bool VolumeDetect { get; set; }
    public bool Phantom { get; set; }
    /// <summary>0 prim, 1 none, 2 convex; -1 when the part stores none.</summary>
    public int PhysicsShapeType { get; set; } = -1;
    /// <summary>Vertices of the physics block the region's mesher builds for the part.</summary>
    public int PhysicsVertices { get; set; }
    /// <summary>Corners of the part's box in the linkset's root frame, in metres.</summary>
    public float[] BoxMin { get; set; } = new float[3];
    public float[] BoxMax { get; set; } = new float[3];
    public List<MergeFace> Faces { get; } = new List<MergeFace>();
}

/// <summary>What the scripts of a linkset say about its numbering.</summary>
public sealed class LinkFacts
{
    /// <summary>Empty when the linkset can be renumbered; otherwise why it cannot (every part then stays as it is).</summary>
    public string LockedBy { get; set; } = "";
    /// <summary>Literal link numbers (2 and up) some script addresses.</summary>
    public HashSet<int> Literal { get; } = new HashSet<int>();
    /// <summary>True when a script addresses the root (LINK_ROOT, 0 or 1).</summary>
    public bool RootAddressed { get; set; }
    public bool Locked => LockedBy.Length > 0;
}

public sealed class MergeLinkset
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Entry { get; set; } = "";
    public List<MergePart> Parts { get; } = new List<MergePart>();
    public LinkFacts Links { get; set; } = new LinkFacts();
}

public sealed class MergeOptions
{
    public double SoftCapMeters { get; set; } = 24;
    public double HardCapMeters { get; set; } = 64;
    /// <summary>The longest side of a merged part that holds alpha-blended faces: the viewer sorts alpha per object, so these stay small.</summary>
    public double AlphaCapMeters { get; set; } = 4;
    public int MaxFacesPerPart { get; set; } = 8;
    public int MaxFaceVertices { get; set; } = 65535;
    public int MaxPhysicsVertices { get; set; } = 65535;
}

public sealed class KeepRule
{
    /// <summary>The number of the merge rule that applies (see MERGE-PLAN.md), or "part", "group" or "sculpt" for the reasons that have no number.</summary>
    public string Rule { get; set; }
    public string Code { get; set; }
    public string Detail { get; set; }
}

public sealed class KeptPartPlan
{
    public Guid PartId { get; set; }
    public int LinkNum { get; set; }
    public int NewLinkNum { get; set; }
    public string Name { get; set; }
    /// <summary>Faces of the part that are drawn alpha-blended.</summary>
    public int AlphaFaces { get; set; }
    public List<KeepRule> Rules { get; } = new List<KeepRule>();
}

public sealed class SlotPlan
{
    public int Slot { get; set; }
    public string MaterialKey { get; set; }
    public bool Alpha { get; set; }
    public bool Baked { get; set; }
    public int SourceFaces { get; set; }
    public int Vertices { get; set; }
}

public sealed class MemberPlan
{
    public Guid PartId { get; set; }
    public int LinkNum { get; set; }
    public string Name { get; set; }
    public string Creator { get; set; }
}

public sealed class MergedPartPlan
{
    public Guid Id { get; set; }
    public int NewLinkNum { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public bool Alpha { get; set; }
    public double ExtentMeters { get; set; }
    public int PhysicsVertices { get; set; }
    public List<MemberPlan> Members { get; } = new List<MemberPlan>();
    public List<SlotPlan> Slots { get; } = new List<SlotPlan>();
    /// <summary>The notecard text for the merged root: the original part names and creators, and the plan file.</summary>
    public string Manifest { get; set; }
}

public sealed class LinksetPlan
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Entry { get; set; }
    public int PartsBefore { get; set; }
    public int PartsAfter { get; set; }
    public string LockedBy { get; set; } = "";
    public List<MergedPartPlan> Merged { get; } = new List<MergedPartPlan>();
    public List<KeptPartPlan> Kept { get; } = new List<KeptPartPlan>();
    public int AlphaFaces => Merged.Sum(m => m.Slots.Where(s => s.Alpha).Sum(s => s.SourceFaces)) + Kept.Sum(k => k.AlphaFaces);
}

public sealed class MergePlan
{
    public int Version { get; set; } = 1;
    public string Archive { get; set; }
    public string ToolVersion { get; set; }
    public List<string> Warnings { get; } = new List<string>();
    public List<LinksetPlan> Linksets { get; } = new List<LinksetPlan>();
    public int PartsBefore => Linksets.Sum(l => l.PartsBefore);
    public int PartsAfter => Linksets.Sum(l => l.PartsAfter);
    public int MergedParts => Linksets.Sum(l => l.Merged.Count);
    public int KeptParts => Linksets.Sum(l => l.Kept.Count);
    public int AlphaFaces => Linksets.Sum(l => l.AlphaFaces);
}
