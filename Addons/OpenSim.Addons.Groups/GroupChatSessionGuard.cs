/*
 * O-136: the one condition that stops GroupsMessagingModule claiming a SessionSend that is not a
 * group's.
 *
 * THE DEFECT. The module's SessionSend arm read imSessionID as a group id whatever it was, with no
 * existence check -- while the SessionGroupStart arm TEN LINES ABOVE resolves the record first and
 * does nothing when it is null (GroupsMessagingModule.cs:650-652). That asymmetry is the bug. For
 * an ad-hoc conference id the unguarded arm reached "send to self first of all"
 * (GroupsMessagingModule.cs:330-333) and echoed the sender their own conference line, defeating
 * A2a's no-echo rule from a module A2a does not own. It also registered a phantom chat session per
 * conference id in m_groupsAgentsInvitedToChatSession.
 *
 * WHY THIS IS A SEAM AND NOT AN INLINE `!= null`. The condition has to be testable without standing
 * up a groups service, a scene and a client, so it takes the resolver rather than the record: a
 * test injects one that returns null (a conference) or a record (a real group) and asserts which
 * way the arm goes. The codebase already works this way -- ChatSessionRequestLogic is "pure
 * decision logic" with the HTTP handler as a thin adapter.
 *
 * DELIBERATELY NOT DEFENSIVE. There is no try/catch here, because the call must behave EXACTLY as
 * the inline lookup in the SessionGroupStart arm does. Swallowing a groups-service exception would
 * be a new behaviour, and a silent one.
 *
 * WHAT THIS DOES NOT CHANGE, and it was checked rather than assumed: for a REAL group id the
 * resolver returns a record and the arm runs exactly as before, so group text is untouched. On a
 * transient groups-service failure the guard does not introduce a new way to lose a group message
 * -- SendMessageToGroup already calls GetGroupMembers for the same message on the same service and
 * already delivers to nobody when that comes back empty. The one thing that does change on such a
 * transient is the SELF-ECHO: today the sender sees their own line while nobody else does; with
 * the guard the message is dropped cleanly. Losing a misleading echo is the better failure.
 */
using System;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Groups
{
    public static class GroupChatSessionGuard
    {
        /// <summary>
        /// Does this SessionSend belong to group chat? True only when the session id resolves to a
        /// real group -- the same test the SessionGroupStart arm already applies before it acts.
        /// </summary>
        public static bool TakesSessionSend(UUID sessionId, Func<UUID, GroupRecord> resolveGroup)
        {
            if (resolveGroup is null)
                return false;
            if (sessionId.IsZero())
                return false;
            return resolveGroup(sessionId) is not null;
        }
    }
}
