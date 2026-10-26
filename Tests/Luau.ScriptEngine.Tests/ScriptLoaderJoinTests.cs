/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Luau.ScriptEngine.Bindings;
using OpenMetaverse;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>
/// The loader keeps compile, cache and the compiled asset bytes; the scheduler's worker makes the script object when
/// the script starts. So a ready script is bytes, the loader announces it, and a compile failure nobody collects
/// through the editor is announced after a grace period.
/// </summary>
[Collection("native-library")]
public class ScriptLoaderJoinTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private const string GoodSlua = "--!slua\nll.Say(0, 'hello')\n";
    private const string BadSlua = "--!slua\nlocal a = = 2\n";

    private sealed class Rig : IDisposable
    {
        public readonly RecordingLog Log = new();
        public readonly ScriptLoader Loader;
        public readonly List<ReadyScript> Readied = new();
        public readonly List<LoadFailure> Unclaimed = new();

        public Rig(NativeCompile native = null, double graceMs = 150)
        {
            RealLibrary.Load();
            Loader = new ScriptLoader(new ScriptCompiler(null, native ?? Host.Compile, Log, null), new FakeAssets(), Log, ownsCompiler: true)
            {
                UnclaimedGrace = TimeSpan.FromMilliseconds(graceMs),
            };
            Loader.ScriptReady += r => { lock (Readied) Readied.Add(r); };
            Loader.FailureUnclaimed += f => { lock (Unclaimed) Unclaimed.Add(f); };
        }

        public void Post(UUID item, string source, UUID? asset = null, uint local = 1) =>
            Loader.Post(new LoadRequest { ItemID = item, LocalID = local, AssetID = asset ?? UUID.Random(), Source = source });

        public ReadyScript[] ReadySnapshot() { lock (Readied) return Readied.ToArray(); }
        public LoadFailure[] UnclaimedSnapshot() { lock (Unclaimed) return Unclaimed.ToArray(); }

        public void Dispose() => Loader.Dispose();
    }

    [RequiresNativeFact]
    public void AReadyScriptHoldsTheCompiledBytesAndNoScriptObject()
    {
        AllocStats before = Host.GetAllocStats();
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Post(item, GoodSlua);
        Assert.Empty(rig.Loader.WaitForCompileErrors(item, Wait));

        ReadyScript ready = rig.Loader.GetReady(item);
        Assert.NotNull(ready.Asset);
        Assert.NotEmpty(ready.Asset);
        Assert.Null(ready.Script);
        AllocStats after = Host.GetAllocStats();
        Assert.Equal(before.LiveScripts, after.LiveScripts);
        Assert.Equal(before.LiveImages, after.LiveImages);
        Assert.Equal(before.LiveVms, after.LiveVms);
    }

    [RequiresNativeFact]
    public void TheCompiledBytesRunWhenAScriptIsMadeFromThem()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Post(item, "--!slua\nossl.Count()\n");
        Assert.Empty(rig.Loader.WaitForCompileErrors(item, Wait));
        ReadyScript ready = rig.Loader.GetReady(item);

        using var h = new SchedulerHarness();
        SchedulerScript s = h.Scheduler.Add(ready.Asset, ready.Language);
        for (int i = 0; i < 100 && s.State == ScriptRunState.Loading; i++) h.Scheduler.DoWork(0);
        h.Scheduler.Start(s);
        h.Pump();

        Assert.Equal(1, h.CountOf(s));
    }

    [RequiresNativeFact]
    public void TheLoaderAnnouncesAScriptWhenItIsReady()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.Post(item, GoodSlua, asset, local: 42);
        Assert.Empty(rig.Loader.WaitForCompileErrors(item, Wait));

        Assert.True(SchedulerHarness.WaitFor(() => rig.ReadySnapshot().Length == 1));
        ReadyScript ready = rig.ReadySnapshot()[0];
        Assert.Equal(item, ready.ItemID);
        Assert.Equal(42u, ready.LocalID);
        Assert.Equal(asset, ready.AssetID);
        Assert.Equal(ScriptLanguage.Slua, ready.Language);
        Assert.NotEmpty(ready.Asset);
    }

    [RequiresNativeFact]
    public void ASaveThatWasReplacedBeforeItFinishedIsNeverAnnounced()
    {
        using var gate = new ManualResetEventSlim();
        using var rig = new Rig(native: (t, l) => { gate.Wait(Wait); return Host.Compile(t, l); });
        UUID item = UUID.Random(), first = UUID.Random(), second = UUID.Random();

        rig.Post(item, "--!slua\nll.Say(0, 'one')", first);
        rig.Post(item, "--!slua\nll.Say(0, 'two')", second);
        gate.Set();
        Assert.Empty(rig.Loader.WaitForCompileErrors(item, Wait));

        Assert.True(SchedulerHarness.WaitFor(() => rig.ReadySnapshot().Length >= 1));
        Thread.Sleep(200); // a window in which a stale announcement would have arrived
        ReadyScript only = Assert.Single(rig.ReadySnapshot());
        Assert.Equal(second, only.AssetID);
    }

    [RequiresNativeFact]
    public void ACompileFailureNobodyCollectsIsAnnouncedAfterTheGrace()
    {
        using var rig = new Rig(graceMs: 100);
        UUID item = UUID.Random();
        rig.Post(item, BadSlua, local: 9);

        Assert.True(SchedulerHarness.WaitFor(() => rig.UnclaimedSnapshot().Length == 1));
        LoadFailure f = rig.UnclaimedSnapshot()[0];
        Assert.Equal(item, f.ItemID);
        Assert.Equal(9u, f.LocalID);
        Assert.StartsWith("(2,0) Error: ", Assert.Single(f.Errors));
        Assert.Empty(rig.ReadySnapshot());
    }

    [RequiresNativeFact]
    public void ACompileFailureAnEditorCollectsInTimeIsNotAnnounced()
    {
        using var rig = new Rig(graceMs: 300);
        UUID item = UUID.Random();
        rig.Post(item, BadSlua);
        Assert.Single(rig.Loader.WaitForCompileErrors(item, Wait)); // the editor's call

        Thread.Sleep(700); // well past the grace
        Assert.Empty(rig.UnclaimedSnapshot());
    }

    [RequiresNativeFact]
    public void AScriptThatCompilesIsNeverAnnouncedAsAFailure()
    {
        using var rig = new Rig(graceMs: 100);
        UUID item = UUID.Random();
        rig.Post(item, GoodSlua);
        Assert.Empty(rig.Loader.WaitForCompileErrors(item, Wait));

        Thread.Sleep(400);
        Assert.Empty(rig.UnclaimedSnapshot());
    }

    [RequiresNativeFact]
    public void ADisownedFailureIsNotAnnounced()
    {
        using var gate = new ManualResetEventSlim();
        using var rig = new Rig(native: (t, l) => { gate.Wait(Wait); return Host.Compile(t, l); }, graceMs: 100);
        UUID item = UUID.Random();
        rig.Post(item, BadSlua);
        rig.Loader.Disown(item);
        gate.Set();

        Thread.Sleep(500);
        Assert.Empty(rig.UnclaimedSnapshot());
    }
}
