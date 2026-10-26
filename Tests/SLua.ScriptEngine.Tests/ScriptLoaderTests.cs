/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using SLua.ScriptEngine.Bindings;
using OpenMetaverse;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>The asset service stand-in: serves text by asset id and records what was asked for.</summary>
internal sealed class FakeAssets : IScriptTextSource
{
    private readonly Dictionary<UUID, string> m_text = new();
    public readonly List<UUID> Asked = new();

    public FakeAssets With(UUID id, string text) { m_text[id] = text; return this; }

    public void Fetch(UUID assetId, Action<string> done)
    {
        lock (Asked) Asked.Add(assetId);
        done(m_text.TryGetValue(assetId, out string t) ? t : null);
    }
}

/// <summary>
/// The loader with the real library: text in, a compiled script held ready (never started) out, with the editor's
/// errors published for the load. The library's counters are process-wide, so these run one at a time.
/// </summary>
[Collection("native-library")]
public class ScriptLoaderTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private const string GoodSlua = "--!slua\nll.Say(0, 'hello')\n";
    private const string GoodLsl = "default\n{\n    state_entry()\n    {\n        llSay(0, \"hello\");\n    }\n}\n";

    private sealed class Rig : IDisposable
    {
        public readonly RecordingLog Log = new();
        public readonly ScriptCompiler Compiler;
        public readonly ScriptLoader Loader;

        public Rig(NativeCompile native = null, IScriptTextSource assets = null, CompileCache cache = null)
        {
            RealLibrary.Load();
            Compiler = new ScriptCompiler(null, native ?? Host.Compile, Log, cache);
            Loader = new ScriptLoader(Compiler, assets ?? new FakeAssets(), Log);
        }

        public List<string> Load(UUID item, string source, UUID? asset = null, uint local = 1)
        {
            Loader.Post(new LoadRequest { ItemID = item, LocalID = local, AssetID = asset ?? UUID.Random(), Source = source });
            return Loader.WaitForCompileErrors(item, Wait);
        }

        public void Dispose()
        {
            Loader.Dispose();
            Compiler.Dispose();
        }
    }

    [RequiresNativeFact]
    public void AnOwnedSluaScriptIsCompiledAndHeldReady()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        Assert.Empty(rig.Load(item, GoodSlua));
        Assert.True(rig.Loader.IsReady(item));
        ReadyScript ready = rig.Loader.GetReady(item);
        Assert.Equal(item, ready.ItemID);
        Assert.Equal(ScriptLanguage.Slua, ready.Language);
        Assert.NotEmpty(ready.Asset);
    }

    [RequiresNativeFact]
    public void AnOwnedLslScriptIsCompiledAndHeldReady()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        Assert.Empty(rig.Load(item, GoodLsl));
        Assert.Equal(ScriptLanguage.Lsl, rig.Loader.GetReady(item).Language);
    }

    [RequiresNativeFact]
    public void AScriptWithNoTextIsFetchedFromTheAssetServiceByItsAssetId()
    {
        UUID asset = UUID.Random(), item = UUID.Random();
        var assets = new FakeAssets().With(asset, GoodSlua);
        using var rig = new Rig(assets: assets);
        Assert.Empty(rig.Load(item, "", asset));
        Assert.Equal(new[] { asset }, assets.Asked);
        Assert.True(rig.Loader.IsReady(item));
        Assert.Equal(asset, rig.Loader.GetReady(item).AssetID);
    }

    [RequiresNativeFact]
    public void AnAssetThatIsMissingOrNotScriptTextIsAnErrorForTheEditor()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        List<string> errors = rig.Load(item, "", UUID.Random());
        string error = Assert.Single(errors);
        Assert.Contains("not script text", error);
        Assert.False(rig.Loader.IsReady(item));
    }

    [RequiresNativeFact]
    public void ACompileErrorReachesTheEditorAndLeavesNothingReady()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        List<string> errors = rig.Load(item, "--!slua\nlocal a = 1\nlocal b = = 2\n");
        string error = Assert.Single(errors);
        Assert.StartsWith("(3,0) Error: ", error);
        Assert.False(rig.Loader.IsReady(item));
        Assert.Equal(0, rig.Loader.ReadyCount);
    }

    [RequiresNativeFact]
    public void TheRefusalOfAnOsFunctionInLslReachesTheEditor()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        List<string> errors = rig.Load(item, "default\n{\n    state_entry()\n    {\n        osGetGridName();\n    }\n}\n");
        Assert.Contains("osGetGridName", Assert.Single(errors));
        Assert.Contains("Phlox", errors[0]);
    }

    [RequiresNativeFact]
    public void PostingReturnsAtOnceAndTheCompileRunsOnItsOwnThreads()
    {
        using var gate = new ManualResetEventSlim();
        string compileThread = null;
        int compileThreadId = 0;
        using var rig = new Rig(native: (text, lsl) =>
        {
            compileThread = Thread.CurrentThread.Name;
            compileThreadId = Environment.CurrentManagedThreadId;
            gate.Wait(Wait);
            return Host.Compile(text, lsl);
        });
        UUID item = UUID.Random();

        long serial = rig.Loader.Post(new LoadRequest { ItemID = item, LocalID = 1, AssetID = UUID.Random(), Source = GoodSlua });

        // The native compile is still held, and the caller has its answer: it did not wait for the compile.
        Assert.True(serial > 0);
        Assert.True(rig.Loader.IsLoading(item));
        Assert.False(rig.Loader.IsReady(item));
        gate.Set();
        Assert.Empty(rig.Loader.WaitForCompileErrors(item, Wait));
        Assert.NotEqual(Environment.CurrentManagedThreadId, compileThreadId);
        Assert.NotEqual(rig.Loader.LoaderThreadId, compileThreadId);
        Assert.Contains("compile", compileThread);
    }

    [RequiresNativeFact]
    public void ManyItemsMadeFromOneAssetCompileOnce()
    {
        using var dir = new TempFolder();
        int natives = 0;
        using var rig = new Rig(native: (t, l) => { Interlocked.Increment(ref natives); return Host.Compile(t, l); },
                                cache: new CompileCache(dir.Path, "runtime=test api=3 fns=none"));
        var items = Enumerable.Range(0, 10).Select(_ => UUID.Random()).ToList();

        foreach (UUID item in items)
            Assert.Empty(rig.Load(item, GoodSlua));

        Assert.Equal(10, rig.Loader.ReadyCount);
        Assert.Equal(1, natives);
    }

    [RequiresNativeFact]
    public void ANewerSaveOfAnItemReplacesTheOlderAndOnlyTheNewerIsHeld()
    {
        using var gate = new ManualResetEventSlim();
        using var rig = new Rig(native: (t, l) => { gate.Wait(Wait); return Host.Compile(t, l); });
        UUID item = UUID.Random(), first = UUID.Random(), second = UUID.Random();

        rig.Loader.Post(new LoadRequest { ItemID = item, LocalID = 1, AssetID = first, Source = "--!slua\nll.Say(0, 'one')" });
        rig.Loader.Post(new LoadRequest { ItemID = item, LocalID = 1, AssetID = second, Source = "--!slua\nll.Say(0, 'two')" });
        gate.Set();

        Assert.Empty(rig.Loader.WaitForCompileErrors(item, Wait)); // the answer for the save this thread posted last
        Assert.Equal(1, rig.Loader.ReadyCount);
        Assert.Equal(second, rig.Loader.GetReady(item).AssetID);
    }

    [RequiresNativeFact]
    public void AWaitingEditorGetsTheTimeoutTextWhenTheCompileTakesTooLong()
    {
        using var gate = new ManualResetEventSlim();
        using var rig = new Rig(native: (t, l) => { gate.Wait(Wait); return Host.Compile(t, l); });
        UUID item = UUID.Random();
        rig.Loader.Post(new LoadRequest { ItemID = item, LocalID = 1, AssetID = UUID.Random(), Source = GoodSlua });

        List<string> answer = rig.Loader.WaitForCompileErrors(item, TimeSpan.FromMilliseconds(200));

        Assert.Equal(new[] { ScriptLoader.ErrorWaitTimedOut }, answer);
        gate.Set();
    }

    [RequiresNativeFact]
    public void AnItemTheLoaderNeverSawGetsNullSoTheOwningEngineAnswers()
    {
        using var rig = new Rig();
        Assert.Null(rig.Loader.WaitForCompileErrors(UUID.Random(), TimeSpan.FromSeconds(1)));
    }

    [RequiresNativeFact]
    public void DisownedWhileAnEditorWaitsTheEditorGetsNullAndNothingIsHeld()
    {
        using var gate = new ManualResetEventSlim();
        using var rig = new Rig(native: (t, l) => { gate.Wait(Wait); return Host.Compile(t, l); });
        UUID item = UUID.Random();
        rig.Loader.Post(new LoadRequest { ItemID = item, LocalID = 1, AssetID = UUID.Random(), Source = GoodSlua });
        List<string> answer = new();
        bool returned = false;
        var waiter = new Thread(() => { answer = rig.Loader.WaitForCompileErrors(item, Wait); returned = true; });
        waiter.Start();
        Thread.Sleep(100);

        rig.Loader.Disown(item);

        Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
        Assert.True(returned);
        Assert.Null(answer);
        gate.Set();
        Thread.Sleep(100);
        Assert.False(rig.Loader.IsReady(item));
        Assert.Null(rig.Loader.WaitForCompileErrors(item, TimeSpan.FromMilliseconds(100)));
    }

    [RequiresNativeFact]
    public void DisowningAReadyScriptReleasesIt()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        Assert.Empty(rig.Load(item, GoodSlua));

        rig.Loader.Disown(item);

        Assert.Equal(0, rig.Loader.ReadyCount);
        Assert.False(rig.Loader.IsReady(item));
    }

    [RequiresNativeFact]
    public void HostileSourceIsAnEditorErrorAndTheLoaderGoesOnToLoadTheNextScript()
    {
        using var rig = new Rig();
        UUID bad = UUID.Random(), good = UUID.Random();

        string deep = "default\n{\n    state_entry()\n    {\n        integer x = " + new string('(', 5000) + "1" + new string(')', 5000) + ";\n    }\n}\n";
        List<string> errors = rig.Load(bad, deep);
        Assert.Contains("nested too deeply", Assert.Single(errors));
        Assert.False(rig.Loader.IsReady(bad));

        Assert.Empty(rig.Load(good, GoodSlua));
        Assert.True(rig.Loader.IsReady(good));
    }

    [RequiresNativeFact]
    public void ACompilerThatFailsUnexpectedlyIsAnErrorForThatScriptOnly()
    {
        using var rig = new Rig(native: (t, l) => t.Contains("explode") ? throw new InvalidOperationException("boom") : Host.Compile(t, l));
        UUID bad = UUID.Random(), good = UUID.Random();

        string error = Assert.Single(rig.Load(bad, "--!slua\n-- explode\n"));
        Assert.Contains("not a script error", error);
        Assert.False(rig.Loader.IsReady(bad));
        Assert.Empty(rig.Load(good, GoodSlua));
    }

    [RequiresNativeFact]
    public void TheLoaderHoldsNoScriptImageOrVmWhileItWorksAndLeavesNoneBehind()
    {
        RealLibrary.Load();
        AllocStats before = Host.GetAllocStats();
        var rig = new Rig();
        for (int i = 0; i < 5; i++)
            Assert.Empty(rig.Load(UUID.Random(), GoodSlua));
        AllocStats held = Host.GetAllocStats();
        Assert.Equal(before.LiveScripts, held.LiveScripts); // the scheduler makes the script objects, not the loader
        Assert.Equal(before.LiveImages, held.LiveImages);
        Assert.Equal(before.LiveVms, held.LiveVms);

        rig.Dispose();

        AllocStats after = Host.GetAllocStats();
        Assert.Equal(before.LiveScripts, after.LiveScripts);
        Assert.Equal(before.LiveImages, after.LiveImages);
        Assert.Equal(before.LiveVms, after.LiveVms);
    }
}
