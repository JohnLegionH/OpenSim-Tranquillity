using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace OpenSimNGC.Appearance.Baking.Tests.Golden;

/// <summary>
/// The golden harness: Truly Bazar's stock-Library outfit baked by the library at 512 px, compared channel by
/// channel against the Firestorm bakes captured on 2026-09-03 (manifest.json). Fixtures are fetched by
/// fetch-fixtures.sh into Golden/fixtures/ (gitignored); when they are absent the test reports that and
/// returns without asserting anything. When present it reports per channel the mean absolute RGB difference,
/// the mean absolute alpha difference and the share of pixels whose RGB differs by more than 8, to
/// Golden/last-run.txt (gitignored) and to the test output. No threshold is asserted (S0b): the numbers come
/// first, the threshold after. The test fails only on an exception or a missing fixture.
/// </summary>
public class GoldenTests
{
    private readonly ITestOutputHelper _out;
    public GoldenTests(ITestOutputHelper output) { _out = output; }

    private static string GoldenDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private sealed record Manifest(string Avatar, string Outfit, string Captured, int BakeSize, Dictionary<string, string> Goldens);
    private sealed record AvatarRow(int Type, int Index, string ItemId, string AssetId);
    private sealed record AvatarJson(string PrincipalId, List<AvatarRow> Wearables, List<int> VisualParams);

    private static readonly (string Key, BakeChannel Channel)[] ChannelKeys =
    {
        ("head", BakeChannel.Head), ("upper", BakeChannel.Upper), ("lower", BakeChannel.Lower), ("eyes", BakeChannel.Eyes), ("hair", BakeChannel.Hair),
        ("skirt", BakeChannel.Skirt), ("leftarm", BakeChannel.LeftArm), ("leftleg", BakeChannel.LeftLeg), ("aux1", BakeChannel.Aux1), ("aux2", BakeChannel.Aux2), ("aux3", BakeChannel.Aux3),
    };

    [Fact]
    public void trulys_stock_outfit_versus_firestorm_goldens()
    {
        var dir = GoldenDir();
        var fixtures = Path.Combine(dir, "fixtures");
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")), opts)!;
        if (!Directory.Exists(fixtures) || !File.Exists(Path.Combine(fixtures, "avatar.json")))
        {
            _out.WriteLine($"SKIPPED: no fixtures at {fixtures}. Run Golden/fetch-fixtures.sh (needs the Legion grid DB and Robust) to populate them; nothing is asserted without them.");
            return;
        }

        var avatar = JsonSerializer.Deserialize<AvatarJson>(File.ReadAllText(Path.Combine(fixtures, "avatar.json")), opts)!;
        string Fixture(string uuid, params string[] exts)
        {
            foreach (var e in exts) { var p = Path.Combine(fixtures, $"{uuid}.{e}"); if (File.Exists(p)) return p; }
            throw new FileNotFoundException($"fixture {uuid} ({string.Join("/", exts)}) missing from {fixtures}; re-run fetch-fixtures.sh");
        }

        // the request: every worn wearable's text, every texture they reference in a drawn slot, at the manifest's size
        var wearables = new List<WearableInput>();
        foreach (var row in avatar.Wearables.OrderBy(w => w.Type).ThenBy(w => w.Index))
            wearables.Add(new WearableInput(new UUID(row.AssetId), row.Type, File.ReadAllText(Fixture(row.AssetId, "bodypart", "clothing"), Encoding.UTF8)));
        var parsed = wearables.Select(w => WearableParser.Parse(w.RawText)).ToList();
        var textures = new Dictionary<UUID, TextureInput>();
        foreach (var pw in parsed)
            foreach (var (_, id) in pw.Textures)
            {
                if (id == UUID.Zero || id == BakeConstants.DefaultAvatarTexture || textures.ContainsKey(id)) continue;
                var p = Path.Combine(fixtures, $"{id}.j2c");
                if (File.Exists(p)) textures[id] = new TextureInput(id, File.ReadAllBytes(p));
            }
        var request = new BakeRequest(wearables, new Dictionary<int, float>(), textures, manifest.BakeSize);

        var backend = new SkiaBakeBackend();
        var results = backend.Bake(request);
        var size = manifest.BakeSize;

        var report = new StringBuilder();
        report.AppendLine($"golden run {DateTimeOffset.Now:O}  avatar={manifest.Avatar}  outfit={manifest.Outfit}  captured={manifest.Captured}  size={size}");
        report.AppendLine($"wearables: {string.Join(", ", parsed.Select(p => $"{p.Kind}('{p.Name}', {p.Params.Count}p, {p.Textures.Count(t => t.Value != UUID.Zero && t.Value != BakeConstants.DefaultAvatarTexture)}t)"))}");
        report.AppendLine($"textures supplied: {textures.Count}");
        report.AppendLine($"channels baked: {string.Join(", ", results.Select(r => r.Channel))}");
        var refusals = results.FirstOrDefault()?.Fidelity.Refusals ?? Array.Empty<string>();
        report.AppendLine($"fidelity refusals: {(refusals.Count == 0 ? "none" : string.Join("; ", refusals))}");
        report.AppendLine();
        report.AppendLine("channel  meanAbsRGB  meanAbsA  pctRGB>8   ours(WxH,alpha)   golden(WxH,alpha)   golden-uuid");

        var compared = 0;
        foreach (var (key, ch) in ChannelKeys)
        {
            if (!manifest.Goldens.TryGetValue(key, out var goldenId)) continue;
            var ours = results.SingleOrDefault(r => r.Channel == ch) ?? throw new InvalidOperationException($"the library produced no {ch} bake");
            var mine = J2kCodec.Decode(ours.J2kBytes);
            var golden = J2kCodec.Decode(File.ReadAllBytes(Fixture(goldenId, "j2c")));
            var a = mine.Resample(size, size);
            var b = golden.Resample(size, size);
            long sumRgb = 0, sumA = 0, over8 = 0;
            var n = size * size;
            for (var i = 0; i < n; i++)
            {
                int dr = Math.Abs(a.R[i] - b.R[i]), dg = Math.Abs(a.G[i] - b.G[i]), db = Math.Abs(a.B[i] - b.B[i]);
                sumRgb += dr + dg + db;
                sumA += Math.Abs(a.A[i] - b.A[i]);
                if (dr > 8 || dg > 8 || db > 8) over8++;
            }
            var meanRgb = sumRgb / (3.0 * n);
            var meanA = sumA / (double)n;
            var pct = 100.0 * over8 / n;
            report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-8} {1,10:F2} {2,9:F2} {3,9:F2}%   {4,-17} {5,-19} {6}",
                key, meanRgb, meanA, pct, $"{mine.W}x{mine.H},{(mine.HasAlpha ? "a" : "-")}", $"{golden.W}x{golden.H},{(golden.HasAlpha ? "a" : "-")}", goldenId));
            compared++;
        }
        report.AppendLine();
        foreach (var r in results)
        {
            report.AppendLine($"[{r.Channel}] hash={r.InputHash[..16]} bytes={r.J2kBytes.Length} missingTextures=[{string.Join(", ", r.Fidelity.MissingTextures)}] unsupportedLayers=[{string.Join(" | ", r.Fidelity.UnsupportedLayers)}]");
            foreach (var line in r.Fidelity.Notes) report.AppendLine($"    {line}");
        }

        var text = report.ToString();
        File.WriteAllText(Path.Combine(dir, "last-run.txt"), text);
        _out.WriteLine(text);
        Assert.Equal(manifest.Goldens.Count, compared);
    }
}
