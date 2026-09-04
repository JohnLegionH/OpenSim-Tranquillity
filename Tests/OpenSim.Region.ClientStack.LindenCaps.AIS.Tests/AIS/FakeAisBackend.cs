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

    // ---------------- mutators (A2) ----------------

    /// <summary>Set to false to make the service refuse writes, as XInventoryService does when AllowDelete is off.</summary>
    public bool AllowWrite = true;

    /// <summary>
    /// Set to true to reproduce this tree's real DeleteFolders behaviour: the two-argument overload on
    /// IInventoryService is onlyIfTrash = true, so a folder outside Trash is silently skipped and true is still
    /// returned (XInventoryService.cs:459-478).
    /// </summary>
    public bool DeleteFoldersOnlyIfTrash = false;

    /// <summary>Fault injection: return false to make this AddItem fail. Null means every add succeeds.</summary>
    public Func<InventoryItemBase, bool> AddItemGate;

    /// <summary>Fault injection: return false to make this DeleteItems fail. Null means every delete succeeds.</summary>
    public Func<IReadOnlyList<UUID>, bool> DeleteItemsGate;

    /// <summary>Runs after every successful write, so a test can change the store underneath the handler.</summary>
    public Action OnWrite;

    /// <summary>The data layer bumps a folder's version on every store or delete of its contents (S0a V6).</summary>
    private void Bump(UUID folderId)
    {
        if (Folders.TryGetValue(folderId, out var folder)) folder.Version = (ushort)(folder.Version + 1);
    }

    public bool AddFolder(InventoryFolderBase folder)
    {
        Calls.Add($"AddFolder({folder.ID})");
        if (!AllowWrite) return false;
        Folders[folder.ID] = folder;
        Bump(folder.ParentID);
        OnWrite?.Invoke();
        return true;
    }

    public bool AddItem(InventoryItemBase item)
    {
        Calls.Add($"AddItem({item.Name})");
        if (!AllowWrite) return false;
        if (AddItemGate is not null && !AddItemGate(item)) return false;
        Items[item.ID] = item;
        Bump(item.Folder);
        OnWrite?.Invoke();
        return true;
    }

    public bool UpdateItem(InventoryItemBase item)
    {
        Calls.Add($"UpdateItem({item.ID})");
        if (!AllowWrite || !Items.ContainsKey(item.ID)) return false;
        Items[item.ID] = item;
        Bump(item.Folder);
        OnWrite?.Invoke();
        return true;
    }

    public bool UpdateFolder(InventoryFolderBase folder)
    {
        Calls.Add($"UpdateFolder({folder.ID})");
        if (!AllowWrite || !Folders.ContainsKey(folder.ID)) return false;
        Folders[folder.ID] = folder;
        Bump(folder.ParentID);
        OnWrite?.Invoke();
        return true;
    }

    public bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds)
    {
        Calls.Add($"DeleteItems[{itemIds.Count}]");
        if (!AllowWrite || agentId != Owner) return false;
        if (DeleteItemsGate is not null && !DeleteItemsGate(itemIds)) return false;
        foreach (var id in itemIds)
            if (Items.TryGetValue(id, out var item)) { Items.Remove(id); Bump(item.Folder); }
        OnWrite?.Invoke();
        return true;
    }

    /// <summary>Recursive, and with the real service's trash gate available for a test to switch on.</summary>
    public bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds, bool onlyIfTrash)
    {
        Calls.Add($"DeleteFolders[{folderIds.Count}, onlyIfTrash={onlyIfTrash}]");
        if (!AllowWrite || agentId != Owner) return false;
        foreach (var id in folderIds)
        {
            if (!Folders.TryGetValue(id, out var folder)) continue;
            if (onlyIfTrash && DeleteFoldersOnlyIfTrash && !UnderTrash(id)) continue;   // as the real service does
            Purge(id);
            Folders.Remove(id);
            Bump(folder.ParentID);
        }
        OnWrite?.Invoke();
        return true;
    }

    public bool PurgeFolder(InventoryFolderBase folder)
    {
        Calls.Add($"PurgeFolder({folder.ID})");
        if (!AllowWrite) return false;
        Purge(folder.ID);
        Bump(folder.ID);
        OnWrite?.Invoke();
        return true;
    }

    /// <summary>Everything under a folder, depth first.</summary>
    private void Purge(UUID folderId)
    {
        foreach (var child in Folders.Values.Where(f => f.ParentID == folderId).Select(f => f.ID).ToList())
        {
            Purge(child);
            Folders.Remove(child);
        }
        foreach (var item in Items.Values.Where(i => i.Folder == folderId).Select(i => i.ID).ToList())
            Items.Remove(item);
    }

    /// <summary>The Trash / Lost And Found test the real service applies before it will delete a folder.</summary>
    public UUID TrashId = UUID.Zero;
    private bool UnderTrash(UUID folderId)
    {
        var id = folderId;
        for (var guard = 0; guard < 64 && Folders.TryGetValue(id, out var folder); guard++)
        {
            if (folder.ParentID == TrashId && !TrashId.IsZero()) return true;
            id = folder.ParentID;
        }
        return false;
    }
}
