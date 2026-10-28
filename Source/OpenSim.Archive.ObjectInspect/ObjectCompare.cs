/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenMetaverse;

namespace OpenSim.Archive.ObjectInspect;

/// <summary>One thing that differs between a matched part (or item) in the reference archive and in the archive.</summary>
public sealed class Difference
{
    /// <summary>"link order", "permissions", "who", "parts" or "items".</summary>
    public string Area { get; set; }
    public string Part { get; set; }
    public string Field { get; set; }
    public string Reference { get; set; }
    public string Archive { get; set; }
}

/// <summary>Where one part stands in the reference archive and in the archive.</summary>
public sealed class PartLink
{
    public string Name { get; set; }
    public int? ReferenceLink { get; set; }
    public int? ArchiveLink { get; set; }
    public int? ReferenceFileOrder { get; set; }
    public int? ArchiveFileOrder { get; set; }
    public bool Changed { get; set; }
}

public sealed class ObjectComparison
{
    public string Name { get; set; }
    /// <summary>How the two objects were paired: "name", "name and part count", or "name, part count and shape" when several objects share the name.</summary>
    public string Matched { get; set; }
    public int ReferenceIndex { get; set; }
    public int ArchiveIndex { get; set; }
    public List<PartLink> Parts { get; set; } = new List<PartLink>();
    public List<Difference> Differences { get; set; } = new List<Difference>();
    public bool LinkOrderDiffers => Differences.Any(d => d.Area == "link order");
    public bool PermissionsDiffer => Differences.Any(d => d.Area == "permissions" || d.Area == "who");
}

public sealed class CompareResult
{
    public List<ObjectComparison> Objects { get; set; } = new List<ObjectComparison>();
    public List<string> OnlyInReference { get; set; } = new List<string>();
    public List<string> OnlyInArchive { get; set; } = new List<string>();
    public int Matched => Objects.Count;
    public int ObjectsWithLinkOrderDifferences => Objects.Count(o => o.LinkOrderDiffers);
    public int ObjectsWithPermissionDifferences => Objects.Count(o => o.PermissionsDiffer);
    public bool Identical { get; set; }

    public string ToText(string referenceLabel = null, string archiveLabel = null)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("oar-inspect compare\n");
        sb.Append("reference: ").Append(referenceLabel ?? "(not named)").Append('\n');
        sb.Append("archive: ").Append(archiveLabel ?? "(not named)").Append('\n');
        sb.Append("objects matched: ").Append(Matched).Append('\n');
        sb.Append("only in reference: ").Append(OnlyInReference.Count).Append('\n');
        sb.Append("only in archive: ").Append(OnlyInArchive.Count).Append('\n');
        sb.Append("objects with link order differences: ").Append(ObjectsWithLinkOrderDifferences).Append('\n');
        sb.Append("objects with permission differences: ").Append(ObjectsWithPermissionDifferences).Append('\n');
        if (Identical)
            sb.Append("\nno differences\n");

        foreach (ObjectComparison o in Objects.Where(x => x.Differences.Count > 0))
        {
            sb.Append("\nOBJECT ").Append(Fmt.Quote(o.Name)).Append(" (matched by ").Append(o.Matched).Append(")\n");
            if (o.LinkOrderDiffers)
            {
                sb.Append("  LINK ORDER\n");
                foreach (PartLink p in o.Parts.Where(x => x.Changed && x.ReferenceLink.HasValue && x.ArchiveLink.HasValue))
                    sb.Append("    ").Append(Fmt.Quote(p.Name)).Append(": link ").Append(Move(p.ReferenceLink, p.ArchiveLink))
                      .Append(", file position ").Append(Move(p.ReferenceFileOrder, p.ArchiveFileOrder)).Append('\n');
            }
            Section(sb, o, "permissions", "PERMISSIONS");
            Section(sb, o, "who", "WHO");
            Section(sb, o, "parts", "PARTS");
            Section(sb, o, "items", "ITEMS");
        }

        if (OnlyInReference.Count > 0)
        {
            sb.Append("\nONLY IN REFERENCE\n");
            foreach (string n in OnlyInReference)
                sb.Append("  ").Append(Fmt.Quote(n)).Append('\n');
        }
        if (OnlyInArchive.Count > 0)
        {
            sb.Append("\nONLY IN ARCHIVE\n");
            foreach (string n in OnlyInArchive)
                sb.Append("  ").Append(Fmt.Quote(n)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Move(int? a, int? b) => a == b ? a + " (unchanged)" : a + " -> " + b;

    private static void Section(StringBuilder sb, ObjectComparison o, string area, string title)
    {
        List<Difference> list = o.Differences.Where(d => d.Area == area).ToList();
        if (list.Count == 0)
            return;
        sb.Append("  ").Append(title).Append('\n');
        foreach (Difference d in list)
        {
            sb.Append("    ").Append(Fmt.Quote(d.Part)).Append(": ");
            if (area == "parts" || area == "items")
                sb.Append(d.Field);
            else
                sb.Append(d.Field).Append(' ').Append(d.Reference).Append(" -> ").Append(d.Archive);
            sb.Append('\n');
        }
    }

    public string ToCsv()
    {
        StringBuilder sb = new StringBuilder("object,area,part,field,reference,archive\r\n");
        foreach (ObjectComparison o in Objects)
            foreach (Difference d in o.Differences)
                sb.Append(ReportCsv.Text(o.Name)).Append(',').Append(d.Area).Append(',').Append(ReportCsv.Text(d.Part)).Append(',')
                  .Append(ReportCsv.Plain(d.Field)).Append(',').Append(ReportCsv.Plain(d.Reference)).Append(',').Append(ReportCsv.Plain(d.Archive)).Append("\r\n");
        foreach (string n in OnlyInReference)
            sb.Append(ReportCsv.Text(n)).Append(",objects,,only in reference,present,absent\r\n");
        foreach (string n in OnlyInArchive)
            sb.Append(ReportCsv.Text(n)).Append(",objects,,only in archive,absent,present\r\n");
        return sb.ToString();
    }

    public string ToJson(string referenceLabel = null, string archiveLabel = null)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("tool", "oar-inspect");
            if (referenceLabel != null) w.WriteString("reference", referenceLabel);
            if (archiveLabel != null) w.WriteString("archive", archiveLabel);
            w.WriteBoolean("identical", Identical);
            w.WriteNumber("matched", Matched);
            w.WriteNumber("objectsWithLinkOrderDifferences", ObjectsWithLinkOrderDifferences);
            w.WriteNumber("objectsWithPermissionDifferences", ObjectsWithPermissionDifferences);
            w.WriteStartArray("onlyInReference");
            foreach (string n in OnlyInReference) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteStartArray("onlyInArchive");
            foreach (string n in OnlyInArchive) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteStartArray("objects");
            foreach (ObjectComparison o in Objects)
            {
                w.WriteStartObject();
                w.WriteString("name", o.Name);
                w.WriteString("matched", o.Matched);
                w.WriteNumber("referenceIndex", o.ReferenceIndex);
                w.WriteNumber("archiveIndex", o.ArchiveIndex);
                w.WriteStartArray("parts");
                foreach (PartLink p in o.Parts)
                {
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    Num(w, "referenceLink", p.ReferenceLink);
                    Num(w, "archiveLink", p.ArchiveLink);
                    Num(w, "referenceFileOrder", p.ReferenceFileOrder);
                    Num(w, "archiveFileOrder", p.ArchiveFileOrder);
                    w.WriteBoolean("changed", p.Changed);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("differences");
                foreach (Difference d in o.Differences)
                {
                    w.WriteStartObject();
                    w.WriteString("area", d.Area);
                    w.WriteString("part", d.Part);
                    w.WriteString("field", d.Field);
                    w.WriteString("reference", d.Reference);
                    w.WriteString("archive", d.Archive);
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

    private static void Num(Utf8JsonWriter w, string name, int? v)
    {
        if (v.HasValue) w.WriteNumber(name, v.Value);
        else w.WriteNull(name);
    }
}

/// <summary>Pairs the objects of two reports and lists the link-order and permission differences, part by part.</summary>
public static class ReportCompare
{
    public static CompareResult Compare(ObjectReport reference, ObjectReport archive)
    {
        CompareResult result = new CompareResult();
        List<(ObjectEntry r, ObjectEntry a, string how)> pairs = Pair(reference.Objects, archive.Objects, result);

        foreach (var (r, a, how) in pairs.OrderBy(p => p.r.Index))
        {
            ObjectComparison c = new ObjectComparison { Name = r.Name, Matched = how, ReferenceIndex = r.Index, ArchiveIndex = a.Index };
            ComparePartsOf(r, a, c);
            result.Objects.Add(c);
        }
        result.Identical = result.OnlyInReference.Count == 0 && result.OnlyInArchive.Count == 0 && result.Objects.All(o => o.Differences.Count == 0);
        return result;
    }

    // ------------------------------------------------------------------ pairing objects

    private static string ShapeSignature(ObjectEntry o) => string.Join("|", o.Parts.Select(p => p.ShapeKey).OrderBy(k => k, StringComparer.Ordinal));

    private static List<(ObjectEntry, ObjectEntry, string)> Pair(List<ObjectEntry> refs, List<ObjectEntry> arcs, CompareResult result)
    {
        List<(ObjectEntry, ObjectEntry, string)> pairs = new List<(ObjectEntry, ObjectEntry, string)>();
        HashSet<ObjectEntry> usedA = new HashSet<ObjectEntry>();
        HashSet<ObjectEntry> pairedR = new HashSet<ObjectEntry>();

        foreach (IGrouping<string, ObjectEntry> group in refs.GroupBy(o => o.Name ?? string.Empty))
        {
            List<ObjectEntry> rl = group.ToList();
            List<ObjectEntry> al = arcs.Where(a => (a.Name ?? string.Empty) == group.Key).ToList();

            if (rl.Count == 1 && al.Count == 1)
            {
                pairs.Add((rl[0], al[0], "name"));
                usedA.Add(al[0]);
                pairedR.Add(rl[0]);
                continue;
            }

            foreach (ObjectEntry r in rl)
            {
                ObjectEntry a = al.FirstOrDefault(x => !usedA.Contains(x) && x.Parts.Count == r.Parts.Count && ShapeSignature(x) == ShapeSignature(r));
                if (a != null)
                {
                    pairs.Add((r, a, "name, part count and shape"));
                    usedA.Add(a);
                    pairedR.Add(r);
                }
            }
            foreach (ObjectEntry r in rl.Where(x => !pairedR.Contains(x)).ToList())
            {
                ObjectEntry a = al.FirstOrDefault(x => !usedA.Contains(x) && x.Parts.Count == r.Parts.Count);
                if (a != null)
                {
                    pairs.Add((r, a, "name and part count"));
                    usedA.Add(a);
                    pairedR.Add(r);
                }
            }
            List<ObjectEntry> restR = rl.Where(x => !pairedR.Contains(x)).ToList();
            List<ObjectEntry> restA = al.Where(x => !usedA.Contains(x)).ToList();
            if (restR.Count == 1 && restA.Count == 1)
            {
                pairs.Add((restR[0], restA[0], "name"));
                usedA.Add(restA[0]);
                pairedR.Add(restR[0]);
            }
        }

        foreach (ObjectEntry r in refs.Where(x => !pairedR.Contains(x)))
            result.OnlyInReference.Add(r.Name);
        foreach (ObjectEntry a in arcs.Where(x => !usedA.Contains(x)))
            result.OnlyInArchive.Add(a.Name);
        return pairs;
    }

    // ------------------------------------------------------------------ parts of two paired objects

    private static void ComparePartsOf(ObjectEntry r, ObjectEntry a, ObjectComparison c)
    {
        List<(PartEntry r, PartEntry a)> matched = MatchParts(r.Parts, a.Parts, out List<PartEntry> onlyR, out List<PartEntry> onlyA);

        foreach (var (rp, ap) in matched)
        {
            bool changed = rp.LinkNum != ap.LinkNum || rp.FileOrder != ap.FileOrder;
            c.Parts.Add(new PartLink
            {
                Name = rp.Name, ReferenceLink = rp.LinkNum, ArchiveLink = ap.LinkNum,
                ReferenceFileOrder = rp.FileOrder, ArchiveFileOrder = ap.FileOrder, Changed = changed
            });
        }
        foreach (PartEntry p in onlyR)
            c.Parts.Add(new PartLink { Name = p.Name, ReferenceLink = p.LinkNum, ReferenceFileOrder = p.FileOrder, Changed = true });
        foreach (PartEntry p in onlyA)
            c.Parts.Add(new PartLink { Name = p.Name, ArchiveLink = p.LinkNum, ArchiveFileOrder = p.FileOrder, Changed = true });
        c.Parts = c.Parts.OrderBy(p => p.ReferenceFileOrder ?? int.MaxValue).ThenBy(p => p.ArchiveFileOrder ?? int.MaxValue).ToList();

        foreach (var (rp, ap) in matched.OrderBy(m => m.r.FileOrder))
        {
            if (rp.LinkNum != ap.LinkNum)
                Add(c, "link order", rp.Name, "link number", rp.LinkNum.ToString(CultureInfo.InvariantCulture), ap.LinkNum.ToString(CultureInfo.InvariantCulture));
            if (rp.FileOrder != ap.FileOrder)
                Add(c, "link order", rp.Name, "file position", rp.FileOrder.ToString(CultureInfo.InvariantCulture), ap.FileOrder.ToString(CultureInfo.InvariantCulture));
            DiffMasks(c, rp.Name, "", rp.Masks, ap.Masks);
            DiffWho(c, rp.Name, "", rp.CreatorId, ap.CreatorId, rp.OwnerId, ap.OwnerId, rp.LastOwnerId, ap.LastOwnerId, rp.GroupId, ap.GroupId);
            DiffItems(c, rp, ap);
        }
        foreach (PartEntry p in onlyR)
            Add(c, "parts", p.Name, "only in reference", "present", "absent");
        foreach (PartEntry p in onlyA)
            Add(c, "parts", p.Name, "only in archive", "absent", "present");
    }

    private static List<(PartEntry, PartEntry)> MatchParts(List<PartEntry> rs, List<PartEntry> arcs, out List<PartEntry> onlyR, out List<PartEntry> onlyA)
    {
        List<(PartEntry, PartEntry)> matched = new List<(PartEntry, PartEntry)>();
        List<PartEntry> restR = new List<PartEntry>(rs);
        List<PartEntry> restA = new List<PartEntry>(arcs);

        // the same part id: the archive was changed in place
        foreach (PartEntry r in rs.ToList())
        {
            if (r.PartId == UUID.Zero) continue;
            PartEntry a = restA.FirstOrDefault(x => x.PartId == r.PartId);
            if (a == null) continue;
            matched.Add((r, a));
            restR.Remove(r);
            restA.Remove(a);
        }
        // the same name and shape, in file order: the ids changed (a load gives parts new ids)
        foreach (PartEntry r in restR.ToList())
        {
            PartEntry a = restA.FirstOrDefault(x => x.Name == r.Name && x.ShapeKey == r.ShapeKey);
            if (a == null) continue;
            matched.Add((r, a));
            restR.Remove(r);
            restA.Remove(a);
        }
        // a name that is left once each: the shape changed
        foreach (PartEntry r in restR.ToList())
        {
            if (restR.Count(x => x.Name == r.Name) != 1) continue;
            List<PartEntry> same = restA.Where(x => x.Name == r.Name).ToList();
            if (same.Count != 1) continue;
            matched.Add((r, same[0]));
            restR.Remove(r);
            restA.Remove(same[0]);
        }
        onlyR = restR;
        onlyA = restA;
        return matched;
    }

    private static void Add(ObjectComparison c, string area, string part, string field, string reference, string archive) =>
        c.Differences.Add(new Difference { Area = area, Part = part, Field = field, Reference = reference, Archive = archive });

    private static string MaskLabel(string name) => name == "next" ? "next owner" : name;

    private static void DiffMasks(ObjectComparison c, string part, string prefix, MaskSet r, MaskSet a)
    {
        foreach (string name in MaskSet.Names)
        {
            uint? rv = r.Get(name), av = a.Get(name);
            if (rv == av)
                continue;
            string rl = MaskSet.Letters(rv), al = MaskSet.Letters(av);
            if (rl == al)
            {
                rl += " (" + Fmt.Hex(rv) + ")";
                al += " (" + Fmt.Hex(av) + ")";
            }
            Add(c, "permissions", part, prefix + MaskLabel(name) + " mask", rl, al);
        }
    }

    private static void DiffWho(ObjectComparison c, string part, string prefix, UUID rCreator, UUID aCreator, UUID rOwner, UUID aOwner, UUID rLast, UUID aLast, UUID rGroup, UUID aGroup)
    {
        void One(string field, UUID rv, UUID av)
        {
            if (rv != av)
                Add(c, "who", part, prefix + field, rv == UUID.Zero ? "none" : rv.ToString(), av == UUID.Zero ? "none" : av.ToString());
        }
        One("creator", rCreator, aCreator);
        One("owner", rOwner, aOwner);
        One("last owner", rLast, aLast);
        One("group", rGroup, aGroup);
    }

    private static void DiffItems(ObjectComparison c, PartEntry r, PartEntry a)
    {
        List<ItemEntry> restA = new List<ItemEntry>(a.Items);
        foreach (ItemEntry ri in r.Items)
        {
            ItemEntry ai = restA.FirstOrDefault(x => x.Name == ri.Name && x.AssetType == ri.AssetType);
            if (ai == null)
            {
                Add(c, "items", r.Name, "item " + Fmt.Quote(ri.Name) + " only in reference", "present", "absent");
                continue;
            }
            restA.Remove(ai);
            string prefix = "item " + Fmt.Quote(ri.Name) + " ";
            DiffMasks(c, r.Name, prefix, ri.Masks, ai.Masks);
            DiffWho(c, r.Name, prefix, ri.CreatorId, ai.CreatorId, ri.OwnerId, ai.OwnerId, ri.LastOwnerId, ai.LastOwnerId, ri.GroupId, ai.GroupId);
        }
        foreach (ItemEntry ai in restA)
            Add(c, "items", r.Name, "item " + Fmt.Quote(ai.Name) + " only in archive", "absent", "present");
    }
}
