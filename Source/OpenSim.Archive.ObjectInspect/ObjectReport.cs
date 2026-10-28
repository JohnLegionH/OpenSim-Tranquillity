/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.ObjectInspect;

/// <summary>The five permission masks a part, a prim inventory item or an inventory item carries.</summary>
public sealed class MaskSet
{
    public uint? Base { get; set; }
    public uint? Owner { get; set; }
    public uint? Group { get; set; }
    public uint? Everyone { get; set; }
    public uint? NextOwner { get; set; }

    /// <summary>The mask names in report order.</summary>
    public static readonly string[] Names = { "base", "owner", "group", "everyone", "next" };

    private const uint ModifyBit = 0x4000, CopyBit = 0x8000, TransferBit = 0x2000;

    /// <summary>Modify, copy and transfer as the letters M, C and T, a dash for each one the mask lacks, and "???" when the mask is absent.</summary>
    public static string Letters(uint? mask)
    {
        if (!mask.HasValue)
            return "???";
        uint m = mask.Value;
        return string.Concat((m & ModifyBit) != 0 ? 'M' : '-', (m & CopyBit) != 0 ? 'C' : '-', (m & TransferBit) != 0 ? 'T' : '-');
    }

    /// <summary>The mask with the given name (see <see cref="Names"/>).</summary>
    public uint? Get(string name)
    {
        switch (name)
        {
            case "base": return Base;
            case "owner": return Owner;
            case "group": return Group;
            case "everyone": return Everyone;
            case "next": return NextOwner;
            default: return null;
        }
    }

    /// <summary>One entry per mask that lacks modify, copy or transfer, as "name:letters" (for example "next:CT"); "name:absent" for a mask the file does not hold.</summary>
    public List<string> Lacks()
    {
        List<string> lacks = new List<string>();
        foreach (string name in Names)
        {
            uint? mask = Get(name);
            if (!mask.HasValue)
            {
                lacks.Add(name + ":absent");
                continue;
            }
            string missing = string.Concat((mask.Value & ModifyBit) == 0 ? "M" : "", (mask.Value & CopyBit) == 0 ? "C" : "", (mask.Value & TransferBit) == 0 ? "T" : "");
            if (missing.Length > 0)
                lacks.Add(name + ":" + missing);
        }
        return lacks;
    }
}

/// <summary>An item inside a part's contents (a prim inventory item).</summary>
public sealed class ItemEntry
{
    public string Name { get; set; }
    public UUID ItemId { get; set; }
    public UUID AssetId { get; set; }
    public int? AssetType { get; set; }
    public string AssetTypeName { get; set; }
    public int? InvType { get; set; }
    public UUID CreatorId { get; set; }
    public string CreatorName { get; set; }
    public UUID OwnerId { get; set; }
    public string OwnerName { get; set; }
    public UUID LastOwnerId { get; set; }
    public string LastOwnerName { get; set; }
    public UUID GroupId { get; set; }
    public MaskSet Masks { get; set; } = new MaskSet();
    public List<string> Lacks { get; set; } = new List<string>();
    public bool Flagged => Lacks.Count > 0;
}

public sealed class PartEntry
{
    /// <summary>Place in the object file, from 1: the root part first, then the others in the order the file lists them.</summary>
    public int FileOrder { get; set; }
    /// <summary>The link number the file stores for the part.</summary>
    public int LinkNum { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public UUID PartId { get; set; }
    /// <summary>Position inside the object: relative to the root part for a child part, the world position for the root part.</summary>
    public Vector3 Position { get; set; }
    /// <summary>The stored physics shape type: 0 prim, 1 none, 2 convex; -1 when the file has none (the region then uses prim).</summary>
    public int ShapeType { get; set; } = -1;
    public string ShapeTypeName { get; set; }
    public bool Phantom { get; set; }
    /// <summary>The part's prim shape fields as one text, used to match parts between two archives.</summary>
    public string ShapeKey { get; set; }
    public UUID CreatorId { get; set; }
    public string CreatorName { get; set; }
    public UUID OwnerId { get; set; }
    public string OwnerName { get; set; }
    public UUID LastOwnerId { get; set; }
    public string LastOwnerName { get; set; }
    public UUID GroupId { get; set; }
    public MaskSet Masks { get; set; } = new MaskSet();
    public List<string> Lacks { get; set; } = new List<string>();
    public List<ItemEntry> Items { get; set; } = new List<ItemEntry>();
    public bool Flagged => Lacks.Count > 0;
}

/// <summary>One linkset: an object file of an OAR, or one linkset of an object held by an inventory item of an IAR.</summary>
public sealed class ObjectEntry
{
    /// <summary>Number in the report, from 1.</summary>
    public int Index { get; set; }
    public string Name { get; set; }
    /// <summary>The archive entry the linkset was read from.</summary>
    public string Entry { get; set; }
    public string RegionDir { get; set; }
    public Vector3 GroupPosition { get; set; }
    public List<PartEntry> Parts { get; set; } = new List<PartEntry>();
    /// <summary>False when the stored link numbers are not 1, 2, 3 ... in file order (a single part may store 0 or 1).</summary>
    public bool LinkOrderOk { get; set; } = true;
    public string LinkOrderNote { get; set; }

    // IAR objects: the inventory item that holds the object, and which linkset of it this is
    public UUID ItemId { get; set; }
    public string ItemName { get; set; }
    public string FolderPath { get; set; }
    public UUID AssetId { get; set; }
    /// <summary>1-based number of the linkset inside a coalesced object; 0 for an object that is not coalesced.</summary>
    public int CoalescedIndex { get; set; }
    public int CoalescedCount { get; set; }
    public bool HasOffset { get; set; }
    /// <summary>The linkset's offset inside the coalesced object (the offsetx, offsety and offsetz attributes).</summary>
    public Vector3 Offset { get; set; }

    public bool Flagged => Parts.Any(p => p.Flagged || p.Items.Any(i => i.Flagged));
}

/// <summary>An inventory item of an IAR, with the masks of its own inventory record.</summary>
public sealed class InventoryItemEntry
{
    public string Name { get; set; }
    public UUID ItemId { get; set; }
    public UUID AssetId { get; set; }
    public int? AssetType { get; set; }
    public string AssetTypeName { get; set; }
    public string FolderPath { get; set; }
    public UUID CreatorId { get; set; }
    public string CreatorName { get; set; }
    public UUID OwnerId { get; set; }
    public string OwnerName { get; set; }
    public UUID GroupId { get; set; }
    public MaskSet Masks { get; set; } = new MaskSet();
    public List<string> Lacks { get; set; } = new List<string>();
    /// <summary>For an object item: the number of linksets the item holds (0 when the asset is not in the archive or is not an object).</summary>
    public int LinksetCount { get; set; }
    public string Note { get; set; }
    public bool Flagged => Lacks.Count > 0;
}

/// <summary>How often one account id appears, so a reader can map ids to people.</summary>
public sealed class PartyCount
{
    public UUID Id { get; set; }
    public string Name { get; set; }
    public int AsCreator { get; set; }
    public int AsOwner { get; set; }
    public int AsLastOwner { get; set; }
    public int AsGroup { get; set; }
}

public sealed class ReportSummary
{
    public int Objects { get; set; }
    public int Parts { get; set; }
    /// <summary>Items inside the contents of parts.</summary>
    public int PartItems { get; set; }
    /// <summary>Inventory items of an IAR.</summary>
    public int InventoryItems { get; set; }
    public int FlaggedObjects { get; set; }
    public int FlaggedParts { get; set; }
    public int FlaggedPartItems { get; set; }
    public int FlaggedInventoryItems { get; set; }
    public int LinkOrderProblems { get; set; }
    /// <summary>How many parts and items lack something, by mask name (see <see cref="MaskSet.Names"/>).</summary>
    public Dictionary<string, int> LackingByMask { get; set; } = new Dictionary<string, int>();
}

public sealed class ObjectReport
{
    public ArchiveKind Kind { get; set; }
    public string Version { get; set; }
    public InspectionStatus Status { get; set; }
    public string StopReason { get; set; }
    public List<ObjectEntry> Objects { get; set; } = new List<ObjectEntry>();
    public List<InventoryItemEntry> InventoryItems { get; set; } = new List<InventoryItemEntry>();
    public List<PartyCount> Parties { get; set; } = new List<PartyCount>();
    public ReportSummary Summary { get; set; } = new ReportSummary();
    public string Filter { get; set; }
    /// <summary>Objects in the archive before the filter was applied.</summary>
    public int ObjectsBeforeFilter { get; set; }
    public List<string> Problems { get; set; } = new List<string>();
}

public sealed class ReadOptions
{
    /// <summary>Keep only objects whose name contains this text (any case); for an IAR also the items whose name contains it. Null or empty keeps everything.</summary>
    public string NameFilter { get; set; }
}
