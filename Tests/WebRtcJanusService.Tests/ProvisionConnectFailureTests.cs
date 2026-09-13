/*
 * O-53(b) (audit W-6): WebRtcJanusService.ProvisionVoiceAccountRequestBAD ignored the result of
 * ConnectToSessionAndAudioBridge. When the connect failed, AudioBridge stayed null and the offer's
 * SelectRoom threw NullReferenceException — a 500 through the caps catch, not a failure map — and a
 * half-built JanusSession was never destroyed.
 *
 * The service here has no [JanusWebRtcVoice] section: nothing connects at construction, and the
 * provision-time connect fails fast (empty gateway URI), which is the connect-returns-false case.
 */
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionConnectFailureTests
    {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000a0053");
        private static readonly UUID Alice = new UUID("aaaaaaaa-1111-1111-1111-0000000a0053");

        private static IConfigSource NoJanusConfig()
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            return config;
        }

        private static OSDMap LocalOffer() => new OSDMap
        {
            ["channel_type"] = OSD.FromString("local"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
            ["parcel_local_id"] = OSD.FromInteger(1),
            ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
        };

        [Test]
        public void ConnectFailure_ReturnsJanusUnavailableFailureMap_WithoutThrowing_AndLeavesNothingOnTheSession()
        {
            var service = new WebRtcJanusService(NoJanusConfig());
            var session = (JanusViewerSession)service.CreateViewerSession(LocalOffer(), Alice, Region);

            OSDMap resp = null;
            Assert.DoesNotThrow(() => resp = service.ProvisionVoiceAccountRequest(session, LocalOffer(), Alice, Region),
                "pre-fix: NullReferenceException from SelectRoom on the null AudioBridge");

            Assert.That(resp, Is.Not.Null, "a failure map, not null");
            Assert.That(resp["response"].AsString(), Is.EqualTo("failed"), "the same shape the room-full arm returns");
            Assert.That(resp["error"].AsString(), Is.EqualTo("janus unavailable"));
            Assert.That(resp.ContainsKey("error_code"), Is.False, "not a capacity rejection: no error_code, no 409");
            Assert.That(resp.ContainsKey("viewer_session"), Is.False, "failure signature: no viewer_session");
            Assert.That(session.Session, Is.Null, "no half-built JanusSession left on the viewer session");
            Assert.That(session.AudioBridge, Is.Null);
            Assert.That(session.Room, Is.Null);
        }
    }
}
