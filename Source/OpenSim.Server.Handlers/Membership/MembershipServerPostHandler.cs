using Microsoft.Extensions.Logging;
using System.Reflection;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;

namespace OpenSim.Server.Handlers.Membership;

// POST /membership on the private port. Query-string form in, XML <ServerResponse> out — the same wire
// shape as the Experience handler. METHOD selects the verb.
public class MembershipServerPostHandler : BaseStreamHandler
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private IMembershipService m_service;

    public MembershipServerPostHandler(IMembershipService service, IServiceAuth auth) :
            base("POST", "/membership", auth)
    {
        m_service = service;
    }

    protected override byte[] ProcessRequest(string path, Stream requestData,
            IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        string body;
        using (StreamReader sr = new StreamReader(requestData))
            body = sr.ReadToEnd();
        body = body.Trim();

        string method = string.Empty;

        try
        {
            Dictionary<string, object> request = ServerUtils.ParseQueryString(body);

            if (!request.ContainsKey("METHOD"))
                return FailureResult();

            method = request["METHOD"].ToString();

            switch (method)
            {
                case "getmembership":
                    return GetMembership(request);
                case "gettier":
                    return GetTier(request);
                case "gettiers":
                    return GetTiers(request);
                case "setmembership":
                    return SetMembership(request);
                case "removemembership":
                    return RemoveMembership(request);
            }
            m_log.LogDebug("[MEMBERSHIP HANDLER]: unknown method request: {Method}", method);
        }
        catch (Exception e)
        {
            m_log.LogDebug("[MEMBERSHIP HANDLER]: Exception in method {Method}: {Error}", method, e);
        }

        return FailureResult();
    }

    private byte[] GetMembership(Dictionary<string, object> request)
    {
        if (!request.ContainsKey("agent_id") || !UUID.TryParse(request["agent_id"].ToString(), out UUID agent_id))
            return FailureResult();

        MembershipTier tier = m_service.GetMembership(agent_id);   // never null

        Dictionary<string, object> result = new Dictionary<string, object>();
        result["tier"] = tier.ToDictionary();

        string xmlString = ServerUtils.BuildXmlResponse(result);
        return Util.UTF8NoBomEncoding.GetBytes(xmlString);
    }

    private byte[] GetTier(Dictionary<string, object> request)
    {
        if (!request.ContainsKey("tier_name"))
            return FailureResult();

        MembershipTier tier = m_service.GetTier(request["tier_name"].ToString());

        Dictionary<string, object> result = new Dictionary<string, object>();
        if (tier == null)
            result["result"] = "null";
        else
            result["tier"] = tier.ToDictionary();

        string xmlString = ServerUtils.BuildXmlResponse(result);
        return Util.UTF8NoBomEncoding.GetBytes(xmlString);
    }

    private byte[] GetTiers(Dictionary<string, object> request)
    {
        MembershipTier[] tiers = m_service.GetTiers();

        Dictionary<string, object> result = new Dictionary<string, object>();
        if (tiers == null || tiers.Length == 0)
            result["result"] = "null";
        else
        {
            int n = 0;
            foreach (MembershipTier t in tiers)
                result["tier_" + n++] = t.ToDictionary();
        }

        string xmlString = ServerUtils.BuildXmlResponse(result);
        return Util.UTF8NoBomEncoding.GetBytes(xmlString);
    }

    private byte[] SetMembership(Dictionary<string, object> request)
    {
        if (!request.ContainsKey("agent_id") || !UUID.TryParse(request["agent_id"].ToString(), out UUID agent_id))
            return FailureResult();
        if (!request.ContainsKey("tier_name"))
            return FailureResult();

        string tierName = request["tier_name"].ToString();

        int expires = 0;
        if (request.ContainsKey("expires"))
            int.TryParse(request["expires"].ToString(), out expires);

        UUID grantedBy = UUID.Zero;
        if (request.ContainsKey("granted_by"))
            UUID.TryParse(request["granted_by"].ToString(), out grantedBy);

        return m_service.SetMembership(agent_id, tierName, expires, grantedBy) ? SuccessResult() : FailureResult();
    }

    private byte[] RemoveMembership(Dictionary<string, object> request)
    {
        if (!request.ContainsKey("agent_id") || !UUID.TryParse(request["agent_id"].ToString(), out UUID agent_id))
            return FailureResult();

        return m_service.RemoveMembership(agent_id) ? SuccessResult() : FailureResult();
    }

    private byte[] SuccessResult()
    {
        Dictionary<string, object> result = new Dictionary<string, object> { ["result"] = "Success" };
        return Util.UTF8NoBomEncoding.GetBytes(ServerUtils.BuildXmlResponse(result));
    }

    private byte[] FailureResult()
    {
        Dictionary<string, object> result = new Dictionary<string, object> { ["result"] = "Failure" };
        return Util.UTF8NoBomEncoding.GetBytes(ServerUtils.BuildXmlResponse(result));
    }
}
