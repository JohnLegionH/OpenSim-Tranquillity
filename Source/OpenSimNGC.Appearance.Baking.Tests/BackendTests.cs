using OpenMetaverse;
using Xunit;

namespace OpenSimNGC.Appearance.Baking.Tests;

/// <summary>The bytes-in/bytes-out surface: wearable text parsing, JPEG 2000 in and out (single tile), the backend end to end, and the input hash.</summary>
public class BackendTests
{
    // ---------------- wearable text ----------------

    private static string WearableText(WearableKind kind, string name, IReadOnlyDictionary<int, float> prms, IReadOnlyDictionary<TextureSlot, UUID> textures)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("LLWearable version 22\n").Append(name).Append('\n').Append("\n");
        sb.Append("\tpermissions 0\n\t{\n\t\tbase_mask\t7fffffff\n\t\towner_mask\t7fffffff\n\t\tgroup_mask\t00000000\n\t\teveryone_mask\t00000000\n\t\tnext_owner_mask\t00082000\n\t\tcreator_id\t11111111-1111-0000-0000-000100bba000\n\t\towner_id\t11111111-1111-0000-0000-000100bba000\n\t\tlast_owner_id\t00000000-0000-0000-0000-000000000000\n\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n\t}\n");
        sb.Append("\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\n");
        sb.Append("type ").Append((int)kind).Append('\n');
        sb.Append("parameters ").Append(prms.Count).Append('\n');
        foreach (var (id, v) in prms) sb.Append(id).Append(' ').Append(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("textures ").Append(textures.Count).Append('\n');
        foreach (var (slot, id) in textures) sb.Append((int)slot).Append(' ').Append(id).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void wearable_text_parses_type_parameters_and_textures()
    {
        var tex = UUID.Random();
        var text = WearableText(WearableKind.Shirt, "Blue Shirt", new Dictionary<int, float> { [800] = 0.75f, [803] = 0f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = tex });
        var w = WearableParser.Parse(text);
        Assert.Equal(WearableKind.Shirt, w.Kind);
        Assert.Equal("Blue Shirt", w.Name);
        Assert.Equal(0.75f, w.Params[800]);
        Assert.Equal(0f, w.Params[803]);
        Assert.Equal(tex, w.Textures[TextureSlot.UpperShirt]);
        Assert.Throws<FormatException>(() => WearableParser.Parse("not a wearable"));
        Assert.Throws<FormatException>(() => WearableParser.Parse("LLWearable version 22\nName\n\nparameters 1\n800 x\n"));
    }

    // ---------------- JPEG 2000 ----------------

    [Fact]
    public void encoded_bakes_are_a_single_tile_codestream_and_round_trip()
    {
        var img = CompositorTests.Flat(96, 96, 200, 40, 10, 128);
        for (var i = 0; i < img.R.Length; i++) img.R[i] = (byte)(i % 96 * 2);   // some structure so the encoder has work to do
        var bytes = J2kCodec.Encode(img);
        Assert.True(bytes.Length > 100);
        Assert.Equal(0xFF, bytes[0]); Assert.Equal(0x4F, bytes[1]);   // raw codestream (SOC), not a JP2 file
        var siz = J2kCodec.ParseSiz(bytes);
        Assert.Equal(96, siz.Xsiz); Assert.Equal(96, siz.Ysiz);
        Assert.True(siz.XTsiz >= siz.Xsiz && siz.YTsiz >= siz.Ysiz, $"tile {siz.XTsiz}x{siz.YTsiz} smaller than image {siz.Xsiz}x{siz.Ysiz}");
        Assert.True(siz.SingleTile, $"{siz.TileCount} tiles");
        Assert.Equal(4, siz.Csiz);

        var back = J2kCodec.Decode(bytes);
        Assert.Equal(96, back.W); Assert.Equal(96, back.H);
        Assert.True(back.HasAlpha);
        var mid = 48 * 96 + 48;
        Assert.InRange(back.G[mid], 30, 50);
        Assert.InRange(back.B[mid], 0, 20);
        Assert.InRange(back.A[mid], 118, 138);
    }

    [Fact]
    public void a_bake_encodes_five_components_and_round_trips_the_morph_mask()
    {
        var img = CompositorTests.Flat(96, 96, 180, 90, 30, 200);
        var mask = new byte[96 * 96];
        for (var i = 0; i < mask.Length; i++) mask[i] = (byte)(i % 96 < 48 ? 0 : 255);
        var bytes = J2kCodec.EncodeBake(img, mask);
        var siz = J2kCodec.ParseSiz(bytes);
        Assert.Equal(5, siz.Csiz);
        Assert.True(siz.SingleTile, $"{siz.TileCount} tiles");
        Assert.Equal(96, siz.Xsiz);
        var back = J2kCodec.Decode(bytes);
        Assert.NotNull(back.Mask);
        Assert.True(back.HasAlpha);
        var row = 48 * 96;
        Assert.InRange(back.Mask![row + 10], 0, 8);
        Assert.InRange(back.Mask![row + 80], 247, 255);
        Assert.InRange(back.A[row + 10], 190, 210);
        Assert.InRange(back.R[row + 10], 170, 190);
        // no mask supplied: 255 everywhere, the value gatherMorphMaskAlpha starts from
        var plain = J2kCodec.Decode(J2kCodec.EncodeBake(img, null));
        Assert.NotNull(plain.Mask);
        Assert.All(plain.Mask!, v => Assert.InRange(v, 250, 255));
        // and the backend's output is five-component
        var (req, _) = ClassicRequest();
        foreach (var r in new SkiaBakeBackend().Bake(req)) Assert.Equal(5, J2kCodec.ParseSiz(r.J2kBytes).Csiz);
    }

    [Fact]
    public void the_default_encoder_would_tile_a_512_bake_so_the_config_pins_one_tile()
    {
        var cfg = J2kCodec.EncoderConfig(512, 512);
        Assert.Equal(512, cfg.Tiles.Width);
        Assert.Equal(512, cfg.Tiles.Height);
        Assert.False(cfg.UseFileFormat);
        var siz = J2kCodec.ParseSiz(J2kCodec.Encode(CompositorTests.Flat(512, 512, 10, 20, 30)));
        Assert.Equal(1, siz.TileCount);
    }

    // ---------------- the backend ----------------

    private static (BakeRequest Request, Dictionary<WearableKind, UUID> Assets) ClassicRequest(int size = 64, float sleeve = 1f)
    {
        var skinTex = UUID.Random(); var hairTex = UUID.Random(); var irisTex = UUID.Random(); var shirtTex = UUID.Random();
        var assets = new Dictionary<WearableKind, UUID> { [WearableKind.Shape] = UUID.Random(), [WearableKind.Skin] = UUID.Random(), [WearableKind.Hair] = UUID.Random(), [WearableKind.Eyes] = UUID.Random(), [WearableKind.Shirt] = UUID.Random() };
        var wearables = new List<WearableInput>
        {
            new(assets[WearableKind.Shape], 0, WearableText(WearableKind.Shape, "Shape", new Dictionary<int, float> { [80] = 0f, [33] = 0.5f }, new Dictionary<TextureSlot, UUID>())),
            new(assets[WearableKind.Skin], 1, WearableText(WearableKind.Skin, "Skin", new Dictionary<int, float> { [111] = 0.5f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.HeadBodypaint] = skinTex, [TextureSlot.UpperBodypaint] = skinTex, [TextureSlot.LowerBodypaint] = skinTex })),
            new(assets[WearableKind.Hair], 2, WearableText(WearableKind.Hair, "Hair", new Dictionary<int, float> { [114] = 0.5f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.Hair] = hairTex })),
            new(assets[WearableKind.Eyes], 3, WearableText(WearableKind.Eyes, "Eyes", new Dictionary<int, float> { [99] = 0f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.EyesIris] = irisTex })),
            new(assets[WearableKind.Shirt], 4, WearableText(WearableKind.Shirt, "Shirt", new Dictionary<int, float> { [800] = sleeve, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 0f, [805] = 1f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = shirtTex })),
        };
        var textures = new Dictionary<UUID, TextureInput>
        {
            [skinTex] = new(skinTex, J2kCodec.Encode(CompositorTests.Flat(32, 32, 200, 150, 120))),
            [hairTex] = new(hairTex, J2kCodec.Encode(CompositorTests.Flat(16, 16, 255, 255, 255, 0))),
            [irisTex] = new(irisTex, J2kCodec.Encode(CompositorTests.Flat(16, 16, 255, 255, 255))),
            [shirtTex] = new(shirtTex, J2kCodec.Encode(CompositorTests.Flat(16, 16, 0, 0, 255))),
        };
        return (new BakeRequest(wearables, new Dictionary<int, float>(), textures, size), assets);
    }

    [Fact]
    public async Task the_backend_bakes_the_five_classic_channels_from_bytes_and_reports_fidelity()
    {
        var (req, _) = ClassicRequest();
        var results = await new SkiaBakeBackend().BakeAsync(req, CancellationToken.None);
        Assert.Equal(new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair }, results.Select(r => r.Channel).ToArray());
        foreach (var r in results)
        {
            var siz = J2kCodec.ParseSiz(r.J2kBytes);
            Assert.Equal(64, siz.Xsiz); Assert.True(siz.SingleTile);
            Assert.Equal(64, r.InputHash.Length);
            Assert.Empty(r.Fidelity.Refusals);
            Assert.Empty(r.Fidelity.MissingTextures);
            Assert.NotEmpty(r.Fidelity.Notes);
        }
        var upper = J2kCodec.Decode(results.Single(r => r.Channel == BakeChannel.Upper).J2kBytes);
        Assert.Contains(upper.B, b => b > 150);   // the blue shirt made it onto the upper bake
        Assert.Contains(results.Single(r => r.Channel == BakeChannel.Upper).Fidelity.Notes, n => n.StartsWith("upper_clothes drawn"));

        // a texture the request does not carry is reported on the channel that needs it, and only there
        var missingTex = req.Textures.Keys.First();
        var trimmed = req with { Textures = req.Textures.Where(kv => kv.Key != missingTex).ToDictionary(kv => kv.Key, kv => kv.Value) };
        var results2 = new SkiaBakeBackend().Bake(trimmed);
        Assert.Contains(results2, r => r.Fidelity.MissingTextures.Contains(missingTex));

        // an unsupported wearable type lands in Refusals on every channel; corrupt text is an exception
        var odd = req with { Wearables = req.Wearables.Append(new WearableInput(UUID.Random(), 99, WearableText((WearableKind)99, "Mystery", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID>()))).ToList() };
        Assert.All(new SkiaBakeBackend().Bake(odd), r => Assert.Contains(r.Fidelity.Refusals, s => s.Contains("not composited")));
        var corrupt = req with { Wearables = req.Wearables.Append(new WearableInput(UUID.Random(), 4, "garbage")).ToList() };
        Assert.Throws<ArgumentException>(() => new SkiaBakeBackend().Bake(corrupt));
    }

    [Fact]
    public void skirt_and_extra_channels_appear_only_when_worn_or_painted()
    {
        var (req, _) = ClassicRequest();
        var parsed = req.Wearables.Select(w => WearableParser.Parse(w.RawText)).ToList();
        Assert.Equal(5, SkiaBakeBackend.ChannelsFor(parsed).Count);
        parsed.Add(new ParsedWearable(WearableKind.Skirt, "Skirt", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID>()));
        parsed.Add(new ParsedWearable(WearableKind.Universal, "U", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID> { [TextureSlot.Aux2Tattoo] = UUID.Random(), [TextureSlot.LeftArmTattoo] = BakeConstants.DefaultAvatarTexture }));
        var chs = SkiaBakeBackend.ChannelsFor(parsed);
        Assert.Contains(BakeChannel.Skirt, chs);
        Assert.Contains(BakeChannel.Aux2, chs);
        Assert.DoesNotContain(BakeChannel.LeftArm, chs);
    }

    // ---------------- the hash ----------------

    [Fact]
    public void bake_hash_is_stable_across_ordering_and_changes_with_every_input()
    {
        var (req, assets) = ClassicRequest();
        var h = BakeHash.Compute(BakeChannel.Upper, req);
        Assert.Matches("^[0-9a-f]{64}$", h);

        // ordering: reversed wearable list, reversed texture dictionary, reversed visual params
        var reordered = new BakeRequest(
            req.Wearables.Reverse().ToList(),
            new Dictionary<int, float> { [33] = 0.5f, [80] = 0f },
            req.Textures.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value),
            req.BakeSize);
        var baseline = req with { VisualParams = new Dictionary<int, float> { [80] = 0f, [33] = 0.5f } };
        Assert.Equal(BakeHash.Compute(BakeChannel.Upper, baseline), BakeHash.Compute(BakeChannel.Upper, reordered));

        // channel and size
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Lower, req));
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Upper, req with { BakeSize = 128 }));

        // a wearable that feeds the channel: new asset id, or a changed parameter it stores
        var shirt = req.Wearables.Single(w => w.WearableType == 4);
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Upper, req with { Wearables = req.Wearables.Select(w => w == shirt ? w with { AssetId = UUID.Random() } : w).ToList() }));
        var (req2, _) = ClassicRequest(sleeve: 0.25f);
        var sameIds = req2 with { Wearables = req2.Wearables.Select((w, i) => w with { AssetId = req.Wearables[i].AssetId }).ToList(), Textures = req.Textures };
        // (textures differ by id between the two requests; align them so only the sleeve differs)
        var shirtText = sameIds.Wearables.Single(w => w.WearableType == 4);
        var shirtTex = WearableParser.Parse(shirt.RawText).Textures[TextureSlot.UpperShirt];
        var realigned = sameIds with { Wearables = sameIds.Wearables.Select(w => w == shirtText
            ? w with { RawText = WearableText(WearableKind.Shirt, "Shirt", new Dictionary<int, float> { [800] = 0.25f, [801] = 1f, [802] = 1f, [781] = 1f, [803] = 0f, [804] = 0f, [805] = 1f }, new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = shirtTex }) }
            : req.Wearables[sameIds.Wearables.ToList().IndexOf(w)]).ToList() };
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Upper, realigned));

        // a texture id the channel draws
        var retextured = req with { Wearables = req.Wearables.Select(w => w == shirt ? w with { RawText = WearableText(WearableKind.Shirt, "Shirt", WearableParser.Parse(shirt.RawText).Params, new Dictionary<TextureSlot, UUID> { [TextureSlot.UpperShirt] = UUID.Random() }) } : w).ToList() };
        Assert.NotEqual(h, BakeHash.Compute(BakeChannel.Upper, retextured));

        // a visual param the channel's layers read (skin colour 111 feeds the upper body's global colour)
        Assert.NotEqual(BakeHash.Compute(BakeChannel.Upper, req with { VisualParams = new Dictionary<int, float> { [111] = 0.1f } }),
                        BakeHash.Compute(BakeChannel.Upper, req with { VisualParams = new Dictionary<int, float> { [111] = 0.9f } }));

        // something that does not feed the upper body (pants asset id) leaves it alone
        var pants = new WearableInput(UUID.Random(), 5, WearableText(WearableKind.Pants, "Pants", new Dictionary<int, float> { [615] = 1f }, new Dictionary<TextureSlot, UUID>()));
        Assert.Equal(h, BakeHash.Compute(BakeChannel.Upper, req with { Wearables = req.Wearables.Append(pants).ToList() }));
        Assert.NotEqual(BakeHash.Compute(BakeChannel.Lower, req), BakeHash.Compute(BakeChannel.Lower, req with { Wearables = req.Wearables.Append(pants).ToList() }));
        _ = assets;
    }
}
