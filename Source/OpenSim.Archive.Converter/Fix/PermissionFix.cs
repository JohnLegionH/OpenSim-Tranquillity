/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.ObjectInspect;
using OpenSim.Archive.Rewrite;
using OpenSim.Framework.Serialization;

namespace OpenSim.Archive.Converter.Fix;

/// <summary>One mask that was changed: where it is, which mask, and its value before and after.</summary>
public sealed class MaskChange
{
    public string Entry { get; set; }
    public string Where { get; set; }
    /// <summary>"base", "owner" or "next".</summary>
    public string Mask { get; set; }
    public uint Before { get; set; }
    public uint After { get; set; }
}

/// <summary>A part, item or inventory item that was left exactly as it was because someone else created it.</summary>
public sealed class LeftAlone
{
    public string Entry { get; set; }
    public string Where { get; set; }
    public UUID Creator { get; set; }
}

public sealed class PermissionFixResult
{
    public List<UUID> Creators { get; } = new List<UUID>();
    public List<MaskChange> Changes { get; } = new List<MaskChange>();
    public List<LeftAlone> Left { get; } = new List<LeftAlone>();
    /// <summary>Masks of the named creators' parts and items that could not be changed, and why (for example a mask the file does not hold).</summary>
    public List<string> NotChanged { get; } = new List<string>();
    /// <summary>Parts, items and inventory items of the named creators whose three masks already held modify, copy and transfer.</summary>
    public int AlreadyComplete { get; set; }
    public int EntriesRead { get; set; }
    public int EntriesChanged { get; set; }

    public string ToText()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("oar-convert --fix-perms\n");
        sb.Append("creators: ").Append(string.Join(", ", Creators)).Append('\n');
        sb.Append("entries read: ").Append(EntriesRead).Append(", entries rewritten: ").Append(EntriesChanged).Append('\n');
        sb.Append("masks changed: ").Append(Changes.Count).Append('\n');
        sb.Append("already complete, no change needed: ").Append(AlreadyComplete).Append('\n');
        sb.Append("could not be changed: ").Append(NotChanged.Count).Append('\n');
        sb.Append("left as it was, created by someone else: ").Append(Left.Count).Append('\n');
        if (Changes.Count == 0 && NotChanged.Count == 0 && AlreadyComplete == 0)
            sb.Append("\nno part or item in the archive was created by the named creators; the output is a copy of the input\n");

        if (Changes.Count > 0)
        {
            sb.Append("\nCHANGED\n");
            foreach (MaskChange c in Changes)
                sb.Append("  ").Append(c.Where).Append(": ").Append(c.Mask).Append(" mask ").Append(MaskSet.Letters(c.Before)).Append(" -> ").Append(MaskSet.Letters(c.After))
                  .Append("  (").Append(c.Before.ToString("x", CultureInfo.InvariantCulture)).Append(" -> ").Append(c.After.ToString("x", CultureInfo.InvariantCulture)).Append(")  [")
                  .Append(c.Entry).Append("]\n");
        }
        if (NotChanged.Count > 0)
        {
            sb.Append("\nCOULD NOT BE CHANGED\n");
            foreach (string n in NotChanged)
                sb.Append("  ").Append(n).Append('\n');
        }
        if (Left.Count > 0)
        {
            sb.Append("\nLEFT AS IT WAS (created by someone else)\n");
            foreach (LeftAlone l in Left)
                sb.Append("  ").Append(l.Where).Append("  creator ").Append(l.Creator == UUID.Zero ? "none" : l.Creator.ToString()).Append("  [").Append(l.Entry).Append("]\n");
        }
        return sb.ToString();
    }

    public string ToJson()
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("tool", "oar-convert --fix-perms");
            w.WriteStartArray("creators");
            foreach (UUID c in Creators)
                w.WriteStringValue(c.ToString());
            w.WriteEndArray();
            w.WriteNumber("entriesRead", EntriesRead);
            w.WriteNumber("entriesChanged", EntriesChanged);
            w.WriteNumber("alreadyComplete", AlreadyComplete);
            w.WriteStartArray("changes");
            foreach (MaskChange c in Changes)
            {
                w.WriteStartObject();
                w.WriteString("entry", c.Entry);
                w.WriteString("where", c.Where);
                w.WriteString("mask", c.Mask);
                w.WriteNumber("before", c.Before);
                w.WriteNumber("after", c.After);
                w.WriteString("beforeLetters", MaskSet.Letters(c.Before));
                w.WriteString("afterLetters", MaskSet.Letters(c.After));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("notChanged");
            foreach (string n in NotChanged)
                w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteStartArray("leftAlone");
            foreach (LeftAlone l in Left)
            {
                w.WriteStartObject();
                w.WriteString("entry", l.Entry);
                w.WriteString("where", l.Where);
                w.WriteString("creator", l.Creator.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}

/// <summary>
/// Writes a copy of an OAR or IAR in which every part and every item in a part's contents, and in an IAR every inventory item,
/// created by the named creators has modify, copy and transfer on its base, owner and next owner masks (the bits are added; every other bit stays).
/// Group and everyone masks, creator, owner, last owner and every other field stay as they were, and so does every part and item by anyone else.
/// Object files are edited in place: only the digits of the changed masks differ in the bytes.
/// </summary>
public static class PermissionFix
{
    private const uint ModifyCopyTransfer = 0x4000 | 0x8000 | 0x2000;

    private static readonly (string name, string element)[] PartMasks = { ("base", "BaseMask"), ("owner", "OwnerMask"), ("next", "NextOwnerMask") };
    private static readonly (string name, string element)[] ItemMasks = { ("base", "BasePermissions"), ("owner", "CurrentPermissions"), ("next", "NextPermissions") };

    public static PermissionFixResult Run(string source, string dest, IReadOnlyCollection<UUID> creators)
    {
        if (creators == null || creators.Count == 0)
            throw new ArgumentException("the creators are required: name the account whose parts and items are to be fixed, an id each", nameof(creators));

        HashSet<UUID> wanted = new HashSet<UUID>(creators);
        PermissionFixResult result = new PermissionFixResult();
        result.Creators.AddRange(creators.Distinct());

        TarRewrite.Run(source, dest, (header, body) =>
        {
            result.EntriesRead++;
            byte[] changed = null;
            if (IsObjectFile(header.Name))
                changed = FixObjectFile(header.Name, body, wanted, result);
            else if (IsInventoryItemFile(header.Name))
                changed = FixInventoryItem(header.Name, body, wanted, result);
            if (changed != null)
                result.EntriesChanged++;
            return changed;
        });
        return result;
    }

    private static bool IsObjectFile(string name) =>
        (name.StartsWith(ArchiveConstants.OBJECTS_PATH, StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        || (name.StartsWith(ArchiveConstants.ASSETS_PATH, StringComparison.Ordinal) && name.EndsWith("object.xml", StringComparison.OrdinalIgnoreCase));

    private static bool IsInventoryItemFile(string name) =>
        name.StartsWith(ArchiveConstants.INVENTORY_PATH, StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ object files (parts and the items in their contents)

    private static byte[] FixObjectFile(string entry, byte[] body, HashSet<UUID> wanted, PermissionFixResult result)
    {
        ParsedObject parsed;
        try
        {
            parsed = ObjectXmlParser.Parse(body);
        }
        catch (XmlException e)
        {
            result.NotChanged.Add("[" + entry + "] the file is not readable and was copied as it is: " + Sanitize.Text(e.Message));
            return null;
        }

        // The parts to look at: every part of the named creators, and every part holding an item of theirs.
        HashSet<Guid> ids = new HashSet<Guid>();
        foreach (ParsedGroup g in parsed.Groups)
            foreach (ParsedPart p in g.Parts)
                if (wanted.Contains(p.Part.Creator.Id) || p.Items.Any(i => wanted.Contains(i.Creator.Id)))
                    ids.Add(p.Part.PartId.Guid);

        ObjectXml.Decoded decoded = null;
        Dictionary<Guid, Queue<ObjectXml.PartSpans>> located = new Dictionary<Guid, Queue<ObjectXml.PartSpans>>();
        if (ids.Count > 0)
        {
            decoded = ObjectXml.Decode(body);
            foreach (ObjectXml.PartSpans spans in ObjectXml.Scan(decoded.Text, ids))
            {
                if (!located.TryGetValue(spans.Id, out Queue<ObjectXml.PartSpans> q))
                    located[spans.Id] = q = new Queue<ObjectXml.PartSpans>();
                q.Enqueue(spans);
            }
        }

        List<(int start, int end, string with)> cuts = new List<(int, int, string)>();
        foreach (ParsedGroup g in parsed.Groups)
        {
            foreach (ParsedPart p in g.Parts)
            {
                PartRecord rec = p.Part;
                string partWhere = "object " + Quote(g.Name) + ", part " + Quote(rec.Name);
                ObjectXml.PartSpans spans = null;
                if (ids.Contains(rec.PartId.Guid) && located.TryGetValue(rec.PartId.Guid, out Queue<ObjectXml.PartSpans> q) && q.Count > 0)
                    spans = q.Dequeue();

                if (wanted.Contains(rec.Creator.Id))
                {
                    if (spans == null)
                        result.NotChanged.Add("[" + entry + "] " + partWhere + ": the part could not be located in the file");
                    else
                        FixMasks(entry, partWhere, decoded.Text, spans, "", PartMasks, rec.Masks, cuts, result);
                }
                else
                    result.Left.Add(new LeftAlone { Entry = entry, Where = partWhere, Creator = rec.Creator.Id });

                if (p.Items.Count == 0)
                    continue;

                List<ObjectXml.Span> itemSpans = spans != null && spans.ByPath.TryGetValue("TaskInventory/TaskInventoryItem", out List<ObjectXml.Span> all)
                    ? all.Where(s => !s.SelfClosing).ToList() : new List<ObjectXml.Span>();
                bool aligned = itemSpans.Count == p.Items.Count;
                for (int k = 0; k < p.Items.Count; k++)
                {
                    ItemRecord item = p.Items[k];
                    string itemWhere = "item " + Quote(item.Name) + " in " + partWhere;
                    if (!wanted.Contains(item.Creator.Id))
                    {
                        result.Left.Add(new LeftAlone { Entry = entry, Where = itemWhere, Creator = item.Creator.Id });
                        continue;
                    }
                    if (!aligned)
                    {
                        result.NotChanged.Add("[" + entry + "] " + itemWhere + ": the items of the part could not be told apart in the file");
                        continue;
                    }
                    FixMasks(entry, itemWhere, decoded.Text, spans, "TaskInventory/TaskInventoryItem/", ItemMasks, item.Masks, cuts, result, itemSpans[k]);
                }
            }
        }

        return cuts.Count == 0 ? null : ObjectXml.ApplyCuts(decoded, cuts, entry);
    }

    /// <summary>Adds a cut for each of the three masks that lacks something; a mask already complete is not touched, a mask not in the file is reported.</summary>
    private static void FixMasks(string entry, string where, string text, ObjectXml.PartSpans spans, string pathPrefix, (string name, string element)[] masks,
        PermissionMasks stored, List<(int start, int end, string with)> cuts, PermissionFixResult result, ObjectXml.Span within = null)
    {
        bool changedAny = false, couldNot = false;
        foreach ((string name, string element) in masks)
        {
            uint? value = name == "base" ? stored.Base : name == "owner" ? stored.Owner : stored.NextOwner;
            if (!value.HasValue)
            {
                result.NotChanged.Add("[" + entry + "] " + where + ": the " + name + " mask (" + element + ") is not in the file or has no readable value, so nothing was written for it");
                couldNot = true;
                continue;
            }
            if ((value.Value & ModifyCopyTransfer) == ModifyCopyTransfer)
                continue;

            ObjectXml.Span span = Find(spans, pathPrefix + element, within);
            if (span == null || span.SelfClosing)
            {
                result.NotChanged.Add("[" + entry + "] " + where + ": the " + name + " mask (" + element + ") could not be located in the file");
                couldNot = true;
                continue;
            }

            uint after = value.Value | ModifyCopyTransfer;
            cuts.Add((span.TagEnd, span.InnerEnd, Format(text.Substring(span.TagEnd, span.InnerEnd - span.TagEnd), after)));
            result.Changes.Add(new MaskChange { Entry = entry, Where = where, Mask = name, Before = value.Value, After = after });
            changedAny = true;
        }
        if (!changedAny && !couldNot)
            result.AlreadyComplete++;
    }

    private static ObjectXml.Span Find(ObjectXml.PartSpans spans, string path, ObjectXml.Span within)
    {
        if (!spans.ByPath.TryGetValue(path, out List<ObjectXml.Span> list))
            return null;
        List<ObjectXml.Span> hits = within == null ? list : list.Where(s => s.TagStart >= within.TagEnd && s.TagStart < within.InnerEnd).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>A number written the way the original was: a signed number stays signed, an unsigned number stays unsigned.</summary>
    private static string Format(string original, uint value) =>
        original.TrimStart().StartsWith("-", StringComparison.Ordinal)
            ? unchecked((int)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string s) => "\"" + s + "\"";

    // ------------------------------------------------------------------ inventory items of an IAR

    private static byte[] FixInventoryItem(string entry, byte[] body, HashSet<UUID> wanted, PermissionFixResult result)
    {
        ItemRecord item;
        try
        {
            item = MetadataParsers.ParseIarItem(body);
        }
        catch (XmlException e)
        {
            result.NotChanged.Add("[" + entry + "] the file is not readable and was copied as it is: " + Sanitize.Text(e.Message));
            return null;
        }

        string where = "inventory item " + Quote(item.Name);
        if (!wanted.Contains(item.Creator.Id))
        {
            result.Left.Add(new LeftAlone { Entry = entry, Where = where, Creator = item.Creator.Id });
            return null;
        }

        ObjectXml.Decoded decoded = ObjectXml.Decode(body);
        List<(int start, int end, string with)> cuts = new List<(int, int, string)>();
        bool changedAny = false, couldNot = false;
        foreach ((string name, string element) in ItemMasks)
        {
            uint? value = name == "base" ? item.Masks.Base : name == "owner" ? item.Masks.Owner : item.Masks.NextOwner;
            Match[] found = Regex.Matches(decoded.Text, "<" + element + @">\s*(-?\d+)\s*</" + element + ">").Cast<Match>().ToArray();
            if (!value.HasValue || found.Length != 1)
            {
                result.NotChanged.Add("[" + entry + "] " + where + ": the " + name + " mask (" + element + ") is not in the file once, so nothing was written for it");
                couldNot = true;
                continue;
            }
            if ((value.Value & ModifyCopyTransfer) == ModifyCopyTransfer)
                continue;

            uint after = value.Value | ModifyCopyTransfer;
            System.Text.RegularExpressions.Group digits = found[0].Groups[1];
            cuts.Add((digits.Index, digits.Index + digits.Length, Format(digits.Value, after)));
            result.Changes.Add(new MaskChange { Entry = entry, Where = where, Mask = name, Before = value.Value, After = after });
            changedAny = true;
        }
        if (!changedAny && !couldNot)
            result.AlreadyComplete++;
        return cuts.Count == 0 ? null : ObjectXml.ApplyCuts(decoded, cuts, entry);
    }
}
