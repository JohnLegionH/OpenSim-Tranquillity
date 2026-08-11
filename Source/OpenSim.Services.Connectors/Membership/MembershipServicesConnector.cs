using log4net;
using System.Reflection;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;
using OpenMetaverse;

namespace OpenSim.Services.Connectors;

// Region-side HTTP client for the Membership Robust service. Mirrors ExperienceServicesConnector: builds a
// query-string form, POSTs to <uri>/membership, parses the XML <ServerResponse>.
public class MembershipServicesConnector : BaseServiceConnector, IMembershipService
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private string m_ServerURI = String.Empty;

    public MembershipServicesConnector()
    {
    }

    public MembershipServicesConnector(string serverURI)
    {
        m_ServerURI = serverURI.TrimEnd('/') + "/membership";
    }

    public MembershipServicesConnector(IConfigSource source)
    {
        Initialise(source);
    }

    public virtual void Initialise(IConfigSource source)
    {
        IConfig gridConfig = source.Configs["MembershipService"];
        if (gridConfig == null)
        {
            m_log.Error("[MEMBERSHIP CONNECTOR]: MembershipService missing from configuration");
            throw new Exception("Membership connector init error");
        }

        string serviceURI = gridConfig.GetString("MembershipServerURI", String.Empty);
        if (serviceURI == String.Empty)
        {
            m_log.Error("[MEMBERSHIP CONNECTOR]: No MembershipServerURI named in section MembershipService");
            throw new Exception("Membership connector init error");
        }
        m_ServerURI = serviceURI + "/membership";
        base.Initialise(source, "MembershipService");
    }

    #region IMembershipService

    public MembershipTier GetMembership(UUID agentID)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        sendData["METHOD"] = "getmembership";
        sendData["agent_id"] = agentID.ToString();

        MembershipTier tier = ParseSingleTier(Post(sendData, "getmembership"));
        // Contract: never null. Fall back to Basic (compiled-constant limits) on any failure.
        return tier ?? MembershipTier.Basic();
    }

    public MembershipTier GetTier(string tierName)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        sendData["METHOD"] = "gettier";
        sendData["tier_name"] = tierName;

        return ParseSingleTier(Post(sendData, "gettier"));
    }

    public MembershipTier[] GetTiers()
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        sendData["METHOD"] = "gettiers";

        List<MembershipTier> tiers = new List<MembershipTier>();
        Dictionary<string, object> replyData = Post(sendData, "gettiers");
        if (replyData != null)
        {
            foreach (object v in replyData.Values)
                if (v is Dictionary<string, object> dict)
                    tiers.Add(new MembershipTier(dict));
        }
        return tiers.ToArray();
    }

    public bool SetMembership(UUID agentID, string tierName, int expires, UUID grantedBy)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        sendData["METHOD"] = "setmembership";
        sendData["agent_id"] = agentID.ToString();
        sendData["tier_name"] = tierName;
        sendData["expires"] = expires.ToString();
        sendData["granted_by"] = grantedBy.ToString();

        return IsSuccess(Post(sendData, "setmembership"));
    }

    public bool RemoveMembership(UUID agentID)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        sendData["METHOD"] = "removemembership";
        sendData["agent_id"] = agentID.ToString();

        return IsSuccess(Post(sendData, "removemembership"));
    }

    #endregion IMembershipService

    private Dictionary<string, object> Post(Dictionary<string, object> sendData, string meth)
    {
        try
        {
            string reply = SynchronousRestFormsRequester.MakeRequest("POST", m_ServerURI,
                    ServerUtils.BuildQueryString(sendData), m_Auth);
            if (reply == string.Empty)
            {
                m_log.DebugFormat("[MEMBERSHIP CONNECTOR]: {0} received empty reply", meth);
                return null;
            }
            return ServerUtils.ParseXmlResponse(reply);
        }
        catch (Exception e)
        {
            m_log.DebugFormat("[MEMBERSHIP CONNECTOR]: Exception on {0}: {1}", meth, e.Message);
            return null;
        }
    }

    private static MembershipTier ParseSingleTier(Dictionary<string, object> replyData)
    {
        if (replyData != null && replyData.TryGetValue("tier", out object v) && v is Dictionary<string, object> dict)
            return new MembershipTier(dict);
        return null;
    }

    private static bool IsSuccess(Dictionary<string, object> replyData)
    {
        return replyData != null
            && replyData.TryGetValue("result", out object v) && v != null
            && v.ToString().Equals("Success", StringComparison.OrdinalIgnoreCase);
    }
}
