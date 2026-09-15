/*
 * Slice 0.2 §6.2: spatial "local" rooms are created with vis_authority=true only when the sim arms. The pre-0.2 create
 * body is also pinned byte-for-byte by VisibilityKnobOffGoldenTests.
 */

using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisAuthorityRoomDeclarationTests
    {
        private static OSDMap Body(AudioBridgeCreateRoomReq req)
        {
            req.ToJson();
            return (OSDMap)req.RawBody["body"];
        }

        [Test]
        public void CreateBody_WithoutDeclaration_HasNoVisAuthorityKey()
        {
            Assert.That(Body(new AudioBridgeCreateRoomReq(7, true, "d")).ContainsKey("vis_authority"), Is.False);
            Assert.That(Body(new AudioBridgeCreateRoomReq(7, true, "d", false)).ContainsKey("vis_authority"), Is.False);
        }

        [Test]
        public void CreateBody_Declared_CarriesVisAuthorityTrue_AfterEveryOtherKey()
        {
            OSDMap body = Body(new AudioBridgeCreateRoomReq(7, true, "d", true));
            Assert.That(body["vis_authority"].AsBoolean(), Is.True);
            Assert.That(OSDParser.SerializeJsonString(body, true), Does.EndWith("\"description\":\"d\",\"vis_authority\":true}"));
        }

        [Test]
        public void OnlySpatialLocalRooms_AreDeclared_AndOnlyWhenArming()
        {
            Assert.That(JanusAudioBridge.ShouldDeclareVisAuthority(true, "local"), Is.True);
            Assert.That(JanusAudioBridge.ShouldDeclareVisAuthority(true, "multiagent"), Is.False, "A2A rooms get no batches");
            Assert.That(JanusAudioBridge.ShouldDeclareVisAuthority(false, "local"), Is.False);
        }
    }
}
