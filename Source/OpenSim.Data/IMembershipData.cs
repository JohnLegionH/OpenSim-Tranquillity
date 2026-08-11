using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data;

// Storage-agnostic membership data contract. Deliberately carries NO MySQL types in any signature so a
// SQLite (or other) implementation can be added later without touching the service layer. Mirrors the
// shape of IExperienceData. This tree's only implementation is MySQL (Source/OpenSim.Data.MySQL).
public interface IMembershipData
{
    // Tiers (read-only in this slice; rows are managed out-of-band until seed values are chosen).
    MembershipTier[] GetTiers();
    MembershipTier GetTier(string tierName);        // null if not found

    // Per-agent membership.
    UserMembership GetUserMembership(UUID agentID);  // null if the agent has no row
    bool StoreUserMembership(UserMembership membership);
    bool RemoveUserMembership(UUID agentID);
}
