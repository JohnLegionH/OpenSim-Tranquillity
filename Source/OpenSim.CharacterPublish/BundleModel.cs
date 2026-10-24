/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;

namespace OpenSim.CharacterPublish;

/// <summary>The asset, inventory and folder type numbers a publish bundle uses (the viewer's llassettype.h, llinventorytype.h, llfoldertype.h).</summary>
public static class BundleTypes
{
    public const int AssetClothing = 5;
    public const int AssetObject = 6;
    public const int AssetNotecard = 7;
    public const int AssetBodypart = 13;
    public const int AssetLink = 24;
    public const int AssetMesh = 49;

    public const int InvNotecard = 7;
    public const int InvAttachment = 17;
    public const int InvWearable = 18;

    public const short FolderCurrentOutfit = 46;
    public const short FolderOutfit = 47;
    public const short FolderMyOutfits = 48;

    /// <summary>The wearable types 0-3 (shape, skin, hair, eyes) are body parts; 4-16 are clothing.</summary>
    public static int AssetTypeOfWearable(int wearableType) => wearableType <= 3 ? AssetBodypart : AssetClothing;
}

/// <summary>
/// Ids derived from content, so assembling the same input twice gives the same ids. An asset id is the first 16 bytes of the
/// SHA-256 of its bytes: the asset service keeps the first asset stored under an id and never overwrites it, so a weak hash
/// would let someone park a different asset under an id another publish will use.
/// </summary>
public static class ContentId
{
    public static UUID OfBytes(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(data, h);
        return new UUID(h[..16].ToArray(), 0);
    }

    /// <summary>An id for an inventory folder or item, derived from the parts that name it (owner, folder number, role).</summary>
    public static UUID Derive(params string[] parts) => OfBytes(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
}

/// <summary>One asset the bundle stores. The bytes are in the file named by <see cref="File"/>, next to bundle.json.</summary>
public sealed record BundleAsset(UUID Id, int AssetType, string File, int Length, string Name);

/// <summary>An inventory item the bundle creates, with every field the inventory service needs.</summary>
public sealed record BundleItem(
    UUID Id,
    // The key of the folder the item goes in; the bundle has one folder, <see cref="Bundle.OutfitFolder"/>.
    string Folder,
    string Role,
    string Name,
    string Description,
    UUID AssetId,
    int AssetType,
    int InvType,
    uint Flags,
    uint BasePermissions,
    uint CurrentPermissions,
    uint NextPermissions,
    uint GroupPermissions,
    uint EveryonePermissions,
    UUID CreatorId,
    UUID OwnerId,
    int CreationDate,
    int SalePrice,
    byte SaleType);

/// <summary>The new outfit folder. Its parent is the owner's My Outfits folder, which the publisher finds or creates.</summary>
public sealed record BundleFolder(UUID Id, string Key, string Name, short Type, string Parent, UUID OwnerId);

/// <summary>
/// How to save the owner's current look before the outfit is written: a new Outfit folder of links to the items in the
/// Current Outfit folder. A plan only; the links are made at publish time from the live Current Outfit folder.
/// </summary>
public sealed record CurrentLookPlan(
    UUID FolderId,
    string FolderName,
    short FolderType,
    string Parent,
    UUID OwnerId,
    short SourceFolderType,
    int LinkAssetType,
    uint LinkPermissions,
    int CreationDate);

/// <summary>A complete publish bundle: every asset, the items and folder that hold them, and the current-look plan.</summary>
public sealed record Bundle(
    int FormatVersion,
    string Character,
    int FolderNumber,
    UUID OwnerId,
    UUID CreatorId,
    int CreationDate,
    IReadOnlyList<BundleAsset> Assets,
    // Asset ids the wearables use that already exist on the grid (textures). Not in the bundle.
    IReadOnlyList<UUID> ExternalAssets,
    BundleFolder OutfitFolder,
    IReadOnlyList<BundleItem> Items,
    CurrentLookPlan? CurrentLook)
{
    public const int CurrentFormatVersion = 1;
    public const string MyOutfitsParent = "my-outfits";
    public const string OutfitFolderKey = "outfit";
}
