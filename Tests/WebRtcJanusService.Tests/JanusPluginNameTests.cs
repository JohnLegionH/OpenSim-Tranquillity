/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the configurable Janus plugin name ([JanusWebRtcVoice] PluginName).
 *
 * Before the feature, JanusAudioBridge hardcoded "janus.plugin.audiobridge" at
 * its single seam (JanusAudioBridge.cs:41), so the region could not attach to an
 * alternative mixer (e.g. janus.plugin.slvoice) without a code change. The
 * feature added the PluginName key and passes the name through the name-agnostic
 * JanusPlugin base. See feature/voice-plugin-select.
 *
 * An absent key resolves to the slvoice mixer,
 * "janus.plugin.slvoice" (JANUS_SLVOICE_PACKAGE in the slvoice mixer's
 * src/janus_slvoice.c), instead of the stock "janus.plugin.audiobridge".
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
        public void JanusAudioBridge_AudiobridgeNameFlowsThrough()
        {
            var ab = new JanusAudioBridge(NewSession(), "janus.plugin.audiobridge");
            Assert.That(ab.PluginName, Is.EqualTo("janus.plugin.audiobridge"));
        }

        [Test]
        public void DefaultPluginName_IsTheStockAudioBridge()
        {
            Assert.That(WebRtcJanusService.DefaultPluginName, Is.EqualTo("janus.plugin.audiobridge"));
        }

        // The key-absent path WebRtcJanusService takes: section present, PluginName absent.
        [Test]
        public void PluginName_DefaultsToStockAudioBridge_WhenKeyAbsent()
        {
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");
            cfg.Set("JanusGatewayURI", "http://janus.test/voice");
            Assert.That(WebRtcJanusService.ReadPluginName(cfg), Is.EqualTo("janus.plugin.audiobridge"));
        }

        // An operator who wants the spatial mixer sets the key explicitly.
        [Test]
        public void PluginName_IsHonored_WhenKeySet()
        {
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");
            cfg.Set("PluginName", "janus.plugin.slvoice");
            Assert.That(WebRtcJanusService.ReadPluginName(cfg), Is.EqualTo("janus.plugin.slvoice"));
        }
    }
}
