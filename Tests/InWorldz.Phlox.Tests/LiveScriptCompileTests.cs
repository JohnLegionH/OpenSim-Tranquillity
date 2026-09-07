using InWorldz.Phlox.Types;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-2c. Every script actually in a prim on this grid, compiled. This is the test PHLOX-2b did
/// not write: overload resolution changed how <b>every</b> call resolves, and the risk it carries is
/// not a build error but a call quietly reaching a different shim.
///
/// <para>
/// The 17 scripts are the distinct script assets across all regions as of 2026-09-07. They are
/// content, not fixtures: they are here because they are what actually runs. <b>Their bodies are
/// not committed</b> — they are residents' content — so this follows the SSB golden convention:
/// <c>LiveScripts/manifest.json</c> carries the asset ids and their SHA-256, and
/// <c>fetch-live-scripts.sh</c> pulls the bodies. Without them these tests are a vacuous pass that
/// says so on the console, exactly as <c>BakeOrchestratorTests</c> behaves without its fixtures.
/// </para>
/// </summary>
public class LiveScriptCompileTests
{
    private readonly ITestOutputHelper _out;
    public LiveScriptCompileTests(ITestOutputHelper o) => _out = o;

    /// <summary>The manhole — the script this whole line of work started from.</summary>
    private const string Manhole = "01d4448d-087e-49fb-9253-4b06ce522811";

    /// <summary>The airship — user-function overloading, rejected by ruling.</summary>
    private const string Airship = "b8079466-322a-47f5-ba8d-cd17d9e0da61";

    private static string Root => Path.Combine(
        Path.GetDirectoryName(typeof(LiveScriptCompileTests).Assembly.Location)!, "LiveScripts");

    private static string Dir => Path.Combine(Root, "scripts");

    private const string SkipNote =
        "SKIPPED: live script bodies not fetched (Tests/InWorldz.Phlox.Tests/LiveScripts/fetch-live-scripts.sh)";

    private static bool Fetched => Directory.Exists(Dir) && Directory.GetFiles(Dir, "*.lsl").Length > 0;

    /// <summary>The manifest's (assetId, sha256) rows - committed, and the authority for what is covered.</summary>
    private static List<(string Id, string Sha)> Manifest()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "manifest.json")));
        return doc.RootElement.GetProperty("scripts").EnumerateArray()
            .Select(e => (e.GetProperty("assetId").GetString()!, e.GetProperty("sha256").GetString()!))
            .ToList();
    }

    private static string Body(string assetId) => File.ReadAllText(Path.Combine(Dir, assetId + ".lsl"));

    public static IEnumerable<object[]> AllScripts()
        => Manifest().Select(m => new object[] { m.Id });

    [Fact]
    public void TheManifestListsEveryScriptAndTheFetchedBodiesMatchIt()
    {
        var manifest = Manifest();
        Assert.Equal(17, manifest.Count);
        Assert.Contains(manifest, m => m.Id == Manhole);
        Assert.Contains(manifest, m => m.Id == Airship);

        if (!Fetched) { Console.WriteLine(SkipNote); return; }

        // A changed script must be visible, not silently alter what these tests cover.
        var wrong = new List<string>();
        foreach (var (id, sha) in manifest)
        {
            var path = Path.Combine(Dir, id + ".lsl");
            if (!File.Exists(path)) { wrong.Add($"{id}: not fetched"); continue; }
            var got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (got != sha) wrong.Add($"{id}: body changed in world");
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void TheManholeCompiles()
    {
        // "Function 'osTeleportAgent' expects 4 arguments, got 3" at lines 16:12 and 20:12, every
        // region start since it was rezzed. Its call is the 3-argument local-teleport overload.
        if (!Fetched) { Console.WriteLine(SkipNote); return; }
        var c = PhloxCompiler.Compile(Body(Manhole));
        Assert.False(c.HasErrors(), c.Report);
    }

    [Fact]
    public void TheAirshipStillFailsOnUserFunctionOverloading()
    {
        // Scope ruling: SL has no user-function overloading, so this stays rejected. The message
        // must still be about the duplicate symbol, not about a built-in.
        if (!Fetched) { Console.WriteLine(SkipNote); return; }
        var c = PhloxCompiler.Compile(Body(Airship));
        Assert.True(c.HasErrors(), "the airship defines SetVehicleSettings at two arities");
        Assert.Contains("SetVehicleSettings", string.Join(" | ", c.Errors));
    }

    [Theory]
    [MemberData(nameof(AllScripts))]
    public void EveryLiveScriptCompilesOrFailsOnlyForAKnownReason(string assetId)
    {
        if (!Fetched) { Console.WriteLine(SkipNote); return; }
        var c = PhloxCompiler.Compile(Body(assetId));
        if (!c.HasErrors()) return;

        // The airship is the one known-bad script, by ruling. Anything else failing is a
        // regression this change introduced, and the message says which script.
        Assert.True(assetId == Airship, $"{assetId} no longer compiles: {c.Report}");
    }

    /// <summary>
    /// The guard PHLOX-2b owed: every built-in these scripts call must still dispatch to the index
    /// it had at 34fb6d201b. The dispatch baseline is the same file DispatchIndexGuardTests uses;
    /// this checks it from the direction that matters — the names live content actually calls.
    /// </summary>
    [Fact]
    public void EveryBuiltinTheseScriptsCallKeepsItsOriginalDispatchIndex()
    {
        var baseline = File.ReadAllLines(Path.Combine(
                Path.GetDirectoryName(typeof(LiveScriptCompileTests).Assembly.Location)!,
                "dispatch-baseline.txt"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(' '))
            .ToDictionary(p => p[0], p => int.Parse(p[1]));

        if (!Fetched) { Console.WriteLine(SkipNote); return; }

        var called = new SortedSet<string>();
        foreach (var file in Directory.GetFiles(Dir, "*.lsl"))
        {
            var text = File.ReadAllText(file);
            foreach (var name in baseline.Keys)
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + name + @"\s*\("))
                    called.Add(name);
        }

        Assert.True(called.Count > 20, $"expected live content to call many built-ins, found {called.Count}");

        var moved = new List<string>();
        foreach (var name in called)
        {
            var sigs = Defaults.SystemMethods[name];
            // The first signature keeps the historical index; that is the one every existing call
            // resolved to before overloading, and must still.
            if (sigs[0].TableIndex != baseline[name]) moved.Add($"{name}: {baseline[name]} -> {sigs[0].TableIndex}");
        }

        _out.WriteLine($"{called.Count} built-ins called by the {Directory.GetFiles(Dir, "*.lsl").Length} live scripts");
        Assert.True(moved.Count == 0, "dispatch index moved for live-called built-ins: " + string.Join(", ", moved));
    }
}
