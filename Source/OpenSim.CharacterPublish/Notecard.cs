/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Text;
using OpenMetaverse;

namespace OpenSim.CharacterPublish;

/// <summary>An inventory item embedded in a notecard (the viewer's LLInventoryItem legacy chunk).</summary>
public sealed record EmbeddedItem(
    UUID ItemId, UUID ParentId, PermissionBlock Permissions, UUID AssetId,
    string AssetType, string InventoryType, uint Flags, SaleType SaleType, int SalePrice,
    string Name, string Description, long CreationDate);

/// <summary>A notecard: UTF-8 text and the inventory items its embedded-item characters refer to.</summary>
public sealed class NotecardDocument
{
    public string Text { get; init; } = "";
    public IReadOnlyList<EmbeddedItem> Items { get; init; } = Array.Empty<EmbeddedItem>();
}

/// <summary>
/// Writes a notecard asset as the viewer's LLNotecard::exportStream does (llinventory/llnotecard.cpp:262-277,
/// embedded items :232-259): "Linden text version 2", the embedded items chunk, "Text length N" (bytes of UTF-8),
/// the text, then "}". An embedded item appears in the text as the character U+100000 plus its index
/// (llui/lltexteditor.h:81, llviewertexteditor.cpp:1007-1024).
/// </summary>
public static class NotecardWriter
{
    /// <summary>The viewer refuses to read a notecard text longer than this many bytes (llnotecard.h:41).</summary>
    public const int MaxTextBytes = 65536;

    /// <summary>First code point of the embedded-item range.</summary>
    public const int FirstEmbeddedChar = 0x100000;

    /// <summary>The text character that stands for the embedded item at <paramref name="index"/>.</summary>
    public static string EmbeddedChar(int index) => char.ConvertFromUtf32(FirstEmbeddedChar + index);

    public static string Write(NotecardDocument d)
    {
        var textBytes = Encoding.UTF8.GetByteCount(d.Text);
        if (textBytes > MaxTextBytes) throw new ArgumentException($"notecard text is {textBytes} bytes; the viewer reads at most {MaxTextBytes}");
        CheckEmbeddedChars(d);

        var sb = new StringBuilder(d.Text.Length + 256);
        sb.Append("Linden text version 2\n{\n");
        sb.Append("LLEmbeddedItems version 1\n{\n");
        sb.Append("count ").Append(d.Items.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (var i = 0; i < d.Items.Count; i++)
        {
            sb.Append("{\next char index ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
            AppendItem(sb, d.Items[i]);
            sb.Append("}\n");
        }
        sb.Append("}\n");
        sb.Append("Text length ").Append(textBytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(d.Text);
        sb.Append("}\n");
        return sb.ToString();
    }

    public static byte[] WriteBytes(NotecardDocument d) => new UTF8Encoding(false).GetBytes(Write(d));

    /// <summary>LLInventoryItem::exportLegacyStream (llinventory.cpp:867-927), for an item whose asset id may be shown.</summary>
    static void AppendItem(StringBuilder sb, EmbeddedItem it)
    {
        // The viewer hides the asset id of an item that is not copy, modify and transfer behind an XOR cipher
        // (shadow_id). That form is not written here; an item that needs it is refused.
        const uint Unrestricted = PermissionMasks.Modify | PermissionMasks.Copy | PermissionMasks.Transfer;
        if ((it.Permissions.BaseMask & Unrestricted) != Unrestricted && it.AssetId != UUID.Zero)
            throw new NotSupportedException("an embedded item that is not copy, modify and transfer needs the viewer's shadow_id form");
        WearableWriter.CheckLine(it.Name, "embedded item name");
        WearableWriter.CheckLine(it.Description, "embedded item description");
        if (it.Name.Contains('|') || it.Description.Contains('|')) throw new ArgumentException("an embedded item name or description cannot hold '|'");

        sb.Append("\tinv_item\t0\n\t{\n");
        sb.Append("\t\titem_id\t").Append(it.ItemId.ToString()).Append('\n');
        sb.Append("\t\tparent_id\t").Append(it.ParentId.ToString()).Append('\n');
        WearableWriter.AppendPermissions(sb, it.Permissions, "\t\t");
        sb.Append("\t\tasset_id\t").Append(it.AssetId.ToString()).Append('\n');
        sb.Append("\t\ttype\t").Append(it.AssetType).Append('\n');
        if (it.InventoryType.Length > 0) sb.Append("\t\tinv_type\t").Append(it.InventoryType).Append('\n');
        sb.Append("\t\tflags\t").Append(it.Flags.ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
        WearableWriter.AppendSaleInfo(sb, it.SaleType, it.SalePrice, "\t\t", "\t\t\t");
        sb.Append("\t\tname\t").Append(it.Name).Append("|\n");
        sb.Append("\t\tdesc\t").Append(it.Description).Append("|\n");
        sb.Append("\t\tcreation_date\t").Append(it.CreationDate.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("\t}\n");
    }

    static void CheckEmbeddedChars(NotecardDocument d)
    {
        for (var i = 0; i < d.Text.Length; i++)
        {
            if (!char.IsHighSurrogate(d.Text[i]) || i + 1 >= d.Text.Length) continue;
            var cp = char.ConvertToUtf32(d.Text[i], d.Text[i + 1]);
            if (cp >= FirstEmbeddedChar && cp < FirstEmbeddedChar + 0x10000 && cp - FirstEmbeddedChar >= d.Items.Count)
                throw new ArgumentException($"the text refers to embedded item {cp - FirstEmbeddedChar}, but the notecard has {d.Items.Count}");
            i++;
        }
    }
}

/// <summary>Reads a notecard asset with the checks of LLNotecard::importStream (llnotecard.cpp:143-228).</summary>
public static class NotecardReader
{
    public static NotecardDocument Read(byte[] data)
    {
        // The text length counts bytes, so the body is cut by bytes before it is decoded.
        var pos = 0;
        string Line()
        {
            var end = Array.IndexOf(data, (byte)'\n', pos);
            if (end < 0) throw new FormatException("notecard: ended inside a line");
            var s = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = end + 1;
            return s;
        }

        var header = Line().Trim();
        if (header != "Linden text version 2" && header != "Linden text version 1") throw new FormatException($"notecard: bad header '{header}'");
        if (Line().Trim() != "{") throw new FormatException("notecard: missing '{'");
        if (Line().Trim() != "LLEmbeddedItems version 1") throw new FormatException("notecard: bad embedded items header");
        if (Line().Trim() != "{") throw new FormatException("notecard: missing '{' before count");
        var cl = Line().Trim();
        if (!cl.StartsWith("count ", StringComparison.Ordinal) || !int.TryParse(cl.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
            throw new FormatException("notecard: bad count");

        var items = new List<EmbeddedItem>();
        for (var i = 0; i < count; i++)
        {
            if (Line().Trim() != "{") throw new FormatException("notecard: missing '{' before item");
            if (Line().Trim() != $"ext char index {i}") throw new FormatException($"notecard: bad ext char index for item {i}");
            if (Line().Trim() != "inv_item\t0") throw new FormatException("notecard: missing inv_item");
            var block = new List<string>();
            // Take lines up to the closing "\t}" of the item, then its own closing "}".
            while (true)
            {
                var l = Line();
                block.Add(l);
                if (l == "\t}") break;
            }
            items.Add(ParseItem(block));
            if (Line().Trim() != "}") throw new FormatException("notecard: missing '}' after item");
        }
        if (Line().Trim() != "}") throw new FormatException("notecard: embedded items not closed");

        var tl = Line();
        if (!tl.StartsWith("Text length ", StringComparison.Ordinal) || !int.TryParse(tl.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var len) || len < 0 || len > NotecardWriter.MaxTextBytes)
            throw new FormatException("notecard: bad text length");
        if (pos + len > data.Length) throw new FormatException("notecard: text shorter than its length");
        var text = Encoding.UTF8.GetString(data, pos, len);
        pos += len;
        // The viewer's reader stops after the text and never looks at the closing brace. Writers differ: the viewer ends
        // with "}" and a newline, some script-written notecards with "}" alone, and stored assets may add a NUL byte.
        var ending = Encoding.UTF8.GetString(data, pos, data.Length - pos).TrimEnd('\0');
        if (ending != "}\n" && ending != "}") throw new FormatException("notecard: bad ending after the text");
        return new NotecardDocument { Text = text, Items = items };
    }

    static EmbeddedItem ParseItem(List<string> block)
    {
        // block holds the "\t{" ... "\t}" lines of the item; the permissions and sale_info sub-blocks are read through the shared reader.
        var cur = new LineCursor(string.Join('\n', block));
        UUID item = UUID.Zero, parent = UUID.Zero, asset = UUID.Zero;
        PermissionBlock? perms = null;
        var (sale, price) = (SaleType.Not, 0);
        string assetType = "", invType = "", name = "", desc = "";
        uint flags = 0;
        long created = 0;
        if (cur.NextPopulated()?.Trim() != "{") throw new FormatException("notecard item: missing '{'");
        while (cur.Next() is { } line)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t == "}") break;
            var sp = t.IndexOfAny(new[] { ' ', '\t' });
            var parts = sp < 0 ? new[] { t, "" } : new[] { t[..sp], t[(sp + 1)..] };
            switch (parts[0])
            {
                case "item_id": item = UUID.Parse(parts[1]); break;
                case "parent_id": parent = UUID.Parse(parts[1]); break;
                case "permissions": perms = WearableReader.ReadPermissionsBody(cur); break;
                case "sale_info": (sale, price) = WearableReader.ReadSaleInfoBody(cur); break;
                case "asset_id": asset = UUID.Parse(parts[1]); break;
                case "shadow_id": throw new NotSupportedException("notecard item: shadow_id form");
                case "type": assetType = parts[1]; break;
                case "inv_type": invType = parts[1]; break;
                case "flags": flags = uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture); break;
                case "name": name = StripBar(parts[1]); break;
                case "desc": desc = StripBar(parts[1]); break;
                case "creation_date": created = long.Parse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture); break;
            }
        }
        return new EmbeddedItem(item, parent, perms ?? throw new FormatException("notecard item: no permissions"),
            asset, assetType, invType, flags, sale, price, name, desc, created);
    }

    static string StripBar(string s) => s.EndsWith('|') ? s[..^1] : throw new FormatException("notecard item: text not closed with '|'");
}
