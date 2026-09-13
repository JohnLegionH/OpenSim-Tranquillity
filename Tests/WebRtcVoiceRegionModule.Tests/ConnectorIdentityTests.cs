/*
 * O-63: ConnectorIdentity - the connector NPC's derived agent id (RFC 4122 v5 under the Legion namespace).
 * Expected values were computed independently with Python's uuid.uuid5.
 */

using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ConnectorIdentityTests
    {
        private const string Grid = "http://legiongrid.ddns.net:8002";

        private static readonly UUID NamespaceDns = new UUID("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        private static readonly UUID NamespaceUrl = new UUID("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

        [Test]
        public void NameBasedV5_MatchesThePublishedVector()
        {
            // Python: uuid.uuid5(uuid.NAMESPACE_DNS, "www.example.com")
            Assert.That(ConnectorIdentity.NameBasedV5(NamespaceDns, "www.example.com"),
                Is.EqualTo(new UUID("2ed6657d-e927-568b-95e1-2665a8aea6a2")));
        }

        [Test]
        public void LegionNamespace_IsTheV5OfItsDocumentedName()
        {
            Assert.That(ConnectorIdentity.NameBasedV5(NamespaceUrl, ConnectorIdentity.LegionNamespaceName),
                Is.EqualTo(ConnectorIdentity.LegionNamespace));
            Assert.That(ConnectorIdentity.LegionNamespace, Is.EqualTo(new UUID("52a31546-4858-5791-8b48-262208d96039")));
        }

        [Test]
        public void SameInputs_SameId_AcrossCalls_AndMatchesTheIndependentValue()
        {
            UUID first = ConnectorIdentity.DeriveAgentId(Grid, "Ebony", "Recorder");
            UUID second = ConnectorIdentity.DeriveAgentId(Grid, "Ebony", "Recorder");

            Assert.That(second, Is.EqualTo(first), "a restart derives the same id");
            Assert.That(first, Is.EqualTo(new UUID("5560f02f-0b43-5ef3-a023-bba0a18edf6a")), "uuid5(legion_ns, \"<grid>/Ebony/Recorder\")");
            Assert.That(ConnectorIdentity.DeriveAgentId(string.Empty, "Ebony", "Recorder"),
                Is.EqualTo(new UUID("7b8f656b-8b15-5053-8947-5f8c05f336b2")), "a grid without GatekeeperURI: \"/Ebony/Recorder\"");
            Assert.That(ConnectorIdentity.IdentityName(Grid, "Ebony", "Recorder"), Is.EqualTo(Grid + "/Ebony/Recorder"));
        }

        [TestCase("http://othergrid.example:8002", "Ebony", "Recorder")]
        [TestCase(Grid, "Transylvania", "Recorder")]
        [TestCase(Grid, "Ebony", "Injector")]
        [TestCase(Grid, "Ebony", "recorder")]   // case-sensitive
        [TestCase(Grid, "ebony", "Recorder")]
        public void AnyOneInputDiffering_GivesADifferentId(string pGrid, string pRegion, string pRecord)
        {
            UUID baseline = ConnectorIdentity.DeriveAgentId(Grid, "Ebony", "Recorder");
            Assert.That(ConnectorIdentity.DeriveAgentId(pGrid, pRegion, pRecord), Is.Not.EqualTo(baseline));
        }

        [TestCase(Grid, "Ebony", "Recorder")]
        [TestCase("", "Elm", "Injector")]
        [TestCase(Grid, "Transylvania", "Speaker")]
        public void DerivedIds_AreVersion5_WithTheRfc4122Variant(string pGrid, string pRegion, string pRecord)
        {
            string s = ConnectorIdentity.DeriveAgentId(pGrid, pRegion, pRecord).ToString();
            Assert.That(s[14], Is.EqualTo('5'), "version nibble");
            Assert.That("89ab".IndexOf(s[19]), Is.GreaterThanOrEqualTo(0), "variant bits 10xx");
        }
    }
}
