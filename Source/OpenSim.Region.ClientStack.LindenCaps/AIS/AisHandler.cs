using System;
using System.Collections.Generic;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;

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
    private readonly UUID m_agentId;
    private readonly IAisInventoryBackend m_backend;
    private readonly string m_capPath;
    private readonly AisMode m_mode;

    public AisHandler(string capPath, UUID agentId, IAisInventoryBackend backend, AisMode mode = AisMode.Inventory)
        : base(capPath, mode == AisMode.Library ? AISv3Module.LibraryCapName : AISv3Module.CapName)
    {
        m_capPath = capPath;
        m_agentId = agentId;
        m_backend = backend ?? throw new ArgumentNullException(nameof(backend));
        m_mode = mode;
    }

    public UUID AgentId => m_agentId;
    public string CapPath => m_capPath;
    public AisMode Mode => m_mode;

    protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        var route = AisRouter.Parse(httpRequest.HttpMethod, httpRequest.RawUrl ?? httpRequest.UriPath, m_capPath);
        Dispatch(route, httpRequest, httpResponse);
    }

    /// <summary>Dispatch a parsed route. Public so the HTTP-level tests can drive it without a scene.</summary>
    public void Dispatch(AisRoute route, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        var body = ReadBody(httpRequest);
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

                case AisOperation.UpdateItem:
                case AisOperation.UpdateCategory:
                case AisOperation.RemoveItem:
                case AisOperation.RemoveCategory:
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

    /// <summary>The request body as an LLSD map; an empty map when there is none or it is not a map.</summary>
    private static OSDMap ReadBody(IOSHttpRequest request)
    {
        try
        {
            var stream = request.InputStream;
            if (stream is null) return new OSDMap();
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return new OSDMap();
            return OSDParser.DeserializeLLSDXml(bytes) as OSDMap ?? new OSDMap();
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
    /// A folder the server refuses to delete: the agent's root, or any folder carrying a system type other than
    /// <see cref="FolderType.Outfit"/>. Mirrors the viewer's own <c>lookupIsProtectedType</c> gate
    /// (<c>llviewerinventory.cpp:1557-1561</c>); the exact membership of that predicate is UNVERIFIED here because
    /// <c>llfoldertype.cpp</c> is not a permitted read, so this is a deliberately conservative rule that still
    /// leaves saved outfits and ordinary user folders deletable.
    /// </summary>
    public static bool IsProtected(InventoryFolderBase folder)
    {
        if (folder is null) return true;
        if (folder.ParentID.IsZero()) return true;                 // the inventory root
        if (folder.Type < 0) return false;                         // FolderType.None: an ordinary user folder
        return folder.Type != (short)FolderType.Outfit;
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
