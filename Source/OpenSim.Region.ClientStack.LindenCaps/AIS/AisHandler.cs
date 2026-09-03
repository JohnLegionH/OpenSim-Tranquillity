using System;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// The InventoryAPIv3 cap handler: one per agent, mounted at a random cap path. Parses the request with
/// <see cref="AisRouter"/> and dispatches on <see cref="AisOperation"/>. Holds only the agent id, the backend and
/// its cap path (Ledger P-2) so the same class can be mounted on Robust in Phase 2.
///
/// A0: every route answers 501 Not Implemented with an LLSD error body of the shape the viewer tolerates on
/// failure (spec §1f: a map; no <c>item_id</c>/<c>category_id</c> + <c>parent_id</c> pairs, so the viewer's
/// update parser finds nothing to apply).
/// </summary>
public sealed class AisHandler : SimpleStreamHandler
{
    private readonly UUID m_agentId;
    private readonly IAisInventoryBackend m_backend;
    private readonly string m_capPath;

    public AisHandler(string capPath, UUID agentId, IAisInventoryBackend backend) : base(capPath, "InventoryAPIv3")
    {
        m_capPath = capPath;
        m_agentId = agentId;
        m_backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public UUID AgentId => m_agentId;
    public string CapPath => m_capPath;

    protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        var route = AisRouter.Parse(httpRequest.HttpMethod, httpRequest.RawUrl ?? httpRequest.UriPath, m_capPath);
        Dispatch(route, httpRequest, httpResponse);
    }

    /// <summary>Dispatch a parsed route. Public so the HTTP-level tests can drive it without a scene.</summary>
    public void Dispatch(AisRoute route, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        httpRequest.InputStream?.Dispose();
        switch (route.Operation)
        {
            case AisOperation.Unknown:
                WriteError(httpResponse, HttpStatusCode.NotFound, "no such AIS v3 route", route);
                return;
            default:
                // A0: the surface is defined, nothing is implemented. Each operation lands here until its
                // session replaces this arm.
                WriteError(httpResponse, HttpStatusCode.NotImplemented, $"{route.Operation} is not implemented", route);
                return;
        }
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
