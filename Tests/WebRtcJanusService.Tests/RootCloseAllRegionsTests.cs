/*
 * O-74: a ROOT client close tears down the agent's voice sessions in EVERY region of this instance.
 * Live 2026-09-13 12:06: Aleric quit; the root close in Ebony captured only the Ebony session, the
 * Transylvania (neighbour, child) close was ignored by the child guard, and the viewer never logged the
 * Transylvania session out - it stayed a ghost participant holding a mixer slot, and the room could not
 * grace-destroy. The decision is WebRtcVoiceServiceModule.CaptureForClientClose (the close handler feeds
 * it the presence's child flag and the regions where the agent is currently root elsewhere).
 *
 * Teleport ordering (EntityTransferModule.TransferAgent_V2): the source presence is flagged child
 * (:1194) before UpdateAgent waits for the destination to become root, made child (:1232), and only then
 * closed (:1260) - so the source's close is a CHILD close and captures nothing.
 */
using OpenMetaverse;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class RootCloseAllRegionsTests
    {
        private static readonly UUID RegionA = new UUID("bbbbbbbb-0000-0000-0000-0000000a0074");
        private static readonly UUID RegionB = new UUID("bbbbbbbb-0000-0000-0000-0000000b0074");
        private static readonly UUID Aleric = new UUID("aaaaaaaa-2222-2222-2222-000000000074");
        private static readonly UUID Login = new UUID("cccccccc-3333-3333-3333-000000000074");
        private static readonly UUID NextLogin = new UUID("dddddddd-4444-4444-4444-000000000074");

        [TearDown]
        public void TearDown()
        {
            foreach (UUID token in new[] { Login, NextLogin, UUID.Zero })
                foreach (UUID region in new[] { RegionA, RegionB })
                    foreach (IVoiceViewerSession s in VoiceViewerSession.CaptureSessionsForClose(region, Aleric, token))
                        VoiceViewerSession.CloseCompleted(s);
        }

        private static FakeViewerSession Register(UUID region, UUID login)
        {
            var s = new FakeViewerSession
            {
                VoiceService = new FakeVoiceService(null),
                AgentId = Aleric,
                RegionId = region,
                ClientSessionId = login,
            };
            VoiceViewerSession.AddViewerSession(s);
            return s;
        }

        private static void Release(IEnumerable<IVoiceViewerSession> captured)
        {
            foreach (IVoiceViewerSession s in captured)
                VoiceViewerSession.CloseCompleted(s);
        }

        [Test]
        public void RootClose_CapturesTheAgentsSessionsInEveryRegion()
        {
            FakeViewerSession root = Register(RegionA, Login);
            FakeViewerSession neighbour = Register(RegionB, Login);

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                Aleric, pClosingPresenceIsChild: false, Login, new HashSet<UUID>());

            Assert.That(captured, Is.EquivalentTo(new IVoiceViewerSession[] { root, neighbour }),
                "the root region's AND the neighbour region's session are captured");
            Assert.That(VoiceViewerSession.IsAgentInRegion(RegionA, Aleric), Is.False);
            Assert.That(VoiceViewerSession.IsAgentInRegion(RegionB, Aleric), Is.False, "no ghost left in the neighbour region");
            Assert.That(VoiceViewerSession.CaptureSessionsForClose(RegionA, Aleric, Login), Is.Empty, "registry empty for the agent in A");
            Assert.That(VoiceViewerSession.CaptureSessionsForClose(RegionB, Aleric, Login), Is.Empty, "registry empty for the agent in B");
            Release(captured);
        }

        [Test]
        public void ChildClose_CapturesNothing()
        {
            Register(RegionA, Login);
            Register(RegionB, Login);

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                Aleric, pClosingPresenceIsChild: true, Login, new HashSet<UUID>());

            Assert.That(captured, Is.Empty, "a child close (border / draw distance) tears nothing down");
            Assert.That(VoiceViewerSession.IsAgentInRegion(RegionA, Aleric), Is.True);
            Assert.That(VoiceViewerSession.IsAgentInRegion(RegionB, Aleric), Is.True);
        }

        [Test]
        public void TeleportSourceClose_IsAChildClose_DestinationSessionSurvives()
        {
            // Same login in both regions: the teleport keeps the client SessionId, so the token alone
            // could not protect the destination. The ordering does: the source is already child when
            // CloseAgent fires after the teleport.
            FakeViewerSession source = Register(RegionA, Login);
            FakeViewerSession destination = Register(RegionB, Login);

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                Aleric, pClosingPresenceIsChild: true, Login, new HashSet<UUID>());

            Assert.That(captured, Is.Empty);
            Assert.That(VoiceViewerSession.IsAgentInRegion(RegionB, Aleric), Is.True, "the new root region's session is untouched");
            Assert.That(destination.ShutdownCalls, Is.EqualTo(0));
            Assert.That(source.ShutdownCalls, Is.EqualTo(0));
        }

        [Test]
        public void RootClose_SkipsARegionWhereTheAgentIsCurrentlyRoot()
        {
            // Belt and braces for any path that closed a root while the agent is already root elsewhere.
            FakeViewerSession closing = Register(RegionA, Login);
            FakeViewerSession liveRoot = Register(RegionB, Login);

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                Aleric, pClosingPresenceIsChild: false, Login, new HashSet<UUID> { RegionB });

            Assert.That(captured, Is.EquivalentTo(new IVoiceViewerSession[] { closing }));
            Assert.That(VoiceViewerSession.IsAgentInRegion(RegionB, Aleric), Is.True, "the region where the agent is root keeps its session");
            Assert.That(liveRoot.ShutdownCalls, Is.EqualTo(0));
            Release(captured);
        }

        [Test]
        public void RootClose_NeverCapturesASuccessorLoginsSession()
        {
            FakeViewerSession dying = Register(RegionA, Login);
            Register(RegionB, NextLogin);   // a relog racing the close, in the neighbour region

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                Aleric, pClosingPresenceIsChild: false, Login, new HashSet<UUID>());

            Assert.That(captured, Is.EquivalentTo(new IVoiceViewerSession[] { dying }));
            Assert.That(VoiceViewerSession.IsAgentInRegion(RegionB, Aleric), Is.True, "the successor login's session survives");
            Release(captured);
        }
    }
}
