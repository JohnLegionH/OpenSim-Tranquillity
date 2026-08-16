/*
 * Scene-free input seam for the VoiceStateFeeder (Phase 3a visibility matrix).
 *
 * Everything the feeder needs to read from the sim is abstracted behind IFeederWorld and
 * three value views, so the matrix logic unit-tests without a Scene (tests inject fakes) -
 * mirroring how the room-race tests injected Func<Task<JanusRoom>> instead of a live Janus.
 * The production adapter (FeederWorldFromScene, in the region module) fills these from
 * ScenePresence / ILandObject / EstateSettings.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    /// A point-in-time view of one avatar. Snapshotted per tick so a matrix build is
    /// deterministic. Child parcels are derived from Position; root parcels use the cached
    /// CurrentParcelUUID (unreliable for children - see parcel-voice-semantics.md 2.2).
    public readonly struct AgentView
    {
        public readonly UUID Id;
        public readonly bool IsChild;
        public readonly Vector3 Position;         // ScenePresence.AbsolutePosition
        public readonly UUID CurrentParcelUUID;   // cached; reliable for root, ignored for child
        public readonly bool IsGod;               // ScenePresence.IsViewerUIGod

        public AgentView(UUID id, bool isChild, Vector3 position, UUID currentParcelUUID, bool isGod)
        {
            Id = id;
            IsChild = isChild;
            Position = position;
            CurrentParcelUUID = currentParcelUUID;
            IsGod = isGod;
        }
    }

    /// Parcel facts plus the sim ban/restrict predicates. The predicates are delegates so the
    /// production adapter forwards to the live LandObject (exemption parity - open item #4) and
    /// tests supply fakes.
    ///
    /// Decision 2(b) - the estate-channel ban void is FIXED here: the production delegates
    /// evaluate parcel ban-list membership with the admin/EM/owner/self exemptions but WITHOUT
    /// the TaxFree short-circuit, so a parcel ban is honored in voice even on a TaxFree estate,
    /// and independently of the banned avatar's physical position (a banned avatar standing
    /// inside the banning parcel is still excluded). The pure rule below is agnostic to how the
    /// delegate decides; it only composes the symmetric structure.
    public readonly struct ParcelView
    {
        public readonly UUID GlobalId;
        public readonly bool SeeAVs;            // LandData.SeeAVs (true = occupants visible)
        public readonly bool AllowVoiceChat;    // ParcelFlags.AllowVoiceChat off land.Flags
        public readonly Func<UUID, bool> IsBannedFromLand;
        public readonly Func<UUID, bool> IsRestrictedFromLand;

        public ParcelView(UUID globalId, bool seeAVs, bool allowVoiceChat,
                          Func<UUID, bool> isBannedFromLand, Func<UUID, bool> isRestrictedFromLand)
        {
            GlobalId = globalId;
            SeeAVs = seeAVs;
            AllowVoiceChat = allowVoiceChat;
            IsBannedFromLand = isBannedFromLand;
            IsRestrictedFromLand = isRestrictedFromLand;
        }

        /// True if this parcel bans or access-restricts the given avatar.
        public bool ExcludesByBan(UUID avatar)
            => (IsBannedFromLand != null && IsBannedFromLand(avatar))
            || (IsRestrictedFromLand != null && IsRestrictedFromLand(avatar));
    }

    /// Estate-level facts. TaxFree overrides the parcel voice-ENABLE flag only (semantics doc
    /// 2.2); Decision 2(b) does not use TaxFree to exempt bans (that fix lives in the ban
    /// delegate above).
    public readonly struct EstateView
    {
        public readonly bool AllowVoice;        // EstateSettings.AllowVoice (master switch)
        public readonly bool TaxFree;           // !AllowAccessOverride
        public readonly Func<UUID, bool> IsBanned;  // EstateSettings.IsBanned

        public EstateView(bool allowVoice, bool taxFree, Func<UUID, bool> isBanned)
        {
            AllowVoice = allowVoice;
            TaxFree = taxFree;
            IsBanned = isBanned;
        }

        public bool IsEstateBanned(UUID avatar) => IsBanned != null && IsBanned(avatar);
    }

    /// Everything the feeder reads from the sim, abstracted. One snapshot drives one matrix.
    public interface IFeederWorld
    {
        /// Root AND child presences (children included - baseline 2.3) as of now.
        IReadOnlyList<AgentView> SnapshotAgents();

        /// Parcel under a world position - LandChannel.GetLandObject(x,y). Child path.
        ParcelView GetParcelAt(Vector3 position);

        /// Parcel by its GlobalID - the root path (cached currentParcelUUID).
        ParcelView GetParcelByGlobalId(UUID parcelGlobalId);

        EstateView Estate { get; }
    }
}
