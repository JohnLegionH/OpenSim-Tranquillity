/*
 * Regression tests for JanusMessages.cs response parsing (OSDToLong).
 *
 * Defect: a Janus "create session" success response carries a 64-bit session id
 * (e.g. 923631757106466) that exceeds Int32. The ported libOMV JSON->OSD parser
 * returns OSDType.Long for it, but JanusMessage.OSDToLong only had cases for
 * Integer/Binary/Array and fell through to the initialized `0` — so the C# side
 * stored session id 0, every follow-up request 404'd (".../voice/0"), the plugin
 * attach failed, and the service disabled itself. Fixed by adding
 * `case OSDType.Long: ret = pIn.AsLong();`. See fix/janus-osdlong-session-id.
 */

using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusMessageParseTests
    {
        // A real Janus "create session" success response; id exceeds Int32.MaxValue.
        private const string CreateSessionSuccess =
            "{\"janus\":\"success\",\"transaction\":\"test123\",\"data\":{\"id\":923631757106466}}";

        private static OSD ParseId(long id) =>
            JanusMessageResp.FromJson("{\"janus\":\"success\",\"data\":{\"id\":" + id + "}}").dataSection["id"];

        // The exact consumer path that yielded "0" before the fix
        // (JanusSession.CreateSession reads CreateSessionResp.returnedId).
        [Test]
        public void CreateSessionResp_ReturnedId_RoundTripsRealResponse()
        {
            var createResp = new CreateSessionResp(JanusMessageResp.FromJson(CreateSessionSuccess));
            Assert.That(createResp.returnedId, Is.EqualTo("923631757106466"));
        }

        // Several ids that exceed Int32 must round-trip exactly (parsed as OSDType.Long).
        [TestCase(923631757106466L)]   // the real id from the field report
        [TestCase(4294967296L)]        // 2^32, just over Int32
        [TestCase(2147483648L)]        // Int32.MaxValue + 1 (boundary)
        [TestCase(9007199254740991L)]  // 2^53 - 1 (max exact double)
        public void OSDToLong_RoundTripsValuesGreaterThanInt32(long id)
        {
            OSD osd = ParseId(id);
            Assert.That(osd.Type, Is.EqualTo(OSDType.Long),
                "precondition: the JSON->OSD parser yields OSDType.Long for a >int32 number");
            Assert.That(JanusMessage.OSDToLong(osd), Is.EqualTo(id));
            // AsInteger() truncates to the low 32 bits — assert we did NOT fall back to it.
            Assert.That(JanusMessage.OSDToLong(osd), Is.Not.EqualTo(osd.AsInteger()));
        }

        // Edge cases the switch handles: OSDType.Integer (small ids) still work.
        [TestCase(0L)]
        [TestCase(1L)]
        [TestCase(12345L)]
        [TestCase(2147483647L)]        // Int32.MaxValue (still an OSDType.Integer)
        public void OSDToLong_Int32RangeValues_StillRoundTrip(long id)
        {
            OSD osd = ParseId(id);
            Assert.That(osd.Type, Is.EqualTo(OSDType.Integer),
                "precondition: an int32-range number parses as OSDType.Integer");
            Assert.That(JanusMessage.OSDToLong(osd), Is.EqualTo(id));
        }

        // The small-id path through the real consumer (CreateSessionResp) still works.
        [Test]
        public void CreateSessionResp_ReturnedId_SmallInt32Id_StillWorks()
        {
            var createResp = new CreateSessionResp(
                JanusMessageResp.FromJson("{\"janus\":\"success\",\"data\":{\"id\":12345}}"));
            Assert.That(createResp.returnedId, Is.EqualTo("12345"));
        }

        // OSDToLong constructed directly from an OSDInteger (the Integer switch arm).
        [Test]
        public void OSDToLong_FromOSDInteger_ReturnsValue()
        {
            Assert.That(JanusMessage.OSDToLong(OSD.FromInteger(424242)), Is.EqualTo(424242L));
        }
    }
}
