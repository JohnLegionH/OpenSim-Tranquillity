/*
 * S-A2A-5 (Docs/voice/a2a-build-plan.md; assessment §4): the viewer_session binding fix.
 *
 * Hazard: VoiceViewerSession.TryGetViewerSession binds by id string only, so any agent presenting
 * another agent's viewer_session id could drive that session (re-provision it into a room, feed it
 * ICE candidates, log it out). Both cap sites in WebRtcVoiceServiceModule now resolve through
 * TryGetViewerSessionFor, which additionally requires the session's AgentId to equal the cap-bound
 * requester; a mismatch is treated exactly as NOT FOUND (no new error shape) and logged at WARN
 * naming both agent ids.
 */
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ViewerSessionBindingTests
    {
        private static readonly UUID Region = new UUID("aaaaaaaa-0000-0000-0000-00000000a2a5");
        private static readonly UUID Owner = new UUID("11111111-1111-1111-1111-1111111a2a05");
        private static readonly UUID Other = new UUID("22222222-2222-2222-2222-2222222a2a05");

        /// <summary>Minimal ILogger that records what was logged, so the WARN is observable.</summary>
        private sealed class CapturingLogger : ILogger
        {
            public readonly List<(LogLevel Level, string Message)> Entries = new List<(LogLevel, string)>();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }

        private VoiceViewerSession _session;
        private CapturingLogger _log;

        [SetUp]
        public void SetUp()
        {
            _session = new VoiceViewerSession(null, Region, Owner);
            VoiceViewerSession.AddViewerSession(_session);
            _log = new CapturingLogger();
        }

        [TearDown]
        public void TearDown()
        {
            VoiceViewerSession.RemoveViewerSession(_session.ViewerSessionID);
        }

        [TestCase("ProvisionVoiceAccountRequest")]
        [TestCase("VoiceSignalingRequest")]
        public void OwnerLookup_Found_NoWarn(string site)
        {
            bool found = WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Owner, site, _log, out IVoiceViewerSession s);
            Assert.That(found, Is.True);
            Assert.That(s, Is.SameAs(_session));
            Assert.That(_log.Entries, Is.Empty);
        }

        [TestCase("ProvisionVoiceAccountRequest")]
        [TestCase("VoiceSignalingRequest")]
        public void OtherAgentLookup_TreatedAsNotFound_AndWarns(string site)
        {
            bool found = WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Other, site, _log, out IVoiceViewerSession s);
            Assert.That(found, Is.False, "another agent's viewer_session must resolve to nothing");
            Assert.That(s, Is.Null, "the mismatched session must not leak to the caller");
            Assert.That(_log.Entries, Has.Count.EqualTo(1));
            Assert.That(_log.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(_log.Entries[0].Message, Does.Contain(site));
            Assert.That(_log.Entries[0].Message, Does.Contain(Other.ToString()), "names the requester");
            Assert.That(_log.Entries[0].Message, Does.Contain(Owner.ToString()), "names the session's bound agent");
            Assert.That(_log.Entries[0].Message, Does.Contain(_session.ViewerSessionID));
        }

        [Test]
        public void OtherAgentLookup_LeavesTheSessionRegistered()
        {
            // Treated as not-found for the requester only: the owner's session is untouched.
            WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Other, "VoiceSignalingRequest", _log, out _);
            Assert.That(VoiceViewerSession.TryGetViewerSession(_session.ViewerSessionID, out IVoiceViewerSession still), Is.True);
            Assert.That(still, Is.SameAs(_session));
            Assert.That(WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Owner, "VoiceSignalingRequest", _log, out _), Is.True);
        }

        [Test]
        public void UnknownId_NotFound_NoWarn()
        {
            // The pre-existing not-found path: no session at all is not a spoof, so no WARN here
            // (the call sites keep their existing "not found" ERROR line).
            bool found = WebRtcVoiceServiceModule.TryGetViewerSessionFor(UUID.Random().ToString(), Owner, "ProvisionVoiceAccountRequest", _log, out IVoiceViewerSession s);
            Assert.That(found, Is.False);
            Assert.That(s, Is.Null);
            Assert.That(_log.Entries, Is.Empty);
        }

        [Test]
        public void ZeroRequester_NeverMatches()
        {
            // A cap handler always has a real agent; UUID.Zero must not become a wildcard.
            bool found = WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, UUID.Zero, "ProvisionVoiceAccountRequest", _log, out _);
            Assert.That(found, Is.False);
            Assert.That(_log.Entries, Has.Count.EqualTo(1));
        }

        [Test]
        public void NullLogger_StillRefuses()
        {
            Assert.That(WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Other, "VoiceSignalingRequest", null, out IVoiceViewerSession s), Is.False);
            Assert.That(s, Is.Null);
        }
    }
}
