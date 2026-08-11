using System.Reflection;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors;
using OpenSim.Framework;

using OpenMetaverse;
using log4net;
using Nini.Config;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Membership;

// Grid-mode membership client region module. Mirrors RemoteExperienceServicesConnector, including the
// [Modules] key quirk: the REMOTE connector is selected by the PLURAL key "MembershipServices".
public class RemoteMembershipServicesConnector : ISharedRegionModule, IMembershipService
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    #region ISharedRegionModule

    private bool m_Enabled = false;

    private IMembershipService m_remoteConnector;

    public Type ReplaceableInterface
    {
        get { return null; }
    }

    public string Name
    {
        get { return "RemoteMembershipServicesConnector"; }
    }

    public void Initialise(IConfigSource source)
    {
        IConfig moduleConfig = source.Configs["Modules"];
        if (moduleConfig != null)
        {
            string name = moduleConfig.GetString("MembershipServices", "");
            if (name == Name)
            {
                m_remoteConnector = new MembershipServicesConnector(source);
                m_Enabled = true;

                m_log.Info("[MEMBERSHIP CONNECTOR]: Remote MembershipService enabled");
            }
        }
    }

    public void PostInitialise()
    {
    }

    public void Close()
    {
    }

    public void AddRegion(Scene scene)
    {
        if (!m_Enabled)
            return;

        scene.RegisterModuleInterface<IMembershipService>(this);
        m_log.InfoFormat("[MEMBERSHIP CONNECTOR]: Enabled for region {0}", scene.RegionInfo.RegionName);
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_Enabled)
            return;
    }

    public void RegionLoaded(Scene scene)
    {
        if (!m_Enabled)
            return;
    }

    #endregion

    #region IMembershipService
    public MembershipTier GetMembership(UUID agentID) => m_remoteConnector.GetMembership(agentID);
    public MembershipTier GetTier(string tierName) => m_remoteConnector.GetTier(tierName);
    public MembershipTier[] GetTiers() => m_remoteConnector.GetTiers();
    public bool SetMembership(UUID agentID, string tierName, int expires, UUID grantedBy) => m_remoteConnector.SetMembership(agentID, tierName, expires, grantedBy);
    public bool RemoveMembership(UUID agentID) => m_remoteConnector.RemoveMembership(agentID);
    #endregion IMembershipService
}
