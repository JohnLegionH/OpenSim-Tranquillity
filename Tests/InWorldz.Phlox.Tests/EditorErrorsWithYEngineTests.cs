using System.Collections;
using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// DEPLOY-PHLOX-22 in-world failure (2026-09-23 20:10, Ebony). Saving phlox22-syntaxerror.lsl and phlox21-deepnest.lsl
/// showed NO error in the editor, and the deep-nest error came as the owner pop-up. The live region runs YEngine AND
/// Phlox; YEngine is added to the scene first, so SceneObjectPartInventory.GetScriptErrors asks it first, and
/// YEngine's GetScriptErrors waited - with no timeout and nothing to wake it - for an item it had declined in
/// OnRezScript: the Save never returned and Phlox was never asked (so no editor claimed the errors, and Phlox sent
/// the pop-up). PHLOX-22 C's tests registered Phlox alone. Also here: a compile that fails before the editor's
/// GetScriptErrors reaches Phlox must reach the editor ONCE - no pop-up as well.
/// </summary>
[Collection("phlox-state")]
public class EditorErrorsWithYEngineTests
{
    private readonly ITestOutputHelper _out;
    public EditorErrorsWithYEngineTests(ITestOutputHelper o) => _out = o;

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(EditorErrorsWithYEngineTests).Assembly.Location)!, "Fixtures", name));

    /// <summary>The script editor's Save, on its own thread (the caps thread), while this thread pumps the scheduler.</summary>
    private static (ArrayList errors, long ms) Save(SchedulerHarness h, string source)
    {
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "saved" + Guid.NewGuid().ToString("N").Substring(0, 6), source).ItemID;
        var sw = Stopwatch.StartNew();
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(item, 0, false, h.Engine.Name, 1));
        while (!save.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(20)) { h.PumpOnce(); Thread.Sleep(1); }
        Assert.True(save.IsCompleted, $"the Save did not return in 20 s ({h.Scene.RequestModuleInterfaces<IScriptModule>().Length} script engines on the scene)");
        return (save.Result, sw.ElapsedMilliseconds);
    }

    private static void PumpFor(SchedulerHarness h, TimeSpan t)
    {
        var until = DateTime.UtcNow + t;
        while (DateTime.UtcNow < until) { h.PumpOnce(); Thread.Sleep(1); }
    }

    [Fact]
    public void WithYEngineOnTheSceneASyntaxErrorReachesTheEditorAndNoPopUp()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        Assert.Equal(2, h.Scene.RequestModuleInterfaces<IScriptModule>().Length);   // YEngine first, as on the live region
        Assert.Same(h.YEngine, h.Scene.RequestModuleInterfaces<IScriptModule>()[0]);
        h.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out var rec));

        var (errors, ms) = Save(h, Fixture("phlox22-syntaxerror.lsl"));
        PumpFor(h, TimeSpan.FromSeconds(3));   // past the owner-alert grace
        _out.WriteLine($"{ms} ms: [{string.Join(" | ", errors.Cast<object>())}] alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.Equal(new[] { "(8,4) Error: missing ';' at '}'" }, errors.Cast<string>().ToArray());
        Assert.Empty(rec.Alerts);
    }

    [Fact]
    public void WithYEngineOnTheSceneTheDeepNestErrorReachesTheEditorAndNoPopUp()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        h.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out var rec));

        var (errors, ms) = Save(h, Fixture("phlox21-deepnest.lsl"));
        PumpFor(h, TimeSpan.FromSeconds(3));
        _out.WriteLine($"{ms} ms: [{string.Join(" | ", errors.Cast<object>())}] alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.Single(errors);
        Assert.Matches(@"^\(\d+,\d+\) Error: expression nested too deeply \(limit 1000\)$", (string)errors[0]!);
        Assert.Empty(rec.Alerts);
    }

    [Fact]
    public void WithYEngineOnTheSceneAGoodScriptSavesAsCompiledAndRuns()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var (errors, _) = Save(h, "default { state_entry() { llSay(0, \"saved beside yengine\"); } }");
        Assert.Empty(errors);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !h.Said.Contains("saved beside yengine")) h.PumpOnce();
        Assert.Contains("saved beside yengine", h.Said);
    }

    [Fact]
    public void YEngineStillReportsItsOwnScriptsErrors()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "yscript",
            "default { state_entry() { llSay(0, \"x\") } }").ItemID;
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(item, 0, false, h.YEngine.ScriptEngineName, 1));
        Assert.True(save.Wait(TimeSpan.FromSeconds(60)), "a YEngine save did not return");
        _out.WriteLine($"[{string.Join(" | ", save.Result.Cast<object>())}]");
        Assert.NotEmpty(save.Result);
    }

    [Fact]
    public void ACompileThatFailsBeforeTheEditorAsksReachesTheEditorOnceWithNoPopUp()
    {
        using var h = new SchedulerHarness();
        h.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out var rec));
        // The race: OnRezScript posts the load, the compile fails and is published, and only THEN does the
        // editor's GetScriptErrors reach Phlox (on live: 17 ms from rez to failure).
        var item = h.RezScript(Fixture("phlox22-syntaxerror.lsl"));
        PumpFor(h, TimeSpan.FromMilliseconds(500));
        var ask = Task.Run(() => h.Engine.GetScriptErrors(item));   // the caps thread, not the scheduler's
        while (!ask.IsCompleted) { h.PumpOnce(); Thread.Sleep(1); }
        PumpFor(h, TimeSpan.FromSeconds(3));   // past the owner-alert grace
        _out.WriteLine($"[{string.Join(" | ", ask.Result.Cast<object>())}] alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.Equal(new[] { "(8,4) Error: missing ';' at '}'" }, ask.Result.Cast<string>().ToArray());   // the outcome is not lost
        Assert.Empty(rec.Alerts);                                                                         // and not reported twice
    }

    [Fact]
    public void AFailureNothingCollectsStillAlertsTheOwnerOnce()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        h.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out var rec));
        h.RezScript(Fixture("phlox22-syntaxerror.lsl"));   // a rez, not a Save: no editor will ask
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && rec.Alerts.Count == 0) h.PumpOnce();
        PumpFor(h, TimeSpan.FromSeconds(1));
        _out.WriteLine($"alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.Single(rec.Alerts);
        Assert.Contains("failed to compile", rec.Alerts[0]);
    }
}
