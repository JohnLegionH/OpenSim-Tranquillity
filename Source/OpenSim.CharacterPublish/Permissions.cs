/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;

namespace OpenSim.CharacterPublish;

/// <summary>Permission bits as the viewer defines them (llpermissionsflags.h).</summary>
public static class PermissionMasks
{
    public const uint Transfer = 0x00002000;
    public const uint Modify = 0x00004000;
    public const uint Copy = 0x00008000;
    public const uint Move = 0x00080000;
    public const uint None = 0;
    public const uint All = 0x7FFFFFFF;

    /// <summary>Copy, modify and transfer (plus move, which every ordinary item carries).</summary>
    public const uint CopyModifyTransfer = Copy | Modify | Transfer | Move;
}

/// <summary>How an item is for sale. The text forms are the viewer's (llsaleinfo.cpp:45-48).</summary>
public enum SaleType
{
    Not = 0,
    Original = 1,
    Copy = 2,
    Contents = 3,
}

/// <summary>The permissions block of a wearable or an embedded inventory item (<c>permissions 0 { ... }</c>).</summary>
public sealed record PermissionBlock(
    uint BaseMask,
    uint OwnerMask,
    uint GroupMask,
    uint EveryoneMask,
    uint NextOwnerMask,
    UUID Creator,
    UUID Owner,
    UUID LastOwner,
    UUID Group,
    bool GroupOwned = false)
{
    /// <summary>Same masks for base, owner and next owner; nothing for group and everyone.</summary>
    public static PermissionBlock ForOwner(UUID creator, UUID owner, uint mask) =>
        new(mask, mask, PermissionMasks.None, PermissionMasks.None, mask, creator, owner, creator, UUID.Zero);
}

/// <summary>Text forms of <see cref="SaleType"/>.</summary>
public static class SaleTypeText
{
    static readonly string[] Names = { "not", "orig", "copy", "cntn" };

    public static string ToText(SaleType t) =>
        (uint)t < Names.Length ? Names[(int)t] : throw new ArgumentOutOfRangeException(nameof(t));

    public static bool TryParse(string s, out SaleType t)
    {
        var i = Array.IndexOf(Names, s);
        t = (SaleType)(i < 0 ? 0 : i);
        return i >= 0;
    }
}
