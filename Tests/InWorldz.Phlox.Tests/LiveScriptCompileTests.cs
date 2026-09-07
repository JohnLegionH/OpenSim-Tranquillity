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
/// The 17 scripts are the distinct script assets across all regions, pulled from
/// <c>primitems</c> → <c>assets</c> on 2026-09-07. They are content, not fixtures: they are here
/// because they are what actually runs.
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

    private static string Dir => Path.Combine(
        Path.GetDirectoryName(typeof(LiveScriptCompileTests).Assembly.Location)!, "LiveScripts");

    public static IEnumerable<object[]> AllScripts()
        => Directory.GetFiles(Dir, "*.lsl").Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    [Fact]
    public void TheManholeCompiles()
    {
        // "Function 'osTeleportAgent' expects 4 arguments, got 3" at lines 16:12 and 20:12, every
        // region start since it was rezzed. Its call is the 3-argument local-teleport overload.
        var c = PhloxCompiler.Compile(File.ReadAllText(Path.Combine(Dir, Manhole + ".lsl")));
        Assert.False(c.HasErrors(), c.Report);
    }

    [Fact]
    public void TheAirshipStillFailsOnUserFunctionOverloading()
    {
        // Scope ruling: SL has no user-function overloading, so this stays rejected. The message
        // must still be about the duplicate symbol, not about a built-in.
        var c = PhloxCompiler.Compile(File.ReadAllText(Path.Combine(Dir, Airship + ".lsl")));
        Assert.True(c.HasErrors(), "the airship defines SetVehicleSettings at two arities");
        Assert.Contains("SetVehicleSettings", string.Join(" | ", c.Errors));
    }

    [Theory]
    [MemberData(nameof(AllScripts))]
    public void EveryLiveScriptCompilesOrFailsOnlyForAKnownReason(string assetId)
    {
        var c = PhloxCompiler.Compile(File.ReadAllText(Path.Combine(Dir, assetId + ".lsl")));
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
