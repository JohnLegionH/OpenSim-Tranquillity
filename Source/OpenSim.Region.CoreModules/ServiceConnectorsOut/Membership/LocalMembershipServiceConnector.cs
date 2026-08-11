using log4net;
using Nini.Config;
using System.Reflection;
using OpenSim.Server.Base;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Membership;

// In-process membership service for standalone. Mirrors LocalExperienceServicesConnector, including the
// [Modules] key quirk: the LOCAL connector is selected by the SINGULAR key "MembershipService".
public class LocalMembershipServicesConnector : ISharedRegionModule, IMembershipService
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private List<Scene> m_Scenes = new List<Scene>();
    protected IMembershipService m_service = null;

    private bool m_Enabled = false;

    #region ISharedRegionModule

    public Type ReplaceableInterface
    {
        get { return null; }
    }

    public string Name
    {
        get { return "LocalMembershipServicesConnector"; }
    }

    public void Initialise(IConfigSource source)
    {
        IConfig moduleConfig = source.Configs["Modules"];
        if (moduleConfig == null)
            return;

        string name = moduleConfig.GetString("MembershipService", "");
        if (name != Name)
            return;

        IConfig svcConfig = source.Configs["MembershipService"];
        if (svcConfig == null)
        {
            m_log.Error("[MEMBERSHIP LOCALCONNECTOR]: MembershipService missing from configuration");
            return;
        }

        string serviceDll = svcConfig.GetString("LocalServiceModule", String.Empty);
        if (serviceDll == String.Empty)
        {
            m_log.Error("[MEMBERSHIP LOCALCONNECTOR]: No MembershipModule named in section MembershipService");
            return;
        }

        Object[] args = new Object[] { source };
        try
        {
            m_service = ServerUtils.LoadPlugin<IMembershipService>(serviceDll, args);
        }
        catch
        {
            m_log.Error("[MEMBERSHIP LOCALCONNECTOR]: Failed to load membership service");
            return;
        }

        if (m_service == null)
        {
            m_log.Error("[MEMBERSHIP LOCALCONNECTOR]: Can't load membership service");
            return;
        }

        m_Enabled = true;
        m_log.Info("[MEMBERSHIP LOCALCONNECTOR]: Enabled!");
    }

    public void Close()
    {
    }

    public void AddRegion(Scene scene)
    {
        if (!m_Enabled)
            return;

        lock (m_Scenes)
        {
            m_Scenes.Add(scene);
            scene.RegisterModuleInterface<IMembershipService>(this);
        }
    }

    public void RegionLoaded(Scene scene)
    {
    }

    public void PostInitialise()
    {
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_Enabled)
            return;

        lock (m_Scenes)
        {
            if (m_Scenes.Contains(scene))
            {
                m_Scenes.Remove(scene);
                scene.UnregisterModuleInterface<IMembershipService>(this);
            }
        }
    }

    #endregion ISharedRegionModule

    #region IMembershipService
    public MembershipTier GetMembership(UUID agentID) => m_service.GetMembership(agentID);
    public MembershipTier GetTier(string tierName) => m_service.GetTier(tierName);
    public MembershipTier[] GetTiers() => m_service.GetTiers();
    public bool SetMembership(UUID agentID, string tierName, int expires, UUID grantedBy) => m_service.SetMembership(agentID, tierName, expires, grantedBy);
    public bool RemoveMembership(UUID agentID) => m_service.RemoveMembership(agentID);
    #endregion IMembershipService
}
