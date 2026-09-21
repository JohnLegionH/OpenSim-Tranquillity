/*
 * P1.1 item 3: room identity is derived independently of session identity, and the collision
 * argument in NonSpatialRoomKey's header is asserted rather than asserted-about.
 *
 * Layer (1) -- structural, against live A2A rooms -- is the one that matters most, because A2A
 * admission resolves a room with UUID.TryParse (A2ASessionRegistry.cs:323-327). The test below
 * runs the ACTUAL live predicate against a room key, so if anyone ever drops the prefix the test
 * fails rather than the grid.
 */
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialRoomKeyTests
    {
        private static readonly UUID S1 = new UUID("11111111-2222-3333-4444-555555555555");
        private static readonly UUID S2 = new UUID("11111111-2222-3333-4444-555555555556");

        [Test]
        public void RoomKey_IsNotTheSessionId()
        {
            string key = NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, S1);
            Assert.That(key, Is.Not.EqualTo(S1.ToString()));
            Assert.That(key, Does.Not.Contain(S1.ToString()), "the mapping is one-way; the key leaks no session id");
        }

        [Test]
        public void RoomKey_IsDeterministic_SoAnyInstanceDerivesTheSame()
        {
            string a = NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, S1);
            string b = NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, S1);
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void RoomKey_DiffersByGrid_ByType_AndBySession()
        {
            string baseline = NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, S1);
            Assert.That(NonSpatialRoomKey.Derive("other-grid", NonSpatialSessionType.Adhoc, S1), Is.Not.EqualTo(baseline));
            Assert.That(NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Group, S1), Is.Not.EqualTo(baseline));
            Assert.That(NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, S2), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void RoomKey_OfEveryType_IsDistinctForTheSameSessionId()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NonSpatialSessionType t in Enum.GetValues(typeof(NonSpatialSessionType)))
                Assert.That(seen.Add(NonSpatialRoomKey.Derive("legion-grid", t, S1)), Is.True, "type " + t + " aliased another");
        }

        // ---- layer (1): structural separation from the LIVE A2A room space --------------------

        [Test]
        public void RoomKey_CanNeverBeMistakenForAnA2AChannel_TheLivePredicateRefusesIt()
        {
            A2ASessionRegistry live = new A2ASessionRegistry();
            live.Record(NonSpatialIdentityTests.Alice, NonSpatialIdentityTests.Bob, out _);

            foreach (NonSpatialSessionType t in Enum.GetValues(typeof(NonSpatialSessionType)))
            {
                string key = NonSpatialRoomKey.Derive("legion-grid", t, S1);
                Assert.That(UUID.TryParse(key, out _), Is.False, "a room key must never parse as a bare UUID: " + key);
                Assert.That(live.TryGetByChannel(key), Is.Null, "the live A2A lookup must not resolve a room key");
            }
        }

        [Test]
        public void AnA2AChannel_IsNeverMistakenForARoomKey()
        {
            A2ASessionRegistry live = new A2ASessionRegistry();
            A2ASession s = live.Record(NonSpatialIdentityTests.Alice, NonSpatialIdentityTests.Bob, out _);
            Assert.That(NonSpatialRoomKey.IsRoomKey(s.ChannelUri), Is.False);
        }

        [Test]
        public void IsRoomKey_RecognisesOurOwnAndRejectsEverythingElse()
        {
            Assert.That(NonSpatialRoomKey.IsRoomKey(NonSpatialRoomKey.Derive("g", NonSpatialSessionType.P2P, S1)), Is.True);
            Assert.That(NonSpatialRoomKey.IsRoomKey(S1.ToString()), Is.False);
            Assert.That(NonSpatialRoomKey.IsRoomKey(null), Is.False);
            Assert.That(NonSpatialRoomKey.IsRoomKey(string.Empty), Is.False);
        }

        // ---- layer (2): structural separation from SPATIAL rooms ------------------------------

        [Test]
        public void SpatialRoomNumbers_IgnoreChannelId_SoARoomKeyCannotAliasAParcel()
        {
            // JanusAudioBridge.cs:269-274 -- the "local" arm hashes region+type+parcel and never
            // reads pChannelID. Proving it here means a room key cannot enter that arm at all.
            string key = NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, S1);
            int withKey = JanusAudioBridge.CalcRoomNumber("legion-grid", "region-1", "local", 7, key);
            int withNothing = JanusAudioBridge.CalcRoomNumber("legion-grid", "region-1", "local", 7, string.Empty);
            Assert.That(withKey, Is.EqualTo(withNothing));
        }

        [Test]
        public void RoomKey_AndABareSessionId_HashToDifferentMultiagentRooms()
        {
            string key = NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, S1);
            int asRoomKey = JanusAudioBridge.CalcRoomNumber("legion-grid", "region-1", "multiagent", 0, key);
            int asSessionId = JanusAudioBridge.CalcRoomNumber("legion-grid", "region-1", "multiagent", 0, S1.ToString());
            Assert.That(asRoomKey, Is.Not.EqualTo(asSessionId),
                "distinct channel strings must not systematically alias; birthday collisions at the int layer are layer (3) and out of scope");
        }

        [Test]
        public void RoomNumbers_AreAlwaysPositive_ForDerivedKeys()
        {
            // The fold guard (JanusAudioBridge.cs:303-304, ledger O-33) must hold for our inputs too.
            for (int i = 0; i < 500; i++)
            {
                string key = NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, UUID.Random());
                Assert.That(JanusAudioBridge.CalcRoomNumber("legion-grid", "r", "multiagent", 0, key), Is.GreaterThan(0));
            }
        }

        [Test]
        public void DerivedKeys_DoNotCollideAcrossManySessions()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 20000; i++)
                Assert.That(keys.Add(NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, UUID.Random())), Is.True);
        }

        [Test]
        public void ExpectedRoomDescription_MatchesTheBridgesShape()
        {
            // JanusAudioBridge.cs:313 -- regionId + "/" + channelType + "/" + parcel + "/" + channelID.
            UUID region = new UUID("aaaaaaaa-0000-0000-0000-000000000001");
            string key = NonSpatialRoomKey.Derive("legion-grid", NonSpatialSessionType.Adhoc, S1);
            Assert.That(NonSpatialRoomKey.ExpectedRoomDescription(region, "multiagent", key),
                        Is.EqualTo(region + "/multiagent/0/" + key));
        }

        [Test]
        public void Tag_RejectsAnUnknownType()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NonSpatialRoomKey.Tag((NonSpatialSessionType)99));
        }
    }
}
