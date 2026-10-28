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
using OpenSim.Archive.Inspection;
using OpenSim.Archive.ObjectInspect;
using OpenSim.Archive.Rewrite;
using OpenSim.Archive.ScriptScan;

namespace OpenSim.Archive.Converter.Fix;

/// <summary>Where one part stood in the archive and where it stands in the output.</summary>
public sealed class PartRelink
{
    public string Name { get; set; }
    public UUID PartId { get; set; }
    public int LinkBefore { get; set; }
    public int LinkAfter { get; set; }
    public int FilePositionBefore { get; set; }
    public int FilePositionAfter { get; set; }
    /// <summary>False for a part the reference does not hold: it keeps its order, after the matched parts.</summary>
    public bool Matched { get; set; }
    public bool Changed => LinkBefore != LinkAfter || FilePositionBefore != FilePositionAfter;
}

public sealed class ObjectRelink
{
    public string Name { get; set; }
    public string Entry { get; set; }
    /// <summary>How the object was paired with the reference's: "name", "name and part count", "name, part count and shape".</summary>
    public string Matched { get; set; }
    public List<PartRelink> Parts { get; } = new List<PartRelink>();
    public bool Changed => Parts.Any(p => p.Changed);
}

/// <summary>A script whose use of link numbers is affected because the parts were renumbered.</summary>
public sealed class ScriptLinkChange
{
    public string Object { get; set; }
    public string Part { get; set; }
    public string Script { get; set; }
    public List<string> Uses { get; } = new List<string>();
}

public sealed class RelinkResult
{
    public string Reference { get; set; }
    public List<ObjectRelink> Objects { get; } = new List<ObjectRelink>();
    /// <summary>Objects of the archive the reference does not hold (or that two objects of one name made ambiguous); left unchanged.</summary>
    public List<string> UnmatchedObjects { get; } = new List<string>();
    /// <summary>Parts of a matched object the reference's object does not hold; they keep their order, after the matched parts.</summary>
    public List<string> UnmatchedParts { get; } = new List<string>();
    public List<ScriptLinkChange> Scripts { get; } = new List<ScriptLinkChange>();
    public int EntriesRead { get; set; }
    public int EntriesChanged { get; set; }
    public int ObjectsInArchive { get; set; }
    public int ObjectsChanged => Objects.Count(o => o.Changed);

    public string ToText()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("oar-convert --relink-from\n");
        sb.Append("reference: ").Append(Reference).Append('\n');
        sb.Append("objects in the archive: ").Append(ObjectsInArchive).Append(", found in the reference: ").Append(Objects.Count)
          .Append(", relinked: ").Append(ObjectsChanged).Append('\n');
        sb.Append("entries read: ").Append(EntriesRead).Append(", entries rewritten: ").Append(EntriesChanged).Append('\n');
        sb.Append("objects not in the reference (left unchanged): ").Append(UnmatchedObjects.Count).Append('\n');
        sb.Append("parts not in the reference (kept in their order, after the matched parts): ").Append(UnmatchedParts.Count).Append('\n');
        sb.Append("scripts whose link number use changes: ").Append(Scripts.Count).Append('\n');
        if (ObjectsChanged == 0)
            sb.Append("\nno object needed relinking; the output is a copy of the input\n");

        foreach (ObjectRelink o in Objects.Where(x => x.Changed))
        {
            sb.Append("\nOBJECT \"").Append(o.Name).Append("\" (matched by ").Append(o.Matched).Append(") [").Append(o.Entry).Append("]\n");
            foreach (PartRelink p in o.Parts.Where(x => x.Changed))
                sb.Append("  \"").Append(p.Name).Append("\": link ").Append(p.LinkBefore).Append(" -> ").Append(p.LinkAfter)
                  .Append(", file position ").Append(p.FilePositionBefore).Append(" -> ").Append(p.FilePositionAfter).Append('\n');
        }

        if (Scripts.Count > 0)
        {
            sb.Append("\nSCRIPTS WHOSE LINK NUMBER USE CHANGES\n");
            foreach (ScriptLinkChange s in Scripts)
            {
                sb.Append("  object \"").Append(s.Object).Append("\", part \"").Append(s.Part).Append("\", script \"").Append(s.Script).Append("\"\n");
                foreach (string u in s.Uses)
                    sb.Append("      ").Append(u).Append('\n');
            }
        }

        if (UnmatchedObjects.Count > 0 || UnmatchedParts.Count > 0)
        {
            sb.Append("\nNOT MATCHED\n");
            foreach (string u in UnmatchedObjects)
                sb.Append("  ").Append(u).Append('\n');
            foreach (string u in UnmatchedParts)
                sb.Append("  ").Append(u).Append('\n');
        }
        return sb.ToString();
    }

    public string ToJson()
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("tool", "oar-convert --relink-from");
            w.WriteString("reference", Reference);
            w.WriteNumber("objectsInArchive", ObjectsInArchive);
            w.WriteNumber("objectsFound", Objects.Count);
            w.WriteNumber("objectsChanged", ObjectsChanged);
            w.WriteNumber("entriesRead", EntriesRead);
            w.WriteNumber("entriesChanged", EntriesChanged);
            w.WriteStartArray("objects");
            foreach (ObjectRelink o in Objects)
            {
                w.WriteStartObject();
                w.WriteString("name", o.Name);
                w.WriteString("entry", o.Entry);
                w.WriteString("matched", o.Matched);
                w.WriteBoolean("changed", o.Changed);
                w.WriteStartArray("parts");
                foreach (PartRelink p in o.Parts)
                {
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    w.WriteString("id", p.PartId.ToString());
                    w.WriteNumber("linkBefore", p.LinkBefore);
                    w.WriteNumber("linkAfter", p.LinkAfter);
                    w.WriteNumber("filePositionBefore", p.FilePositionBefore);
                    w.WriteNumber("filePositionAfter", p.FilePositionAfter);
                    w.WriteBoolean("matched", p.Matched);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("unmatchedObjects");
            foreach (string u in UnmatchedObjects) w.WriteStringValue(u);
            w.WriteEndArray();
            w.WriteStartArray("unmatchedParts");
            foreach (string u in UnmatchedParts) w.WriteStringValue(u);
            w.WriteEndArray();
            w.WriteStartArray("scripts");
            foreach (ScriptLinkChange s in Scripts)
            {
                w.WriteStartObject();
                w.WriteString("object", s.Object);
                w.WriteString("part", s.Part);
                w.WriteString("script", s.Script);
                w.WriteStartArray("uses");
                foreach (string u in s.Uses) w.WriteStringValue(u);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}

/// <summary>
/// Writes a copy of an OAR or IAR in which each object found in a reference archive has the reference's link order back. Objects are paired as a
/// compare pairs them; parts are matched by name, shape and position relative to the root. The root stays the root and parts the reference does
/// not hold keep their order after the matched ones. A changed object file is edited in place: the part elements trade places and the LinkNum
/// digits change, every other byte stays.
/// </summary>
public static class LinkRestore
{
    private sealed class Plan
    {
        public ObjectEntry Archive;
        public ObjectRelink Result;
        /// <summary>Part ids by new place: the root first, then the other parts in their new order.</summary>
        public List<PartEntry> NewOrder;
        public Dictionary<Guid, int> NewLink = new Dictionary<Guid, int>();
    }

    public static RelinkResult Run(string source, string reference, string dest)
    {
        ObjectReport archive = ObjectReader.Read(source);
        if (archive.Status == InspectionStatus.NotAnArchive)
            throw new InvalidDataException("the archive '" + source + "' is not an archive: " + archive.StopReason);
        ObjectReport referenceReport = ObjectReader.Read(reference);
        if (referenceReport.Status == InspectionStatus.NotAnArchive)
            throw new InvalidDataException("the reference '" + reference + "' is not an archive: " + referenceReport.StopReason);

        RelinkResult result = new RelinkResult { Reference = reference, ObjectsInArchive = archive.Objects.Count };
        List<Plan> plans = new List<Plan>();
        var pairs = ReportCompare.PairObjects(referenceReport.Objects, archive.Objects, out _, out List<ObjectEntry> onlyInArchive);
        foreach (var (r, a, how) in pairs.OrderBy(p => p.archive.Index))
        {
            Plan plan = MakePlan(r, a, how, result);
            plans.Add(plan);
            result.Objects.Add(plan.Result);
        }
        foreach (ObjectEntry a in onlyInArchive)
            result.UnmatchedObjects.Add("object \"" + a.Name + "\" is not in the reference [" + a.Entry + "]");

        List<Plan> changed = plans.Where(p => p.Result.Changed).ToList();
        if (changed.Count > 0)
            ListScripts(source, changed, result);

        Dictionary<string, List<Plan>> byEntry = changed.GroupBy(p => p.Archive.Entry).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        TarRewrite.Run(source, dest, (header, body) =>
        {
            result.EntriesRead++;
            if (!byEntry.TryGetValue(header.Name, out List<Plan> mine))
                return null;
            result.EntriesChanged++;
            return Apply(header.Name, body, mine);
        });
        return result;
    }

    // ------------------------------------------------------------------ planning

    private static string PositionKey(PartEntry p) =>
        string.Join(",", new[] { p.Position.X, p.Position.Y, p.Position.Z }.Select(v => Math.Round(v * 1000.0).ToString(CultureInfo.InvariantCulture)));

    private static bool SameKind(PartEntry a, PartEntry b) => a.Name == b.Name && a.ShapeKey == b.ShapeKey && PositionKey(a) == PositionKey(b);

    private static Plan MakePlan(ObjectEntry reference, ObjectEntry archive, string how, RelinkResult result)
    {
        Plan plan = new Plan { Archive = archive, Result = new ObjectRelink { Name = archive.Name, Entry = archive.Entry, Matched = how } };
        PartEntry root = archive.Parts.FirstOrDefault(p => p.IsRoot) ?? archive.Parts[0];

        // the reference's order: by stored link number (an unnumbered part after the numbered ones), then by place in its file
        List<PartEntry> refOrder = reference.Parts.OrderBy(p => p.LinkNum == 0 ? int.MaxValue : p.LinkNum).ThenBy(p => p.FileOrder).ToList();
        Dictionary<PartEntry, int> rank = new Dictionary<PartEntry, int>();
        for (int i = 0; i < refOrder.Count; i++)
            rank[refOrder[i]] = i;

        List<PartEntry> unused = reference.Parts.ToList();
        Dictionary<PartEntry, int> matchedRank = new Dictionary<PartEntry, int>();
        foreach (PartEntry p in archive.Parts.Where(x => x != root).OrderBy(x => x.FileOrder))
        {
            PartEntry m = unused.FirstOrDefault(r => SameKind(p, r));
            if (m == null)
                continue;
            unused.Remove(m);
            matchedRank[p] = rank[m];
        }

        List<PartEntry> others = archive.Parts.Where(x => x != root).OrderBy(x => x.FileOrder).ToList();
        List<PartEntry> matched = others.Where(matchedRank.ContainsKey).OrderBy(x => matchedRank[x]).ToList();
        List<PartEntry> unmatched = others.Where(x => !matchedRank.ContainsKey(x)).ToList();
        foreach (PartEntry p in unmatched)
            result.UnmatchedParts.Add("object \"" + archive.Name + "\": part \"" + p.Name + "\" is not in the reference; it keeps its order after the matched parts");

        plan.NewOrder = new List<PartEntry> { root };
        plan.NewOrder.AddRange(matched);
        plan.NewOrder.AddRange(unmatched);

        bool single = archive.Parts.Count == 1;
        List<int> slots = others.Select(x => x.FileOrder).OrderBy(x => x).ToList();
        foreach (PartEntry p in archive.Parts.OrderBy(x => x.FileOrder))
        {
            int index = plan.NewOrder.IndexOf(p);
            int linkAfter = single ? p.LinkNum : index + 1;
            int positionAfter = p == root ? p.FileOrder : slots[index - 1];
            plan.NewLink[p.PartId.Guid] = linkAfter;
            plan.Result.Parts.Add(new PartRelink
            {
                Name = p.Name, PartId = p.PartId, LinkBefore = p.LinkNum, LinkAfter = linkAfter,
                FilePositionBefore = p.FileOrder, FilePositionAfter = positionAfter, Matched = p == root || matchedRank.ContainsKey(p)
            });
        }
        return plan;
    }

    // ------------------------------------------------------------------ scripts

    private static void ListScripts(string source, List<Plan> changed, RelinkResult result)
    {
        ScanResult scan = new ArchiveScriptScanner().Scan(source);
        foreach (Plan plan in changed)
        {
            PartEntry root = plan.Archive.Parts.FirstOrDefault(p => p.IsRoot) ?? plan.Archive.Parts[0];
            // the number a part answers to: the stored one, or its place in the file when none is stored (the loader numbers those by file order)
            int Before(PartEntry p) => p.LinkNum != 0 ? p.LinkNum : p.FileOrder;
            Dictionary<int, PartEntry> oldAt = new Dictionary<int, PartEntry>(), newAt = new Dictionary<int, PartEntry>();
            foreach (PartEntry p in plan.Archive.Parts)
            {
                oldAt[Before(p)] = p;
                newAt[plan.NewLink[p.PartId.Guid]] = p;
            }
            bool anyChange = plan.Archive.Parts.Any(p => Before(p) != plan.NewLink[p.PartId.Guid]);

            foreach (ScriptLinkUses links in scan.ScriptLinks.Where(l => l.Entry == plan.Archive.Entry && l.LinksetId == root.PartId.Guid))
            {
                PartEntry host = plan.Archive.Parts.FirstOrDefault(p => p.PartId.Guid == links.HostPartId);
                ScriptLinkChange change = new ScriptLinkChange { Object = plan.Archive.Name, Part = host?.Name ?? "?", Script = links.Script };
                foreach (LinkUse use in links.Uses)
                {
                    string what = null;
                    switch (use.Kind)
                    {
                        case LinkUseKind.Literal:
                            if (oldAt.TryGetValue(use.Number, out PartEntry was) && newAt.TryGetValue(use.Number, out PartEntry now) && was != now)
                                what = "link " + use.Number + " was part \"" + was.Name + "\" and is now part \"" + now.Name + "\"";
                            break;
                        case LinkUseKind.Unknown:
                            if (anyChange) what = "a link number the scan cannot work out";
                            break;
                        case LinkUseKind.Enumeration:
                            if (anyChange) what = "depends on how the parts are numbered";
                            break;
                        case LinkUseKind.LinkMessageSender:
                            if (anyChange) what = "reads the sender's link number";
                            break;
                    }
                    if (what != null)
                        change.Uses.Add("line " + use.Line + ": " + use.Function + " - " + what);
                }
                if (change.Uses.Count > 0)
                    result.Scripts.Add(change);
            }
        }
    }

    // ------------------------------------------------------------------ editing the object file

    private static byte[] Apply(string entry, byte[] body, List<Plan> plans)
    {
        ObjectXml.Decoded decoded = ObjectXml.Decode(body);
        string text = decoded.Text;
        HashSet<Guid> ids = new HashSet<Guid>(plans.SelectMany(p => p.Archive.Parts).Select(p => p.PartId.Guid));
        Dictionary<Guid, Queue<ObjectXml.PartSpans>> located = new Dictionary<Guid, Queue<ObjectXml.PartSpans>>();
        foreach (ObjectXml.PartSpans spans in ObjectXml.Scan(text, ids))
        {
            if (!located.TryGetValue(spans.Id, out Queue<ObjectXml.PartSpans> q))
                located[spans.Id] = q = new Queue<ObjectXml.PartSpans>();
            q.Enqueue(spans);
        }

        List<(int start, int end, string with)> cuts = new List<(int, int, string)>();
        foreach (Plan plan in plans)
        {
            Dictionary<PartEntry, ObjectXml.PartSpans> spans = new Dictionary<PartEntry, ObjectXml.PartSpans>();
            foreach (PartEntry p in plan.Archive.Parts)
            {
                if (!located.TryGetValue(p.PartId.Guid, out Queue<ObjectXml.PartSpans> q) || q.Count == 0)
                    throw new InvalidDataException("part \"" + p.Name + "\" of object \"" + plan.Archive.Name + "\" could not be located in '" + entry + "'");
                spans[p] = q.Dequeue();
            }

            PartEntry root = plan.NewOrder[0];
            cuts.AddRange(LinkNumEdits(text, spans[root], plan.NewLink[root.PartId.Guid]));

            // the other parts trade places: slot k (the k-th place a non-root part has in the file) takes the text of the part that belongs there now
            List<PartEntry> inFile = plan.Archive.Parts.Where(p => p != root).OrderBy(p => spans[p].Start).ToList();
            for (int k = 0; k < inFile.Count; k++)
            {
                (int slotStart, int slotEnd) = Block(text, spans[inFile[k]]);
                PartEntry owner = plan.NewOrder[k + 1];
                (int bs, int be) = Block(text, spans[owner]);
                string content = Edited(text, bs, be, LinkNumEdits(text, spans[owner], plan.NewLink[owner.PartId.Guid]));
                if (owner == inFile[k] && content == text.Substring(slotStart, slotEnd - slotStart))
                    continue;
                cuts.Add((slotStart, slotEnd, content));
            }
        }
        return cuts.Count == 0 ? null : ObjectXml.ApplyCuts(decoded, cuts, entry);
    }

    /// <summary>The text of a part as the region writes it into OtherParts: the part element, with the Part element around it when there is one.</summary>
    private static (int start, int end) Block(string text, ObjectXml.PartSpans part)
    {
        const string open = "<Part>", close = "</Part>";
        int before = part.Start, after = part.End;
        while (before > 0 && char.IsWhiteSpace(text[before - 1]))
            before--;
        while (after < text.Length && char.IsWhiteSpace(text[after]))
            after++;
        if (before >= open.Length && string.CompareOrdinal(text, before - open.Length, open, 0, open.Length) == 0
            && string.CompareOrdinal(text, after, close, 0, close.Length) == 0)
            return (before - open.Length, after + close.Length);
        return (part.Start, part.End);
    }

    private static string Edited(string text, int start, int end, List<(int start, int end, string with)> edits)
    {
        StringBuilder sb = new StringBuilder();
        int at = start;
        foreach ((int s, int e, string with) in edits.OrderBy(x => x.start))
        {
            sb.Append(text, at, s - at).Append(with);
            at = e;
        }
        return sb.Append(text, at, end - at).ToString();
    }

    /// <summary>The edits that give a part the link number: the digits of its LinkNum element, or a LinkNum element where it has none.</summary>
    private static List<(int start, int end, string with)> LinkNumEdits(string text, ObjectXml.PartSpans part, int number)
    {
        string value = number.ToString(CultureInfo.InvariantCulture);
        List<(int, int, string)> edits = new List<(int, int, string)>();
        if (!part.ByPath.TryGetValue("LinkNum", out List<ObjectXml.Span> found) || found.Count == 0)
        {
            edits.Add((part.CloseAt, part.CloseAt, "<LinkNum>" + value + "</LinkNum>"));
            return edits;
        }
        if (found.Count > 1)
            throw new InvalidDataException("a part has more than one LinkNum element");
        ObjectXml.Span span = found[0];
        if (span.SelfClosing)
            edits.Add((span.TagStart, span.TagEnd, "<LinkNum>" + value + "</LinkNum>"));
        else if (text.Substring(span.TagEnd, span.InnerEnd - span.TagEnd).Trim() != value)
            edits.Add((span.TagEnd, span.InnerEnd, value));
        return edits;
    }
}
