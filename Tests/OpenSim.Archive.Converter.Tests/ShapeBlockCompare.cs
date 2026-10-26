/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>What comparing the extra-parameter blocks, material overrides and media entries of two parts (or of two archives, part by part) found.</summary>
public sealed class ShapeBlockReport
{
    /// <summary>Differences in meaning, one line each, naming the part, the block or face and the field.</summary>
    public List<string> Differences { get; } = new List<string>();
    /// <summary>Things that differ in form only (block order), one line each.</summary>
    public List<string> Notes { get; } = new List<string>();
    public int Parts, Blocks, PbrFaces, Overrides, MediaEntries;

    public string ToText() => "parts " + Parts + ", extra-parameter blocks " + Blocks + ", PBR faces " + PbrFaces + ", overrides " + Overrides + ", media entries " + MediaEntries
        + ", differences " + Differences.Count + ", notes " + Notes.Count + Environment.NewLine
        + string.Concat(Differences.Select(d => "  DIFFERENT: " + d + Environment.NewLine)) + string.Concat(Notes.Select(n => "  note: " + n + Environment.NewLine));
}

/// <summary>
/// Compares what a region keeps of a part's shape data across a save: the extra-parameter blocks (by type, and field by field for the sculpt
/// block and the PBR assignments), the material overrides (the text of each face), and the media entries (each field of each face's entry).
/// An empty place in a media list and a default entry are the same thing, as the region reads them.
/// </summary>
public static class ShapeBlockCompare
{
    private const int SculptBlock = 0x30, RenderMaterialBlock = 0x80, FaceLimit = 64;

    private static string Type(int type) => "0x" + type.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>The blocks of an extra-parameter list in the order stored: a count, then for each a 16-bit type, a 32-bit length and the bytes. Null when the list is cut short.</summary>
    private static List<(int Type, byte[] Payload)> Blocks(byte[] extra)
    {
        List<(int, byte[])> blocks = new List<(int, byte[])>();
        if (extra == null || extra.Length == 0)
            return blocks;
        int count = extra[0], at = 1;
        for (int i = 0; i < count; i++)
        {
            if (at + 6 > extra.Length)
                return null;
            int type = BitConverter.ToUInt16(extra, at);
            long length = BitConverter.ToUInt32(extra, at + 2);
            if (at + 6 + length > extra.Length)
                return null;
            blocks.Add((type, extra.AsSpan(at + 6, (int)length).ToArray()));
            at += (int)(6 + length);
        }
        return blocks;
    }

    private static Dictionary<int, Guid> Materials(byte[] payload)
    {
        Dictionary<int, Guid> faces = new Dictionary<int, Guid>();
        int count = payload.Length >= 1 ? payload[0] : 0;
        for (int i = 0; i < count && 1 + 17 * (i + 1) <= payload.Length; i++)
            faces[payload[1 + 17 * i]] = new UUID(payload, 2 + 17 * i).Guid;
        return faces;
    }

    private static Dictionary<int, string> Overrides(byte[] data)
    {
        Dictionary<int, string> faces = new Dictionary<int, string>();
        if (data == null || data.Length == 0)
            return faces;
        int count = data[0], at = 1;
        for (int i = 0; i < count && at + 3 <= data.Length; i++)
        {
            int face = data[at], length = data[at + 1] | (data[at + 2] << 8);
            at += 3;
            if (at + length > data.Length)
                break;
            faces[face] = Encoding.UTF8.GetString(data, at, length);
            at += length;
        }
        return faces;
    }

    private static readonly string DefaultMedia = OSDParser.SerializeJsonString(new MediaEntry().GetOSD());

    /// <summary>The fields of a media entry by name, as text; empty for no entry or a default one.</summary>
    private static SortedDictionary<string, string> Fields(MediaEntry entry)
    {
        SortedDictionary<string, string> fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (entry == null || OSDParser.SerializeJsonString(entry.GetOSD()) == DefaultMedia)
            return fields;
        if (entry.GetOSD() is OSDMap map)
            foreach (KeyValuePair<string, OSD> kv in map)
                fields[kv.Key] = OSDParser.SerializeJsonString(kv.Value);
        return fields;
    }

    public static void ComparePart(string name, ParsedPart before, ParsedPart after, ShapeBlockReport report)
    {
        report.Parts++;
        List<(int Type, byte[] Payload)> a = Blocks(before.ExtraParams), b = Blocks(after.ExtraParams);
        if (a == null || b == null)
            report.Differences.Add(name + ": the extra parameters of the " + (a == null ? "first" : "second") + " part cannot be read");
        else
        {
            report.Blocks += a.Count;
            foreach ((int type, byte[] payload) in a)
            {
                int index = b.FindIndex(x => x.Type == type);
                if (index < 0)
                {
                    report.Differences.Add(name + ": block " + Type(type) + " is missing after");
                    continue;
                }
                byte[] other = b[index].Payload;
                if (type == SculptBlock)
                {
                    if (payload.Length < 17 || other.Length < 17)
                        report.Differences.Add(name + ": sculpt block too short");
                    else
                    {
                        if (new UUID(payload, 0) != new UUID(other, 0))
                            report.Differences.Add(name + ": sculpt texture " + new UUID(payload, 0) + " -> " + new UUID(other, 0));
                        if (payload[16] != other[16])
                            report.Differences.Add(name + ": sculpt type " + payload[16] + " -> " + other[16]);
                    }
                }
                else if (type == RenderMaterialBlock)
                {
                    Dictionary<int, Guid> x = Materials(payload), y = Materials(other);
                    report.PbrFaces += x.Count;
                    foreach (KeyValuePair<int, Guid> kv in x)
                        if (!y.TryGetValue(kv.Key, out Guid id))
                            report.Differences.Add(name + ": face " + kv.Key + " material " + kv.Value + " is missing after");
                        else if (id != kv.Value)
                            report.Differences.Add(name + ": face " + kv.Key + " material " + kv.Value + " -> " + id);
                    foreach (int face in y.Keys.Where(f => !x.ContainsKey(f)))
                        report.Differences.Add(name + ": face " + face + " material " + y[face] + " appears after");
                }
                else if (!payload.AsSpan().SequenceEqual(other))
                    report.Differences.Add(name + ": block " + Type(type) + " bytes differ (" + payload.Length + " -> " + other.Length + ")");
            }
            foreach ((int type, _) in b.Where(x => a.All(y => y.Type != x.Type)))
                report.Differences.Add(name + ": block " + Type(type) + " appears after");
            if (a.Select(x => x.Type).OrderBy(t => t).SequenceEqual(b.Select(x => x.Type).OrderBy(t => t)) && !a.Select(x => x.Type).SequenceEqual(b.Select(x => x.Type)))
                report.Notes.Add(name + ": the blocks are in another order (" + string.Join(",", a.Select(x => Type(x.Type))) + " -> " + string.Join(",", b.Select(x => Type(x.Type))) + ")");
        }

        Dictionary<int, string> oa = Overrides(before.MaterialOverrides), ob = Overrides(after.MaterialOverrides);
        report.Overrides += oa.Count;
        foreach (KeyValuePair<int, string> kv in oa)
            if (!ob.TryGetValue(kv.Key, out string text))
                report.Differences.Add(name + ": override of face " + kv.Key + " is missing after");
            else if (text != kv.Value)
                report.Differences.Add(name + ": override of face " + kv.Key + " text " + kv.Value + " -> " + text);
        foreach (int face in ob.Keys.Where(f => !oa.ContainsKey(f)))
            report.Differences.Add(name + ": override of face " + face + " appears after");

        List<MediaEntry> ma = MediaFaceData.Read(before, FaceLimit, out bool unreadableA), mb = MediaFaceData.Read(after, FaceLimit, out bool unreadableB);
        if (unreadableA || unreadableB)
            report.Differences.Add(name + ": the media list of the " + (unreadableA ? "first" : "second") + " part cannot be read");
        else
            for (int face = 0; face < Math.Max(ma.Count, mb.Count); face++)
            {
                SortedDictionary<string, string> x = Fields(face < ma.Count ? ma[face] : null), y = Fields(face < mb.Count ? mb[face] : null);
                if (x.Count > 0)
                    report.MediaEntries++;
                if (x.Count > 0 && y.Count == 0)
                    report.Differences.Add(name + ": face " + face + " media entry is missing after");
                else if (x.Count == 0 && y.Count > 0)
                    report.Differences.Add(name + ": face " + face + " media entry appears after");
                else
                    foreach (string key in x.Keys.Union(y.Keys).OrderBy(k => k, StringComparer.Ordinal))
                    {
                        x.TryGetValue(key, out string p);
                        y.TryGetValue(key, out string q);
                        if (p != q)
                            report.Differences.Add(name + ": face " + face + " media field " + key + " " + (p ?? "(none)") + " -> " + (q ?? "(none)"));
                    }
            }
    }

    /// <summary>As <see cref="CompareArchives(string, string)"/>, matching parts by name, link number and position when asked (not yet written).</summary>
    public static ShapeBlockReport CompareArchives(string beforePath, string afterPath, bool matchByPosition)
    {
        if (!matchByPosition)
            return CompareArchives(beforePath, afterPath);
        ArchiveMergePlanner.Plan(beforePath, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> before);
        ArchiveMergePlanner.Plan(afterPath, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> after);
        // a region gives every part a new id on load, so a part is found again by its name, link number, the position of its object and its own offset in it
        string Key(ParsedPart p) => string.Join("|", p.Part?.Name, p.Part?.LinkNum, (p.Part?.X ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
            (p.Part?.Y ?? 0).ToString("0.00", CultureInfo.InvariantCulture), (p.Part?.Z ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
            p.OffsetPosition.X.ToString("0.00", CultureInfo.InvariantCulture), p.OffsetPosition.Y.ToString("0.00", CultureInfo.InvariantCulture), p.OffsetPosition.Z.ToString("0.00", CultureInfo.InvariantCulture));
        ILookup<string, ParsedPart> afterByKey = after.Values.ToLookup(Key);
        ShapeBlockReport report = new ShapeBlockReport();
        HashSet<string> seen = new HashSet<string>();
        foreach (IGrouping<string, ParsedPart> group in before.Values.GroupBy(Key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            List<ParsedPart> others = afterByKey[group.Key].ToList();
            seen.Add(group.Key);
            if (group.Count() != 1 || others.Count > 1)
            {
                report.Differences.Add(group.Key + ": " + group.Count() + " part(s) before and " + others.Count + " after share this name, link number and position, so they cannot be paired");
                continue;
            }
            if (others.Count == 0)
                report.Differences.Add(group.Key + ": the part is missing after");
            else
                ComparePart(group.Key, group.First(), others[0], report);
        }
        foreach (string key in after.Values.Select(Key).Distinct().Where(k => !seen.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            report.Differences.Add(key + ": the part appears after");
        return report;
    }

    /// <summary>Every part the two archives share by id; a part in one only is a difference.</summary>
    public static ShapeBlockReport CompareArchives(string beforePath, string afterPath)
    {
        ArchiveMergePlanner.Plan(beforePath, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> before);
        ArchiveMergePlanner.Plan(afterPath, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> after);
        ShapeBlockReport report = new ShapeBlockReport();
        foreach (KeyValuePair<Guid, ParsedPart> kv in before.OrderBy(k => k.Key))
        {
            string name = (kv.Value.Part?.Name ?? "part") + " " + kv.Key;
            if (!after.TryGetValue(kv.Key, out ParsedPart other))
                report.Differences.Add(name + ": the part is missing after");
            else
                ComparePart(name, kv.Value, other, report);
        }
        foreach (Guid id in after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k))
            report.Differences.Add((after[id].Part?.Name ?? "part") + " " + id + ": the part appears after");
        return report;
    }
}
