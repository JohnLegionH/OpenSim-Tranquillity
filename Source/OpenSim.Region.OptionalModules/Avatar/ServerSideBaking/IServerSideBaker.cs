using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>Why a server-side bake was requested. S1 uses only <see cref="Console"/>.</summary>
public enum BakeReason
{
    Console,
    Login,
    CofChanged,
    Cap,
}

/// <summary>What happened to one bake channel.</summary>
public enum ChannelStatus
{
    /// <summary>Composited, stored as an asset, written to the TextureEntry face.</summary>
    Baked,
    /// <summary>Inputs unchanged; the stored bake was reused (S2, not produced in S1).</summary>
    Reused,
    /// <summary>The library produced nothing for the channel (nothing worn for it); the face was left as it was.</summary>
    Skipped,
    /// <summary>An input for the channel was unusable (missing/unparseable wearable, missing/undecodable texture); the face was left as it was.</summary>
    Failed,
}

/// <summary>One channel's outcome. <see cref="AssetId"/> is the stored bake for Baked/Reused, UUID.Zero otherwise; <see cref="InputHash"/> is the bake's <c>BakeHash</c> (the stored asset's Description) or "".</summary>
public sealed record ChannelOutcome(BakeChannel Channel, ChannelStatus Status, UUID AssetId, string InputHash, string Reason, FidelityReport Fidelity);

/// <summary>The result of one bake run for one agent.</summary>
public sealed record BakeOutcome(UUID AgentId, BakeReason Reason, IReadOnlyList<ChannelOutcome> Channels, long ElapsedMs)
{
    public int Count(ChannelStatus status)
    {
        var n = 0;
        foreach (var c in Channels) if (c.Status == status) n++;
        return n;
    }
}

/// <summary>
/// Region-scoped server-side baker (Design Brief §4.2, ADR-002/004/005). Composites the agent's current wearables
/// through <see cref="IBakeBackend"/>, stores the bakes as assets, writes the baked faces of the presence's
/// TextureEntry, sends the appearance and queues the normal appearance save.
/// </summary>
public interface IServerSideBaker
{
    Task<BakeOutcome> BakeAsync(ScenePresence sp, BakeReason reason, CancellationToken ct);
}
