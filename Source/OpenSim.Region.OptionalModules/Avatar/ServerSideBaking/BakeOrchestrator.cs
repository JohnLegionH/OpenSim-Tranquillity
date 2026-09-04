using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// The scene-free part of the pipeline (Design Brief §4.2 steps 2, 4-6): resolve the agent's wearables and
/// textures from the asset service into a <see cref="BakeRequest"/>, run the backend, store the bakes as assets
/// and write the baked faces. Takes only the wearable set, the visual params, an asset service, a backend and
/// the <see cref="AvatarAppearance"/> to write into, so it is unit-tested with fakes and no ScenePresence.
/// Sending and the appearance save (steps 7) stay in <see cref="ServerSideBakingModule"/>.
/// </summary>
public static class BakeOrchestrator
{
    /// <summary>Why a wearable or texture could not be used and which channels it takes down.</summary>
    public sealed record InputFailure(BakeChannel Channel, string Reason);

    /// <summary>The resolved inputs: the request the backend gets, plus the channels already lost to unusable inputs.</summary>
    public sealed record ResolvedInputs(BakeRequest Request, IReadOnlyList<InputFailure> Failures, IReadOnlyList<string> Notes)
    {
        public bool IsFailed(BakeChannel ch) { foreach (var f in Failures) if (f.Channel == ch) return true; return false; }
        public string FailureReason(BakeChannel ch) => string.Join("; ", Failures.Where(f => f.Channel == ch).Select(f => f.Reason));
    }

    private static readonly BakeChannel[] AllChannels = Enum.GetValues<BakeChannel>();

    /// <summary>The TextureEntry face a channel is written to: <c>AvatarAppearance.BAKE_INDICES</c> in BakeChannel order.</summary>
    public static int FaceOf(BakeChannel ch) => AvatarAppearance.BAKE_INDICES[(int)ch];

    /// <summary>The asset name a stored bake carries (ADR-004): <c>bake:&lt;agent&gt;:&lt;channel&gt;</c>.</summary>
    public static string AssetNameFor(UUID agentId, BakeChannel ch) => $"bake:{agentId}:{ch.ToString().ToLowerInvariant()}";

    // ------------------------------------------------------------------ step 2 + 4: inputs

    /// <summary>
    /// Fetch every worn wearable asset (types 5/13) and every texture they reference, once each. A wearable that
    /// cannot be fetched or parsed fails the channels its type feeds (the shape feeds them all); a texture that
    /// cannot be fetched fails the channels whose layer sets draw its slot. Nothing else is refused (ADR-005).
    /// </summary>
    public static ResolvedInputs Resolve(AvatarWearable[] wearables, byte[] visualParams, IAssetService assets, TexLayerCompositor compositor, int bakeSize)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(compositor);
        var inputs = new List<WearableInput>();
        var parsed = new List<ParsedWearable>();
        var failures = new List<InputFailure>();
        var notes = new List<string>();

        if (wearables is not null)
        {
            for (var type = 0; type < wearables.Length; type++)
            {
                var slot = wearables[type];
                if (slot is null) continue;
                for (var j = 0; j < slot.Count; j++)
                {
                    var assetId = slot[j].AssetID;
                    if (assetId.IsZero())
                    {
                        // The slot is worn, there is just no asset behind it (typically a default system wearable
                        // item). That is still a worn wearable: the viewer counts wearables, not textures
                        // (LLTexLayerTemplate::updateWearableCache, lltexlayer.cpp:1615-1638), so it contributes its
                        // layers' morph masks with the avatar's own parameter values. Passing it on as an empty
                        // WearableInput is what the library expects (S1c, MORPH-MASK-PASS.md §2.4); dropping it here
                        // was Ledger Q-12. It carries no textures, so nothing is fetched for it.
                        inputs.Add(new WearableInput(UUID.Zero, type, ""));
                        parsed.Add(new ParsedWearable((WearableKind)type, "", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID>()));
                        notes.Add($"wearable type {(WearableKind)type}:{j} is worn with no asset; kept as a worn instance with no textures");
                        continue;
                    }
                    var asset = assets.Get(assetId.ToString());
                    if (asset?.Data is not { Length: > 0 })
                    {
                        Fail(failures, ChannelsFedBy((WearableKind)type, compositor), $"wearable type {(WearableKind)type} asset {assetId} not found");
                        continue;
                    }
                    var text = Encoding.UTF8.GetString(asset.Data);
                    ParsedWearable pw;
                    try { pw = WearableParser.Parse(text); }
                    catch (FormatException ex)
                    {
                        Fail(failures, ChannelsFedBy((WearableKind)type, compositor), $"wearable type {(WearableKind)type} asset {assetId} unparseable: {ex.Message}");
                        continue;
                    }
                    inputs.Add(new WearableInput(assetId, type, text));
                    parsed.Add(pw with { Kind = (WearableKind)type });
                }
            }
        }

        var textures = new Dictionary<UUID, TextureInput>();
        foreach (var pw in parsed)
        {
            foreach (var (texSlot, id) in pw.Textures)
            {
                if (id.IsZero() || id == BakeConstants.DefaultAvatarTexture || textures.ContainsKey(id)) continue;
                var asset = assets.Get(id.ToString());
                if (asset?.Data is not { Length: > 0 })
                {
                    Fail(failures, ChannelsDrawing(texSlot, compositor), $"texture {id} ({texSlot}) not found");
                    continue;
                }
                textures[id] = new TextureInput(id, asset.Data);
            }
        }

        // the presence's VisualParams, decoded through the parameter table as an overlay (the wearables' own values win)
        var overlay = new Dictionary<int, float>();
        if (visualParams is not null)
        {
            var list = VisualParamEncoder.SendList(compositor.Lad);
            if (visualParams.Length == list.Count)
            {
                for (var i = 0; i < list.Count; i++)
                    overlay[list[i].Id] = list[i].Min + visualParams[i] / 255f * (list[i].Max - list[i].Min);
            }
            else notes.Add($"VisualParams has {visualParams.Length} bytes, the parameter table {list.Count}; not overlaid");
        }

        return new ResolvedInputs(new BakeRequest(inputs, overlay, textures, bakeSize), failures, notes);
    }

    private static void Fail(List<InputFailure> failures, IEnumerable<BakeChannel> channels, string reason)
    {
        foreach (var ch in channels) failures.Add(new InputFailure(ch, reason));
    }

    /// <summary>Channels whose layer sets draw a slot owned by the wearable type; the shape (parameters only) feeds every channel.</summary>
    public static IEnumerable<BakeChannel> ChannelsFedBy(WearableKind kind, TexLayerCompositor compositor)
    {
        if (kind == WearableKind.Shape) return AllChannels;
        return AllChannels.Where(ch => compositor.SlotsOf(ch).Any(s => TexLayerCompositor.WearableOf(s) == kind));
    }

    /// <summary>Channels whose layer sets draw the given texture slot.</summary>
    public static IEnumerable<BakeChannel> ChannelsDrawing(TextureSlot slot, TexLayerCompositor compositor)
        => AllChannels.Where(ch => compositor.SlotsOf(ch).Contains(slot));

    // ------------------------------------------------------------------ step 6: store + faces

    /// <summary>
    /// Store each bake as a texture asset (ADR-004 marker: name <c>bake:&lt;agent&gt;:&lt;channel&gt;</c>, description = input
    /// hash, not temporary, not local, creator = agent) and write its UUID to the channel's baked face. Channels in
    /// <paramref name="failed"/> and channels the backend produced nothing for keep their current face.
    /// </summary>
    public static IReadOnlyList<ChannelOutcome> StoreAndApply(IReadOnlyList<BakeResult> results, ResolvedInputs inputs, UUID agentId, IAssetService assets, AvatarAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(appearance);
        var byChannel = results.ToDictionary(r => r.Channel);
        var outcomes = new List<ChannelOutcome>(AllChannels.Length);
        var empty = new FidelityReport(Array.Empty<string>(), Array.Empty<UUID>(), Array.Empty<string>(), Array.Empty<string>());
        foreach (var ch in AllChannels)
        {
            byChannel.TryGetValue(ch, out var result);
            if (inputs.IsFailed(ch))
            {
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Failed, UUID.Zero, result?.InputHash ?? "", inputs.FailureReason(ch), result?.Fidelity ?? empty));
                continue;
            }
            if (result is null)
            {
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Skipped, UUID.Zero, "", "nothing worn for this channel", empty));
                continue;
            }
            if (result.J2kBytes is not { Length: > 0 })
            {
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Failed, UUID.Zero, result.InputHash, "backend returned no bytes", result.Fidelity));
                continue;
            }
            var asset = new AssetBase(UUID.Random(), AssetNameFor(agentId, ch), (sbyte)AssetType.Texture, agentId.ToString())
            {
                Data = result.J2kBytes,
                Description = result.InputHash,
                Temporary = false,
                Local = false,
            };
            var storedId = assets.Store(asset);
            if (string.IsNullOrEmpty(storedId) || !UUID.TryParse(storedId, out var id) || id.IsZero())
            {
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Failed, UUID.Zero, result.InputHash, "asset service refused the bake", result.Fidelity));
                continue;
            }
            appearance.Texture.CreateFace((uint)FaceOf(ch)).TextureID = id;
            outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Baked, id, result.InputHash, "", result.Fidelity));
        }
        return outcomes;
    }

    // ------------------------------------------------------------------ the whole scene-free run

    /// <summary>Resolve, bake, store, write faces. Sending and saving are the caller's.</summary>
    public static BakeOutcome Run(UUID agentId, BakeReason reason, AvatarWearable[] wearables, byte[] visualParams, AvatarAppearance appearance,
        IAssetService assets, IBakeBackend backend, TexLayerCompositor compositor, int bakeSize, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var inputs = Resolve(wearables, visualParams, assets, compositor, bakeSize);
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<BakeResult> results;
        try
        {
            results = backend.BakeAsync(inputs.Request, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException ex)
        {
            // ADR-005: corrupt input is the one refusal; every channel keeps its existing face
            var all = AllChannels.Select(ch => new ChannelOutcome(ch, ChannelStatus.Failed, UUID.Zero, "", $"backend refused the inputs: {ex.Message}",
                new FidelityReport(Array.Empty<string>(), Array.Empty<UUID>(), Array.Empty<string>(), Array.Empty<string>()))).ToList();
            return new BakeOutcome(agentId, reason, all, sw.ElapsedMilliseconds);
        }
        var outcomes = StoreAndApply(results, inputs, agentId, assets, appearance);
        return new BakeOutcome(agentId, reason, outcomes, sw.ElapsedMilliseconds);
    }
}
