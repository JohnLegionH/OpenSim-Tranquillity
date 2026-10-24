/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Text;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish;

/// <summary>
/// Writes the wearable text as the viewer's LLWearable::exportStream does
/// (llappearance/llwearable.cpp:96-143), with LLPermissions::exportLegacyStream (llpermissions.cpp:668-705) and
/// LLSaleInfo::exportLegacyStream (llsaleinfo.cpp:81-88) for the two blocks. Lines end in "\n", blocks are indented
/// with tabs, and the text ends with the last line's "\n" and nothing after it.
/// The format follows the behaviour of the Second Life viewer (Linden Lab, LGPL); no viewer code is copied.
/// </summary>
public static class WearableWriter
{
    public static string Write(WearableDocument d)
    {
        Validate(d);
        var sb = new StringBuilder(2048);
        sb.Append("LLWearable version ").Append(d.DefinitionVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(d.Name).Append('\n');
        sb.Append(d.Description).Append('\n');
        AppendPermissions(sb, d.Permissions, "\t");
        AppendSaleInfo(sb, d.SaleType, d.SalePrice, "\t", "\t\t");
        sb.Append("type ").Append(((int)d.Type).ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("parameters ").Append(d.Parameters.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var p in d.Parameters)
            sb.Append(p.Key.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(WearableFormat.TerseFloat(p.Value)).Append('\n');
        sb.Append("textures ").Append(d.Textures.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var t in d.Textures)
            sb.Append(t.Key.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(t.Value.ToString()).Append('\n');
        return sb.ToString();
    }

    /// <summary>The asset bytes: UTF-8 without a byte order mark.</summary>
    public static byte[] WriteBytes(WearableDocument d) => new UTF8Encoding(false).GetBytes(Write(d));

    /// <summary>
    /// The <c>permissions 0</c> block. Masks are 8 lower-case hex digits ("%08x"), ids are lower-case UUIDs,
    /// and <c>group_owned 1</c> follows the ids only when the owner is a group.
    /// </summary>
    internal static void AppendPermissions(StringBuilder sb, PermissionBlock p, string indent)
    {
        var inner = indent + "\t";
        sb.Append(indent).Append("permissions 0\n").Append(indent).Append("{\n");
        sb.Append(inner).Append("base_mask\t").Append(Hex(p.BaseMask)).Append('\n');
        sb.Append(inner).Append("owner_mask\t").Append(Hex(p.OwnerMask)).Append('\n');
        sb.Append(inner).Append("group_mask\t").Append(Hex(p.GroupMask)).Append('\n');
        sb.Append(inner).Append("everyone_mask\t").Append(Hex(p.EveryoneMask)).Append('\n');
        sb.Append(inner).Append("next_owner_mask\t").Append(Hex(p.NextOwnerMask)).Append('\n');
        sb.Append(inner).Append("creator_id\t").Append(p.Creator.ToString()).Append('\n');
        sb.Append(inner).Append("owner_id\t").Append(p.Owner.ToString()).Append('\n');
        sb.Append(inner).Append("last_owner_id\t").Append(p.LastOwner.ToString()).Append('\n');
        sb.Append(inner).Append("group_id\t").Append(p.Group.ToString()).Append('\n');
        if (p.GroupOwned) sb.Append(inner).Append("group_owned\t1\n");
        sb.Append(indent).Append("}\n");
    }

    /// <summary>The <c>sale_info 0</c> block: indented one tab in a wearable, two in an embedded inventory item.</summary>
    internal static void AppendSaleInfo(StringBuilder sb, SaleType type, int price, string indent, string inner)
    {
        sb.Append(indent).Append("sale_info\t0\n").Append(indent).Append("{\n");
        sb.Append(inner).Append("sale_type\t").Append(SaleTypeText.ToText(type)).Append('\n');
        sb.Append(inner).Append("sale_price\t").Append(price.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(indent).Append("}\n");
    }

    static string Hex(uint v) => v.ToString("x8", CultureInfo.InvariantCulture);

    static void Validate(WearableDocument d)
    {
        CheckLine(d.Name, "name");
        CheckLine(d.Description, "description");
        // The server-side baker's parser scans the description line as a section, so a description that opens with one of
        // its keywords would be mis-read there.
        if (!SafeDescription.IsSafe(d.Description))
            throw new ArgumentException("description starts with a wearable section keyword (type, parameters, textures)");
        if (d.DefinitionVersion < 0) throw new ArgumentException("definition version must not be negative");
        if (!Enum.IsDefined(d.Type) || d.Type == WearableKind.Invalid)
            throw new ArgumentException($"wearable type {(int)d.Type} is not one of the viewer's types 0-16");
        if (d.Parameters.Count > WearableFormat.MaxParameters)
            throw new ArgumentException($"{d.Parameters.Count} parameters; the viewer reads at most {WearableFormat.MaxParameters}");
        if (d.Textures.Count > WearableFormat.MaxTextures)
            throw new ArgumentException($"{d.Textures.Count} textures; the viewer reads at most {WearableFormat.MaxTextures}");
        foreach (var t in d.Textures.Keys)
            if (t < 0 || t >= WearableFormat.TextureSlotCount)
                throw new ArgumentException($"texture slot {t} is outside 0-{WearableFormat.TextureSlotCount - 1}");
        if (d.SalePrice < 0) throw new ArgumentException("sale price must not be negative");
    }

    internal static void CheckLine(string s, string what)
    {
        if (s.Contains('\n') || s.Contains('\r') || s.Contains('\0'))
            throw new ArgumentException($"{what} must be a single line without control characters");
        if (Encoding.UTF8.GetByteCount(s) > WearableFormat.MaxLineBytes)
            throw new ArgumentException($"{what} is longer than the viewer's {WearableFormat.MaxLineBytes} byte line buffer");
    }
}
