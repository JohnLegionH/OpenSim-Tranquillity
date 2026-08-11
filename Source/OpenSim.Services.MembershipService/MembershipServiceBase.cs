using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.MembershipService;

// Loads the IMembershipData storage plugin from [DatabaseService] (overridden by [MembershipService]).
// Byte-for-byte the ExperienceServiceBase pattern.
public class MembershipServiceBase : ServiceBase
{
    protected IMembershipData m_Database = null;

    public MembershipServiceBase(IConfigSource config)
        : base(config)
    {
        string dllName = string.Empty;
        string connString = string.Empty;

        IConfig dbConfig = config.Configs["DatabaseService"];
        if (dbConfig != null)
        {
            if (dllName == string.Empty)
                dllName = dbConfig.GetString("StorageProvider", string.Empty);
            if (connString == string.Empty)
                connString = dbConfig.GetString("ConnectionString", string.Empty);
        }

        // [MembershipService] overrides [DatabaseService], if present.
        IConfig membershipConfig = config.Configs["MembershipService"];
        if (membershipConfig != null)
        {
            dllName = membershipConfig.GetString("StorageProvider", dllName);
            connString = membershipConfig.GetString("ConnectionString", connString);
        }

        if (dllName.Equals(string.Empty))
            throw new Exception("No StorageProvider configured");

        m_Database = LoadPlugin<IMembershipData>(dllName, new object[] { connString });
        if (m_Database == null)
            throw new Exception("Could not find a storage interface in the given module " + dllName);
    }
}
