/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

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
    public List<MaskChange> Changes { get; } = new List<MaskChange>();
    public List<LeftAlone> Left { get; } = new List<LeftAlone>();
    /// <summary>Masks of the named creators' parts and items that could not be changed, and why (for example a mask the file does not hold).</summary>
    public List<string> NotChanged { get; } = new List<string>();
    /// <summary>Parts, items and inventory items of the named creators whose three masks already held modify, copy and transfer.</summary>
    public int AlreadyComplete { get; set; }
    public int EntriesRead { get; set; }
    public int EntriesChanged { get; set; }

    public string ToText() => string.Empty;
    public string ToJson() => string.Empty;
}

/// <summary>
/// Writes a copy of an OAR or IAR in which every part and every item in a part's contents, and in an IAR every inventory item,
/// created by the named creators has modify, copy and transfer on its base, owner and next owner masks. Everything else is copied byte for byte.
/// </summary>
public static class PermissionFix
{
    public static PermissionFixResult Run(string source, string dest, IReadOnlyCollection<UUID> creators)
    {
        return new PermissionFixResult();
    }
}
