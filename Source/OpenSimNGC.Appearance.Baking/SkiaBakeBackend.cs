using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// The library's bake backend: wearable text in, JPEG 2000 bakes out. Wearables are parsed with
/// <see cref="WearableParser"/>, textures decoded with <see cref="J2kCodec"/>, each channel composited by
/// <see cref="TexLayerCompositor"/> at <see cref="BakeRequest.BakeSize"/>, encoded as a five-component single-tile
/// codestream (RGB, visibility alpha, morph mask; Docs/BUMP-PASS.md) and hashed with
/// <see cref="BakeHash"/>. Pure with respect to its inputs; no I/O.
/// </summary>
public sealed class SkiaBakeBackend : IBakeBackend
{
    private static readonly BakeChannel[] ClassicChannels = { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair };
    private static readonly (BakeChannel Channel, TextureSlot Slot)[] ExtraChannels =
    {
        (BakeChannel.LeftArm, TextureSlot.LeftArmTattoo), (BakeChannel.LeftLeg, TextureSlot.LeftLegTattoo),
        (BakeChannel.Aux1, TextureSlot.Aux1Tattoo), (BakeChannel.Aux2, TextureSlot.Aux2Tattoo), (BakeChannel.Aux3, TextureSlot.Aux3Tattoo),
    };

    private readonly TexLayerCompositor _compositor;

    /// <summary>A backend over the embedded avatar_lad.xml and character images.</summary>
    public SkiaBakeBackend() : this(new TexLayerCompositor()) { }

    public SkiaBakeBackend(TexLayerCompositor compositor) { _compositor = compositor; }

    public TexLayerCompositor Compositor => _compositor;

    /// <summary>JPEG 2000 quality for the encoded bakes (0..1).</summary>
    public double Quality { get; init; } = 0.85;

    /// <summary>The channels an outfit needs: the five classic ones always, the skirt when a skirt is worn, and each extra (Bakes-on-Mesh) channel a worn wearable paints.</summary>
    public static IReadOnlyList<BakeChannel> ChannelsFor(IReadOnlyList<ParsedWearable> wearables)
    {
        var list = new List<BakeChannel>(ClassicChannels);
        if (wearables.Any(w => w.Kind == WearableKind.Skirt)) list.Add(BakeChannel.Skirt);
        foreach (var (ch, slot) in ExtraChannels)
            if (wearables.Any(w => w.Textures.TryGetValue(slot, out var id) && id != UUID.Zero && id != BakeConstants.DefaultAvatarTexture))
                list.Add(ch);
        return list;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BakeResult>> BakeAsync(BakeRequest r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Bake(r, ct));
    }

    /// <summary>Synchronous form of <see cref="BakeAsync"/>.</summary>
    public IReadOnlyList<BakeResult> Bake(BakeRequest r, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.BakeSize < 8 || r.BakeSize > 4096) throw new ArgumentException($"BakeSize {r.BakeSize} out of range");

        // 1. wearables (corrupt text is a refusal, ADR-005)
        var parsed = new List<(WearableInput Input, ParsedWearable Wearable)>();
        foreach (var w in r.Wearables)
        {
            ParsedWearable pw;
            try { pw = WearableParser.Parse(w.RawText); }
            catch (FormatException ex) { throw new ArgumentException($"wearable {w.AssetId}: {ex.Message}", ex); }
            if ((int)pw.Kind != w.WearableType && w.WearableType is >= 0 and < 255)
                pw = pw with { Kind = (WearableKind)w.WearableType };   // the caller's slot wins over a mislabelled asset, as the viewer's does
            parsed.Add((w, pw));
        }
        var wearables = parsed.Select(p => p.Wearable).ToList();
        var channels = ChannelsFor(wearables);

        // 2. textures (undecodable bytes are a refusal; absent ones are reported per channel)
        var decoded = new Dictionary<UUID, RgbaPlanes>();
        foreach (var (id, tex) in r.Textures)
        {
            ct.ThrowIfCancellationRequested();
            try { decoded[id] = J2kCodec.Decode(tex.J2kBytes); }
            catch (ArgumentException ex) { throw new ArgumentException($"texture {id}: {ex.Message}", ex); }
        }

        var worn = new List<WornWearable>();
        var summaries = new List<FidelityCheck.WornSummary>();
        foreach (var (input, pw) in parsed)
        {
            var label = $"{pw.Kind} {input.AssetId.ToString()[..8]}";
            var textures = new Dictionary<TextureSlot, RgbaPlanes>();
            foreach (var (slot, id) in pw.Textures)
                if (decoded.TryGetValue(id, out var img)) textures[slot] = img;
            worn.Add(new WornWearable { Kind = pw.Kind, Label = label, Params = pw.Params, TextureIds = pw.Textures, Textures = textures });
            summaries.Add(new FidelityCheck.WornSummary(pw.Kind, label, pw.Textures));
        }

        // 3. the fidelity gate's evidence, once for the outfit; the caller decides what to do with it
        var refusals = FidelityCheck.Check(summaries, _compositor, channels);

        // 4. each channel
        var results = new List<BakeResult>(channels.Count);
        foreach (var ch in channels)
        {
            ct.ThrowIfCancellationRequested();
            var slots = _compositor.SlotsOf(ch).ToHashSet();
            var missing = new List<UUID>();
            foreach (var pw in wearables)
                foreach (var (slot, id) in pw.Textures)
                    if (slots.Contains(slot) && id != UUID.Zero && id != BakeConstants.DefaultAvatarTexture && !decoded.ContainsKey(id) && !missing.Contains(id))
                        missing.Add(id);

            var composite = _compositor.Bake(ch, worn, r.BakeSize, r.VisualParams);
            var bytes = J2kCodec.EncodeBake(composite.Image, composite.MorphMask, Quality);
            var unsupported = composite.Layers
                .Where(l => l.Status == "skipped" && (l.Detail.Contains("missing", StringComparison.Ordinal) || l.Detail.Contains("unknown", StringComparison.Ordinal)))
                .Select(l => $"{l.Layer}: {l.Detail}")
                .ToList();
            var notes = composite.Layers.Select(l => $"{l.Layer} {l.Status}: {l.Detail}").ToList();
            if (composite.Invisible) notes.Insert(0, "invisible: the whole region is hidden by an alpha wearable");
            var fidelity = new FidelityReport(unsupported, missing, notes, refusals);
            results.Add(new BakeResult(ch, bytes, BakeHash.Compute(ch, r), fidelity));
        }
        return results;
    }

    /// <summary>Whether the shape's `male` parameter (80) says male, from the parsed wearables.</summary>
    public bool IsMale(IReadOnlyList<ParsedWearable> wearables)
        => _compositor.IsMale(wearables.Select(w => new WornWearable { Kind = w.Kind, Params = w.Params, TextureIds = w.Textures }).ToList());
}
