/*
 * A2a item 1: ChatterBoxSessionAgentListUpdates for an AD-HOC conference -- the participant list
 * in every member's IM panel, kept current on join, on accept and on departure.
 *
 * MODELLED ON THE GROUP PATTERN, which is the only server-side precedent in the tree (O-111):
 *     GroupsMessagingModule.cs:663   the joining agent's own ENTER, sent to that agent when its
 *                                    SessionGroupStart is answered
 *     GroupsMessagingModule.cs:623   the joining agent's ENTER, sent to a member who is being
 *                                    brought into the session by an incoming message
 * Both send a ONE-ENTRY update list describing the agent who just moved, addressed to one
 * recipient. That is the shape; what differs for a conference is the fan-out, because a group
 * session discovers its members lazily from traffic while a conference knows its seat list exactly.
 *
 * SO THE RULE HERE IS: one join produces N sends, not one.
 *   - every OTHER seat holder receives the joiner's ENTER, so their panel gains the new name;
 *   - the JOINER receives the WHOLE roster as ENTERs, including its own entry, so its panel is
 *     correct immediately instead of filling in as people happen to speak. A2AAgentListDelivery
 *     does the same for the two-party case (EnterUpdates sends the other party plus self), and a
 *     conference is that generalised to N.
 * A departure is the mirror: every REMAINING seat holder receives the departed agent's LEAVE. The
 * departed agent is sent nothing -- it has left, and on a presence close there is nothing to send to.
 *
 * can_voice_chat IS TRUE ON EVERY ENTRY, by construction, because this class never uses the 1-arg
 * GroupChatListAgentUpdateData ctor -- it delegates to A2AAgentListDelivery.Update, which uses the
 * 5-arg ctor with cv:true. The 1-arg ctor defaults canVoice to FALSE (IEventQueue.cs:43-50), and
 * O-42 found what that costs on a voice channel: the viewer reads can_voice_chat:false for a peer
 * as a decline and hangs the call up (llimview.cpp:4366-4382). That trace was P2P, so the hang-up
 * is not proven for a conference; cv:true is nonetheless the truthful value here -- this IS a voice
 * conference and every seat holder can speak in it -- so there is no reason to risk the other one.
 *
 * DELIVERY IS LOCAL-ONLY AND THAT IS NOT A DEFECT. The event queue is per-instance: a member on
 * another regionserver has no queue here, and A2AAgentListDelivery.Deliver answers
 * "unreachable(no-presence)" for them, which this class logs per recipient. Their roster arrives
 * from THEIR instance, which adopted the session from the ring (AdoptRemoteRing) and runs this same
 * code on the accept it handles locally. Nothing is silently lost; the instrument line says exactly
 * who was reachable.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice.NonSpatial
{
    public static class AdhocConferenceRoster
    {
        public const string InstrumentTag = "[ADHOC ROSTER]";

        /// <summary>
        /// Every seat holder, in the store's order. The same seat test the cap, admission and the
        /// text fan-out use, so the roster cannot disagree with who is actually in the room.
        /// </summary>
        public static List<UUID> SeatHolders(NonSpatialVoiceSession session)
        {
            List<UUID> all = new List<UUID>();
            if (session is null)
                return all;
            foreach (NonSpatialMember m in session.Members)
                if (m.HoldsSeat)
                    all.Add(m.AgentId);
            return all;
        }

        /// <summary>The whole roster as ENTERs -- what a joiner needs to render the panel at once.</summary>
        public static List<GroupChatListAgentUpdateData> FullRosterUpdates(NonSpatialVoiceSession session)
        {
            List<GroupChatListAgentUpdateData> updates = new List<GroupChatListAgentUpdateData>();
            foreach (UUID a in SeatHolders(session))
                updates.Add(A2AAgentListDelivery.Update(a, true));
            return updates;
        }

        /// <summary>One agent's ENTER -- what everybody already in the room needs.</summary>
        public static List<GroupChatListAgentUpdateData> EnterUpdates(UUID joiner)
            => new List<GroupChatListAgentUpdateData> { A2AAgentListDelivery.Update(joiner, true) };

        /// <summary>One agent's LEAVE.</summary>
        public static List<GroupChatListAgentUpdateData> LeaveUpdates(UUID departed)
            => new List<GroupChatListAgentUpdateData> { A2AAgentListDelivery.Update(departed, false) };

        /// <summary>
        /// A member took a seat (start conference by the creator, "call", or "accept invitation").
        /// Sends the joiner the whole roster and everybody else the joiner's ENTER. Returns one
        /// instrument line per send; never throws into the caller's request.
        /// </summary>
        public static List<string> SendJoin(IEnumerable<Scene> scenes, NonSpatialVoiceSession session,
                                            UUID joiner, Func<Scene, IEventQueue> queueOf = null)
        {
            List<string> lines = new List<string>();
            if (session is null || session.Type != NonSpatialSessionType.Adhoc || joiner == UUID.Zero)
                return lines;

            List<UUID> seats = SeatHolders(session);

            // The joiner's own panel: the entire room at once, its own entry included.
            if (seats.Contains(joiner))
            {
                string d = A2AAgentListDelivery.Deliver(scenes, joiner, session.SessionId,
                                                        FullRosterUpdates(session), queueOf);
                lines.Add(Line(joiner, session.SessionId, A2AAgentListDelivery.TransitionEnter, joiner,
                               d, "roster=" + seats.Count));
            }

            // Everybody else: just the one new name.
            List<GroupChatListAgentUpdateData> entered = EnterUpdates(joiner);
            foreach (UUID a in seats)
            {
                if (a == joiner)
                    continue;
                string d = A2AAgentListDelivery.Deliver(scenes, a, session.SessionId, entered, queueOf);
                lines.Add(Line(a, session.SessionId, A2AAgentListDelivery.TransitionEnter, joiner, d));
            }
            return lines;
        }

        /// <summary>
        /// A member left, for any of the O-108 lifecycles. Every REMAINING seat holder is told.
        /// Call this AFTER the engine has recorded the departure, so the departed agent is already
        /// out of <see cref="SeatHolders"/> and cannot be sent its own LEAVE.
        /// </summary>
        public static List<string> SendLeave(IEnumerable<Scene> scenes, NonSpatialVoiceSession session,
                                             UUID departed, Func<Scene, IEventQueue> queueOf = null)
        {
            List<string> lines = new List<string>();
            if (session is null || session.Type != NonSpatialSessionType.Adhoc || departed == UUID.Zero)
                return lines;

            List<GroupChatListAgentUpdateData> left = LeaveUpdates(departed);
            foreach (UUID a in SeatHolders(session))
            {
                if (a == departed)
                    continue;   // defensive: a caller that ran this before the engine recorded the departure
                string d = A2AAgentListDelivery.Deliver(scenes, a, session.SessionId, left, queueOf);
                lines.Add(Line(a, session.SessionId, A2AAgentListDelivery.TransitionLeave, departed, d));
            }
            return lines;
        }

        /// <summary>Permanent instrument: one greppable line per recipient.</summary>
        public static string Line(UUID recipient, UUID sessionId, string transition, UUID about,
                                  string decision, string detail = null)
            => $"{InstrumentTag} agent={recipient} session={sessionId} transition={transition} "
               + $"about={about} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
