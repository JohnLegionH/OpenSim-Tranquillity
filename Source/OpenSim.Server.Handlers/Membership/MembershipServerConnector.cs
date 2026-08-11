using Nini.Config;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.Handlers.Membership;

// NOTE: file is MembershipServerConnector.cs but the CLASS is MembershipServiceConnector — this mirrors
// the Experience handler's filename!=classname quirk on purpose. The [ServiceList] entry and any
// LocalServiceModule that names this connector must use the SHORT CLASS NAME "MembershipServiceConnector";
// a fully-qualified name silently returns null from ServerUtils.LoadPlugin.
public class MembershipServiceConnector : ServiceConnector
{
    private IMembershipService m_MembershipService;
    private string m_ConfigName = "MembershipService";

    public MembershipServiceConnector(IConfigSource config, IHttpServer server, string configName) :
            base(config, server, configName)
    {
        IConfig serverConfig = config.Configs[m_ConfigName];
        if (serverConfig == null)
            throw new Exception(String.Format("No section {0} in config file", m_ConfigName));

        string service = serverConfig.GetString("LocalServiceModule", String.Empty);

        if (service == String.Empty)
            throw new Exception("LocalServiceModule not present in MembershipService config file MembershipService section");

        Object[] args = new Object[] { config };
        m_MembershipService = ServerUtils.LoadPlugin<IMembershipService>(service, args);

        IServiceAuth auth = ServiceAuth.Create(config, m_ConfigName);

        server.AddStreamHandler(new MembershipServerPostHandler(m_MembershipService, auth));
    }
}
