/*
 * Security regression tests for the voice-provisioning channel_type guard
 * (WebRtcVoiceRegionModule.IsProvisionableChannelType), the fail-closed fix for ledger O-29.
 *
 * Defect: in WebRtcVoiceRegionModule.ProvisionVoiceAccountRequest, every parcel/estate
 * ban & restrict check was nested under `channel_type == "local"`. A provision request with
 * any other channel_type -- or none -- skipped all of them and reached the voice service,
 * provisioning voice past a parcel or estate ban. The CAP is client-reachable, so this was a
 * hand-craftable authorization bypass (avatar-to-avatar / "multiagent" is unimplemented, so
 * nothing legitimate drives a non-"local" channel_type today).
 *
 * Fix: the handler now refuses any request that is not exactly channel_type == "local", BEFORE
 * room selection or Janus session creation, with the same llsd<undef/> + 403 response an
 * unauthorized local request already gets. The admission decision is the pure, side-effect-free
 * IsProvisionableChannelType so it is unit-testable here (the full handler needs a live Scene,
 * ScenePresence and LandChannel and is not constructed in this harness -- the same reason the
 * viewer-session guard is tested via its extracted HasRealViewerSession helper, not the handler).
 *
 * SCOPE NOTE: "a local request with a ban still refuses" and "a clean local request still
 * provisions" are Scene-integration behaviours in the auth block BELOW this guard. That block is
 * byte-for-byte unchanged by the O-29 fix (the guard is added strictly BEFORE it, and a "local"
 * channel_type still falls straight through into it), so those behaviours are preserved by
 * construction. Local_channelType_IsAdmitted below is the unit-level proof that a "local" request
 * is NOT refused by the new guard and proceeds to those unchanged checks.
 */

using NUnit.Framework;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionChannelTypeGuardTests
    {
        // "multiagent" (the unimplemented avatar-to-avatar channel) is refused.
        [Test]
        public void Multiagent_channelType_IsRefused()
        {
            var req = new OSDMap { { "channel_type", OSD.FromString("multiagent") } };
            Assert.That(WebRtcVoiceRegionModule.IsProvisionableChannelType(req, out string ct), Is.False,
                "a non-\"local\" channel_type must be refused (fail closed, O-29)");
            Assert.That(ct, Is.EqualTo("multiagent"), "the seen value is surfaced for the refusal log");
        }

        // Any other arbitrary/hand-crafted value is refused too.
        [Test]
        public void ArbitraryChannelType_IsRefused()
        {
            var req = new OSDMap { { "channel_type", OSD.FromString("estate") } };
            Assert.That(WebRtcVoiceRegionModule.IsProvisionableChannelType(req, out _), Is.False);
        }

        // A missing channel_type also bypassed the checks in the old code -> must be refused.
        [Test]
        public void MissingChannelType_IsRefused()
        {
            var req = new OSDMap();
            Assert.That(WebRtcVoiceRegionModule.IsProvisionableChannelType(req, out string ct), Is.False,
                "a request with no channel_type must be refused (fail closed)");
            Assert.That(ct, Is.EqualTo(string.Empty), "absent channel_type is surfaced as empty for the log");
        }

        // An empty channel_type is not "local" -> refused.
        [Test]
        public void EmptyChannelType_IsRefused()
        {
            var req = new OSDMap { { "channel_type", OSD.FromString(string.Empty) } };
            Assert.That(WebRtcVoiceRegionModule.IsProvisionableChannelType(req, out _), Is.False);
        }

        // The ONE admitted case: exactly "local". Proves a local request is not refused by the guard
        // and proceeds into the unchanged parcel/estate ban & restrict checks below it.
        [Test]
        public void Local_channelType_IsAdmitted()
        {
            var req = new OSDMap { { "channel_type", OSD.FromString("local") } };
            Assert.That(WebRtcVoiceRegionModule.IsProvisionableChannelType(req, out string ct), Is.True,
                "\"local\" is the only authorized channel and must proceed to the auth checks");
            Assert.That(ct, Is.EqualTo("local"));
        }

        // Guard against a case-folding regression: SL sends lowercase "local"; anything else is refused.
        [Test]
        public void MixedCaseLocal_IsRefused()
        {
            var req = new OSDMap { { "channel_type", OSD.FromString("Local") } };
            Assert.That(WebRtcVoiceRegionModule.IsProvisionableChannelType(req, out _), Is.False,
                "admission is an exact match on \"local\"; a differing case is not silently accepted");
        }
    }
}
