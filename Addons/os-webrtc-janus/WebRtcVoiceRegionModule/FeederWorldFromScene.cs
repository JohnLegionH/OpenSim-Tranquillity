/*
 * Binds the Scene-free IFeederWorld (Addons/os-webrtc-janus/Visibility) to a live
 * Scene / LandChannel / EstateSettings. This is the ONLY Scene-coupled part of the feeder;
 * it is exercised by the real-scene integration test and the in-world DEBUG smoke check, not
 * the pure engine unit tests.
 *
 * The parcel ban/restrict delegates read ACTUAL LandObject state on each call (this is where
 * VoiceStateFeeder.Tick's derivation hardening earns its keep — a live ParcelAccessList can
 * mutate mid-scan).
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public sealed class FeederWorldFromScene : IFeederWorld
    {
        // Unknown/absent parcel: benign (visible, voice-on, no bans) so an unresolved parcel
        // never spuriously hides or mutes. SeeAVs=true is important - default(ParcelView) would
        // be SeeAVs=false and hide everyone.
        private static readonly ParcelView UnknownParcel =
            new ParcelView(UUID.Zero, seeAVs: true, allowVoiceChat: true, null, null);

        private readonly Scene m_scene;

        public FeederWorldFromScene(Scene scene)
        {
            m_scene = scene;
        }

        public IReadOnlyList<AgentView> SnapshotAgents()
        {
            var agents = new List<AgentView>();
            // ForEachScenePresence copies each presence's fields into our list here (the "copied
            // snapshot"); root AND child are included. If the enumeration itself throws, Tick()
            // catches it and keeps the last matrix.
            m_scene.ForEachScenePresence(sp =>
                agents.Add(new AgentView(
                    sp.UUID, sp.IsChildAgent, sp.AbsolutePosition, sp.currentParcelUUID, sp.IsViewerUIGod)));
            return agents;
        }

        public ParcelView GetParcelAt(Vector3 position)
            => ToParcelView(m_scene.LandChannel?.GetLandObject(position.X, position.Y));

        public ParcelView GetParcelByGlobalId(UUID parcelGlobalId)
            => ToParcelView(m_scene.LandChannel?.GetLandObject(parcelGlobalId));

        public EstateView Estate
        {
            get
            {
                EstateSettings es = m_scene.RegionInfo.EstateSettings;
                return new EstateView(es.AllowVoice, es.TaxFree, a => es.IsBanned(a));
            }
        }

        private ParcelView ToParcelView(ILandObject parcel)
        {
            if (parcel == null)
                return UnknownParcel;
            LandData ld = parcel.LandData;
            if (ld == null)
                return UnknownParcel;

            bool allowVoiceChat = (ld.Flags & (uint)ParcelFlags.AllowVoiceChat) != 0;
            EstateSettings estate = m_scene.RegionInfo.EstateSettings;

            // Ban delegate — Decision 2b (ban-only void fix). When the estate is NOT TaxFree we
            // defer to the sim predicate (parity, zero drift). Under TaxFree,
            // LandObject.IsBannedFromLand returns false (defect §E: the legacy provision path at
            // WebRtcVoiceRegionModule.cs:301 never even ran on the estate channel), so we
            // re-evaluate ban-list membership minus the TaxFree short-circuit. Reads live LandData.
            Func<UUID, bool> banned = avatar =>
                estate.TaxFree
                    ? LandBan.IsBannedIgnoringTaxFree(
                        ld,
                        a => m_scene.Permissions.IsAdministrator(a),
                        a => estate.IsEstateManagerOrOwner(a),
                        avatar,
                        Util.UnixTimeSinceEpoch())
                    : parcel.IsBannedFromLand(avatar);

            // Restrict delegate — DELIBERATE divergence from the ban fix: use the real predicate
            // unconditionally, so access-restriction keeps the sim's TaxFree self-nullify. The §E
            // void fix is ban-only by decision; restrict is not re-implemented here.
            // (Diverges from the legacy :301 path only for bans, not restrict.)
            Func<UUID, bool> restricted = avatar => parcel.IsRestrictedFromLand(avatar);

            return new ParcelView(ld.GlobalID, ld.SeeAVs, allowVoiceChat, banned, restricted);
        }
    }
}
