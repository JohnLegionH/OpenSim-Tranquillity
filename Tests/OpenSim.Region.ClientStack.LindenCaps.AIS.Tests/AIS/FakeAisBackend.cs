using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// An in-memory inventory for the AIS tests: folders and items keyed by id, with the same rules the region
/// backend has — a folder's contents are the rows whose parent is that folder, links are items with
/// <c>AssetType.Link</c>, and everything is scoped to one owner. Records what was asked of it so a test can prove
/// the handler resolved link targets with one batched call rather than N single ones.
/// </summary>
public sealed class FakeAisBackend : IAisInventoryBackend
{
    public readonly Dictionary<UUID, InventoryFolderBase> Folders = new();
    public readonly Dictionary<UUID, InventoryItemBase> Items = new();
    public readonly List<string> Calls = new();

    public UUID Owner;
    public UUID CurrentOutfitId = UUID.Zero;

    public FakeAisBackend(UUID owner) { Owner = owner; }

    // ---------------- building ----------------

    public InventoryFolderBase AddFolder(UUID id, UUID parent, string name, int version = 1, short type = -1)
    {
        var folder = new InventoryFolderBase(id, name, Owner, type, parent, (ushort)version);
        Folders[id] = folder;
        return folder;
    }

    public InventoryItemBase AddItem(UUID id, UUID folder, string name, int assetType = (int)AssetType.Clothing, UUID assetId = default)
    {
        var item = new InventoryItemBase(id, Owner)
        {
            Folder = folder,
            Name = name,
            Description = "",
            AssetType = assetType,
            InvType = (int)InventoryType.Wearable,
            AssetID = assetId.IsZero() ? UUID.Random() : assetId,
            CreationDate = 1756900000,
            Flags = 0,
            CreatorId = Owner.ToString(),
            BasePermissions = 0x7fffffff,
            CurrentPermissions = 0x7fffffff,
            NextPermissions = 532480,
            SalePrice = 0,
            SaleType = 0,
        };
        Items[id] = item;
        return item;
    }

    /// <summary>A link row: an item of <c>AssetType.Link</c> whose asset id is the target item's id.</summary>
    public InventoryItemBase AddLink(UUID id, UUID folder, string name, UUID target)
        => AddItem(id, folder, name, (int)AssetType.Link, target);

    // ---------------- IAisInventoryBackend ----------------

    public InventoryFolderBase GetFolderForType(UUID agentId, FolderType type)
    {
        Calls.Add($"GetFolderForType({type})");
        if (agentId != Owner) return null;
        if (type == FolderType.CurrentOutfit && !CurrentOutfitId.IsZero())
            return Folders.TryGetValue(CurrentOutfitId, out var cof) ? cof : null;
        return Folders.Values.FirstOrDefault(f => f.Type == (short)type);
    }

    public InventoryFolderBase GetFolder(UUID agentId, UUID folderId)
    {
        Calls.Add($"GetFolder({folderId})");
        if (agentId != Owner) return null;
        return Folders.TryGetValue(folderId, out var folder) ? folder : null;
    }

    public InventoryCollection GetFolderContent(UUID agentId, UUID folderId)
    {
        Calls.Add($"GetFolderContent({folderId})");
        if (agentId != Owner || !Folders.TryGetValue(folderId, out var folder)) return null;
        return new InventoryCollection
        {
            OwnerID = Owner,
            FolderID = folderId,
            Version = folder.Version,
            Folders = Folders.Values.Where(f => f.ParentID == folderId).ToList(),
            Items = Items.Values.Where(i => i.Folder == folderId).ToList(),
        };
    }

    public IReadOnlyList<InventoryFolderBase> GetSubFolders(UUID agentId, UUID folderId)
    {
        Calls.Add($"GetSubFolders({folderId})");
        if (agentId != Owner || !Folders.ContainsKey(folderId)) return Array.Empty<InventoryFolderBase>();
        return Folders.Values.Where(f => f.ParentID == folderId).ToList();
    }

    public IReadOnlyList<InventoryFolderBase> GetInventorySkeleton(UUID agentId)
    {
        Calls.Add("GetInventorySkeleton");
        return agentId != Owner ? Array.Empty<InventoryFolderBase>() : Folders.Values.ToList();
    }

    public IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds)
    {
        Calls.Add($"GetItems[{itemIds.Count}]");
        if (agentId != Owner) return Array.Empty<InventoryItemBase>();
        var found = new List<InventoryItemBase>();
        foreach (var id in itemIds) if (Items.TryGetValue(id, out var item)) found.Add(item);
        return found;
    }

    public InventoryItemBase GetItem(UUID agentId, UUID itemId)
    {
        Calls.Add($"GetItem({itemId})");
        if (agentId != Owner) return null;
        return Items.TryGetValue(itemId, out var item) ? item : null;
    }

    // mutators: A1 is the read surface, nothing calls these
    public bool AddFolder(InventoryFolderBase folder) => throw new InvalidOperationException("A1 must not mutate");
    public bool AddItem(InventoryItemBase item) => throw new InvalidOperationException("A1 must not mutate");
    public bool UpdateItem(InventoryItemBase item) => throw new InvalidOperationException("A1 must not mutate");
    public bool UpdateFolder(InventoryFolderBase folder) => throw new InvalidOperationException("A1 must not mutate");
    public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds) => throw new InvalidOperationException("A1 must not mutate");
    public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds) => throw new InvalidOperationException("A1 must not mutate");
    public bool PurgeFolder(InventoryFolderBase folder) => throw new InvalidOperationException("A1 must not mutate");
}
