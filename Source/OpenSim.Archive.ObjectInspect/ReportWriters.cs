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

namespace OpenSim.Archive.ObjectInspect;

/// <summary>Formatting shared by the three outputs.</summary>
internal static class Fmt
{
    public static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Vec(Vector3 v) => "<" + Num(v.X) + ", " + Num(v.Y) + ", " + Num(v.Z) + ">";

    public static string Hex(uint? mask) => mask.HasValue ? mask.Value.ToString("x", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>An account as "Name (id)", or the id alone when the archive names nobody, or "none" for the blank id.</summary>
    public static string Who(UUID id, string name)
    {
        if (id == UUID.Zero)
            return "none";
        return string.IsNullOrEmpty(name) ? id.ToString() : name + " (" + id + ")";
    }

    public static string KindName(ArchiveKind kind) => kind == ArchiveKind.Oar ? "OAR" : kind == ArchiveKind.Iar ? "IAR" : "unknown";

    private static string MaskLabel(string name) => name == "next" ? "next owner" : name;

    /// <summary>"next:CT" as "next owner: copy, transfer"; "group:absent" as "group: not in the file".</summary>
    public static string LacksText(string code)
    {
        int colon = code.IndexOf(':');
        string name = code.Substring(0, colon), what = code.Substring(colon + 1);
        if (what == "absent")
            return MaskLabel(name) + ": not in the file";
        List<string> words = new List<string>();
        foreach (char c in what)
            words.Add(c == 'M' ? "modify" : c == 'C' ? "copy" : "transfer");
        return MaskLabel(name) + ": " + string.Join(", ", words);
    }

    public static string LacksLine(List<string> lacks) => string.Join("; ", lacks.Select(LacksText));

    public static string Masks(MaskSet m) =>
        "base " + MaskSet.Letters(m.Base) + "  owner " + MaskSet.Letters(m.Owner) + "  group " + MaskSet.Letters(m.Group) +
        "  everyone " + MaskSet.Letters(m.Everyone) + "  next " + MaskSet.Letters(m.NextOwner);

    public static string Quote(string s) => "\"" + s + "\"";
}

/// <summary>The readable report.</summary>
public static class ReportText
{
    public static string Build(ObjectReport report, string archiveLabel = null)
    {
        StringBuilder sb = new StringBuilder();
        if (archiveLabel != null)
            sb.Append("archive: ").Append(archiveLabel).Append('\n');

        if (report.Status == InspectionStatus.NotAnArchive)
        {
            sb.Append("status: not an archive");
            if (!string.IsNullOrEmpty(report.StopReason))
                sb.Append(" (").Append(report.StopReason).Append(')');
            sb.Append('\n');
            return sb.ToString();
        }

        sb.Append("kind: ").Append(Fmt.KindName(report.Kind));
        if (report.Version != null)
            sb.Append(" (version ").Append(report.Version).Append(')');
        sb.Append('\n');
        if (report.Status == InspectionStatus.Partial)
            sb.Append("status: partial - ").Append(report.StopReason).Append('\n');
        if (report.Filter != null)
            sb.Append("filter: \"").Append(report.Filter).Append("\" (").Append(report.Summary.Objects).Append(" of ").Append(report.ObjectsBeforeFilter).Append(" objects)\n");

        bool iar = report.Kind == ArchiveKind.Iar;
        ReportSummary s = report.Summary;
        sb.Append("\nSUMMARY\n");
        sb.Append("  objects: ").Append(s.Objects).Append('\n');
        sb.Append("  parts: ").Append(s.Parts).Append('\n');
        sb.Append("  items in part contents: ").Append(s.PartItems).Append('\n');
        if (iar)
            sb.Append("  inventory items: ").Append(s.InventoryItems).Append('\n');
        sb.Append("  flagged objects: ").Append(s.FlaggedObjects).Append('\n');
        sb.Append("  flagged parts: ").Append(s.FlaggedParts).Append('\n');
        sb.Append("  flagged part items: ").Append(s.FlaggedPartItems).Append('\n');
        if (iar)
            sb.Append("  flagged inventory items: ").Append(s.FlaggedInventoryItems).Append('\n');
        sb.Append("  objects with link order problems: ").Append(s.LinkOrderProblems).Append('\n');
        sb.Append("  lacking by mask: ").Append(string.Join(", ", MaskSet.Names.Select(n => (n == "next" ? "next" : n) + " " + s.LackingByMask.GetValueOrDefault(n)))).Append('\n');
        sb.Append("  masks are shown as M modify, C copy, T transfer; a dash means the mask lacks it. Flagged means a mask lacks at least one of the three.\n");

        if (report.Parties.Count > 0)
        {
            sb.Append("\nACCOUNTS\n");
            foreach (PartyCount p in report.Parties)
                sb.Append("  ").Append(p.Id).Append("  ").Append(string.IsNullOrEmpty(p.Name) ? "(no name in the archive)" : p.Name)
                  .Append("  creator ").Append(p.AsCreator).Append("  owner ").Append(p.AsOwner)
                  .Append("  last owner ").Append(p.AsLastOwner).Append("  group ").Append(p.AsGroup).Append('\n');
        }

        if (iar)
        {
            sb.Append("\nINVENTORY ITEMS\n");
            foreach (InventoryItemEntry i in report.InventoryItems)
            {
                sb.Append("  ").Append(string.IsNullOrEmpty(i.FolderPath) ? "" : i.FolderPath + " / ").Append(i.Name).Append(" [").Append(i.AssetTypeName).Append("]\n");
                sb.Append("      id ").Append(i.ItemId).Append("  asset ").Append(i.AssetId).Append('\n');
                sb.Append("      who     creator ").Append(Fmt.Who(i.CreatorId, i.CreatorName)).Append("; owner ").Append(Fmt.Who(i.OwnerId, i.OwnerName))
                  .Append("; group ").Append(Fmt.Who(i.GroupId, null)).Append('\n');
                sb.Append("      masks   ").Append(Fmt.Masks(i.Masks)).Append('\n');
                if (i.Lacks.Count > 0)
                    sb.Append("      LACKS   ").Append(Fmt.LacksLine(i.Lacks)).Append('\n');
                if (i.LinksetCount > 0)
                    sb.Append("      holds ").Append(i.LinksetCount).Append(i.LinksetCount == 1 ? " linkset" : " linksets").Append('\n');
                if (!string.IsNullOrEmpty(i.Note))
                    sb.Append("      note: ").Append(i.Note).Append('\n');
            }
        }

        foreach (ObjectEntry o in report.Objects)
        {
            sb.Append('\n').Append("OBJECT ").Append(o.Index).Append(' ').Append(Fmt.Quote(o.Name ?? string.Empty));
            if (!string.IsNullOrEmpty(o.Entry))
                sb.Append("  (").Append(o.Entry).Append(')');
            sb.Append('\n');
            if (iar)
            {
                sb.Append("  item ").Append(Fmt.Quote(o.ItemName ?? string.Empty)).Append(string.IsNullOrEmpty(o.FolderPath) ? "" : " in " + o.FolderPath);
                if (o.CoalescedIndex > 0)
                    sb.Append(", linkset ").Append(o.CoalescedIndex).Append(" of ").Append(o.CoalescedCount);
                if (o.HasOffset)
                    sb.Append(", offset ").Append(Fmt.Vec(o.Offset));
                sb.Append('\n');
            }
            sb.Append("  link order: ").Append(o.LinkOrderOk ? "ok" : "PROBLEM - " + o.LinkOrderNote).Append('\n');
            foreach (PartEntry p in o.Parts)
            {
                sb.Append("  [").Append(p.FileOrder).Append("] link ").Append(p.LinkNum).Append("  ").Append(Fmt.Quote(p.Name ?? string.Empty))
                  .Append("  pos ").Append(Fmt.Vec(p.Position)).Append("  shape ").Append(p.ShapeTypeName).Append(p.Phantom ? ", phantom" : "")
                  .Append("  desc ").Append(Fmt.Quote(p.Description ?? string.Empty)).Append('\n');
                sb.Append("      who     creator ").Append(Fmt.Who(p.CreatorId, p.CreatorName)).Append("; owner ").Append(Fmt.Who(p.OwnerId, p.OwnerName))
                  .Append("; last owner ").Append(Fmt.Who(p.LastOwnerId, p.LastOwnerName)).Append("; group ").Append(Fmt.Who(p.GroupId, null)).Append('\n');
                sb.Append("      masks   ").Append(Fmt.Masks(p.Masks)).Append('\n');
                if (p.Lacks.Count > 0)
                    sb.Append("      LACKS   ").Append(Fmt.LacksLine(p.Lacks)).Append('\n');
                foreach (ItemEntry i in p.Items)
                {
                    sb.Append("      item ").Append(Fmt.Quote(i.Name ?? string.Empty)).Append(" [").Append(i.AssetTypeName).Append("]\n");
                    sb.Append("         who     creator ").Append(Fmt.Who(i.CreatorId, i.CreatorName)).Append("; owner ").Append(Fmt.Who(i.OwnerId, i.OwnerName))
                      .Append("; last owner ").Append(Fmt.Who(i.LastOwnerId, i.LastOwnerName)).Append("; group ").Append(Fmt.Who(i.GroupId, null)).Append('\n');
                    sb.Append("         masks   ").Append(Fmt.Masks(i.Masks)).Append('\n');
                    if (i.Lacks.Count > 0)
                        sb.Append("         LACKS   ").Append(Fmt.LacksLine(i.Lacks)).Append('\n');
                }
            }
        }

        if (report.Problems.Count > 0)
        {
            sb.Append("\nPROBLEMS\n");
            foreach (string p in report.Problems.Take(20))
                sb.Append("  ").Append(p).Append('\n');
            if (report.Problems.Count > 20)
                sb.Append("  ... and ").Append(report.Problems.Count - 20).Append(" more\n");
        }
        return sb.ToString();
    }
}

/// <summary>One row per part, per item in a part and per inventory item, for a spreadsheet.</summary>
public static class ReportCsv
{
    public const string Header = "kind,object,entry,item_name,folder,coalesced,offset,link_num,file_order,name,description,x,y,z,shape_type,phantom,link_order," +
        "creator_id,creator_name,owner_id,owner_name,last_owner_id,last_owner_name,group_id,base,owner,group,everyone,next," +
        "base_hex,owner_hex,group_hex,everyone_hex,next_hex,lacks,flagged";

    /// <summary>
    /// A cell of text from the archive. Quoted when it holds a comma, quote or line break. Text a spreadsheet would take for a
    /// formula (it starts with =, +, - or @, or with a tab or carriage return) gets a leading apostrophe so it stays text.
    /// </summary>
    internal static string Text(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        if (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' || s[0] == '\t' || s[0] == '\r')
            s = "'" + s;
        return Plain(s);
    }

    /// <summary>A cell the tool wrote itself (ids, numbers, letters).</summary>
    internal static string Plain(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string Id(UUID id) => id == UUID.Zero ? string.Empty : id.ToString();

    public static string Build(ObjectReport report)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");

        foreach (InventoryItemEntry i in report.InventoryItems)
        {
            Row(sb, "inventory", "", "", "", i.FolderPath, "", "", "", "", i.Name, "", "", "", "", "", "", "",
                i.CreatorId, i.CreatorName, i.OwnerId, i.OwnerName, UUID.Zero, null, i.GroupId, i.Masks, i.Lacks);
        }

        foreach (ObjectEntry o in report.Objects)
        {
            string coalesced = o.CoalescedIndex > 0 ? o.CoalescedIndex + " of " + o.CoalescedCount : "";
            string offset = o.HasOffset ? Fmt.Num(o.Offset.X) + " " + Fmt.Num(o.Offset.Y) + " " + Fmt.Num(o.Offset.Z) : "";
            string linkOrder = o.LinkOrderOk ? "ok" : "PROBLEM";
            foreach (PartEntry p in o.Parts)
            {
                Row(sb, "part", o.Name, o.Entry, o.ItemName, o.FolderPath, coalesced, offset, p.LinkNum.ToString(CultureInfo.InvariantCulture), p.FileOrder.ToString(CultureInfo.InvariantCulture),
                    p.Name, p.Description, Fmt.Num(p.Position.X), Fmt.Num(p.Position.Y), Fmt.Num(p.Position.Z), p.ShapeTypeName, p.Phantom ? "true" : "false", linkOrder,
                    p.CreatorId, p.CreatorName, p.OwnerId, p.OwnerName, p.LastOwnerId, p.LastOwnerName, p.GroupId, p.Masks, p.Lacks);
                foreach (ItemEntry i in p.Items)
                    Row(sb, "item", o.Name, o.Entry, o.ItemName, o.FolderPath, coalesced, offset, p.LinkNum.ToString(CultureInfo.InvariantCulture), p.FileOrder.ToString(CultureInfo.InvariantCulture),
                        i.Name, "", "", "", "", "", "", linkOrder,
                        i.CreatorId, i.CreatorName, i.OwnerId, i.OwnerName, i.LastOwnerId, i.LastOwnerName, i.GroupId, i.Masks, i.Lacks);
            }
        }
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string kind, string obj, string entry, string itemName, string folder, string coalesced, string offset,
        string linkNum, string fileOrder, string name, string description, string x, string y, string z, string shape, string phantom, string linkOrder,
        UUID creator, string creatorName, UUID owner, string ownerName, UUID lastOwner, string lastOwnerName, UUID group, MaskSet m, List<string> lacks)
    {
        string[] cells =
        {
            kind, Text(obj), Text(entry), Text(itemName), Text(folder), coalesced, offset, linkNum, fileOrder, Text(name), Text(description), x, y, z, shape, phantom, linkOrder,
            Id(creator), Text(creatorName), Id(owner), Text(ownerName), Id(lastOwner), Text(lastOwnerName), Id(group),
            MaskSet.Letters(m.Base), MaskSet.Letters(m.Owner), MaskSet.Letters(m.Group), MaskSet.Letters(m.Everyone), MaskSet.Letters(m.NextOwner),
            Fmt.Hex(m.Base), Fmt.Hex(m.Owner), Fmt.Hex(m.Group), Fmt.Hex(m.Everyone), Fmt.Hex(m.NextOwner),
            string.Join("; ", lacks), lacks.Count > 0 ? "true" : "false"
        };
        // the cells from Text() are already quoted; the others hold no comma, quote or line break
        sb.Append(string.Join(",", cells.Select(c => c ?? string.Empty))).Append("\r\n");
    }
}

/// <summary>The same report as JSON, for tools.</summary>
public static class ReportJson
{
    public static string Build(ObjectReport report, string archiveLabel = null)
    {
        using MemoryStream ms = new MemoryStream();
        using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("tool", "oar-inspect");
            w.WriteStartObject("archive");
            if (archiveLabel != null)
                w.WriteString("file", archiveLabel);
            w.WriteString("kind", report.Kind.ToString());
            if (report.Version != null)
                w.WriteString("version", report.Version);
            w.WriteString("status", report.Status.ToString());
            if (!string.IsNullOrEmpty(report.StopReason))
                w.WriteString("stopReason", report.StopReason);
            w.WriteEndObject();
            if (report.Filter != null)
            {
                w.WriteString("filter", report.Filter);
                w.WriteNumber("objectsBeforeFilter", report.ObjectsBeforeFilter);
            }

            ReportSummary s = report.Summary;
            w.WriteStartObject("summary");
            w.WriteNumber("objects", s.Objects);
            w.WriteNumber("parts", s.Parts);
            w.WriteNumber("partItems", s.PartItems);
            w.WriteNumber("inventoryItems", s.InventoryItems);
            w.WriteNumber("flaggedObjects", s.FlaggedObjects);
            w.WriteNumber("flaggedParts", s.FlaggedParts);
            w.WriteNumber("flaggedPartItems", s.FlaggedPartItems);
            w.WriteNumber("flaggedInventoryItems", s.FlaggedInventoryItems);
            w.WriteNumber("linkOrderProblems", s.LinkOrderProblems);
            w.WriteStartObject("lackingByMask");
            foreach (string n in MaskSet.Names)
                w.WriteNumber(n, s.LackingByMask.GetValueOrDefault(n));
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartArray("accounts");
            foreach (PartyCount p in report.Parties)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id.ToString());
                if (!string.IsNullOrEmpty(p.Name)) w.WriteString("name", p.Name);
                w.WriteNumber("asCreator", p.AsCreator);
                w.WriteNumber("asOwner", p.AsOwner);
                w.WriteNumber("asLastOwner", p.AsLastOwner);
                w.WriteNumber("asGroup", p.AsGroup);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("inventoryItems");
            foreach (InventoryItemEntry i in report.InventoryItems)
            {
                w.WriteStartObject();
                w.WriteString("name", i.Name);
                w.WriteString("id", i.ItemId.ToString());
                w.WriteString("assetId", i.AssetId.ToString());
                w.WriteString("assetType", i.AssetTypeName);
                w.WriteString("folder", i.FolderPath);
                Who(w, "creator", i.CreatorId, i.CreatorName);
                Who(w, "owner", i.OwnerId, i.OwnerName);
                Who(w, "group", i.GroupId, null);
                Masks(w, i.Masks);
                Lacks(w, i.Lacks);
                w.WriteBoolean("flagged", i.Flagged);
                w.WriteNumber("linksets", i.LinksetCount);
                if (!string.IsNullOrEmpty(i.Note)) w.WriteString("note", i.Note);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("objects");
            foreach (ObjectEntry o in report.Objects)
            {
                w.WriteStartObject();
                w.WriteNumber("index", o.Index);
                w.WriteString("name", o.Name);
                w.WriteString("entry", o.Entry);
                if (!string.IsNullOrEmpty(o.RegionDir)) w.WriteString("regionDir", o.RegionDir);
                if (o.ItemId != UUID.Zero)
                {
                    w.WriteString("itemName", o.ItemName);
                    w.WriteString("itemId", o.ItemId.ToString());
                    w.WriteString("folder", o.FolderPath);
                    w.WriteString("assetId", o.AssetId.ToString());
                }
                w.WriteNumber("coalescedIndex", o.CoalescedIndex);
                w.WriteNumber("coalescedCount", o.CoalescedCount);
                if (o.HasOffset) Vec(w, "offset", o.Offset);
                Vec(w, "groupPosition", o.GroupPosition);
                w.WriteBoolean("linkOrderOk", o.LinkOrderOk);
                if (o.LinkOrderNote != null) w.WriteString("linkOrderNote", o.LinkOrderNote);
                w.WriteBoolean("flagged", o.Flagged);
                w.WriteStartArray("parts");
                foreach (PartEntry p in o.Parts)
                {
                    w.WriteStartObject();
                    w.WriteNumber("fileOrder", p.FileOrder);
                    w.WriteNumber("linkNum", p.LinkNum);
                    w.WriteString("name", p.Name);
                    w.WriteString("description", p.Description);
                    w.WriteString("id", p.PartId.ToString());
                    Vec(w, "position", p.Position);
                    w.WriteString("shapeType", p.ShapeTypeName);
                    w.WriteNumber("shapeTypeStored", p.ShapeType);
                    w.WriteBoolean("phantom", p.Phantom);
                    Who(w, "creator", p.CreatorId, p.CreatorName);
                    Who(w, "owner", p.OwnerId, p.OwnerName);
                    Who(w, "lastOwner", p.LastOwnerId, p.LastOwnerName);
                    Who(w, "group", p.GroupId, null);
                    Masks(w, p.Masks);
                    Lacks(w, p.Lacks);
                    w.WriteBoolean("flagged", p.Flagged);
                    w.WriteStartArray("items");
                    foreach (ItemEntry i in p.Items)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", i.Name);
                        w.WriteString("id", i.ItemId.ToString());
                        w.WriteString("assetId", i.AssetId.ToString());
                        w.WriteString("assetType", i.AssetTypeName);
                        Who(w, "creator", i.CreatorId, i.CreatorName);
                        Who(w, "owner", i.OwnerId, i.OwnerName);
                        Who(w, "lastOwner", i.LastOwnerId, i.LastOwnerName);
                        Who(w, "group", i.GroupId, null);
                        Masks(w, i.Masks);
                        Lacks(w, i.Lacks);
                        w.WriteBoolean("flagged", i.Flagged);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("problems");
            foreach (string p in report.Problems)
                w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    internal static void Who(Utf8JsonWriter w, string name, UUID id, string display)
    {
        w.WriteStartObject(name);
        w.WriteString("id", id.ToString());
        if (!string.IsNullOrEmpty(display))
            w.WriteString("name", display);
        w.WriteEndObject();
    }

    internal static void Vec(Utf8JsonWriter w, string name, Vector3 v)
    {
        w.WriteStartObject(name);
        w.WriteNumber("x", v.X);
        w.WriteNumber("y", v.Y);
        w.WriteNumber("z", v.Z);
        w.WriteEndObject();
    }

    private static void Masks(Utf8JsonWriter w, MaskSet m)
    {
        w.WriteStartObject("masks");
        foreach (string name in MaskSet.Names)
        {
            uint? v = m.Get(name);
            w.WriteStartObject(name);
            if (v.HasValue)
                w.WriteNumber("value", v.Value);
            w.WriteString("letters", MaskSet.Letters(v));
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void Lacks(Utf8JsonWriter w, List<string> lacks)
    {
        w.WriteStartArray("lacks");
        foreach (string l in lacks)
            w.WriteStringValue(l);
        w.WriteEndArray();
    }
}
