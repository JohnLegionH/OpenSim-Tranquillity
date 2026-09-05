using System.Collections.Concurrent;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// One region's server-side-baking state: whether the flag resolved on here, which agents this sim has baked and
/// at what COF version, and the §4.3 handshake's counters. Registered on the scene as
/// <see cref="IServerSideBakingRegion"/> so <c>LLClientView</c> and <c>ScenePresence</c> can read the two wire
/// facts without referencing this module.
/// </summary>
public sealed class ServerSideBakingRegion : IServerSideBakingRegion
{
    private readonly ConcurrentDictionary<UUID, int> m_bakedCof = new();

    public ServerSideBakingRegion(bool enabled, CofHandshake handshake)
    {
        ServerSideBakingEnabled = enabled;
        Handshake = handshake;
    }

    /// <inheritdoc/>
    public bool ServerSideBakingEnabled { get; }

    /// <summary>This region's copy of the §4.3 handshake, with its own per-agent mismatch counters.</summary>
    public CofHandshake Handshake { get; }

    /// <inheritdoc/>
    public int BakedCofVersion(UUID agentId) => m_bakedCof.TryGetValue(agentId, out var v) ? v : -1;

    /// <summary>
    /// Record that this sim applied a bake to the agent at the given COF version, which is what makes the
    /// appearance carry an <c>AppearanceData</c> block from now on (V4/V5).
    ///
    /// <para>Only ever called on a flag-on region. On a flag-off region a console bake still writes faces and
    /// sends the appearance, but it must not change the wire — Firestorm is client-baking there and expects the
    /// packet it has always had (ADR-001).</para>
    /// </summary>
    public void RecordBake(UUID agentId, int cofVersion)
    {
        if (!ServerSideBakingEnabled || cofVersion < 0) return;
        m_bakedCof[agentId] = cofVersion;
    }

    /// <summary>Forget an agent on close, so a returning agent is not credited with a bake this sim no longer knows about.</summary>
    public void Forget(UUID agentId)
    {
        m_bakedCof.TryRemove(agentId, out _);
        Handshake.Clear(agentId);
    }

    /// <summary>
    /// Whether the flag is on for one region. The simulator-wide <c>[Appearance] ServerSideBaking</c> is the
    /// default and a <c>[&lt;Region Name&gt;]</c> section may override it with the same key — the per-region idiom
    /// <c>AISv3Module.ResolveEnabled</c> uses for <c>AIS_Enabled</c>, which in turn follows
    /// <c>AutoBackupModule</c>. Static and free of <c>Scene</c> so it can be tested with a plain config source.
    ///
    /// <para>The reason it is per region is the same as AIS's: flipping it hands the LL viewer's whole appearance
    /// path to this code, and Firestorm on that region stops client-baking the moment bit 0 appears in the
    /// handshake. It has to be possible to try it on exactly one region (Design Brief §4.5).</para>
    /// </summary>
    public static bool ResolveEnabled(bool simulatorDefault, IConfigSource sceneConfig, string regionName)
    {
        if (sceneConfig is null || string.IsNullOrEmpty(regionName)) return simulatorDefault;
        IConfig regionConfig = sceneConfig.Configs[regionName];
        return regionConfig is null ? simulatorDefault : regionConfig.GetBoolean("ServerSideBaking", simulatorDefault);
    }
}
