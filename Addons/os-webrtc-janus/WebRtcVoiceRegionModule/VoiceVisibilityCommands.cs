/*
 * Console surface for the peer_ctl_batch sink's own counters (slice 0.6).
 *
 * WHY THIS EXISTS. JanusPeerCtlBatchSink has carried LastSendFallback* and LastSendStats since
 * slices S3b/S4, and its own doc comment said so plainly: "PLUMBING only -- read by nobody today;
 * surfaced for a future decision". The 0.6 shadow soak is that decision. The soak has to report the
 * sink's fallback counters, and a counter nothing can read is a counter the soak cannot report, so
 * this adds the minimal reader rather than leaving the pass condition quietly unmeasurable.
 *
 * STRICTLY READ-ONLY. It formats volatile ints already being maintained; it sends nothing, mutates
 * nothing, and cannot change the wire or the audio path. That is deliberate: it ships in the same
 * restart as [WebRtcVoice] VisibilityArmingEnabled=true, and a reader that cannot act is a reader
 * that cannot be a second candidate cause for anything the soak observes.
 *
 * Region scoping and the snapshot supplier follow VoiceModerationCommands exactly: a COPY of the
 * per-region map, never the live dictionary, because a console handler writes to a terminal and must
 * not do that while holding the lock the CAP handler and RegionLoaded contend for.
 */

using System;
using System.Collections.Generic;
using System.Reflection;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public sealed class VoiceVisibilityCommands
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string logHeader = "[VOICE VISIBILITY CONSOLE]";

        private readonly Func<List<KeyValuePair<Scene, VoiceVisibilityService>>> m_regions;

        public VoiceVisibilityCommands(Func<List<KeyValuePair<Scene, VoiceVisibilityService>>> regions)
        {
            m_regions = regions;
        }

        public void Register()
        {
            if (MainConsole.Instance is null)
                return;   // unit tests / embedded hosts have no console

            MainConsole.Instance.Commands.AddCommand("Voice", false, "show voice visibility",
                "show voice visibility",
                "Show the peer_ctl_batch sink's counters for the most recent send, per region",
                "Every figure describes the MOST RECENT send only, except FallbackRoom which is fixed at start.\n"
                    + "Fallback listeners/sources > 0 means an agent had no room record and was addressed at the\n"
                    + "estate/local fallback room instead of its own (OQ4).\n"
                    + "Of the inner-reply stats, skipped > 0 is real loss and anomalies > 0 is protocol drift;\n"
                    + "deferred > 0 is normal (the mixer replays those at join).\n"
                    + "Reports the region selected with \"change region\", or every region at the root prompt.",
                HandleShowVoiceVisibility);
        }

        // --- Handlers -------------------------------------------------------------------------

        private void HandleShowVoiceVisibility(string module, string[] args)
        {
            List<KeyValuePair<Scene, VoiceVisibilityService>> regions = SelectedRegions();
            if (regions.Count == 0)
            {
                MainConsole.Instance.Output(
                    "No region here is running the voice visibility feeder, so there is no sink to report (see VisibilityFeederEnabled).");
                return;
            }

            foreach (KeyValuePair<Scene, VoiceVisibilityService> kv in regions)
            {
                string region = kv.Key.RegionInfo.RegionName;
                JanusPeerCtlBatchSink sink = kv.Value.JanusSink;
                if (sink is null)
                {
                    MainConsole.Instance.Output(
                        "Region \"{0}\": matrix-only, no Janus peer_ctl sink (emission off, or [JanusWebRtcVoice] admin config missing).",
                        region);
                    continue;
                }

                PeerCtlSendStats s = sink.LastSendStats;
                MainConsole.Instance.Output("Region \"{0}\": fallback room {1}, last send addressed {2} room(s)",
                    region, sink.FallbackRoom, sink.LastSendRooms);
                MainConsole.Instance.Output(
                    "  fallback (no room record)  excl listeners {0}, excl sources {1}, mute listeners {2}, mute sources {3}",
                    sink.LastSendFallbackListeners, sink.LastSendFallbackSources,
                    sink.LastSendMuteFallbackListeners, sink.LastSendMuteFallbackSources);
                MainConsole.Instance.Output(
                    "  inner replies              parsed {0}, entries {1}, mute entries {2}, deferred {3} (normal), skipped {4} (loss), anomalies {5} (drift)",
                    s.RepliesParsed, s.Entries, s.MuteEntries, s.DeferredListeners, s.Skipped, s.Anomalies);
            }
        }

        // --- Helpers --------------------------------------------------------------------------

        /// The regions this invocation reports on: the one selected with "change region", else all.
        private List<KeyValuePair<Scene, VoiceVisibilityService>> SelectedRegions()
        {
            List<KeyValuePair<Scene, VoiceVisibilityService>> all = m_regions();
            IScene selected = MainConsole.Instance.ConsoleScene;
            if (selected is null)
                return all;
            all.RemoveAll(kv => !ReferenceEquals(kv.Key, selected));
            return all;
        }
    }
}
