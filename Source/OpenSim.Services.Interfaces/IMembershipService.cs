using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces;

public interface IMembershipService
{
    // The agent's RESOLVED effective tier. NEVER returns null: if the agent has no membership row (or it
    // points at a tier that doesn't exist), a hardcoded Basic-equivalent is returned whose limits mirror
    // the compiled Constants, so a caller sees today's behaviour when nothing is configured.
    MembershipTier GetMembership(UUID agentID);

    MembershipTier GetTier(string tierName);   // null if not found
    MembershipTier[] GetTiers();

    // Assign/replace an agent's membership. expires is unix seconds (0 = no expiry).
    bool SetMembership(UUID agentID, string tierName, int expires, UUID grantedBy);
    bool RemoveMembership(UUID agentID);
}
