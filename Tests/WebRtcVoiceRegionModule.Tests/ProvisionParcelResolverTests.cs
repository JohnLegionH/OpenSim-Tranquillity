/*
 * Unit tests for ProvisionParcelResolver - O-48 (audit W-1). The server-derived parcel always wins;
 * the viewer's parcel_local_id only decides what gets logged.
 */

using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionParcelResolverTests
    {
        [Test]
        public void Honest_SameId_NoMismatch()
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(clientLocalId: 7, serverLocalId: 7);

            Assert.That(res.ServerLocalId, Is.EqualTo(7));
            Assert.That(res.ClientMismatch, Is.False);
            Assert.That(res.ClientAbsent, Is.False);
        }

        [Test]
        public void Spoof_DifferentId_MismatchAndServerIdWins()
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(clientLocalId: 12, serverLocalId: 7);

            Assert.That(res.ServerLocalId, Is.EqualTo(7));
            Assert.That(res.ClientMismatch, Is.True);
            Assert.That(res.ClientAbsent, Is.False);
        }

        [Test]
        public void Absent_NullId_ClientAbsentAndServerIdWins()
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(clientLocalId: null, serverLocalId: 7);

            Assert.That(res.ServerLocalId, Is.EqualTo(7));
            Assert.That(res.ClientMismatch, Is.False);
            Assert.That(res.ClientAbsent, Is.True);
        }
    }
}
