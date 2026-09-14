/*
 * V-1 (O-78): [WebRtcVoice] VisibilityFeederEnabled and VisibilityEmitEnabled default to true, so
 * a fresh deployment pushes mix-enforced permissions to the mixer. Before V-1 both defaulted to
 * false and an install that never set them pushed nothing. These tests pin the key-absent
 * resolution the region module's Initialise uses (WebRtcVoiceRegionModule.cs, the two Read*
 * calls), independent of any ini file.
 */
using NUnit.Framework;
using Nini.Config;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisibilityDefaultsTests
    {
        // A [WebRtcVoice] section as a minimal install writes it: enabled, no visibility keys.
        private static IConfig SectionWithoutVisibilityKeys()
        {
            IniConfigSource src = new IniConfigSource();
            IConfig cfg = src.AddConfig("WebRtcVoice");
            cfg.Set("Enabled", "true");
            return cfg;
        }

        [Test]
        public void Defaults_AreTrue()
        {
            Assert.That(WebRtcVoiceRegionModule.DefaultVisibilityFeederEnabled, Is.True);
            Assert.That(WebRtcVoiceRegionModule.DefaultVisibilityEmitEnabled, Is.True);
        }

        [Test]
        public void VisibilityFeederEnabled_ResolvesTrue_WhenKeyAbsent()
        {
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityFeederEnabled(SectionWithoutVisibilityKeys()), Is.True);
        }

        [Test]
        public void VisibilityEmitEnabled_ResolvesTrue_WhenKeyAbsent()
        {
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityEmitEnabled(SectionWithoutVisibilityKeys()), Is.True);
        }

        // An operator who wants the pre-V-1 behaviour sets the keys explicitly.
        [Test]
        public void ExplicitFalse_IsHonored()
        {
            IConfig cfg = SectionWithoutVisibilityKeys();
            cfg.Set("VisibilityFeederEnabled", "false");
            cfg.Set("VisibilityEmitEnabled", "false");
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityFeederEnabled(cfg), Is.False);
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityEmitEnabled(cfg), Is.False);
        }
    }
}
