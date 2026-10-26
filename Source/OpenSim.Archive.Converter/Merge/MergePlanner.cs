/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>
/// Decides, for each linkset, which parts merge into which merged parts and which stay as they are, and why. Writes nothing.
/// A part stays as it is for any reason that applies to it (the plan lists all of them); the parts left over are grouped by what
/// must be equal for two parts to share a mesh (creator, permissions, surface material, physics class, alpha), and each group is
/// cut into pieces that respect the caps: at most 8 material slots, the face and physics vertex limits, and the size caps.
/// </summary>
public static class MergePlanner
{
    public const string PlanFileName = "merge-plan.json";

    private const string RegionWarning =
        "Merged parts are collision meshes made of many boxes and walls. A region older than 6.4.2 (the Jolt native-shape fix, NGC PR #378) "
        + "can treat a merged part with few faces as a solid box. This tool runs offline and cannot check the region's version.";

    private sealed class Candidate
    {
        public MergePart Part;
        public int Number;
        public bool Alpha;
        public string ClassKey;
    }

    public static MergePlan Plan(IEnumerable<MergeLinkset> linksets, MergeOptions options, string archive)
    {
        MergePlan plan = new MergePlan { Archive = archive };
        foreach (MergeLinkset l in linksets)
            plan.Linksets.Add(PlanLinkset(l, options));
        if (plan.MergedParts > 0)
            plan.Warnings.Add(RegionWarning);
        return plan;
    }

    /// <summary>The number a part has in its linkset: the root is 1, a single part is 0.</summary>
    public static int NumberOf(MergePart p, int partCount) => partCount <= 1 ? 0 : p.IsRoot ? 1 : p.LinkNum;

    public static LinksetPlan PlanLinkset(MergeLinkset linkset, MergeOptions options)
    {
        options ??= new MergeOptions();
        List<MergePart> parts = linkset.Parts.OrderBy(p => NumberOf(p, linkset.Parts.Count)).ThenBy(p => p.PartId).ToList();
        LinksetPlan plan = new LinksetPlan
        {
            Id = linkset.Id, Name = linkset.Name, Entry = linkset.Entry, PartsBefore = parts.Count, LockedBy = linkset.Links.LockedBy
        };

        foreach (MergePart reset in parts)
            foreach (MergeFace face in reset.Faces)
                face.UvClass = 0;
        Dictionary<Guid, KeptPartPlan> kept = new Dictionary<Guid, KeptPartPlan>();
        List<Candidate> candidates = new List<Candidate>();
        int protectedUpTo = linkset.Links.Literal.Count > 0 ? linkset.Links.Literal.Max() : 0;
        foreach (MergePart p in parts)
        {
            int number = NumberOf(p, parts.Count);
            List<KeepRule> rules = KeepRules(linkset, p, number, parts.Count, protectedUpTo, options);
            if (rules.Count > 0)
            {
                KeptPartPlan k = new KeptPartPlan { PartId = p.PartId, LinkNum = number, Name = p.Name, AlphaFaces = p.Faces.Count(f => f.Alpha) };
                k.Rules.AddRange(rules);
                kept[p.PartId] = k;
            }
            else
                candidates.Add(new Candidate { Part = p, Number = number, Alpha = p.Faces.Any(f => f.Alpha), ClassKey = ClassKey(p) });
        }

        List<List<Candidate>> clusters = new List<List<Candidate>>();
        foreach (IGrouping<string, Candidate> group in candidates.GroupBy(c => c.ClassKey).OrderBy(g => g.Min(c => c.Number)))
        {
            List<Candidate> members = group.OrderBy(c => c.Number).ToList();
            if (members.Count == 1)
            {
                kept[members[0].Part.PartId] = SoleInClass(members[0], candidates);
                continue;
            }
            foreach (List<Candidate> cluster in Cut(members, options))
            {
                if (cluster.Count == 1)
                    kept[cluster[0].Part.PartId] = SplitAlone(cluster[0], options);
                else
                    clusters.Add(cluster);
            }
        }

        // output order: the lowest original link number of each output part, so the root stays 1
        List<(int lowest, MergedPartPlan merged, KeptPartPlan kept)> outputs = new List<(int, MergedPartPlan, KeptPartPlan)>();
        foreach (List<Candidate> c in clusters)
            outputs.Add((c.Min(x => x.Number), BuildMerged(linkset, c, options), null));
        foreach (KeptPartPlan k in kept.Values)
            outputs.Add((k.LinkNum, null, k));
        outputs = outputs.OrderBy(o => o.lowest).ThenBy(o => o.merged != null ? 0 : 1).ToList();

        int next = 1, index = 1;
        foreach (var o in outputs)
        {
            if (o.merged != null)
            {
                o.merged.NewLinkNum = next++;
                o.merged.Name = Limit(linkset.Name + " / merged " + index++, 63);
                plan.Merged.Add(o.merged);
            }
            else
            {
                o.kept.NewLinkNum = next++;
                plan.Kept.Add(o.kept);
            }
        }
        plan.PartsAfter = plan.Merged.Count + plan.Kept.Count;
        foreach (MergedPartPlan m in plan.Merged)
            m.Manifest = Manifest(m);
        return plan;
    }

    // ---------------------------------------------------------------- why a part stays as it is

    private static List<KeepRule> KeepRules(MergeLinkset ls, MergePart p, int number, int partCount, int protectedUpTo, MergeOptions o)
    {
        List<KeepRule> r = new List<KeepRule>();
        void Add(string rule, string code, string detail) => r.Add(new KeepRule { Rule = rule, Code = code, Detail = detail });

        if (partCount <= 1)
            Add("7", "SINGLE_PART_OBJECT", "the object has one part; parts of different objects are never merged");
        if (ls.Links.Locked)
            Add("17", "LINKSET_LOCKED", ls.Links.LockedBy);
        if (p.Scripted)
            Add("17", "SCRIPTED", "the part holds a script");
        bool addressed = ls.Links.Literal.Contains(number) || (p.IsRoot && ls.Links.RootAddressed);
        if (addressed)
            Add("17", "ADDRESSED", "a script addresses the part by link number " + number);
        else if (partCount > 1 && number <= protectedUpTo)
            Add("17", "PROTECTED_LINK_NUMBER", "a script addresses link number " + protectedUpTo + ", so numbers up to it do not change");
        if (p.HasContents) Add("part", "CONTENTS", "the part holds items other than scripts");
        if (p.Kind == "mesh") Add("11", "ALREADY_MESH", "an existing mesh keeps its makers' levels of detail");
        else if (p.Kind != "prim" && p.Kind != "sculpt") Add("part", "NOT_PRIM", "the part is not a prim");
        if (p.NotConvertible.Length > 0) Add("part", "NOT_CONVERTED", "the converter keeps the part as a prim (" + p.NotConvertible + ")");
        if (p.Light) Add("part", "LIGHT", "per-part light parameters");
        if (p.TextureAnimation) Add("part", "TEXTURE_ANIMATION", "a texture animation moves the textures of every face of the part");
        if (p.PbrDataUnreadable) Add("part", "PBR_DATA", "the part's PBR material assignments or overrides cannot be read the way the region reads them, or name a face the part does not have");
        if (p.MediaDataUnreadable) Add("part", "MEDIA_DATA", "the part's media list cannot be read, or holds an entry for a face the part does not have");
        if (p.Faces.Any(f => f.UvTooWide || UvPrecision.TooWide(f))) Add("part", "UV_PRECISION", "the texture coordinates of a face run so wide that a mesh asset would show them more than half a texel off");
        if (p.Faces.Any(f => !f.Bakeable && f.TexGen != 0)) Add("part", "UNBAKED_MAPPING", "a face with planar or other texture mapping that cannot be baked would be projected on the merged shape instead of its own");
        if (p.Flexible) Add("part", "FLEXI", "per-part flexible path");
        if (p.SitTarget) Add("part", "SIT_TARGET", "per-part sit target");
        if (p.HoverText) Add("part", "HOVER_TEXT", "per-part hover text");
        if (p.SitText) Add("part", "SIT_TEXT", "per-part sit text");
        if (p.TouchText) Add("part", "TOUCH_TEXT", "per-part touch text");
        if (p.ClickAction) Add("part", "CLICK_ACTION", "per-part click action");
        if (p.SaleType) Add("part", "SALE_TYPE", "per-part sale type");
        if (p.SalePrice) Add("part", "SALE_PRICE", "per-part sale price");
        if (p.HoverTextColor) Add("part", "HOVER_TEXT_COLOR", "per-part hover text colour");
        if (p.PayPrice) Add("part", "PAY_PRICE", "per-part pay prices");
        if (p.Particles) Add("part", "PARTICLES", "per-part particle system");
        if (p.Sound) Add("part", "SOUND", "per-part sound");
        if (p.Physical) Add("part", "PHYSICAL", "a physical part's hull is not the union of its pieces' hulls");
        if (p.VolumeDetect) Add("part", "VOLUME_DETECT", "a trigger volume");
        if (p.PhysicsShapeType == 2) Add("part", "CONVEX_PHYSICS", "a convex hull of a merged mesh is not the union of the hulls");
        if (p.IsRoot && p.PhysicsShapeType == 1) Add("part", "ROOT_PHYSICS_NONE", "the region gives a root with no physics shape its default shape");

        double extent = Extent(new[] { p });
        if (extent > o.HardCapMeters)
            Add("9", "HARD_CAP", "longest side " + Meters(extent) + " m is over the hard cap of " + Meters(o.HardCapMeters) + " m");
        else if (extent > o.SoftCapMeters)
            Add("9", "SOFT_CAP", "longest side " + Meters(extent) + " m is over the soft cap of " + Meters(o.SoftCapMeters) + " m");
        return r;
    }

    private static KeptPartPlan SoleInClass(Candidate c, List<Candidate> all)
    {
        KeptPartPlan k = new KeptPartPlan { PartId = c.Part.PartId, LinkNum = c.Number, Name = c.Part.Name, AlphaFaces = c.Part.Faces.Count(f => f.Alpha) };
        bool differs = all.Any(o => o.Part.PartId != c.Part.PartId && (o.Part.Creator != c.Part.Creator || o.Part.Masks != c.Part.Masks));
        if (differs)
            k.Rules.Add(new KeepRule { Rule = "10", Code = "SOLE_CREATOR_OR_PERMISSIONS", Detail = "no other plain part has the same creator and permissions" });
        else
            k.Rules.Add(new KeepRule { Rule = "group", Code = "SOLE_IN_CLASS", Detail = "no other part that can merge shares its creator, permissions, surface material, physics class and alpha class" });
        return k;
    }

    private static KeptPartPlan SplitAlone(Candidate c, MergeOptions o)
    {
        KeptPartPlan k = new KeptPartPlan { PartId = c.Part.PartId, LinkNum = c.Number, Name = c.Part.Name, AlphaFaces = c.Part.Faces.Count(f => f.Alpha) };
        if (c.Alpha)
            k.Rules.Add(new KeepRule { Rule = "18", Code = "ALPHA_CAP", Detail = "alpha-blended parts merge only within " + Meters(o.AlphaCapMeters) + " m, and none fits with this one" });
        else
            k.Rules.Add(new KeepRule { Rule = "9", Code = "SPLIT_BY_CAPS", Detail = "no neighbour fits with it under the size, face and vertex caps" });
        return k;
    }

    private static string ClassKey(MergePart p)
    {
        string physics = p.Phantom ? "phantom" : p.PhysicsShapeType == 1 ? "none" : "solid";
        bool alpha = p.Faces.Any(f => f.Alpha);
        return string.Join("|", p.Creator, p.Masks, p.SurfaceMaterial.ToString(CultureInfo.InvariantCulture), physics, alpha ? "alpha" : "opaque");
    }

    // ---------------------------------------------------------------- cutting a group into mergeable pieces

    private static bool Fits(List<Candidate> set, MergeOptions o)
    {
        if (set.Count == 1)
            return true;
        if (set.Sum(c => c.Part.PhysicsVertices) > o.MaxPhysicsVertices)
            return false;
        if (SlotCount(set, o) > o.MaxFacesPerPart)
            return false;
        double cap = set[0].Alpha ? o.AlphaCapMeters : o.SoftCapMeters;
        return Extent(set.Select(c => c.Part)) <= cap + 1e-6;
    }

    /// <summary>
    /// The most faces the geometry stage can build for a material of <paramref name="total"/> vertices whose largest source face has
    /// <paramref name="largest"/> vertices. The stage closes a face as soon as the next triangle (up to three new vertices) might not fit,
    /// so a closed face holds at least two vertices fewer than the limit; and where it closes inside a source face, the vertices that face
    /// shares across the cut are written again in the next face, at most <paramref name="largest"/> of them. With k faces built,
    /// (k - 1) * (limit - 2 - largest) is at most the total, which bounds k. Counting with the plain limit could plan a face too few.
    /// </summary>
    private static int FacesNeeded(long total, long largest, MergeOptions o)
    {
        long capacity = Math.Max(1, o.MaxFaceVertices - 2);
        if (total <= capacity)
            return 1;
        long effective = Math.Max(Math.Max(1, capacity / 4), capacity - largest);
        return (int)(total / effective + 1);
    }

    private static int SlotCount(IEnumerable<Candidate> set, MergeOptions o)
    {
        // a material takes a mesh face for each group of its faces that can share one (see UvPrecision.Groups), and more when it is long
        Dictionary<string, List<MergeFace>> keys = new Dictionary<string, List<MergeFace>>(StringComparer.Ordinal);
        foreach (Candidate c in set)
            foreach (MergeFace f in c.Part.Faces)
            {
                if (!keys.TryGetValue(f.SlotKey, out List<MergeFace> list))
                    keys[f.SlotKey] = list = new List<MergeFace>();
                list.Add(f);
            }
        return keys.Values.Sum(list => UvPrecision.Groups(list).Sum(g => FacesNeeded(g.Sum(f => (long)f.Vertices), g.Max(f => (long)f.Vertices), o)));
    }

    /// <summary>Two ways to cut a group, by place and by material; the one with fewer pieces wins, then the one with the smaller boxes.</summary>
    private static List<List<Candidate>> Cut(List<Candidate> members, MergeOptions o)
    {
        List<List<Candidate>> spatial = new List<List<Candidate>>();
        CutSpatial(members, o, spatial);
        List<List<Candidate>> byMaterial = CutByMaterial(members, o);
        int a = spatial.Count, b = byMaterial.Count;
        if (b < a || (b == a && Volume(byMaterial) < Volume(spatial) - 1e-9))
            return byMaterial;
        return spatial;
    }

    private static void CutSpatial(List<Candidate> set, MergeOptions o, List<List<Candidate>> into)
    {
        if (set.Count == 1 || Fits(set, o))
        {
            into.Add(set);
            return;
        }
        float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
        foreach (Candidate c in set)
            for (int i = 0; i < 3; i++)
            {
                float mid = (c.Part.BoxMin[i] + c.Part.BoxMax[i]) / 2;
                lo[i] = Math.Min(lo[i], mid);
                hi[i] = Math.Max(hi[i], mid);
            }
        int axis = 0;
        for (int i = 1; i < 3; i++)
            if (hi[i] - lo[i] > hi[axis] - lo[axis] + 1e-6f)
                axis = i;
        List<Candidate> sorted = set.OrderBy(c => c.Part.BoxMin[axis] + c.Part.BoxMax[axis]).ThenBy(c => c.Number).ToList();
        int half = sorted.Count / 2;
        CutSpatial(sorted.Take(half).OrderBy(c => c.Number).ToList(), o, into);
        CutSpatial(sorted.Skip(half).OrderBy(c => c.Number).ToList(), o, into);
    }

    private static List<List<Candidate>> CutByMaterial(List<Candidate> members, MergeOptions o)
    {
        List<Candidate> ordered = members.OrderBy(c => DominantKey(c.Part), StringComparer.Ordinal).ThenBy(c => c.Number).ToList();
        List<List<Candidate>> result = new List<List<Candidate>>();
        List<Candidate> current = null;
        foreach (Candidate c in ordered)
        {
            if (current != null)
            {
                current.Add(c);
                if (Fits(current, o))
                    continue;
                current.RemoveAt(current.Count - 1);
            }
            current = new List<Candidate> { c };
            result.Add(current);
        }
        foreach (List<Candidate> r in result)
            r.Sort((x, y) => x.Number.CompareTo(y.Number));
        return result.OrderBy(r => r.Min(c => c.Number)).ToList();
    }

    private static string DominantKey(MergePart p) =>
        p.Faces.GroupBy(f => f.SlotKey).OrderByDescending(g => g.Sum(f => (long)f.Vertices)).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault() ?? "";

    private static double Volume(List<List<Candidate>> clusters)
    {
        double total = 0;
        foreach (List<Candidate> c in clusters)
        {
            double v = 1;
            for (int i = 0; i < 3; i++)
                v *= Math.Max(0.001, c.Max(x => x.Part.BoxMax[i]) - c.Min(x => x.Part.BoxMin[i]));
            total += v;
        }
        return total;
    }

    /// <summary>The longest side of the box that holds all the parts.</summary>
    public static double Extent(IEnumerable<MergePart> parts)
    {
        float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
        foreach (MergePart p in parts)
            for (int i = 0; i < 3; i++)
            {
                lo[i] = Math.Min(lo[i], p.BoxMin[i]);
                hi[i] = Math.Max(hi[i], p.BoxMax[i]);
            }
        return Math.Max(hi[0] - lo[0], Math.Max(hi[1] - lo[1], hi[2] - lo[2]));
    }

    // ---------------------------------------------------------------- the merged part

    private static MergedPartPlan BuildMerged(MergeLinkset ls, List<Candidate> cluster, MergeOptions o)
    {
        MergedPartPlan m = new MergedPartPlan
        {
            Alpha = cluster[0].Alpha,
            ExtentMeters = Math.Round(Extent(cluster.Select(c => c.Part)), 4),
            PhysicsVertices = cluster.Sum(c => c.Part.PhysicsVertices)
        };
        foreach (Candidate c in cluster)
            m.Members.Add(new MemberPlan { PartId = c.Part.PartId, LinkNum = c.Number, Name = c.Part.Name, Creator = c.Part.Creator });

        // faces of one material that are too far apart in texture space to share a mesh face get a class of their own, which is part of the key
        Dictionary<string, List<MergeFace>> byKey = new Dictionary<string, List<MergeFace>>(StringComparer.Ordinal);
        foreach (Candidate c in cluster)
            foreach (MergeFace f in c.Part.Faces)
            {
                if (!byKey.TryGetValue(f.SlotKey, out List<MergeFace> list))
                    byKey[f.SlotKey] = list = new List<MergeFace>();
                list.Add(f);
            }
        foreach (List<MergeFace> list in byKey.Values)
        {
            List<List<MergeFace>> groups = UvPrecision.Groups(list);
            for (int g = 1; g < groups.Count; g++)
                foreach (MergeFace f in groups[g])
                    f.UvClass = g;
        }

        // a slot per material key in order of first use; a key with more vertices than a face holds takes more slots
        List<string> order = new List<string>();
        Dictionary<string, (int faces, long vertices, bool alpha, bool baked, long largest)> keys = new Dictionary<string, (int, long, bool, bool, long)>(StringComparer.Ordinal);
        foreach (Candidate c in cluster)
            foreach (MergeFace f in c.Part.Faces)
            {
                string k = f.SlotKey;
                if (!keys.TryGetValue(k, out var cur))
                {
                    order.Add(k);
                    cur = (0, 0, f.Alpha, f.Bakeable, 0);
                }
                keys[k] = (cur.faces + 1, cur.vertices + f.Vertices, cur.alpha, cur.baked, Math.Max(cur.largest, f.Vertices));
            }
        int slot = 0;
        foreach (string k in order)
        {
            var v = keys[k];
            int n = FacesNeeded(v.vertices, v.largest, o);
            long perSlot = (v.vertices + n - 1) / n;
            long left = v.vertices;
            for (int i = 0; i < n; i++)
            {
                long take = Math.Min(left, perSlot);
                left -= take;
                // the source faces are counted on the key's first slot; how they split across slots is the geometry stage's decision
                m.Slots.Add(new SlotPlan { Slot = slot++, MaterialKey = k, Alpha = v.alpha, Baked = v.baked, SourceFaces = i == 0 ? v.faces : 0, Vertices = (int)take });
            }
        }

        string hash = Convert.ToHexString(ManagedDigest.Sha256(Encoding.UTF8.GetBytes(string.Join(",", cluster.Select(c => c.Part.PartId.ToString("N")))))).ToLowerInvariant();
        // the lowest-numbered member (the root, when it is a member) is the part that is rewritten into the merged part, so the id stays its own
        m.Id = cluster.OrderBy(c => c.Number).First().Part.PartId;
        m.Description = "oarx-merge " + hash.Substring(0, 8) + " " + m.Members.Count + " parts";
        return m;
    }

    private static string Manifest(MergedPartPlan m)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Merged part " + m.Name);
        sb.AppendLine("Made by oar-convert --merge from " + m.Members.Count + " parts; the plan file is " + PlanFileName + ".");
        sb.AppendLine();
        sb.AppendLine("link | name | creator | id");
        foreach (MemberPlan p in m.Members)
            sb.AppendLine(p.LinkNum + " | " + p.Name + " | " + p.Creator + " | " + p.PartId);
        return sb.ToString();
    }

    private static string Meters(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Limit(string s, int max) => s.Length <= max ? s : s.Substring(0, max);
}

/// <summary>Decides whether a face's transform can be baked into merged texture coordinates (merge rule 12).</summary>
public static class FaceBaking
{
    /// <summary>The texture mapping value of a planar face: the viewer's TEX_GEN_PLANAR, 0x02 in the texture entry's flags.</summary>
    public const int PlanarMapping = (int)OpenMetaverse.MappingType.Planar;

    /// <summary>The most a map's transform may differ from the face's, in any of its five figures, for the face still to count as drawing both alike (a texture entry and a material store the same figure in different steps).</summary>
    public const float MapTransformTolerance = 5e-4f;

    /// <summary>
    /// A face bakes unless it uses a texture mapping other than default or planar, names a legacy material that
    /// is not in the archive (a PBR face does not use it), or names one with a normal or specular map drawn with a transform other than the face's.
    /// A media face bakes like any other. The viewer draws the
    /// diffuse texture with the face's transform and each map with the material's own transform for that map, all from the same base
    /// coordinates, so a face whose maps are drawn as its diffuse is can have the one baked set of coordinates serve all three. The planner
    /// then checks the corners (see <see cref="MapDeviationTexels"/>).
    /// </summary>
    public static bool Bakeable(FaceTransform transform, int texGen, string materialId, LegacyMaterialInfo material, bool pbr, bool media, out string why)
    {
        why = "";
        if (texGen != 0 && texGen != PlanarMapping) { why = "texture mapping other than default or planar"; return false; }
        // a PBR face is drawn from its PBR material; the viewer ignores a legacy material on it (llvovolume.cpp: "ignore traditional material if GLTF material is present")
        if (pbr || string.IsNullOrEmpty(materialId))
            return true;
        if (material == null) { why = "legacy material not in the archive"; return false; }
        // The viewer draws a material's normal and specular maps with the material's own transform, from the same base coordinates as the
        // diffuse texture (llface.cpp getGeometryVolume). Once the face's transform is in the mesh coordinates, a map that is drawn with
        // the same transform finds its coordinates already right.
        if ((material.HasNormalMap && !Near(material.NormalTransform, transform)) || (material.HasSpecularMap && !Near(material.SpecularTransform, transform)))
        {
            why = "legacy material maps are drawn with a transform of their own";
            return false;
        }
        return true;
    }

    private static bool Near(FaceTransform a, FaceTransform b) =>
        MathF.Abs(a.RepeatU - b.RepeatU) <= MapTransformTolerance && MathF.Abs(a.RepeatV - b.RepeatV) <= MapTransformTolerance
        && MathF.Abs(a.OffsetU - b.OffsetU) <= MapTransformTolerance && MathF.Abs(a.OffsetV - b.OffsetV) <= MapTransformTolerance
        && MathF.Abs(a.Rotation - b.Rotation) <= MapTransformTolerance;

    /// <summary>
    /// The most a map's coordinates may differ from the diffuse coordinates a merged face carries, in texels of the map's own texture. A texture
    /// entry and a material store the same repeat, offset or rotation in different steps, so two faces drawn alike by their maker differ by a
    /// fraction of a texel on a large planar face. This is half of the budget for storing the coordinates in the mesh (<see cref="UvPrecision.MaxTexels"/>).
    /// </summary>
    public const double MapTolerance = 0.25;

    /// <summary>
    /// Compares, corner by corner, the coordinates the viewer gives a map of the face (the base coordinates, projected when the face is planar,
    /// with the map's transform) with the diffuse coordinates the merged face will carry. Returns the worst difference in texels of the map's
    /// own texture (<paramref name="width"/> by <paramref name="height"/> pixels, 0 when unknown).
    /// </summary>
    public static double MapDeviationTexels(float[] diffuseUv, float[] positions, float[] normals, float[] uvs, FaceTransform map, bool planar, System.Numerics.Vector3 scale, int width, int height)
    {
        float[] drawn = FaceUv.Bake(positions, normals, uvs, map, planar, scale);
        double w = width > 0 ? width : UvPrecision.UnknownResolution, h = height > 0 ? height : UvPrecision.UnknownResolution, worst = 0;
        for (int i = 0; i < drawn.Length; i += 2)
            worst = Math.Max(worst, Math.Max(Math.Abs(drawn[i] - diffuseUv[i]) * w, Math.Abs(drawn[i + 1] - diffuseUv[i + 1]) * h));
        return worst;
    }
}
