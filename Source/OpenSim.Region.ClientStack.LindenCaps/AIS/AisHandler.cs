using System;
using System.Collections.Generic;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using Microsoft.Extensions.Logging;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>Which cap this handler is mounted as. The routes are the same; the library one is read-only.</summary>
public enum AisMode
{
    /// <summary>InventoryAPIv3: the agent's own inventory.</summary>
    Inventory,
    /// <summary>LibraryAPIv3: the shared library, owned by the library owner, read-only (mutations answer 405).</summary>
    Library,
}

/// <summary>
/// The InventoryAPIv3 / LibraryAPIv3 cap handler: one per agent per cap, mounted at a random cap path. Parses the
/// request with <see cref="AisRouter"/> and dispatches on <see cref="AisOperation"/>. Holds only an agent id, a
/// backend and its cap path (Ledger P-2) so the same class can be mounted on Robust in Phase 2.
///
/// <para>A1 implemented the **read** surface: <c>GET /item</c>, <c>/category/{id}/children</c> (whole and subset),
/// <c>/categories</c>, <c>/links</c>, <c>/category/current/links</c> and <c>/orphans</c>. A2 adds the
/// **single-object mutations**: <c>PATCH /item</c>, <c>PATCH /category</c>, <c>DELETE /item</c> and
/// <c>DELETE /category</c>, each answering with the delta envelope of §1d-bis. SlamFolder, PurgeDescendents,
/// CreateInventory and COPY are still 501 (A3/A4). Every mutation is 405 on the library cap.</para>
///
/// <para><b>Which collections a response carries.</b> The viewer derives a folder's descendent count only when
/// <c>_embedded</c> has all three of <c>categories</c>, <c>items</c>, <c>links</c> — or, for a Current Outfit or
/// Outfit folder, from <c>links</c> alone (spec §1c, <c>llaisapi.cpp:1466-1482</c>). It then uses that count to
/// accept the folder's <c>version</c>. So a route that returns a folder's **complete** contents
/// (<c>/children</c>) emits all three, and a route that returns a **partial** view (<c>/categories</c>,
/// <c>/links</c>, a subset) emits only the collection it was asked for — emitting empty siblings there would make
/// the viewer compute a wrong descendent count and version a folder it has not actually seen. This refines risk
/// A-R3, which said "always all three".</para>
/// </summary>
public sealed class AisHandler : SimpleStreamHandler
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(AisHandler));

    private readonly UUID m_agentId;
    private readonly IAisInventoryBackend m_backend;
    private readonly string m_capPath;
    private readonly AisMode m_mode;

    /// <summary>
    /// Where a library COPY writes, and as whom. Only the library cap has these: its own <see cref="AgentId"/> is
    /// the library owner, so it needs the viewing agent's inventory to copy *into*. Null on the inventory cap,
    /// where COPY is not an operation the viewer sends.
    /// </summary>
    private readonly IAisInventoryBackend m_destination;
    private readonly UUID m_destinationAgentId;

    public AisHandler(string capPath, UUID agentId, IAisInventoryBackend backend, AisMode mode = AisMode.Inventory,
        IAisInventoryBackend destination = null, UUID destinationAgentId = default)
        : base(capPath, mode == AisMode.Library ? AISv3Module.LibraryCapName : AISv3Module.CapName)
    {
        m_capPath = capPath;
        m_agentId = agentId;
        m_backend = backend ?? throw new ArgumentNullException(nameof(backend));
        m_mode = mode;
        m_destination = destination;
        m_destinationAgentId = destinationAgentId;
    }

    public UUID AgentId => m_agentId;
    public string CapPath => m_capPath;
    public AisMode Mode => m_mode;

    protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        var route = AisRouter.Parse(httpRequest.HttpMethod, httpRequest.RawUrl ?? httpRequest.UriPath, m_capPath);
        // A6: without this a request that arrives and fails is indistinguishable in the log from one that never
        // arrived, which is exactly what made the first live run take a code read to diagnose.
        if (m_log.IsEnabled(LogLevel.Debug))
            m_log.LogDebug("[AIS]: {Verb} {Url} -> {Operation} (cap {Mode}, agent {Agent})",
                httpRequest.HttpMethod, httpRequest.RawUrl ?? httpRequest.UriPath, route.Operation, m_mode, m_agentId);
        Dispatch(route, httpRequest, httpResponse);
    }

    /// <summary>Dispatch a parsed route. Public so the HTTP-level tests can drive it without a scene.</summary>
    public void Dispatch(AisRoute route, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        var raw = ReadBodyOsd(httpRequest);
        var body = raw as OSDMap ?? new OSDMap();
        try
        {
            switch (route.Operation)
            {
                case AisOperation.Unknown:
                    WriteError(httpResponse, HttpStatusCode.NotFound, "no such AIS v3 route", route);
                    return;

                case AisOperation.FetchItem: FetchItem(route, httpResponse); return;
                case AisOperation.FetchCategoryChildren: FetchChildren(route, httpResponse); return;
                case AisOperation.FetchCategorySubset: FetchSubset(route, httpResponse); return;
                case AisOperation.FetchCategoryCategories: FetchCategories(route, httpResponse); return;
                case AisOperation.FetchCategoryLinks:
                case AisOperation.FetchCOF: FetchLinks(route, httpResponse); return;
                case AisOperation.FetchOrphans: FetchOrphans(route, httpResponse); return;
                case AisOperation.CopyCategory: CopyCategory(route, httpRequest, httpResponse); return;

                case AisOperation.UpdateItem:
                case AisOperation.UpdateCategory:
                case AisOperation.RemoveItem:
                case AisOperation.RemoveCategory:
                case AisOperation.SlamFolder:
                case AisOperation.CreateInventory:
                case AisOperation.PurgeDescendents:
                    // COPY is the exception: it is a library-cap operation by design (the viewer sends it to
                    // {lib}, spec 1a row 5), and it writes into the *agent's* inventory, not the library.
                    if (m_mode == AisMode.Library)
                    {
                        WriteError(httpResponse, HttpStatusCode.MethodNotAllowed, $"{route.Operation} is not allowed on the library: LibraryAPIv3 is read-only", route);
                        return;
                    }
                    switch (route.Operation)
                    {
                        case AisOperation.UpdateItem: UpdateItem(route, body, httpResponse); return;
                        case AisOperation.UpdateCategory: UpdateCategory(route, body, httpResponse); return;
                        case AisOperation.RemoveItem: RemoveItem(route, httpResponse); return;
                        case AisOperation.SlamFolder: SlamFolder(route, raw, httpResponse); return;
                        case AisOperation.CreateInventory: CreateInventory(route, body, httpResponse); return;
                        case AisOperation.PurgeDescendents: PurgeDescendents(route, httpResponse); return;
                        default: RemoveCategory(route, httpResponse); return;
                    }

                default:
                    // Every mutation. On the library cap they are refused outright; on the inventory cap they are
                    // not implemented yet (A2). Both bodies are flat maps the viewer's update parser ignores (§1f).
                    if (m_mode == AisMode.Library)
                        WriteError(httpResponse, HttpStatusCode.MethodNotAllowed, $"{route.Operation} is not allowed on the library: LibraryAPIv3 is read-only", route);
                    else
                        WriteError(httpResponse, HttpStatusCode.NotImplemented, $"{route.Operation} is not implemented", route);
                    return;
            }
        }
        catch (Exception ex)
        {
            WriteError(httpResponse, HttpStatusCode.InternalServerError, ex.Message, route);
        }
    }

    // ------------------------------------------------------------------ the read routes

    /// <summary>GET /item/{id} — an item, or a link map when the row is a link (§1c: <c>linked_id</c> selects parseLink).</summary>
    private void FetchItem(AisRoute route, IOSHttpResponse response)
    {
        var item = m_backend.GetItem(m_agentId, route.Id);
        if (item is null) { WriteError(response, HttpStatusCode.NotFound, $"no item {route.Id}", route); return; }
        var body = AisEnvelope.IsLink(item) ? AisEnvelope.Link(item, m_agentId) : AisEnvelope.Item(item, m_agentId);
        Write(response, body, route);
    }

    /// <summary>
    /// GET /category/{id}/children?depth=N — the folder with its complete contents, expanded N generations
    /// (<see cref="AisInventory.Walk"/>). All three collections at every expanded level.
    /// </summary>
    private void FetchChildren(AisRoute route, IOSHttpResponse response)
    {
        // MAX_FOLDER_DEPTH_REQUEST (llaisapi.cpp:58): the viewer clamps every depth it sends to 50, so anything
        // above that is a client we do not know asking the region to walk further than any viewer would use.
        var depth = System.Math.Clamp(route.Depth, 0, AisInventory.MaxDepth);
        var walked = AisInventory.Walk(m_backend, m_agentId, route.Id, depth);
        if (walked.Count == 0) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }

        var expanded = new Dictionary<UUID, AisFolderContents>();
        foreach (var c in walked) expanded[c.Folder.ID] = c;
        Write(response, Expand(walked[0], expanded), route);
    }

    /// <summary>
    /// A folder as a category map with all three collections; each sub-folder is expanded in turn when the walk
    /// reached it, and appears as a bare category map (no <c>_embedded</c>) when it did not.
    /// </summary>
    private OSDMap Expand(AisFolderContents contents, Dictionary<UUID, AisFolderContents> expanded)
    {
        var categories = new OSDMap();
        var remaining = Without(expanded, contents.Folder.ID);
        foreach (var child in contents.SubFolders)
        {
            categories[child.ID.ToString()] = remaining.TryGetValue(child.ID, out var childContents)
                ? Expand(childContents, remaining)
                : AisEnvelope.Category(child, m_agentId);
        }
        var embedded = AisEnvelope.EmbeddedMap(categories, AisEnvelope.ItemsMap(contents.Items, m_agentId), AisEnvelope.LinksMap(contents.Links, m_agentId));
        return AisEnvelope.Category(contents.Folder, m_agentId, embedded);
    }

    /// <summary>Guards the recursion against a cycle in the folder graph (a folder that is its own ancestor).</summary>
    private static Dictionary<UUID, AisFolderContents> Without(Dictionary<UUID, AisFolderContents> map, UUID id)
    {
        var copy = new Dictionary<UUID, AisFolderContents>(map);
        copy.Remove(id);
        return copy;
    }

    /// <summary>
    /// GET /category/{id}/children?depth=N&amp;children=a,b,... — only the named children. The viewer ignores the
    /// top-level category for a subset and parses <c>_embedded</c> one level shallower (§1c,
    /// <c>llaisapi.cpp:1194-1202</c>), so the folder is still the envelope but its collections carry only what was
    /// asked for. A named child that does not exist is simply absent: the viewer asked for it, so its absence is
    /// the answer, and failing the whole request would lose the children that do exist.
    /// </summary>
    private void FetchSubset(AisRoute route, IOSHttpResponse response)
    {
        var contents = AisInventory.GetContents(m_backend, m_agentId, route.Id);
        if (contents is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }

        var wanted = new HashSet<UUID>(route.Children);
        var categories = new OSDMap();
        foreach (var child in contents.SubFolders)
            if (wanted.Contains(child.ID)) categories[child.ID.ToString()] = AisEnvelope.Category(child, m_agentId);
        var items = new OSDMap();
        foreach (var item in contents.Items)
            if (wanted.Contains(item.ID)) items[item.ID.ToString()] = AisEnvelope.Item(item, m_agentId);
        var links = new OSDMap();
        foreach (var link in contents.Links)
            if (wanted.Contains(link.ID)) links[link.ID.ToString()] = AisEnvelope.Link(link, m_agentId);

        Write(response, AisEnvelope.Category(contents.Folder, m_agentId, AisEnvelope.EmbeddedMap(categories, items, links)), route);
    }

    /// <summary>GET /category/{id}/categories — sub-folders only, so <c>_embedded</c> carries <c>categories</c> alone.</summary>
    private void FetchCategories(AisRoute route, IOSHttpResponse response)
    {
        var folder = m_backend.GetFolder(m_agentId, route.Id);
        if (folder is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }
        var categories = new OSDMap();
        foreach (var child in m_backend.GetSubFolders(m_agentId, route.Id) ?? (IReadOnlyList<InventoryFolderBase>)Array.Empty<InventoryFolderBase>())
            categories[child.ID.ToString()] = AisEnvelope.Category(child, m_agentId);
        var embedded = new OSDMap { [AisEnvelope.Categories] = categories };
        Write(response, AisEnvelope.Category(folder, m_agentId, embedded), route);
    }

    /// <summary>
    /// GET /category/{id}/links and GET /category/current/links — the folder and everything its links point to.
    /// <c>_embedded</c> carries <c>links</c> (the link rows) and, so the viewer has the targets it will need,
    /// <c>items</c> holding the **link targets** — the items the links resolve to, not the links themselves. That
    /// is what the existing descendents cap sends for the same reason ("viewers are lasy and want a copy of the
    /// linked item sent before the link to it", <c>FetchInvDescHandler.cs:429</c>), and what
    /// <see cref="AisInventory.ResolveLinkTargets"/> gathers.
    ///
    /// <para>For a Current Outfit or Outfit folder the viewer takes the descendent count from <c>links</c> alone
    /// (§1c), which is exactly this shape.</para>
    /// </summary>
    private void FetchLinks(AisRoute route, IOSHttpResponse response)
    {
        var folderId = route.Id;
        if (route.Operation == AisOperation.FetchCOF)
        {
            var cof = AisInventory.GetCurrentOutfit(m_backend, m_agentId);
            if (cof is null) { WriteError(response, HttpStatusCode.NotFound, "the agent has no Current Outfit folder", route); return; }
            folderId = cof.ID;
        }

        var contents = AisInventory.GetContents(m_backend, m_agentId, folderId);
        if (contents is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {folderId}", route); return; }

        var targets = AisInventory.ResolveLinkTargets(m_backend, m_agentId, contents.Links);
        var embedded = new OSDMap
        {
            [AisEnvelope.Links] = AisEnvelope.LinksMap(contents.Links, m_agentId),
            [AisEnvelope.Items] = AisEnvelope.ItemsMap(targets, m_agentId),
        };
        Write(response, AisEnvelope.Category(contents.Folder, m_agentId, embedded), route);
    }

    /// <summary>
    /// GET /orphans — folders whose parent no longer exists. No <c>category_id</c>/<c>item_id</c> at top level, so
    /// the viewer parses <c>_embedded</c> straight (§1c). Orphaned items are not reported; see
    /// <see cref="AisInventory.FindOrphans"/> for why.
    /// </summary>
    private void FetchOrphans(AisRoute route, IOSHttpResponse response)
    {
        var orphans = AisInventory.FindOrphans(m_backend, m_agentId);
        var categories = new OSDMap();
        foreach (var folder in orphans.Folders) categories[folder.ID.ToString()] = AisEnvelope.Category(folder, m_agentId);
        var embedded = new OSDMap
        {
            [AisEnvelope.Categories] = categories,
            [AisEnvelope.Items] = AisEnvelope.ItemsMap(orphans.Items, m_agentId),
        };
        Write(response, new OSDMap { [AisEnvelope.Embedded] = embedded }, route);
    }

    // ------------------------------------------------------------------ the mutation routes (A2)

    /// <summary>
    /// The request body as LLSD, whatever its top-level type. A slam body is a bare **array**
    /// (<c>llappearancemgr.cpp:2209-2245</c>, <c>:1795-1833</c>), so it cannot be forced to a map here.
    /// </summary>
    private static OSD ReadBodyOsd(IOSHttpRequest request)
    {
        try
        {
            var stream = request.InputStream;
            if (stream is null) return new OSDMap();
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return new OSDMap();
            return OSDParser.DeserializeLLSDXml(bytes) ?? new OSDMap();
        }
        catch { return new OSDMap(); }
        finally { try { request.InputStream?.Dispose(); } catch { } }
    }

    /// <summary>
    /// PATCH /item/{id}. The updated item goes back as **top-level content** — on a mutation the viewer ignores
    /// anything embedded that is not in <c>_created_items</c> (§1c) — and its parent folder is listed in
    /// <c>_updated_category_versions</c>, without which the viewer discards even the zero-delta entry
    /// <c>parseItem</c> creates (§1d-bis, <c>llaisapi.cpp:1625-1629</c>). Fields this tree cannot store are
    /// ignored, not refused: the viewer sends the whole item map, so most keys carry unchanged values.
    /// </summary>
    private void UpdateItem(AisRoute route, OSDMap body, IOSHttpResponse response)
    {
        var item = m_backend.GetItem(m_agentId, route.Id);
        if (item is null) { WriteError(response, HttpStatusCode.NotFound, $"no item {route.Id}", route); return; }

        var applied = AisMutation.ApplyToItem(body, item);
        if (applied.Any && !m_backend.UpdateItem(item))
        {
            WriteError(response, HttpStatusCode.InternalServerError, $"the inventory service refused the update of item {route.Id}", route);
            return;
        }

        var envelope = AisEnvelope.Item(item, m_agentId);
        AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, item.Folder));
        Write(response, envelope, route);
    }

    /// <summary>
    /// PATCH /category/{id}. <c>parseCategory</c> creates zero-delta entries for the category **and** its parent
    /// (§1d-bis, <c>llaisapi.cpp:1419-1428</c>), so both are listed. <c>thumbnail</c> and <c>favorite</c> have no
    /// storage in this tree and are dropped.
    /// </summary>
    private void UpdateCategory(AisRoute route, OSDMap body, IOSHttpResponse response)
    {
        var folder = m_backend.GetFolder(m_agentId, route.Id);
        if (folder is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }

        var applied = AisMutation.ApplyToFolder(body, folder);
        if (applied.Any && !m_backend.UpdateFolder(folder))
        {
            WriteError(response, HttpStatusCode.InternalServerError, $"the inventory service refused the update of category {route.Id}", route);
            return;
        }

        var fresh = m_backend.GetFolder(m_agentId, route.Id) ?? folder;
        var envelope = AisEnvelope.Category(fresh, m_agentId);
        AisMutation.ReportVersion(envelope, fresh);
        AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, fresh.ParentID));
        Write(response, envelope, route);
    }

    /// <summary>
    /// DELETE /item/{id}. No content: the removal travels as <c>_removed_items</c> and the parent's new version
    /// as <c>_updated_category_versions</c> (§1d-bis). The parent is read **after** the delete, so the version is
    /// the post-operation one the data layer bumped (S0a V6, tree state T3/T4).
    /// </summary>
    private void RemoveItem(AisRoute route, IOSHttpResponse response)
    {
        var item = m_backend.GetItem(m_agentId, route.Id);
        if (item is null) { WriteError(response, HttpStatusCode.NotFound, $"no item {route.Id}", route); return; }
        var parentId = item.Folder;

        if (!m_backend.DeleteItems(m_agentId, new[] { route.Id }))
        {
            WriteError(response, HttpStatusCode.InternalServerError, $"the inventory service refused the delete of item {route.Id}", route);
            return;
        }

        var envelope = new OSDMap();
        AisMutation.ReportRemoved(envelope, AisMutation.RemovedItems, route.Id);
        AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, parentId));
        Write(response, envelope, route);
    }

    /// <summary>
    /// COPY /category/{sourceId}?tid= — CopyLibraryCategory (<see cref="AisCopy"/> for the permission rule this
    /// reuses and the no-rollback reasoning).
    ///
    /// <para>The destination folder id travels in the HTTP <c>Destination</c> header
    /// (<c>llcorehttputil.cpp:1135</c>, A1). The tid carries a quirk: when the viewer does **not** want
    /// sub-folders it appends the literal <c>,depth=0</c> to the tid value rather than adding a query parameter
    /// (<c>llaisapi.cpp:275-278</c>), which <see cref="AisRouter"/> already splits out into the <c>depth</c>
    /// query value — so <c>depth == 0</c> here means "this folder only".</para>
    ///
    /// <para>The destination is **not** subjected to the protected-folder rule. Copying a library folder into
    /// Clothing or into the inventory root is the ordinary case, and that rule governs moving, deleting and
    /// retyping a folder, not adding children to it — the same reconciliation as slam (A3) and purge. What is
    /// checked is the thing that matters: the destination must exist and be the agent's own folder.</para>
    /// </summary>
    private void CopyCategory(AisRoute route, IOSHttpRequest request, IOSHttpResponse response)
    {
        if (m_mode != AisMode.Library || m_destination is null)
        {
            WriteError(response, HttpStatusCode.NotImplemented,
                "COPY is a LibraryAPIv3 operation; this cap cannot serve it", route);
            return;
        }

        var destinationHeader = request.Headers["Destination"];
        if (!UUID.TryParse(destinationHeader, out var destinationId) || destinationId.IsZero())
        {
            WriteError(response, HttpStatusCode.BadRequest,
                "COPY needs a Destination header carrying the destination folder id", route);
            return;
        }

        var destinationFolder = m_destination.GetFolder(m_destinationAgentId, destinationId);
        if (destinationFolder is null)
        {
            WriteError(response, HttpStatusCode.NotFound, $"no destination category {destinationId}", route);
            return;
        }

        // ",depth=0" on the tid means this folder only (llaisapi.cpp:275-278)
        var copySubfolders = route.Depth != 0;

        var outcome = AisCopy.Run(m_backend, m_destination, m_agentId, m_destinationAgentId,
            route.Id, destinationId, copySubfolders);

        var envelope = new OSDMap();
        var categoryIds = new OSDArray();
        var itemIds = new OSDArray();
        var embeddedCategories = new OSDMap();
        var embeddedItems = new OSDMap();
        foreach (var folder in outcome.Categories)
        {
            categoryIds.Add(OSD.FromUUID(folder.ID));
            embeddedCategories[folder.ID.ToString()] = AisEnvelope.Category(folder, m_destinationAgentId,
                AisEnvelope.EmbeddedMap(new OSDMap(), new OSDMap(), new OSDMap()));
        }
        foreach (var item in outcome.Items)
        {
            itemIds.Add(OSD.FromUUID(item.ID));
            embeddedItems[item.ID.ToString()] = AisEnvelope.Item(item, m_destinationAgentId);
        }

        if (!outcome.Ok)
        {
            // additive, so a partial copy leaves what it made and risks nothing that existed before
            WriteError(response, HttpStatusCode.InternalServerError,
                $"{outcome.Failure}; {categoryIds.Count} categories and {itemIds.Count} items were created before the failure", route);
            return;
        }

        if (categoryIds.Count > 0) envelope["_created_categories"] = categoryIds;
        if (itemIds.Count > 0) envelope["_created_items"] = itemIds;
        if (embeddedCategories.Count > 0 || embeddedItems.Count > 0)
        {
            var embedded = new OSDMap();
            if (embeddedCategories.Count > 0) embedded[AisEnvelope.Categories] = embeddedCategories;
            if (embeddedItems.Count > 0) embedded[AisEnvelope.Items] = embeddedItems;
            envelope[AisEnvelope.Embedded] = embedded;
        }
        AisMutation.ReportVersion(envelope, m_destination.GetFolder(m_destinationAgentId, destinationId));
        Write(response, envelope, route);
    }
    /// <summary>
    /// DELETE /category/{id}/children — empty the folder, keeping the folder (<see cref="AisPurge"/>, which
    /// documents who calls it, why the deltas must be enumerated and what the composition costs).
    ///
    /// <para>The protected-folder rule is deliberately not applied: Trash and Lost and Found are protected types
    /// and are precisely the folders this operation exists to empty (<c>llinventorymodel.cpp:4125-4131</c>).</para>
    /// </summary>
    private void PurgeDescendents(AisRoute route, IOSHttpResponse response)
    {
        var folderId = route.Id;
        if (route.IsAlias)
        {
            var cof = AisInventory.GetCurrentOutfit(m_backend, m_agentId);
            if (cof is null) { WriteError(response, HttpStatusCode.NotFound, "the agent has no Current Outfit folder", route); return; }
            folderId = cof.ID;
        }

        var folder = m_backend.GetFolder(m_agentId, folderId);
        if (folder is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {folderId}", route); return; }

        var outcome = AisPurge.Run(m_backend, m_agentId, folder);

        var envelope = new OSDMap();
        foreach (var id in outcome.RemovedCategories) AisMutation.ReportRemoved(envelope, AisMutation.CategoriesRemoved, id);
        foreach (var id in outcome.RemovedItems) AisMutation.ReportRemoved(envelope, AisMutation.RemovedItems, id);
        AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, folderId));

        if (!outcome.Ok)
        {
            // Partly purged. A purge cannot be rolled back, so the honest answer is to say which children
            // survived; re-issuing the purge finishes the job (see AisPurge.Run).
            WriteError(response, HttpStatusCode.InternalServerError,
                $"category {folderId} was only partly purged; these children remain: {string.Join(", ", outcome.Survivors)}", route);
            return;
        }
        Write(response, envelope, route);
    }
    /// <summary>
    /// POST /category/{parentId}?tid= — create categories, items and links in that folder.
    ///
    /// <para><b>The route is the parent category itself</b>, not <c>/children</c>: <c>AISAPI::CreateInventory</c>
    /// builds <c>{inv}/category/{parentId}</c> (<c>llaisapi.cpp:115</c>).</para>
    ///
    /// <para><b>The body</b> is a map of arrays, and A4 pinned two of the three against their builders:</para>
    /// <list type="bullet">
    ///   <item><b><c>links</c> — verified.</b> <c>link_inventory_array</c> builds each entry with exactly
    ///   <c>linked_id</c>, <c>type</c> (<c>AT_LINK</c> or <c>AT_LINK_FOLDER</c>), <c>inv_type</c> (the
    ///   <i>target's</i> inventory type), <c>name</c> and <c>desc</c>, and sends them as
    ///   <c>new_inventory["links"]</c> (<c>llviewerinventory.cpp:1352-1370</c>). No <c>parent_id</c>: the folder
    ///   is the one in the URL.</item>
    ///   <item><b><c>items</c> — verified, and deliberately refused.</b> The one builder wraps the item's whole
    ///   <c>asLLSD()</c> with a null <c>item_id</c> and a null <c>asset_id</c> — <i>"don't know yet, whenever
    ///   server creates it"</i> — because the server is expected to mint the asset
    ///   (<c>llviewerinventory.cpp:1124-1157</c>). It sits inside <c>#ifdef USE_AIS_FOR_NC</c>, which is never
    ///   defined in that file, above the viewer's own comment <i>"not yet implemented within AIS3"</i>
    ///   (<c>:1120-1121</c>) — so a stock viewer never sends it. This handler answers **501** for a non-empty
    ///   <c>items</c> array rather than creating an item with no asset behind it, which is what A3's guess did.
    ///   </item>
    ///   <item><b><c>categories</c> — verified (A5).</b> <c>LLInventoryCategory::asAISCreateCatLLSD</c>
    ///   (<c>indra/llinventory/llinventory.cpp:1256-1276</c>) emits exactly <c>category_id</c> (null on a create,
    ///   since the viewer builds the category with <c>LLUUID::null</c>, <c>llinventorymodel.cpp:1038</c>),
    ///   <c>parent_id</c>, <c>type_default</c> as an **integer** preferred type, <c>name</c>, and — only when set —
    ///   <c>thumbnail</c>{<c>asset_id</c>} and <c>favorite</c>{<c>toggled</c>}. It is a base-class method, which is
    ///   why A4 could not find it in <c>llviewerinventory.cpp</c>. Everything it sends is accepted;
    ///   <c>thumbnail</c> and <c>favorite</c> have no column in this tree and are dropped, as they are for a
    ///   PATCH.</item>
    /// </list>
    ///    /// </summary>
    private void CreateInventory(AisRoute route, OSDMap body, IOSHttpResponse response)
    {
        var parent = m_backend.GetFolder(m_agentId, route.Id);
        if (parent is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }

        // Refused before anything is written, so a mixed body does not half-succeed. See the remarks above:
        // the viewer's own items builder is compiled out and expects the server to create the asset.
        if (body["items"] is OSDArray requested && requested.Count > 0)
        {
            WriteError(response, HttpStatusCode.NotImplemented,
                "creating inventory items through AIS is not implemented: the body carries a null asset_id for the server to fill, and this region does not create assets. The viewer's own path is disabled (USE_AIS_FOR_NC).", route);
            return;
        }

        var createdCategories = new OSDMap();
        var createdItems = new OSDMap();
        var createdLinks = new OSDMap();
        var categoryIds = new OSDArray();
        var itemIds = new OSDArray();

        if (body["categories"] is OSDArray categories)
        {
            foreach (var entry in categories)
            {
                if (entry is not OSDMap m) continue;
                // asAISCreateCatLLSD sends parent_id alongside the URL parent; honour it when it names a real
                // folder, and fall back to the folder the POST addressed, as an item create does.
                var bodyParent = m["parent_id"].AsUUID();
                var folder = new InventoryFolderBase(UUID.Random(), m["name"].AsString() ?? "", m_agentId,
                    (short)(m.ContainsKey("type_default") ? m["type_default"].AsInteger()
                        : m.ContainsKey("type") ? m["type"].AsInteger() : -1),
                    bodyParent.IsZero() ? route.Id : bodyParent, 1);
                if (!m_backend.AddFolder(folder))
                {
                    WriteError(response, HttpStatusCode.InternalServerError, $"could not create the category {folder.Name}", route);
                    return;
                }
                categoryIds.Add(OSD.FromUUID(folder.ID));
                createdCategories[folder.ID.ToString()] = AisEnvelope.Category(folder, m_agentId,
                    AisEnvelope.EmbeddedMap(new OSDMap(), new OSDMap(), new OSDMap()));
            }
        }

        foreach (var (key, isLink) in new[] { ("links", true) })
        {
            if (body[key] is not OSDArray array) continue;
            foreach (var entry in array)
            {
                if (entry is not OSDMap m) continue;
                var row = NewItem(m, isLink, route.Id);
                if (!m_backend.AddItem(row))
                {
                    WriteError(response, HttpStatusCode.InternalServerError, $"could not create {(isLink ? "the link" : "the item")} {row.Name}", route);
                    return;
                }
                itemIds.Add(OSD.FromUUID(row.ID));
                if (AisEnvelope.IsLink(row)) createdLinks[row.ID.ToString()] = AisEnvelope.Link(row, m_agentId);
                else createdItems[row.ID.ToString()] = AisEnvelope.Item(row, m_agentId);
            }
        }

        var envelope = new OSDMap();
        if (categoryIds.Count > 0) envelope["_created_categories"] = categoryIds;
        if (itemIds.Count > 0) envelope["_created_items"] = itemIds;
        if (createdCategories.Count > 0 || createdItems.Count > 0 || createdLinks.Count > 0)
        {
            var embedded = new OSDMap();
            if (createdCategories.Count > 0) embedded[AisEnvelope.Categories] = createdCategories;
            if (createdItems.Count > 0) embedded[AisEnvelope.Items] = createdItems;
            if (createdLinks.Count > 0) embedded[AisEnvelope.Links] = createdLinks;
            envelope[AisEnvelope.Embedded] = embedded;
        }
        AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, route.Id));
        Write(response, envelope, route);
    }

    /// <summary>An item or link row from a create body. Unknown keys are ignored, as they are for a PATCH.</summary>
    private InventoryItemBase NewItem(OSDMap m, bool isLink, UUID parentId)
    {
        var assetType = m.ContainsKey("type") ? m["type"].AsInteger()
            : isLink ? (int)AssetType.Link : (int)AssetType.Unknown;
        var linked = m["linked_id"].AsUUID();
        return new InventoryItemBase(UUID.Random(), m_agentId)
        {
            // the body may name a parent; absent, the object goes in the folder the POST addressed
            Folder = m.ContainsKey("parent_id") && !m["parent_id"].AsUUID().IsZero()
                ? m["parent_id"].AsUUID() : parentId,
            Name = m["name"].AsString() ?? "",
            Description = m["desc"].AsString() ?? "",
            AssetID = linked.IsZero() ? m["asset_id"].AsUUID() : linked,
            AssetType = assetType,
            InvType = m.ContainsKey("inv_type") ? m["inv_type"].AsInteger() : 0,
            Flags = (uint)m["flags"].AsInteger(),
            CreatorId = m_agentId.ToString(),
            CreationDate = (int)Util.UnixTimeSinceEpoch(),
            BasePermissions = (uint)OpenSim.Framework.PermissionMask.All,
            CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All,
            NextPermissions = (uint)OpenSim.Framework.PermissionMask.All,
        };
    }
    /// <summary>
    /// PUT /category/{id}/links — replace the folder's links (<see cref="AisSlam"/>, which documents the
    /// ordering and the exact guarantee). <c>current</c> resolves to the Current Outfit folder as it does for a
    /// fetch.
    ///
    /// <para>A slam is deliberately **not** subject to the protected-folder rule. That rule is the viewer's
    /// <c>lookupIsProtectedType</c>, which governs moving, deleting and retyping a folder
    /// (<c>llfoldertype.cpp:151-153</c>) — the Current Outfit folder is protected by it, and slamming the Current
    /// Outfit is the single most common thing the viewer does (<c>llappearancemgr.cpp:2251</c>).</para>
    ///
    /// <para>Only **links** are touched. Non-link items in the folder are left alone: the viewer builds the body
    /// from link rows only (<c>:1795-1833</c> switches on <c>AT_LINK</c> / <c>AT_LINK_FOLDER</c> and ignores
    /// everything else), so a slam has nothing to say about them.</para>
    ///
    /// <para>The envelope: the created links are named in <c>_created_items</c> and carried in
    /// <c>_embedded.links</c> — on a mutation the viewer accepts an embedded object only when its id is in
    /// <c>_created_items</c> (§1c) — the removed ones in <c>_removed_items</c>, and the folder's fresh version in
    /// <c>_updated_category_versions</c>. Each parsed link adds +1 to the folder's descendent count
    /// (<c>llaisapi.cpp:1310-1312</c>) and each removal −1 (<c>:1130</c>), so the arithmetic closes.</para>
    /// </summary>
    private void SlamFolder(AisRoute route, OSD rawBody, IOSHttpResponse response)
    {
        var folderId = route.Id;
        if (route.IsAlias)
        {
            var cof = AisInventory.GetCurrentOutfit(m_backend, m_agentId);
            if (cof is null) { WriteError(response, HttpStatusCode.NotFound, "the agent has no Current Outfit folder", route); return; }
            folderId = cof.ID;
        }

        var contents = AisInventory.GetContents(m_backend, m_agentId, folderId);
        if (contents is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {folderId}", route); return; }

        var wanted = AisSlam.ParseBody(rawBody);
        if (wanted is null)
        {
            WriteError(response, HttpStatusCode.BadRequest,
                "a slam body must be an LLSD array of link maps (name, desc, linked_id, type)", route);
            return;
        }

        var outcome = AisSlam.Run(m_backend, m_agentId, folderId, contents.Links, wanted);
        if (!outcome.Ok)
        {
            var detail = outcome.CompensationFailed
                ? $"{outcome.Failure}; the rollback also failed and these links remain: {string.Join(", ", outcome.Leftover)}"
                : outcome.Failure;
            WriteError(response, HttpStatusCode.InternalServerError, detail, route);
            return;
        }

        var envelope = new OSDMap();
        var createdIds = new OSDArray();
        var links = new OSDMap();
        foreach (var link in outcome.Created)
        {
            createdIds.Add(OSD.FromUUID(link.ID));
            links[link.ID.ToString()] = AisEnvelope.Link(link, m_agentId);
        }
        if (createdIds.Count > 0)
        {
            envelope["_created_items"] = createdIds;
            envelope[AisEnvelope.Embedded] = new OSDMap { [AisEnvelope.Links] = links };
        }
        foreach (var removed in outcome.Removed) AisMutation.ReportRemoved(envelope, AisMutation.RemovedItems, removed);
        AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, folderId));
        Write(response, envelope, route);
    }
    /// <summary>
    /// DELETE /category/{id}. Only the folder id goes in <c>_categories_removed</c>: the viewer purges the
    /// descendents itself (<c>LLInventoryModel::onObjectDeletedFromServer</c> calls
    /// <c>onDescendentsPurgedFromServer</c> for a category, <c>llinventorymodel.cpp:2019-2023</c>), so they are
    /// implied rather than enumerated.
    ///
    /// <para><b>Deletion is not restricted to Trash</b> (A2b, Ledger A-Q9 resolved). The call passes
    /// <c>onlyIfTrash: false</c> through the <c>IInventoryService</c> overload added in A2b, because the viewer
    /// deletes any non-protected folder wherever it sits (<c>llviewerinventory.cpp:1545-1568</c>, read in A2). The
    /// result is still verified by re-reading the folder rather than trusting the return value, since the service
    /// returns true even when it deleted nothing.</para>
    ///
    /// <para><b>Protected folders are refused</b> with 403. The viewer refuses to send RemoveCategory for a folder
    /// whose type <c>LLFolderType::lookupIsProtectedType</c> accepts (<c>llviewerinventory.cpp:1557-1561</c>), so
    /// this is defence in depth rather than a path the viewer exercises. That predicate's exact membership lives in
    /// <c>llfoldertype.cpp</c>, which is not a permitted read, so the server rule is stated in our own terms and
    /// marked UNVERIFIED against the viewer's list: a folder is protected when it is the agent's root or carries a
    /// system type, **except** <see cref="FolderType.Outfit"/>, which is an ordinary saved outfit that users delete
    /// routinely.</para>
    /// </summary>
    private void RemoveCategory(AisRoute route, IOSHttpResponse response)
    {
        var folder = m_backend.GetFolder(m_agentId, route.Id);
        if (folder is null) { WriteError(response, HttpStatusCode.NotFound, $"no category {route.Id}", route); return; }
        var parentId = folder.ParentID;

        if (IsProtected(folder))
        {
            WriteError(response, HttpStatusCode.Forbidden,
                $"category {route.Id} is a protected system folder and cannot be deleted", route);
            return;
        }

        m_backend.DeleteFolders(m_agentId, new[] { route.Id }, onlyIfTrash: false);
        if (m_backend.GetFolder(m_agentId, route.Id) is not null)
        {
            WriteError(response, HttpStatusCode.InternalServerError,
                $"the inventory service did not delete category {route.Id}", route);
            return;
        }

        var envelope = new OSDMap();
        AisMutation.ReportRemoved(envelope, AisMutation.CategoriesRemoved, route.Id);
        AisMutation.ReportVersion(envelope, m_backend.GetFolder(m_agentId, parentId));
        Write(response, envelope, route);
    }

    /// <summary>
    /// The folder types the viewer does **not** protect, taken from `LLFolderDictionary` itself
    /// (`indra/llinventory/llfoldertype.cpp:85-127`): every `addEntry` whose PROTECTED column is `false`. They are
    /// `FT_NONE`, the unused ensemble range `FT_ENSEMBLE_START`..`FT_ENSEMBLE_END`, `FT_OUTFIT`, and the three
    /// marketplace types. Everything else in the table is protected, and — importantly —
    /// `lookupIsProtectedType` **returns true for any type the table does not contain** (`:154-162`), which is why
    /// this is expressed as an allow-list with a protected default.
    /// </summary>
    private static readonly HashSet<short> UnprotectedFolderTypes = new()
    {
        (short)FolderType.None,
        (short)FolderType.Outfit,
        (short)FolderType.MarketplaceListings,
        (short)FolderType.MarkplaceStock,
        // FT_MARKETPLACE_VERSION (55) is unprotected in the viewer's table but this tree's FolderType has no
        // member for it, so it falls through to the protected default. It is a marketplace type no OpenSim grid
        // creates; see the session decisions.
    };

    /// <summary>The unused ensemble range, entered as unprotected in the viewer's table (`llfoldertype.cpp:106-109`).</summary>
    private const short EnsembleStart = 26;
    private const short EnsembleEnd = 45;

    /// <summary>
    /// A folder the server refuses to delete. This is the viewer's own rule: `LLFolderType::lookupIsProtectedType`
    /// looks the type up in `LLFolderDictionary` and returns that entry's PROTECTED flag, **defaulting to true for
    /// an unknown type** (`indra/llinventory/llfoldertype.cpp:154-162`). The viewer refuses to send RemoveCategory
    /// for such a folder (`llviewerinventory.cpp:1557-1561`), so this is defence in depth — but it also means a
    /// type this tree has and the viewer does not, such as `FolderType.Suitcase`, is protected automatically,
    /// which is the safe answer.
    ///
    /// <para>The agent's inventory root is refused as well. The viewer covers it by type
    /// (`FT_ROOT_INVENTORY` is protected), and this adds the structural case of a folder with no parent, which is
    /// either the root or an orphan and is not something a delete should walk into.</para>
    /// </summary>
    public static bool IsProtected(InventoryFolderBase folder)
    {
        if (folder is null) return true;
        if (folder.ParentID.IsZero()) return true;                      // the inventory root, or an orphan
        if (folder.Type >= EnsembleStart && folder.Type <= EnsembleEnd) return false;
        return !UnprotectedFolderTypes.Contains(folder.Type);           // unknown types are protected, as the viewer's are
    }
    // ------------------------------------------------------------------ wire

    /// <summary>
    /// 200 with an LLSD XML body. The viewer sends and reads LLSD XML and sets both <c>Content-Type</c> and
    /// <c>Accept</c> to <c>application/llsd+xml</c> on every AIS request (A-Q2, resolved A1:
    /// <c>llcorehttputil.cpp:1219-1222</c> <c>checkDefaultHeaders</c>; bodies serialised with
    /// <c>LLSDSerialize::toXML</c> at <c>:144</c>, <c>:169</c>, <c>:193</c> and parsed with
    /// <c>LLSDSerialize::fromXML</c> at <c>:123</c>).
    ///
    /// <para><c>tid</c> is echoed when the request carried one, so a client can correlate a response with the
    /// transaction it asked for. Nothing in the permitted viewer files reads it back, so it is inert there.</para>
    /// </summary>
    private static void Write(IOSHttpResponse response, OSDMap body, AisRoute route)
    {
        if (!route.Tid.IsZero()) body["tid"] = route.Tid;
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/llsd+xml";
        response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(body);
    }

    /// <summary>The error body: an LLSD map with conventional keys the viewer ignores (spec §1f).</summary>
    public static OSDMap ErrorBody(HttpStatusCode status, string message, AisRoute route)
    {
        return new OSDMap
        {
            ["error_code"] = (int)status,
            ["error_description"] = status.ToString(),
            ["message"] = message,
            ["operation"] = route.Operation.ToString(),
            ["verb"] = route.Verb,
            ["path"] = route.Path,
        };
    }

    private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string message, AisRoute route)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/llsd+xml";
        response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(ErrorBody(status, message, route));
    }
}
