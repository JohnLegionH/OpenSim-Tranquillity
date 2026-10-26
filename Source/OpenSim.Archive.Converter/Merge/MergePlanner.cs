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
        if (p.HasContents) Add("M0-2", "CONTENTS", "the part holds items other than scripts");
        if (p.Kind == "mesh") Add("11", "ALREADY_MESH", "an existing mesh keeps its makers' levels of detail");
        else if (p.Kind == "sculpt") Add("M1", "SCULPT_NOT_PLANNED", "a sculpt is converted on its own; merging it needs the geometry stage to read its map");
        else if (p.Kind != "prim") Add("M0-2", "NOT_PRIM", "the part is not a prim");
        if (p.NotConvertible.Length > 0) Add("M0-2", "NOT_CONVERTED", "the converter keeps the part as a prim (" + p.NotConvertible + ")");
        if (p.Light) Add("M0-2", "LIGHT", "per-part light parameters");
        if (p.Flexible) Add("M0-2", "FLEXI", "per-part flexible path");
        if (p.SitTarget) Add("M0-2", "SIT_TARGET", "per-part sit target");
        if (p.HoverText) Add("M0-2", "HOVER_TEXT", "per-part hover text");
        if (p.Particles) Add("M0-2", "PARTICLES", "per-part particle system");
        if (p.Sound) Add("M0-2", "SOUND", "per-part sound");
        if (p.Physical) Add("M0-2", "PHYSICAL", "a physical part's hull is not the union of its pieces' hulls");
        if (p.VolumeDetect) Add("M0-2", "VOLUME_DETECT", "a trigger volume");
        if (p.PhysicsShapeType == 2) Add("M0-2", "CONVEX_PHYSICS", "a convex hull of a merged mesh is not the union of the hulls");
        if (p.IsRoot && p.PhysicsShapeType == 1) Add("M0-2", "ROOT_PHYSICS_NONE", "the region gives a root with no physics shape its default shape");

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
            k.Rules.Add(new KeepRule { Rule = "M0-5", Code = "SOLE_IN_CLASS", Detail = "no other plain part shares its surface material, physics class and alpha class" });
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

    private static int SlotCount(IEnumerable<Candidate> set, MergeOptions o)
    {
        Dictionary<string, long> vertices = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (Candidate c in set)
            foreach (MergeFace f in c.Part.Faces)
                vertices[f.SlotKey] = (vertices.TryGetValue(f.SlotKey, out long v) ? v : 0) + f.Vertices;
        return vertices.Values.Sum(v => (int)Math.Max(1, (v + o.MaxFaceVertices - 1) / o.MaxFaceVertices));
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

        // a slot per material key in order of first use; a key with more vertices than a face holds takes more slots
        List<string> order = new List<string>();
        Dictionary<string, (int faces, long vertices, bool alpha, bool baked)> keys = new Dictionary<string, (int, long, bool, bool)>(StringComparer.Ordinal);
        foreach (Candidate c in cluster)
            foreach (MergeFace f in c.Part.Faces)
            {
                string k = f.SlotKey;
                if (!keys.TryGetValue(k, out var cur))
                {
                    order.Add(k);
                    cur = (0, 0, f.Alpha, f.Bakeable);
                }
                keys[k] = (cur.faces + 1, cur.vertices + f.Vertices, cur.alpha, cur.baked);
            }
        int slot = 0;
        foreach (string k in order)
        {
            var v = keys[k];
            int n = (int)Math.Max(1, (v.vertices + o.MaxFaceVertices - 1) / o.MaxFaceVertices);
            long left = v.vertices;
            for (int i = 0; i < n; i++)
            {
                long take = Math.Min(left, o.MaxFaceVertices);
                left -= take;
                // the source faces are counted on the key's first slot; how they split across slots is the geometry stage's decision
                m.Slots.Add(new SlotPlan { Slot = slot++, MaterialKey = k, Alpha = v.alpha, Baked = v.baked, SourceFaces = i == 0 ? v.faces : 0, Vertices = (int)take });
            }
        }

        string hash = Convert.ToHexString(ManagedDigest.Sha256(Encoding.UTF8.GetBytes(string.Join(",", cluster.Select(c => c.Part.PartId.ToString("N")))))).ToLowerInvariant();
        m.Id = new Guid(hash.Substring(0, 32));
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
    /// <summary>
    /// A face bakes unless it is a PBR or media face, uses planar texture mapping, or names a legacy material whose normal or specular
    /// map is drawn with a transform other than the diffuse texture's (or whose material is not in the archive).
    /// </summary>
    public static bool Bakeable(FaceTransform transform, int texGen, string materialId, LegacyMaterialInfo material, bool pbr, bool media, out string why)
    {
        why = "";
        if (pbr) { why = "PBR material face"; return false; }
        if (media) { why = "media face"; return false; }
        if (texGen != 0) { why = "planar texture mapping"; return false; }
        if (string.IsNullOrEmpty(materialId))
            return true;
        if (material == null) { why = "legacy material not in the archive"; return false; }
        if ((material.HasNormalMap && !material.NormalTransform.SameAs(transform)) || (material.HasSpecularMap && !material.SpecularTransform.SameAs(transform)))
        {
            why = "legacy material map drawn with its own transform";
            return false;
        }
        return true;
    }
}
