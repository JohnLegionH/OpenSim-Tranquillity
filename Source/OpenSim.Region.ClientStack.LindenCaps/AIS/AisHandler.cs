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
/// <para>A1 implements the **read** surface: <c>GET /item</c>, <c>/category/{id}/children</c> (whole and subset),
/// <c>/categories</c>, <c>/links</c>, <c>/category/current/links</c> and <c>/orphans</c>. Every mutation still
/// answers 501 on the inventory cap and 405 on the library cap. Nothing in this session writes to inventory.</para>
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
        httpRequest.InputStream?.Dispose();
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
        var depth = route.Depth < 0 ? 0 : route.Depth;
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
