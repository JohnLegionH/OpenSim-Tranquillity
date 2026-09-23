/*
 * O-136: GroupsMessagingModule's SessionSend arm must take the message only when the id is a REAL
 * group. Without the guard it claimed every SessionSend -- an ad-hoc conference's included -- and
 * echoed it back to the sender from "send to self first of all" (GroupsMessagingModule.cs:330-333),
 * defeating A2a's no-echo rule from a module A2a does not own.
 *
 * The arm ten lines above (SessionGroupStart) has always resolved the record first and done nothing
 * when it is null (:650-652). The asymmetry was the defect; these tests pin both halves.
 */
using System;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Groups;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class GroupChatSessionGuardTests
    {
        private static readonly UUID RealGroup = new UUID("f7b9f744-0000-4000-8000-000000000001");
        private static readonly UUID ConferenceId = new UUID("c0f0c0f0-0000-4000-8000-000000000002");

        private static GroupRecord RecordFor(UUID id) => new GroupRecord { GroupID = id, GroupName = "Emo Necko Gecko" };

        [Test]
        public void AConferenceIdDoesNotReachTheGroupArm()
        {
            // the groups service knows nothing about a conference id, so it resolves to null
            bool taken = GroupChatSessionGuard.TakesSessionSend(ConferenceId, _ => null);

            Assert.That(taken, Is.False,
                "an unguarded arm reached 'send to self first of all' and echoed the sender their own line");
        }

        [Test]
        public void ARealGroupIdStillReachesTheGroupArm()
        {
            bool taken = GroupChatSessionGuard.TakesSessionSend(RealGroup, RecordFor);

            Assert.That(taken, Is.True, "group text must be unchanged by O-136");
        }

        [Test]
        public void TheGuardAsksAboutTheSessionIdItWasGiven()
        {
            UUID asked = UUID.Zero;
            GroupChatSessionGuard.TakesSessionSend(RealGroup, id => { asked = id; return RecordFor(id); });

            Assert.That(asked, Is.EqualTo(RealGroup),
                "resolving some other id would make the guard answer about the wrong session");
        }

        [Test]
        public void AZeroIdAndAMissingResolverAreBothRefused()
        {
            Assert.That(GroupChatSessionGuard.TakesSessionSend(UUID.Zero, RecordFor), Is.False);
            Assert.That(GroupChatSessionGuard.TakesSessionSend(RealGroup, null), Is.False);
        }

        /// <summary>
        /// Deliberately NOT swallowed. The guard has to behave exactly as the inline lookup in the
        /// SessionGroupStart arm does; catching here would be a new behaviour, and a silent one.
        /// </summary>
        [Test]
        public void AThrowingGroupsServicePropagatesRatherThanBeingSwallowed()
        {
            Assert.That(() => GroupChatSessionGuard.TakesSessionSend(RealGroup,
                            _ => throw new InvalidOperationException("groups service down")),
                        Throws.TypeOf<InvalidOperationException>());
        }
    }
}
