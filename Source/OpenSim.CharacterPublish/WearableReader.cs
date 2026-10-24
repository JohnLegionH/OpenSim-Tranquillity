/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish;

/// <summary>
/// Reads a wearable text with the same strictness as the viewer's LLWearable::importStream
/// (llappearance/llwearable.cpp:208-): the header, the name and description lines (which may be empty), the
/// permissions and sale_info blocks, then type, parameters and textures, each on its own populated line.
/// Used to check what <see cref="WearableWriter"/> wrote and to read real wearables for comparison.
/// </summary>
public static class WearableReader
{
    public static WearableDocument Read(string text)
    {
        // Some stored assets end with a NUL byte after the last newline; the viewer's line reader never sees it as content.
        text = text.TrimEnd('\0');
        var lines = new LineCursor(text);

        var header = lines.NextPopulated() ?? throw new FormatException("wearable: empty asset");
        var hp = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (hp.Length != 3 || hp[0] != "LLWearable" || hp[1] != "version" || !int.TryParse(hp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            throw new FormatException("wearable: bad header");

        var name = lines.Next() ?? throw new FormatException("wearable: no name line");
        var description = lines.Next() ?? throw new FormatException("wearable: no description line");

        var perms = ReadPermissions(lines);
        var (saleType, price) = ReadSaleInfo(lines);

        var typeLine = lines.NextPopulated() ?? throw new FormatException("wearable: no type line");
        var type = ParseKeyInt(typeLine, "type");
        if (type < 0 || type > 16) throw new FormatException($"wearable: bad type {type}");

        var np = ParseKeyInt(lines.NextPopulated() ?? throw new FormatException("wearable: no parameters header"), "parameters");
        if (np < 0 || np > WearableFormat.MaxParameters) throw new FormatException($"wearable: {np} parameters");
        var prms = new SortedDictionary<int, float>();
        for (var i = 0; i < np; i++)
        {
            var l = (lines.NextPopulated() ?? throw new FormatException($"wearable: parameter {i} missing")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (l.Length < 2 || !int.TryParse(l[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !float.TryParse(l[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                throw new FormatException($"wearable: bad parameter line {i}");
            prms[id] = w;
        }

        var nt = ParseKeyInt(lines.NextPopulated() ?? throw new FormatException("wearable: no textures header"), "textures");
        if (nt < 0 || nt > WearableFormat.MaxTextures) throw new FormatException($"wearable: {nt} textures");
        var texs = new SortedDictionary<int, UUID>();
        for (var i = 0; i < nt; i++)
        {
            var l = (lines.NextPopulated() ?? throw new FormatException($"wearable: texture {i} missing")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (l.Length < 2 || !int.TryParse(l[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) || slot >= WearableFormat.TextureSlotCount)
                throw new FormatException($"wearable: bad texture line {i}");
            if (!UUID.TryParse(l[1], out var id)) throw new FormatException($"wearable: bad texture id on line {i}");
            texs[slot] = id;
        }

        return new WearableDocument
        {
            DefinitionVersion = version, Name = name, Description = description, Permissions = perms,
            SaleType = saleType, SalePrice = price, Type = (WearableKind)type, Parameters = prms, Textures = texs,
        };
    }

    static int ParseKeyInt(string line, string key)
    {
        var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || p[0] != key || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"wearable: expected '{key} <n>', got '{line.Trim()}'");
        return v;
    }

    internal static PermissionBlock ReadPermissions(LineCursor lines)
    {
        var head = lines.NextPopulated() ?? throw new FormatException("permissions: missing");
        if (ParseKeyInt(head, "permissions") != 0) throw new FormatException("permissions: version is not 0");
        return ReadPermissionsBody(lines);
    }

    /// <summary>The "{ ... }" part of a permissions block, once its "permissions 0" line has been read.</summary>
    internal static PermissionBlock ReadPermissionsBody(LineCursor lines)
    {
        uint b = 0, o = 0, g = 0, e = 0, n = 0;
        UUID creator = UUID.Zero, owner = UUID.Zero, last = UUID.Zero, group = UUID.Zero;
        var groupOwned = false;
        foreach (var (k, v) in ReadBlock(lines, "permissions"))
        {
            switch (k)
            {
                case "base_mask": b = Hex(v); break;
                case "owner_mask": o = Hex(v); break;
                case "group_mask": g = Hex(v); break;
                case "everyone_mask": e = Hex(v); break;
                case "next_owner_mask": n = Hex(v); break;
                case "creator_id": creator = Id(v); break;
                case "owner_id": owner = Id(v); break;
                case "last_owner_id": last = Id(v); break;
                case "group_id": group = Id(v); break;
                case "group_owned": groupOwned = v == "1"; break;
            }
        }
        return new PermissionBlock(b, o, g, e, n, creator, owner, last, group, groupOwned);
    }

    internal static (SaleType, int) ReadSaleInfo(LineCursor lines)
    {
        var head = lines.NextPopulated() ?? throw new FormatException("sale_info: missing");
        if (ParseKeyInt(head, "sale_info") != 0) throw new FormatException("sale_info: version is not 0");
        return ReadSaleInfoBody(lines);
    }

    /// <summary>The "{ ... }" part of a sale_info block, once its "sale_info 0" line has been read.</summary>
    internal static (SaleType, int) ReadSaleInfoBody(LineCursor lines)
    {
        var type = SaleType.Not;
        var price = 0;
        foreach (var (k, v) in ReadBlock(lines, "sale_info"))
        {
            if (k == "sale_type") { if (!SaleTypeText.TryParse(v, out type)) throw new FormatException($"sale_info: unknown sale type '{v}'"); }
            else if (k == "sale_price") price = int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        return (type, price);
    }

    /// <summary>The "{ key value ... }" lines of a legacy block, as the viewer's import loops read them.</summary>
    internal static List<(string Key, string Value)> ReadBlock(LineCursor lines, string what)
    {
        var res = new List<(string, string)>();
        var open = lines.NextPopulated();
        if (open?.Trim() != "{") throw new FormatException($"{what}: missing '{{'");
        while (true)
        {
            var l = lines.Next() ?? throw new FormatException($"{what}: block not closed");
            var t = l.Trim();
            if (t.Length == 0) continue;
            if (t == "}") return res;
            var sp = t.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            res.Add((sp[0], sp.Length > 1 ? sp[1].Trim() : ""));
        }
    }

    static uint Hex(string v) => uint.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    static UUID Id(string v) => UUID.TryParse(v, out var id) ? id : throw new FormatException($"bad id '{v}'");
}

/// <summary>Line-by-line access with the viewer's two read modes: any line, or the next line that is not empty.</summary>
internal sealed class LineCursor
{
    readonly string[] _lines;
    int _i;

    public LineCursor(string text) => _lines = text.Split('\n');

    /// <summary>The next line, empty or not (std::istream::getline). Null at the end.</summary>
    public string? Next() => _i < _lines.Length ? _lines[_i++] : null;

    /// <summary>The next non-empty line (LLWearable::getNextPopulatedLine). Null at the end.</summary>
    public string? NextPopulated()
    {
        while (_i < _lines.Length)
        {
            var l = _lines[_i++];
            if (l.Length > 0) return l;
        }
        return null;
    }
}
