using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenMetaverse;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>One channel's stored bake: the asset that holds it and the hash of the inputs that made it.</summary>
public sealed record StoredBake(UUID AssetId, string Hash);

/// <summary>
/// ADR-004's per-agent bake index, held in the avatar service's key/value <c>Avatars</c> table. No schema change
/// and no service change: the table is already <c>(PrincipalID, Name, Value)</c> and
/// <see cref="IAvatarService"/> already exposes the three calls this needs —
/// <see cref="IAvatarService.GetAvatar"/> to read every key, <see cref="IAvatarService.SetItems"/> to write a
/// batch and <see cref="IAvatarService.RemoveItems"/> to drop one. All three exist on the local service
/// (<c>OpenSim.Services.AvatarService/AvatarService.cs</c>) and on the Robust connector
/// (<c>OpenSim.Services.Connectors/Avatar/AvatarServicesConnector.cs</c>, methods <c>getavatar</c>,
/// <c>setitems</c>, <c>removeitems</c>), so a grid region and a standalone use the same code.
///
/// <para>The keys, per agent:</para>
/// <list type="bullet">
///   <item><c>Bake:&lt;channel&gt;</c> — the stored bake's asset UUID (channel is the <see cref="BakeChannel"/> name, e.g. <c>Bake:Head</c>).</item>
///   <item><c>BakeHash:&lt;channel&gt;</c> — the <see cref="BakeHash"/> of the inputs that produced it.</item>
///   <item><c>BakeCOFVersion</c> — the Current Outfit folder's <c>Version</c> at bake time (ADR-006).</item>
///   <item><c>BakeSize</c> — the <c>[Appearance] BakeSize</c> the bakes were made at.</item>
///   <item><c>BakeUpdated</c> — UTC, round-trip ("o") format; what the TTL reaper will compare against.</item>
/// </list>
/// The longest key is <c>BakeHash:LeftArm</c> at 16 characters, well inside the table's <c>Name varchar(32)</c>
/// (<c>OpenSim.Data.MySQL/Resources/Avatar.migrations:7</c>).
///
/// <para>
/// <b>Hazard this index lives with.</b> <c>AvatarService.SetAvatar</c> starts with
/// <c>m_Database.Delete("PrincipalID", ...)</c> (AvatarService.cs:93) — it drops *every* row for the agent and
/// rewrites only the keys <c>AvatarData(AvatarAppearance)</c> derives (IAvatarService.cs:142-189), which are the
/// wearables, visual params, height, serial and attachments. So any appearance save wipes this index. It cannot
/// corrupt it — the index is either wholly there or wholly gone — and a missing index means "re-bake", which is
/// always safe. What it costs is a needless re-bake and an orphaned previous asset that supersede can no longer
/// find; the bake assets are named <c>bake:&lt;agent&gt;:&lt;channel&gt;</c> precisely so a reaper can still find
/// those. The bake path itself therefore does not queue an appearance save (see
/// <see cref="ServerSideBakingModule"/>): a bake changes only the baked faces, which the avatar service does not
/// persist at all.
/// </para>
/// </summary>
public sealed class BakeIndex
{
    public const string BakeKeyPrefix = "Bake:";
    public const string HashKeyPrefix = "BakeHash:";
    public const string CofVersionKey = "BakeCOFVersion";
    public const string SizeKey = "BakeSize";
    public const string UpdatedKey = "BakeUpdated";

    /// <summary>An index with nothing in it: every channel re-bakes.</summary>
    public static readonly BakeIndex Empty = new(new Dictionary<BakeChannel, StoredBake>(), 0, 0, default);

    private readonly Dictionary<BakeChannel, StoredBake> m_bakes;

    private BakeIndex(Dictionary<BakeChannel, StoredBake> bakes, int cofVersion, int size, DateTime updatedUtc)
    {
        m_bakes = bakes;
        CofVersion = cofVersion;
        Size = size;
        UpdatedUtc = updatedUtc;
    }

    /// <summary>The stored bake per channel, for channels that have one.</summary>
    public IReadOnlyDictionary<BakeChannel, StoredBake> Bakes => m_bakes;

    /// <summary>The COF folder version recorded at bake time, or 0 if none is stored.</summary>
    public int CofVersion { get; }

    /// <summary>The bake size the stored bakes were made at, or 0 if none is stored.</summary>
    public int Size { get; }

    /// <summary>When the index was last written (UTC), or <c>default</c> if never.</summary>
    public DateTime UpdatedUtc { get; }

    public bool TryGet(BakeChannel ch, out StoredBake bake) => m_bakes.TryGetValue(ch, out bake);

    public static string BakeKey(BakeChannel ch) => BakeKeyPrefix + ch;
    public static string HashKey(BakeChannel ch) => HashKeyPrefix + ch;

    // ------------------------------------------------------------------ read

    /// <summary>Read the index for one agent. A null service, an absent record or an unparseable value all read as empty.</summary>
    public static BakeIndex Read(IAvatarService avatars, UUID agentId)
    {
        if (avatars is null) return Empty;
        AvatarData data;
        try { data = avatars.GetAvatar(agentId); }
        catch (Exception) { return Empty; }
        return Parse(data?.Data);
    }

    /// <summary>The parsing half of <see cref="Read"/>, over the raw key/value map.</summary>
    public static BakeIndex Parse(IReadOnlyDictionary<string, string> data)
    {
        if (data is null || data.Count == 0) return Empty;

        var assetIds = new Dictionary<BakeChannel, UUID>();
        var hashes = new Dictionary<BakeChannel, string>();
        foreach (var (key, value) in data)
        {
            // "BakeHash:Head" does not start with "Bake:", so the two prefixes cannot collide.
            if (key.StartsWith(BakeKeyPrefix, StringComparison.Ordinal))
            {
                if (Enum.TryParse<BakeChannel>(key.Substring(BakeKeyPrefix.Length), out var ch)
                    && UUID.TryParse(value, out var id) && !id.IsZero())
                    assetIds[ch] = id;
            }
            else if (key.StartsWith(HashKeyPrefix, StringComparison.Ordinal))
            {
                if (Enum.TryParse<BakeChannel>(key.Substring(HashKeyPrefix.Length), out var ch) && !string.IsNullOrEmpty(value))
                    hashes[ch] = value;
            }
        }

        // only a channel with both halves counts: a UUID with no hash can never match, and a hash with no UUID
        // has nothing to reuse.
        var bakes = new Dictionary<BakeChannel, StoredBake>();
        foreach (var (ch, id) in assetIds)
            if (hashes.TryGetValue(ch, out var hash)) bakes[ch] = new StoredBake(id, hash);

        var cof = data.TryGetValue(CofVersionKey, out var c) && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cv) ? cv : 0;
        var size = data.TryGetValue(SizeKey, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv) ? sv : 0;
        var updated = data.TryGetValue(UpdatedKey, out var u)
                      && DateTime.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var uv)
            ? uv.ToUniversalTime() : default;

        return new BakeIndex(bakes, cof, size, updated);
    }

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Write the index for one agent: one <c>Bake:</c>/<c>BakeHash:</c> pair per live bake plus the three scalars,
    /// in a single <see cref="IAvatarService.SetItems"/> call. Channels absent from <paramref name="bakes"/> are
    /// left exactly as they are — a channel that stops being produced (a skirt taken off) keeps its key and its
    /// asset, because the agent's face still points at that asset; expiry is the reaper's job (ADR-004).
    /// </summary>
    public static bool Write(IAvatarService avatars, UUID agentId, IEnumerable<KeyValuePair<BakeChannel, StoredBake>> bakes,
        int cofVersion, int bakeSize, DateTime updatedUtc)
    {
        if (avatars is null) return false;
        var names = new List<string>();
        var values = new List<string>();
        foreach (var (ch, bake) in bakes)
        {
            if (bake is null || bake.AssetId.IsZero() || string.IsNullOrEmpty(bake.Hash)) continue;
            names.Add(BakeKey(ch)); values.Add(bake.AssetId.ToString());
            names.Add(HashKey(ch)); values.Add(bake.Hash);
        }
        if (names.Count == 0) return false;
        names.Add(CofVersionKey); values.Add(cofVersion.ToString(CultureInfo.InvariantCulture));
        names.Add(SizeKey); values.Add(bakeSize.ToString(CultureInfo.InvariantCulture));
        names.Add(UpdatedKey); values.Add(updatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        return avatars.SetItems(agentId, names.ToArray(), values.ToArray());
    }

    /// <summary>Drop the keys for the named channels (the reaper's per-channel half; nothing in the bake path calls it).</summary>
    public static bool Remove(IAvatarService avatars, UUID agentId, IEnumerable<BakeChannel> channels)
    {
        if (avatars is null) return false;
        var names = channels.SelectMany(ch => new[] { BakeKey(ch), HashKey(ch) }).ToArray();
        return names.Length != 0 && avatars.RemoveItems(agentId, names);
    }
}
