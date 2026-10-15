/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Archive.Inspection;

/// <summary>
/// Which asset types, inventory types and system folder names belong together. The pairs are a copy of the table in
/// OpenSim.Framework SLUtil.MAPPINGS (a parity test reads the private table by reflection), so the checks need no grid.
/// </summary>
public static class TypeAgreement
{
    private static readonly Dictionary<int, int[]> InventoryTypesOf = new Dictionary<int, int[]>
    {
        [(int)AssetType.Texture] = new[] { (int)InventoryType.Texture, (int)InventoryType.Snapshot },
        [(int)AssetType.TextureTGA] = new[] { (int)InventoryType.Texture },
        [(int)AssetType.ImageTGA] = new[] { (int)InventoryType.Texture },
        [(int)AssetType.ImageJPEG] = new[] { (int)InventoryType.Texture },
        [(int)AssetType.Sound] = new[] { (int)InventoryType.Sound },
        [(int)AssetType.SoundWAV] = new[] { (int)InventoryType.Sound },
        [(int)AssetType.CallingCard] = new[] { (int)InventoryType.CallingCard },
        [(int)AssetType.Landmark] = new[] { (int)InventoryType.Landmark },
        [(int)AssetType.Clothing] = new[] { (int)InventoryType.Wearable },
        [(int)AssetType.Bodypart] = new[] { (int)InventoryType.Wearable },
        [(int)AssetType.Object] = new[] { (int)InventoryType.Object, (int)InventoryType.Attachment },
        [(int)AssetType.Notecard] = new[] { (int)InventoryType.Notecard },
        [(int)AssetType.LSLText] = new[] { (int)InventoryType.LSL },
        [(int)AssetType.LSLBytecode] = new[] { (int)InventoryType.LSL },
        [(int)AssetType.Animation] = new[] { (int)InventoryType.Animation },
        [(int)AssetType.Gesture] = new[] { (int)InventoryType.Gesture },
        [(int)AssetType.Simstate] = new[] { (int)InventoryType.Snapshot },
        [(int)AssetType.Mesh] = new[] { (int)InventoryType.Mesh },
        [(int)AssetType.Material] = new[] { (int)InventoryType.Material },
        [(int)AssetType.Folder] = new[] { (int)InventoryType.Folder }
    };

    /// <summary>The table, for the parity test.</summary>
    public static IReadOnlyDictionary<int, int[]> Table => InventoryTypesOf;

    public static bool IsLink(int assetType) => assetType == (int)AssetType.Link || assetType == (int)AssetType.LinkFolder;

    /// <summary>True when the item's asset type and inventory type are a pair in the table. Types outside the table and links are not judged.</summary>
    public static bool InventoryTypeAgrees(int assetType, int invType)
    {
        if (IsLink(assetType) || !InventoryTypesOf.TryGetValue(assetType, out int[] allowed))
            return true;
        return Array.IndexOf(allowed, invType) >= 0;
    }

    private static int Family(int assetType)
    {
        switch ((AssetType)assetType)
        {
            case AssetType.Texture:
            case AssetType.TextureTGA:
            case AssetType.ImageTGA:
            case AssetType.ImageJPEG:
                return (int)AssetType.Texture;
            case AssetType.Sound:
            case AssetType.SoundWAV:
                return (int)AssetType.Sound;
            case AssetType.LSLText:
            case AssetType.LSLBytecode:
                return (int)AssetType.LSLText;
            default:
                return assetType;
        }
    }

    /// <summary>Texture encodings are one family, sound encodings are one family, script text and bytecode are one family.</summary>
    public static bool SameFamily(int a, int b) => Family(a) == Family(b);

    /// <summary>
    /// A sculpt field names a texture for a sculpted prim and a mesh asset for a mesh prim, so either is right there.
    /// </summary>
    public static bool ReferenceAgrees(ReferenceKind kind, int claimed, int actual)
    {
        if (SameFamily(claimed, actual))
            return true;
        if (kind == ReferenceKind.Sculpt && Family(actual) == (int)AssetType.Mesh)
            return true;
        // render material ids and terrain PBR ids name either the viewer material asset or the OpenSim material
        if ((kind == ReferenceKind.Material || kind == ReferenceKind.TerrainPbr) && (actual == (int)AssetType.Material || actual == (int)AssetType.OSMaterial))
            return true;
        return false;
    }

    /// <summary>Default names of the grid's single-instance system folders (see DESIGN.md B.2), by folder type.</summary>
    public static readonly IReadOnlyDictionary<string, string> SystemFolderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["My Inventory"] = "Root",
        ["Current Outfit"] = "CurrentOutfit",
        ["Trash"] = "Trash",
        ["Lost And Found"] = "LostAndFound",
        ["My Suitcase"] = "Suitcase",
        ["Favorites"] = "Favorites",
        ["Photo Album"] = "Snapshot",
        ["My Outfits"] = "MyOutfits"
    };
}
