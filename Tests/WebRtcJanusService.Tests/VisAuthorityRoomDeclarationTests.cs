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
            // Slice 1.4: sim_created is appended after vis_authority, so the declaration is no longer the LAST
            // key - it is still after every key the pre-0.2 body had, which is what this assertion is for.
            Assert.That(OSDParser.SerializeJsonString(body, true),
                Does.EndWith("\"description\":\"d\",\"vis_authority\":true,\"sim_created\":true}"));
        }

        // ---- slice 1.4 (ledger O-105): the sim marks every room it creates ----------------------------

        [Test]
        public void CreateBody_AlwaysCarriesSimCreated_DeclaredOrNot()
        {
            // The mixer's capability requirement keys on THIS from 1.4, not on vis_authority, so an undeclared
            // sim room - every A2A "multiagent" room - is gated too. Every constructor overload must carry it.
            Assert.That(Body(new AudioBridgeCreateRoomReq(7))["sim_created"].AsBoolean(), Is.True);
            Assert.That(Body(new AudioBridgeCreateRoomReq(7, true, "d"))["sim_created"].AsBoolean(), Is.True);
            Assert.That(Body(new AudioBridgeCreateRoomReq(7, false, "d", false))["sim_created"].AsBoolean(), Is.True,
                "an undeclared (A2A/multiagent) room is marked too: that is the whole point of O-105");
            Assert.That(Body(new AudioBridgeCreateRoomReq(7, true, "d", true))["sim_created"].AsBoolean(), Is.True);
        }

        [Test]
        public void SimCreated_IsIndependentOfTheDeclaration()
        {
            // A room can be marked and undeclared (multiagent), or marked and declared (spatial local). The two
            // keys answer different questions: who ARMS the room, and who OWNS it.
            OSDMap undeclared = Body(new AudioBridgeCreateRoomReq(7, false, "a2a", false));
            Assert.That(undeclared.ContainsKey("vis_authority"), Is.False);
            Assert.That(undeclared["sim_created"].AsBoolean(), Is.True);

            OSDMap declared = Body(new AudioBridgeCreateRoomReq(7, true, "local", true));
            Assert.That(declared["vis_authority"].AsBoolean(), Is.True);
            Assert.That(declared["sim_created"].AsBoolean(), Is.True);
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
