/*
 * S-CON-1 (Docs/voice/connector-build-plan.md; brief Amendment 2 D2): the AllowNpcVoice guard.
 *
 * An NPC presence must not provision voice unless the operator opened the surface
 * ([WebRtcVoice] AllowNpcVoice=true) or the agent id is a registered voice-connector identity —
 * NPC voice exists only through a policy record. The decision is the pure, side-effect-free
 * WebRtcVoiceServiceModule.IsNpcProvisionRefused so it is unit-testable here (the full provision
 * path needs a live Scene and ScenePresence — the same reason IsProvisionableChannelType and
 * HasRealViewerSession are tested via their extracted predicates). The call site resolves
 * IVoiceConnectorRegistry per scene; a null registry contributes isConnectorIdentity=false,
 * identical to an empty one — covered by the false cases below.
 */
using NUnit.Framework;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NpcProvisionGuardTests
    {
        [Test]
        public void Npc_Disallowed_NotConnector_IsRefused()
        {
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: false, pIsConnectorIdentity: false), Is.True);
        }

        [Test]
        public void Npc_AllowNpcVoiceTrue_IsAdmitted()
        {
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: true, pIsConnectorIdentity: false), Is.False);
        }

        [Test]
        public void Npc_ConnectorIdentity_IsAdmitted()
        {
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: false, pIsConnectorIdentity: true), Is.False);
        }

        [Test]
        public void Avatar_IsNeverRefused()
        {
            // A real-avatar presence is unaffected by every knob combination.
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, false, false), Is.False);
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, true, false), Is.False);
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, false, true), Is.False);
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, true, true), Is.False);
        }
    }
}
