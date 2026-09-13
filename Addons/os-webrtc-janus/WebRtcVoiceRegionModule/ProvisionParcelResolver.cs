/*
 * O-48 (audit W-1): which parcel a "local" voice provision is authorized and roomed against.
 *
 * For a ROOT agent the parcel is derived server-side from the avatar's position; the viewer's
 * parcel_local_id is a hint only. It never selects the parcel — it only decides what gets logged (a
 * mismatch, or an absent id). Before O-48 the region module looked the parcel up by the client's id,
 * so a viewer could name another parcel and join its mixer room. A mismatch is never refused: a viewer
 * mid-crossing can be honestly stale (O-11).
 *
 * O-48a: a CHILD agent (the viewer's neighbour-region provision) has a position that is not inside this
 * region, so position derivation finds no parcel. A child keeps the pre-slice-1 path: a client id, if
 * sent, selects the parcel and every check runs against it; no id (the stock viewer, live 2026-09-13)
 * means no parcel checks and the service's estate room (-999).
 *
 * Pure and Scene-free so the decision unit-tests without a Scene.
 */

namespace osWebRtcVoice
{
    public enum ParcelSource
    {
        /// <summary>Root agent: the parcel under the avatar's position.</summary>
        ServerPosition,
        /// <summary>Child agent that sent parcel_local_id: the client's parcel (pre-slice-1 path).</summary>
        ClientHint,
        /// <summary>Child agent with no parcel_local_id: no parcel checks, estate room.</summary>
        None,
    }

    public readonly record struct ParcelResolveInput(int? ClientLocalId, bool IsChildAgent);

    /// <param name="Source">Where the parcel comes from.</param>
    /// <param name="LocalId">The parcel to check and room against; null for <see cref="ParcelSource.None"/>,
    /// or for a root agent whose position resolved to no parcel (a refusal).</param>
    /// <param name="ServerLocalId">The position-derived parcel (root only; null otherwise).</param>
    public readonly record struct ParcelResolution(ParcelSource Source, int? LocalId, int? ServerLocalId, bool ClientMismatch, bool ClientAbsent);

    public static class ProvisionParcelResolver
    {
        /// <summary>Whether the handler should look the parcel up by the avatar's position (root agents only).</summary>
        public static bool DerivesFromPosition(ParcelResolveInput input) => !input.IsChildAgent;

        /// <param name="serverLocalId">The parcel under the avatar's position, or null when none was found
        /// or none was looked up (a child agent). Ignored for a child agent.</param>
        public static ParcelResolution Resolve(ParcelResolveInput input, int? serverLocalId)
        {
            int? client = input.ClientLocalId;

            if (input.IsChildAgent)
            {
                return client is null
                    ? new ParcelResolution(ParcelSource.None, LocalId: null, ServerLocalId: null, ClientMismatch: false, ClientAbsent: true)
                    : new ParcelResolution(ParcelSource.ClientHint, LocalId: client, ServerLocalId: null, ClientMismatch: false, ClientAbsent: false);
            }

            return new ParcelResolution(
                Source: ParcelSource.ServerPosition,
                LocalId: serverLocalId,
                ServerLocalId: serverLocalId,
                ClientMismatch: client is not null && serverLocalId is not null && client != serverLocalId,
                ClientAbsent: client is null);
        }

        /// <summary>Root-agent form (O-48 slice 1).</summary>
        public static ParcelResolution Resolve(int? clientLocalId, int serverLocalId)
            => Resolve(new ParcelResolveInput(clientLocalId, IsChildAgent: false), serverLocalId);
    }
}
