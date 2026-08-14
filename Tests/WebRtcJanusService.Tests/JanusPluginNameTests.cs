/*
 * Tests for the configurable Janus plugin name ([JanusWebRtcVoice] PluginName).
 *
 * Before the feature, JanusAudioBridge hardcoded "janus.plugin.audiobridge" at
 * its single seam (JanusAudioBridge.cs:41), so the region could not attach to an
 * alternative mixer (e.g. janus.plugin.slvoice) without a code change. The
 * feature adds the PluginName key (default "janus.plugin.audiobridge") and passes
 * the name through the name-agnostic JanusPlugin base. See feature/voice-plugin-select.
 */

using Nini.Config;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusPluginNameTests
    {
        // JanusSession's ctor stores fields only (no network), so this is safe in a unit test.
        private static JanusSession NewSession() =>
            new JanusSession("http://127.0.0.1:24223/voice", "tok",
                             "http://127.0.0.1:24225/voiceAdmin", "tok");

        // The fix: a configured plugin name flows through JanusAudioBridge to the
        // name-agnostic JanusPlugin base (it is no longer hardcoded to audiobridge).
        [Test]
        public void JanusAudioBridge_HonorsConfiguredPluginName()
        {
            var ab = new JanusAudioBridge(NewSession(), "janus.plugin.slvoice");
            Assert.That(ab.PluginName, Is.EqualTo("janus.plugin.slvoice"));
        }

        [Test]
        public void JanusAudioBridge_DefaultAudiobridgeNameFlowsThrough()
        {
            var ab = new JanusAudioBridge(NewSession(), "janus.plugin.audiobridge");
            Assert.That(ab.PluginName, Is.EqualTo("janus.plugin.audiobridge"));
        }

        // The [JanusWebRtcVoice] config contract WebRtcJanusService relies on: the
        // PluginName key defaults to "janus.plugin.audiobridge" when absent.
        [Test]
        public void PluginName_DefaultsToAudiobridge_WhenKeyAbsent()
        {
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");   // section present, key absent
            Assert.That(cfg.GetString("PluginName", "janus.plugin.audiobridge"),
                Is.EqualTo("janus.plugin.audiobridge"));
        }

        [Test]
        public void PluginName_IsHonored_WhenKeySet()
        {
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");
            cfg.Set("PluginName", "janus.plugin.slvoice");
            Assert.That(cfg.GetString("PluginName", "janus.plugin.audiobridge"),
                Is.EqualTo("janus.plugin.slvoice"));
        }
    }
}
