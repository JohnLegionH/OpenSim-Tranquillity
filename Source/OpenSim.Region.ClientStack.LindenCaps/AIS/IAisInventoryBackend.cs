using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// Everything the AIS v3 routes need from inventory, and nothing else. The handler is written against this
/// interface only (Ledger P-2): no Scene, no ScenePresence. Phase 1 implements it over the region's
/// <c>IInventoryService</c>; Phase 2 hosts the same handler on Robust over the service directly.
///
/// A0 defines the surface; every member is implemented in A1+. Links are ordinary <see cref="InventoryItemBase"/>
/// rows with <c>AssetType.Link</c> / <c>AssetType.LinkFolder</c>; the handler splits them out of item lists into
/// the <c>_embedded.links</c> collection (spec §1c), and resolves their targets with <see cref="GetItems"/>
/// (tree state T5: the service has no link-aware fetch).
/// </summary>
public interface IAisInventoryBackend
{
    /// <summary>The agent's folder of a system type (spec §1b: "current" = <c>FolderType.CurrentOutfit</c>); null if absent.</summary>
    InventoryFolderBase GetFolderForType(UUID agentId, FolderType type);

    /// <summary>A folder with its current <c>Version</c> freshly read (tree state T4); null if absent or not the agent's.</summary>
    InventoryFolderBase GetFolder(UUID agentId, UUID folderId);

    /// <summary>A folder's direct children: sub-folders and items (links included in <c>Items</c>), with the folder's version.</summary>
    InventoryCollection GetFolderContent(UUID agentId, UUID folderId);

    /// <summary>Items by id, e.g. link targets; absent ids are simply missing from the result.</summary>
    IReadOnlyList<InventoryItemBase> GetItems(UUID agentId, IReadOnlyList<UUID> itemIds);

    /// <summary>One item (or link) by id; null if absent or not the agent's.</summary>
    InventoryItemBase GetItem(UUID agentId, UUID itemId);

    /// <summary>Create a folder under its <c>ParentID</c>. The data layer bumps the parent's version (S0a V6).</summary>
    bool AddFolder(InventoryFolderBase folder);

    /// <summary>Create an item or link under its <c>Folder</c>. Bumps the parent's version (S0a V6).</summary>
    bool AddItem(InventoryItemBase item);

    /// <summary>Update an item's mutable fields (name, description, flags, parent on move).</summary>
    bool UpdateItem(InventoryItemBase item);

    /// <summary>Update a folder's mutable fields (name, type, parent on move).</summary>
    bool UpdateFolder(InventoryFolderBase folder);

    /// <summary>Delete items (and links) by id. Bumps each parent's version (S0a V6).</summary>
    bool DeleteItems(UUID agentId, IReadOnlyList<UUID> itemIds);

    /// <summary>Delete folders by id, recursively.</summary>
    bool DeleteFolders(UUID agentId, IReadOnlyList<UUID> folderIds);

    /// <summary>Delete a folder's contents but keep the folder (AIS PurgeDescendents).</summary>
    bool PurgeFolder(InventoryFolderBase folder);
}
