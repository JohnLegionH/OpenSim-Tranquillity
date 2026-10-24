/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;

using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Compare;

/// <summary>One thing that differs between the two archives.</summary>
public sealed class Difference
{
    /// <summary>The build (linkset) it belongs to, or "(archive entries)" for entries that are not object files.</summary>
    public string Build { get; set; }
    public string PartId { get; set; }
    public string PartName { get; set; }
    public int? LinkNum { get; set; }
    /// <summary>"entry", "part", or one of <see cref="ComparePlan.PartFields"/>.</summary>
    public string Field { get; set; }
    public string ItemId { get; set; }
    /// <summary>"changed", "added" or "removed".</summary>
    public string Kind { get; set; }
    public string Original { get; set; }
    public string Rewritten { get; set; }
    /// <summary>The one line the text report prints.</summary>
    public string Text { get; set; }
    public bool Planned { get; set; }
    /// <summary>Why a difference that the plan names still fails, or which plan line covered it.</summary>
    public string PlanNote { get; set; }
}

/// <summary>The outcome of a comparison.</summary>
public sealed class CompareResult
{
    public const string ArchiveBuild = "(archive entries)";

    public bool Pass => Unplanned.Count == 0 && UnmetPlan.Count == 0;
    public string Original { get; set; }
    public string Rewritten { get; set; }
    public int PartsOriginal { get; set; }
    public int PartsRewritten { get; set; }
    public int PartsMatched { get; set; }
    public int EntriesOriginal { get; set; }
    public int EntriesRewritten { get; set; }
    public List<Difference> Unplanned { get; set; } = new List<Difference>();
    public List<Difference> Planned { get; set; } = new List<Difference>();
    /// <summary>Plan lines that the comparison did not find, each a failure.</summary>
    public List<string> UnmetPlan { get; set; } = new List<string>();

    private const int MaxLines = 200;

    public string ToText()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(Pass ? "PASS" : "FAIL");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "original: {0} ({1} parts, {2} entries); rewritten: {3} ({4} parts, {5} entries); {6} parts matched",
            System.IO.Path.GetFileName(Original), PartsOriginal, EntriesOriginal, System.IO.Path.GetFileName(Rewritten), PartsRewritten, EntriesRewritten, PartsMatched));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} unplanned difference(s), {1} planned difference(s) matched, {2} planned change(s) not found",
            Unplanned.Count, Planned.Count, UnmetPlan.Count));

        if (Unplanned.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Unplanned differences:");
            AppendByBuild(sb, Unplanned);
        }
        if (UnmetPlan.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Planned but not found:");
            foreach (string u in UnmetPlan.Take(MaxLines))
                sb.AppendLine("  " + u);
            if (UnmetPlan.Count > MaxLines)
                sb.AppendLine("  ... " + (UnmetPlan.Count - MaxLines) + " more (see the JSON report)");
        }
        if (Planned.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Planned differences that matched:");
            AppendByBuild(sb, Planned);
        }
        return sb.ToString();
    }

    private static void AppendByBuild(StringBuilder sb, List<Difference> list)
    {
        int lines = 0;
        foreach (IGrouping<string, Difference> build in list.GroupBy(d => d.Build))
        {
            if (lines >= MaxLines)
                break;
            sb.AppendLine(build.Key);
            lines++;
            foreach (Difference d in build)
            {
                if (lines >= MaxLines)
                    break;
                sb.AppendLine("  " + d.Text);
                lines++;
            }
        }
        if (list.Count > lines)
            sb.AppendLine("  ... more lines are in the JSON report");
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        result = Pass ? "PASS" : "FAIL",
        original = Original,
        rewritten = Rewritten,
        partsOriginal = PartsOriginal,
        partsRewritten = PartsRewritten,
        partsMatched = PartsMatched,
        entriesOriginal = EntriesOriginal,
        entriesRewritten = EntriesRewritten,
        unplannedCount = Unplanned.Count,
        plannedCount = Planned.Count,
        unmetPlan = UnmetPlan,
        unplanned = Unplanned,
        planned = Planned
    }, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Compares an original OAR with a rewritten one at archive level, with no load. Parts are matched by part UUID.
/// Entries that are not object files must be byte-identical; the parts' fields are compared one by one. A difference passes
/// only when the plan names it.
/// </summary>
public sealed class ArchiveComparer
{
    private static readonly string[] CompareOrder =
    {
        "name", "description", "linkNum", "position", "groupPosition", "rotation", "scale", "shape", "textureEntry", "faces",
        "creator", "owner", "permissions", "scriptStates"
    };

    public CompareResult Compare(string originalPath, string rewrittenPath, ComparePlan plan = null)
    {
        if (string.Equals(System.IO.Path.GetFullPath(originalPath), System.IO.Path.GetFullPath(rewrittenPath), StringComparison.OrdinalIgnoreCase))
            throw new CompareException("both archives are the same file");
        return Compare(ArchiveSnapshot.Read(originalPath), ArchiveSnapshot.Read(rewrittenPath), plan);
    }

    /// <summary>
    /// Compares an archive with the one a region saved after loading it. Parts are matched by build position, link number
    /// and name (see <see cref="ArchiveSnapshot.ByPosition"/>), items by name, type and asset id, and only asset entries are
    /// compared; no plan applies. Parts that share a key (identical prims stacked on one spot) are paired in the order of
    /// their own contents.
    /// </summary>
    public CompareResult CompareByPosition(string originalPath, string rewrittenPath)
    {
        if (string.Equals(System.IO.Path.GetFullPath(originalPath), System.IO.Path.GetFullPath(rewrittenPath), StringComparison.OrdinalIgnoreCase))
            throw new CompareException("both archives are the same file");
        return CompareByPosition(ArchiveSnapshot.Read(originalPath), ArchiveSnapshot.Read(rewrittenPath));
    }

    public CompareResult CompareByPosition(ArchiveSnapshot a, ArchiveSnapshot b)
    {
        int partsA = a.Parts.Count, partsB = b.Parts.Count;
        ArchiveSnapshot ka = a.ByPosition(out _), kb = b.ByPosition(out _);
        foreach (ArchiveSnapshot k in new[] { ka, kb })
            foreach (string key in k.Entries.Keys.Where(e => !e.StartsWith("assets/", StringComparison.Ordinal)).ToList())
                k.Entries.Remove(key);
        CompareResult r = Compare(ka, kb, ComparePlan.Empty());
        r.PartsOriginal = partsA;
        r.PartsRewritten = partsB;
        return r;
    }

    public CompareResult Compare(ArchiveSnapshot a, ArchiveSnapshot b, ComparePlan plan = null)
    {
        plan ??= ComparePlan.Empty();
        foreach (PartChange c in plan.PartChanges)
            c.Met = false;

        CompareResult r = new CompareResult
        {
            Original = a.Path, Rewritten = b.Path,
            PartsOriginal = a.Parts.Count, PartsRewritten = b.Parts.Count,
            EntriesOriginal = a.Entries.Count, EntriesRewritten = b.Entries.Count
        };
        HashSet<string> usedEntryLines = new HashSet<string>(StringComparer.Ordinal);
        HashSet<Guid> usedAdded = new HashSet<Guid>(), usedRemoved = new HashSet<Guid>();

        CompareEntries(a, b, plan, r, usedEntryLines);
        CompareParts(a, b, plan, r, usedAdded, usedRemoved);
        CheckStandIns(b, plan, r);

        foreach (PartChange c in plan.PartChanges.Where(c => !c.Met))
            r.UnmetPlan.Add(ComparePlan.Describe(c));
        foreach (string s in plan.AddedEntries.Where(s => !usedEntryLines.Contains("added:" + s)))
            r.UnmetPlan.Add("added entry " + s + " is not in the rewritten archive, or is in the original too");
        foreach (string s in plan.RemovedEntries.Where(s => !usedEntryLines.Contains("removed:" + s)))
            r.UnmetPlan.Add("removed entry " + s + " is still in the rewritten archive, or was not in the original");
        foreach (string s in plan.ChangedEntries.Where(s => !usedEntryLines.Contains("changed:" + s)))
            r.UnmetPlan.Add("changed entry " + s + " has the same bytes in both archives, or is not in both");
        foreach (Guid id in plan.AddedPartIds.Where(id => !usedAdded.Contains(id)))
            r.UnmetPlan.Add("added part " + id + " is not new in the rewritten archive");
        foreach (Guid id in plan.RemovedPartIds.Where(id => !usedRemoved.Contains(id)))
            r.UnmetPlan.Add("removed part " + id + " is still in the rewritten archive, or was not in the original");
        return r;
    }

    /// <summary>
    /// A stand-in mesh id (see StandInIds) is never a real asset. The plan, the rewritten archive's entries and its parts'
    /// shapes must not hold one, whatever the plan allows; this is a failure, not a difference a plan can cover.
    /// </summary>
    private static void CheckStandIns(ArchiveSnapshot b, ComparePlan plan, CompareResult r)
    {
        void Fail(string where, string text) => r.Unplanned.Add(new Difference
        {
            Build = CompareResult.ArchiveBuild, Field = "standInId", Kind = "standIn", PartName = where,
            Text = where + ": holds a stand-in mesh id (" + StandInIds.Prefix + "...); the mesh was never really added" + text
        });

        foreach (string s in plan.AddedEntries.Concat(plan.ChangedEntries).Concat(plan.RemovedEntries))
            if (StandInIds.Is(s))
                Fail("plan entry " + s, string.Empty);
        foreach (string entry in b.Entries.Keys)
            if (StandInIds.Is(entry))
                Fail("entry " + entry, string.Empty);
        foreach (PartSnapshot p in b.Parts.Values)
            foreach (KeyValuePair<string, SortedDictionary<string, string>> g in p.Groups)
                foreach (KeyValuePair<string, string> v in g.Value)
                    if (StandInIds.Is(v.Value))
                        Fail(PartTag(p) + " " + g.Key + "/" + v.Key, string.Empty);
    }

    private static void CompareEntries(ArchiveSnapshot a, ArchiveSnapshot b, ComparePlan plan, CompareResult r, HashSet<string> used)
    {
        foreach (KeyValuePair<string, string> e in a.Entries)
        {
            if (!b.Entries.TryGetValue(e.Key, out string other))
                Add(r, plan.RemovedEntries, "removed", e.Key, used, "removed from the rewritten archive", e.Value, null);
            else if (!string.Equals(e.Value, other, StringComparison.Ordinal))
                Add(r, plan.ChangedEntries, "changed", e.Key, used, "bytes differ (" + Short(e.Value) + " -> " + Short(other) + ")", e.Value, other);
        }
        foreach (KeyValuePair<string, string> e in b.Entries)
            if (!a.Entries.ContainsKey(e.Key))
                Add(r, plan.AddedEntries, "added", e.Key, used, "added, not in the original", null, e.Value);
    }

    private static void Add(CompareResult r, List<string> planList, string kind, string entry, HashSet<string> used, string what, string was, string now)
    {
        string line = ComparePlan.Match(planList, entry);
        Difference d = new Difference
        {
            Build = CompareResult.ArchiveBuild, Field = "entry", Kind = kind, PartName = entry, Original = was, Rewritten = now,
            Planned = line != null, Text = entry + ": " + what + (line == null ? string.Empty : " (planned)")
        };
        if (line != null)
        {
            used.Add(kind + ":" + line);
            r.Planned.Add(d);
        }
        else
            r.Unplanned.Add(d);
    }

    private static void CompareParts(ArchiveSnapshot a, ArchiveSnapshot b, ComparePlan plan, CompareResult r, HashSet<Guid> usedAdded, HashSet<Guid> usedRemoved)
    {
        foreach (PartSnapshot pa in a.Parts.Values)
        {
            if (!b.Parts.TryGetValue(pa.Id, out PartSnapshot pb))
            {
                bool planned = plan.RemovedPartIds.Contains(pa.Id);
                if (planned)
                    usedRemoved.Add(pa.Id);
                Record(r, planned, new Difference
                {
                    Build = pa.BuildLabel, PartId = pa.Id.ToString(), PartName = pa.Name, LinkNum = pa.LinkNum, Field = "part", Kind = "removed",
                    Text = PartTag(pa) + ": part is missing from the rewritten archive" + (planned ? " (planned)" : string.Empty)
                });
                continue;
            }
            r.PartsMatched++;
            ComparePart(pa, pb, plan, r);
        }
        foreach (PartSnapshot pb in b.Parts.Values)
            if (!a.Parts.ContainsKey(pb.Id))
            {
                bool planned = plan.AddedPartIds.Contains(pb.Id);
                if (planned)
                    usedAdded.Add(pb.Id);
                Record(r, planned, new Difference
                {
                    Build = pb.BuildLabel, PartId = pb.Id.ToString(), PartName = pb.Name, LinkNum = pb.LinkNum, Field = "part", Kind = "added",
                    Text = PartTag(pb) + ": part is new in the rewritten archive" + (planned ? " (planned)" : string.Empty)
                });
            }
    }

    private static void Record(CompareResult r, bool planned, Difference d)
    {
        d.Planned = planned;
        (planned ? r.Planned : r.Unplanned).Add(d);
    }

    private static string PartTag(PartSnapshot p) =>
        "link " + p.LinkNum.ToString(CultureInfo.InvariantCulture) + " '" + Clip(p.Name, 40) + "' [" + p.Id.ToString("N").Substring(0, 8) + "]";

    private static string Clip(string s, int max) =>
        s == null ? string.Empty : s.Length <= max ? s : s.Substring(0, max) + "...";

    private static string Short(string hash) => hash == null ? "-" : hash.Length > 8 ? hash.Substring(0, 8) : hash;

    private static void ComparePart(PartSnapshot pa, PartSnapshot pb, ComparePlan plan, CompareResult r)
    {
        List<PartChange> mine = plan.PartChanges.Where(c => c.PartId == pa.Id).ToList();
        string build = pa.BuildLabel;

        foreach (string group in CompareOrder)
        {
            pa.Groups.TryGetValue(group, out SortedDictionary<string, string> ga);
            pb.Groups.TryGetValue(group, out SortedDictionary<string, string> gb);
            if (ga == null && gb == null)
                continue;
            ga ??= new SortedDictionary<string, string>(StringComparer.Ordinal);
            gb ??= new SortedDictionary<string, string>(StringComparer.Ordinal);

            List<string> changed = new List<string>();
            foreach (string key in ga.Keys.Union(gb.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                ga.TryGetValue(key, out string va);
                gb.TryGetValue(key, out string vb);
                if (!string.Equals(va, vb, StringComparison.Ordinal))
                    changed.Add(key.Length == 0
                        ? "'" + Clip(va ?? "(absent)", 40) + "' -> '" + Clip(vb ?? "(absent)", 40) + "'"
                        : key + " " + Clip(va ?? "(absent)", 24) + " -> " + Clip(vb ?? "(absent)", 24));
            }
            if (changed.Count == 0)
                continue;

            string detail = changed.Count <= 4 ? string.Join(", ", changed) : string.Join(", ", changed.Take(4)) + " (+" + (changed.Count - 4) + " more)";
            Difference d = new Difference
            {
                Build = build, PartId = pa.Id.ToString(), PartName = pa.Name, LinkNum = pa.LinkNum, Field = group, Kind = "changed",
                Original = Join(ga), Rewritten = Join(gb), Text = PartTag(pa) + ": " + group + " " + detail
            };
            Judge(d, group, pa, pb, mine);
            Record(r, d.Planned, d);
        }

        CompareItems(pa, pb, mine, r);

        // A planned mesh shape also states the face count; check it even when the count did not change.
        foreach (PartChange c in mine.Where(c => c.Field == "shape" && c.Met))
        {
            string faces = pb.Single("faces");
            if (faces != c.Faces.Value.ToString(CultureInfo.InvariantCulture) && !r.Unplanned.Any(u => u.PartId == pa.Id.ToString() && u.Field == "faces"))
                Record(r, false, new Difference
                {
                    Build = build, PartId = pa.Id.ToString(), PartName = pa.Name, LinkNum = pa.LinkNum, Field = "faces", Kind = "changed",
                    Original = pa.Single("faces"), Rewritten = faces,
                    Text = PartTag(pa) + ": faces planned " + c.Faces + ", found " + faces,
                    PlanNote = "the plan states " + c.Faces + " faces"
                });
        }
    }

    private static string Join(SortedDictionary<string, string> g) =>
        g.Count == 1 && g.ContainsKey(string.Empty) ? g[string.Empty] : string.Join("; ", g.Select(kv => kv.Key + "=" + kv.Value));

    private static bool IsMesh(PartSnapshot p) =>
        p.Groups.TryGetValue("shape", out SortedDictionary<string, string> s)
        && s.TryGetValue("SculptEntry", out string entry) && string.Equals(entry, "true", StringComparison.OrdinalIgnoreCase)
        && s.TryGetValue("SculptType", out string type) && type == "5";

    /// <summary>Decides whether the plan covers a difference; marks the plan line it used.</summary>
    private static void Judge(Difference d, string group, PartSnapshot pa, PartSnapshot pb, List<PartChange> mine)
    {
        // A shape change to mesh covers the shape fields and the face count, when the plan states it.
        PartChange shape = mine.FirstOrDefault(c => c.Field == "shape");
        if (shape != null && (group == "shape" || group == "faces"))
        {
            if (!IsMesh(pb))
            {
                d.PlanNote = "the plan says the shape becomes a mesh; the rewritten part is not a mesh";
                d.Text += " (" + d.PlanNote + ")";
            }
            else if (group == "faces" && pb.Single("faces") != shape.Faces.Value.ToString(CultureInfo.InvariantCulture))
            {
                d.PlanNote = "the plan states " + shape.Faces + " faces";
                d.Text += " (" + d.PlanNote + ")";
            }
            else
            {
                if (group == "shape")
                    shape.Met = true;
                d.Planned = true;
                d.Text += " (planned)";
            }
            return;
        }

        PartChange c = mine.FirstOrDefault(x => x.Field == group);
        if (c == null)
            return;
        string now = pb.Single(group);
        if (c.To != null && now != null && !string.Equals(c.To.Trim(), now, StringComparison.Ordinal))
        {
            d.PlanNote = "the plan says '" + Clip(c.To, 40) + "'";
            d.Text += " (" + d.PlanNote + ")";
            return;
        }
        c.Met = true;
        d.Planned = true;
        d.Text += " (planned)";
    }

    private static void CompareItems(PartSnapshot pa, PartSnapshot pb, List<PartChange> mine, CompareResult r)
    {
        foreach (ItemSnapshot ia in pa.Items.Values)
        {
            if (!pb.Items.TryGetValue(ia.Id, out ItemSnapshot ib))
                ItemDiff(pa, ia, "removed", "item '" + Clip(ia.Name, 30) + "' dropped", mine, r);
            else
            {
                List<string> changed = new List<string>();
                foreach (string key in ia.Fields.Keys.Union(ib.Fields.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    ia.Fields.TryGetValue(key, out string va);
                    ib.Fields.TryGetValue(key, out string vb);
                    if (!string.Equals(va, vb, StringComparison.Ordinal))
                        changed.Add(key + " " + Clip(va ?? "(absent)", 24) + " -> " + Clip(vb ?? "(absent)", 24));
                }
                if (changed.Count > 0)
                    ItemDiff(pa, ia, "changed", "item '" + Clip(ia.Name, 30) + "' changed: " + string.Join(", ", changed.Take(4)) + (changed.Count > 4 ? " (+" + (changed.Count - 4) + " more)" : string.Empty), mine, r);
            }
        }
        foreach (ItemSnapshot ib in pb.Items.Values)
            if (!pa.Items.ContainsKey(ib.Id))
                ItemDiff(pa, ib, "added", "item '" + Clip(ib.Name, 30) + "' added", mine, r);
    }

    private static void ItemDiff(PartSnapshot p, ItemSnapshot item, string kind, string what, List<PartChange> mine, CompareResult r)
    {
        PartChange c = mine.FirstOrDefault(x => x.Field == "contents" && (x.ItemId == null || x.ItemId == item.Id));
        if (c != null)
            c.Met = true;
        Record(r, c != null, new Difference
        {
            Build = p.BuildLabel, PartId = p.Id.ToString(), PartName = p.Name, LinkNum = p.LinkNum, Field = "contents", ItemId = item.Id.ToString(),
            Kind = kind, Text = PartTag(p) + ": contents " + what + (c != null ? " (planned)" : string.Empty)
        });
    }
}
