/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Decides whether GroupsMessagingModule should handle a SessionSend instant message.
 *
 * A SessionSend names the chat session it belongs to. The module used to read that id as a group id
 * whatever it was, while the SessionGroupStart arm resolves the group record first and does nothing
 * when it is null. For an id that is not a group (an ad-hoc conference session, which shares the
 * group IM path), the unguarded arm sent the line back to the sender and registered a chat session
 * for an id the groups service has no record of.
 *
 * The condition takes the resolver rather than the record so it can be tested without a groups
 * service, a scene and a client. It does not catch exceptions from the resolver: it behaves exactly
 * as the lookup in the SessionGroupStart arm does. For a real group id the resolver returns a record
 * and the arm runs as before.
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
