/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Which callee join made an avatar-to-avatar call active, with the call kept in the session store.
 *
 * The callee's join sends both parties the active-pair update, so exactly one join may claim the change however
 * many arrive together. A store hands out copies, so comparing the call's state before and after a join cannot
 * decide it: joins that arrive together all read Invited first. The engine decides inside the store's mutation
 * (the first seating of the callee), and A2ACalls reports that outcome.
 */
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2AActivationRaceTests
    {
        private const int Joiners = 8;
        private static readonly UUID CallId = A2ACalls.ComputeSessionId(Alice, Bob);

        /// <summary>One callee join, as the region module does it: true when this join made the call active.</summary>
        private static bool CalleeJoin(A2ACalls calls, A2ASession admitted, UUID region, string viewerSession)
        {
            calls.MarkProvisioned(admitted.SessionId, Bob, region, viewerSession, out bool becameActive);
            return becameActive;
        }

        private static bool CallerJoin(A2ACalls calls, A2ASession admitted, string viewerSession)
        {
            calls.MarkProvisioned(admitted.SessionId, Alice, RegionA, viewerSession, out bool becameActive);
            return becameActive;
        }

        private static A2ACalls RingingCall(A2ACalls calls, UUID region)
        {
            Assert.That(calls.Record(Alice, Bob, region, out _), Is.Not.Null);
            Assert.That(calls.IssueToken(CallId, Alice), Is.Not.Null);
            Assert.That(calls.MarkProvisioned(CallId, Alice, region, "vs-alice"), Is.Not.Null);
            return calls;
        }

        private static int WinnersOfSimultaneousJoins(A2ACalls calls, Func<int, UUID> regionOf)
        {
            A2ASession admitted = calls.TryGet(CallId, Bob);
            Assert.That(admitted.State, Is.EqualTo(A2ASessionState.Invited));
            int winners = 0;
            using Barrier gate = new Barrier(Joiners);
            Task[] joins = Enumerable.Range(0, Joiners).Select(n => Task.Factory.StartNew(() =>
            {
                gate.SignalAndWait();
                if (CalleeJoin(calls, admitted, regionOf(n), "vs-bob-" + n))
                    Interlocked.Increment(ref winners);
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(joins);
            return winners;
        }

        [Test]
        public void LocalStore_SimultaneousCalleeJoins_ExactlyOneMakesTheCallActive()
        {
            int tooMany = 0, none = 0;
            for (int round = 0; round < 300; round++)
            {
                NonSpatialVoiceSessionEngine engine = NewEngine();
                A2ACalls calls = RingingCall(new A2ACalls(() => engine), RegionA);
                int winners = WinnersOfSimultaneousJoins(calls, n => RegionA);
                if (winners > 1) tooMany++;
                if (winners == 0) none++;
            }
            Assert.That(tooMany, Is.EqualTo(0), "rounds in which more than one join was told it made the call active");
            Assert.That(none, Is.EqualTo(0), "rounds in which no join was told it made the call active");
        }

        [Test]
        public void GridStore_CalleeJoinsFromTwoRegionServers_ExactlyOneMakesTheCallActive()
        {
            int tooMany = 0, none = 0;
            for (int round = 0; round < 100; round++)
            {
                VoiceSessionService service = VoiceSessionServiceTests.NewService();
                GridNonSpatialSessions regionA = VoiceSessionServiceTests.NewRegion(service);
                GridNonSpatialSessions regionB = VoiceSessionServiceTests.NewRegion(service);
                A2ACalls a = RingingCall(new A2ACalls(() => regionA), RegionA);
                A2ACalls b = new A2ACalls(() => regionB);
                A2ASession admittedA = a.TryGet(CallId, Bob), admittedB = b.TryGet(CallId, Bob);
                int winners = 0;
                using Barrier gate = new Barrier(Joiners);
                Task[] joins = Enumerable.Range(0, Joiners).Select(n => Task.Factory.StartNew(() =>
                {
                    gate.SignalAndWait();
                    bool won = n % 2 == 0
                        ? CalleeJoin(a, admittedA, RegionA, "vs-bob-" + n)
                        : CalleeJoin(b, admittedB, RegionB, "vs-bob-" + n);
                    if (won) Interlocked.Increment(ref winners);
                }, TaskCreationOptions.LongRunning)).ToArray();
                Task.WaitAll(joins);
                if (winners > 1) tooMany++;
                if (winners == 0) none++;
            }
            Assert.That(tooMany, Is.EqualTo(0), "rounds in which more than one join was told it made the call active");
            Assert.That(none, Is.EqualTo(0), "rounds in which no join was told it made the call active");
        }

        [Test]
        public void ARejoinOfAnActiveCall_IsNotATransition()
        {
            NonSpatialVoiceSessionEngine engine = NewEngine();
            A2ACalls calls = RingingCall(new A2ACalls(() => engine), RegionA);
            A2ASession admitted = calls.TryGet(CallId, Bob);
            Assert.That(CalleeJoin(calls, admitted, RegionA, "vs-1"), Is.True);
            A2ASession active = calls.TryGet(CallId, Bob);
            Assert.That(CalleeJoin(calls, active, RegionA, "vs-2"), Is.False, "a reconnect of an active call is not a new accept");
        }

        [Test]
        public void TheCallersJoin_NeverMakesTheCallActive()
        {
            NonSpatialVoiceSessionEngine engine = NewEngine();
            A2ACalls calls = new A2ACalls(() => engine);
            calls.Record(Alice, Bob, RegionA, out _);
            A2ASession admitted = calls.TryGet(CallId, Alice);
            Assert.That(CallerJoin(calls, admitted, "vs-alice"), Is.False);
            Assert.That(calls.TryGet(CallId, Alice).State, Is.EqualTo(A2ASessionState.Invited));
        }
    }
}
