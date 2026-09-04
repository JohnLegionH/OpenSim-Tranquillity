using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>One folder's direct contents, with links already split out of the item list (spec §1c).</summary>
public sealed record AisFolderContents(
    InventoryFolderBase Folder,
    IReadOnlyList<InventoryFolderBase> SubFolders,
    IReadOnlyList<InventoryItemBase> Items,
    IReadOnlyList<InventoryItemBase> Links);

/// <summary>Folders whose parent no longer exists (GET /orphans).</summary>
public sealed record AisOrphans(IReadOnlyList<InventoryFolderBase> Folders, IReadOnlyList<InventoryItemBase> Items);

/// <summary>
/// The composition the fetch routes need on top of the backend primitives: split links out of an item list,
/// resolve link targets, walk a folder to a depth, find orphans. Pure with respect to the backend — no Scene, no
/// ScenePresence (Ledger P-2) — so both the region host and Phase 2's Robust host share exactly this behaviour
/// rather than each re-deriving it.
/// </summary>
public static class AisInventory
{
    /// <summary>A folder's contents with links separated from items; null when the folder is not the agent's.</summary>
    public static AisFolderContents GetContents(IAisInventoryBackend backend, UUID agentId, UUID folderId)
    {
        var folder = backend.GetFolder(agentId, folderId);
        if (folder is null) return null;
        var collection = backend.GetFolderContent(agentId, folderId);
        var items = new List<InventoryItemBase>();
        var links = new List<InventoryItemBase>();
        if (collection?.Items is not null)
            foreach (var item in collection.Items)
                (AisEnvelope.IsLink(item) ? links : items).Add(item);
        var folders = (IReadOnlyList<InventoryFolderBase>)(collection?.Folders ?? new List<InventoryFolderBase>());
        return new AisFolderContents(folder, folders, items, links);
    }

    /// <summary>
    /// The items a set of links points at. There is no link-aware fetch in <c>IInventoryService</c> (tree state
    /// T5), so this does what the existing descendents cap does: collect the <c>AssetType.Link</c> rows' asset ids
    /// and resolve them with one <c>GetMultipleItems</c>
    /// (<c>Source/OpenSim.Capabilities.Handlers/FetchInventory/FetchInvDescHandler.cs:424-460</c>,
    /// <c>ProcessLinks</c>). Links to links are dropped for the same reason that handler drops them (:454-457):
    /// they are not observed in practice and following them invites cycles. Broken links simply resolve to
    /// nothing.
    /// </summary>
    public static IReadOnlyList<InventoryItemBase> ResolveLinkTargets(IAisInventoryBackend backend, UUID agentId, IEnumerable<InventoryItemBase> links)
    {
        var ids = new List<UUID>();
        foreach (var link in links ?? Enumerable.Empty<InventoryItemBase>())
            if (link.AssetType == (int)AssetType.Link && !link.AssetID.IsZero() && !ids.Contains(link.AssetID))
                ids.Add(link.AssetID);
        if (ids.Count == 0) return System.Array.Empty<InventoryItemBase>();

        var resolved = backend.GetItems(agentId, ids) ?? (IReadOnlyList<InventoryItemBase>)System.Array.Empty<InventoryItemBase>();
        var targets = new List<InventoryItemBase>(resolved.Count);
        foreach (var item in resolved)
            if (item is not null && item.AssetType != (int)AssetType.Link) targets.Add(item);
        return targets;
    }

    /// <summary>
    /// A folder and its descendants to <paramref name="depth"/> levels below it. <c>depth = 0</c> expands the
    /// requested folder only: its own <c>categories</c>, <c>items</c> and <c>links</c> are listed, and each child
    /// category appears as a bare map with no <c>_embedded</c>. Each further level of depth expands one more
    /// generation. Returned in breadth-first order with the requested folder first.
    ///
    /// <para>The depth number comes from the viewer (<c>depth=N</c> on the URL, spec §1a rows 10-13), but how much
    /// a server expands for a given N is the server's choice: the viewer only consumes what arrives, and its one
    /// hard rule is that a category must carry all three collections or none (spec §1c). This reading — N counts
    /// generations expanded below the requested folder — is therefore **UNVERIFIED** against the viewer and is
    /// stated here as the contract the tests pin.</para>
    /// </summary>
    public static IReadOnlyList<AisFolderContents> Walk(IAisInventoryBackend backend, UUID agentId, UUID rootId, int depth)
    {
        var expanded = new List<AisFolderContents>();
        var root = GetContents(backend, agentId, rootId);
        if (root is null) return expanded;
        expanded.Add(root);

        var frontier = new List<AisFolderContents> { root };
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<AisFolderContents>();
            foreach (var parent in frontier)
                foreach (var child in parent.SubFolders)
                {
                    var contents = GetContents(backend, agentId, child.ID);
                    if (contents is null) continue;
                    expanded.Add(contents);
                    next.Add(contents);
                }
            frontier = next;
        }
        return expanded;
    }

    /// <summary>The agent's Current Outfit folder (spec §1b, tree state T2); null when the agent has none.</summary>
    public static InventoryFolderBase GetCurrentOutfit(IAisInventoryBackend backend, UUID agentId)
        => backend.GetFolderForType(agentId, FolderType.CurrentOutfit);

    /// <summary>
    /// Folders whose <c>ParentID</c> names a folder that is not in the agent's skeleton — the only orphan class
    /// this tree can find without walking every folder's contents. Orphaned **items** are not reported: the
    /// inventory service has no query for them and finding them would mean listing every folder
    /// (<c>IInventoryService</c> surface, tree state T5). An empty result is therefore "no orphan folders", not
    /// "no orphans of any kind", and the route says so in its own documentation.
    /// </summary>
    public static AisOrphans FindOrphans(IAisInventoryBackend backend, UUID agentId)
    {
        var skeleton = backend.GetInventorySkeleton(agentId);
        if (skeleton is null || skeleton.Count == 0)
            return new AisOrphans(System.Array.Empty<InventoryFolderBase>(), System.Array.Empty<InventoryItemBase>());

        var known = new HashSet<UUID>();
        foreach (var folder in skeleton) known.Add(folder.ID);
        var orphans = new List<InventoryFolderBase>();
        foreach (var folder in skeleton)
            if (!folder.ParentID.IsZero() && !known.Contains(folder.ParentID)) orphans.Add(folder);
        return new AisOrphans(orphans, System.Array.Empty<InventoryItemBase>());
    }
}
