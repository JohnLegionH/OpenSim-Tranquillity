/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// What the engine does with a compiled script's saved state while it does not run scripts: it looks the state up in its own
/// database when the script has compiled, answers the region's ownership question from that database, and removes a script's
/// row when the script is deleted, but not at shutdown. The loader is replaced by a recorder; the database is a real SQLite file.
/// </summary>
public class EngineSavedStateTests
{
    private const string Phlox = "InWorldz.Phlox";
    private const uint LocalId = 7300;

    private sealed class Rig : IDisposable
    {
        public readonly TempFolder Dir = new();
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly RecordingLog Log = new();
        public readonly RecordingLoader Loader = new();
        public readonly SqliteStateStore Store;
        public readonly SLuaEngine Engine;

        public Rig(bool saveState = true)
        {
            Store = new SqliteStateStore(System.IO.Path.Combine(Dir.Path, "state", "script_state.db"), EngineIdentity.EngineName);
            Engine = new SLuaEngine(Log, (settings, log) => true, Store, (settings, log, scene) => Loader);
            var source = new IniConfigSource();
            IConfig slua = source.AddConfig(EngineIdentity.ConfigSection);
            slua.Set("Enabled", "true");
            slua.Set("SaveScriptState", saveState ? "true" : "false");
            slua.Set("StateLoadRetries", "1");
            slua.Set("StateLoadRetryDelayMs", "0");
            Engine.Initialise(source);
            Engine.AddRegion(Scene);
        }

        public void Rez(UUID item, string script = "--!slua\nprint(1)") =>
            Scene.EventManager.TriggerRezScript(LocalId, item, script, 0, false, EngineIdentity.EngineName, 0);

        public void Ready(UUID item, UUID asset, bool fromCache = false) =>
            Loader.RaiseReady(new ReadyScript { ItemID = item, LocalID = LocalId, AssetID = asset, Language = ScriptLanguage.Slua, Asset = new byte[] { 1 }, FromCache = fromCache });

        public void SaveRow(UUID item, UUID asset, byte[] bytes)
        {
            Store.Save(item, asset, bytes);
            Assert.True(Store.Flush());
        }

        public void Dispose()
        {
            Engine.RemoveRegion(Scene);
            Store.Dispose();
            Dir.Dispose();
        }
    }

    /// <summary>What the engine found for the item's compiled script; it must have looked.</summary>
    private static StateLoadResult Joined(Rig rig, UUID item)
    {
        StateLoadResult joined = rig.Engine.JoinedStateOf(item);
        Assert.NotNull(joined);
        return joined;
    }

    private static byte[] Record(string engine) =>
        new ScriptStateRecord { Engine = engine, Language = ScriptLanguage.Slua, Native = new byte[] { 9, 8, 7 } }.Encode();

    [Fact]
    public void ACompiledScriptWithItsOwnRecordIsReadyToBeRestoredFromIt()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));
        rig.Rez(item);

        rig.Ready(item, asset);

        StateLoadResult joined = Joined(rig, item);
        Assert.Equal(StateLoadKind.Restore, joined.Kind);
        Assert.Equal(new byte[] { 9, 8, 7 }, joined.Record.Native);
        Assert.Contains(rig.Log.Lines, l => l.Contains(item.ToString()) && l.Contains("its saved state was found"));
    }

    [Fact]
    public void ACompiledScriptWithNoRowStartsFresh()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item);

        rig.Ready(item, UUID.Random());

        Assert.Equal(StateLoadKind.Fresh, Joined(rig, item).Kind);
        Assert.Contains(rig.Log.Lines, l => l.Contains(item.ToString()) && l.Contains("no saved state"));
    }

    [Fact]
    public void ARowOfTheSameItemForAnotherAssetIsNotRestored()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));
        rig.Rez(item);

        rig.Ready(item, UUID.Random()); // the script was edited: a new asset

        Assert.Equal(StateLoadKind.Fresh, Joined(rig, item).Kind);
        Assert.True(rig.Store.HasSavedState(item, asset)); // the old row stays
    }

    [Fact]
    public void BytesThatAreNoRecordAreSetAsideAndTheScriptStartsFresh()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        rig.Rez(item);

        rig.Ready(item, asset);
        Assert.True(rig.Store.Flush());

        Assert.Equal(StateLoadKind.Fresh, Joined(rig, item).Kind);
        Assert.False(rig.Store.HasSavedState(item, asset));
        Assert.NotNull(rig.Store.RejectedReason(item));
    }

    [Fact]
    public void AStoreThatCannotBeReadHoldsTheScriptAndLeavesItsRow()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));
        rig.Store.FailReadFor = id => id == item;
        rig.Rez(item);

        rig.Ready(item, asset);

        StateLoadResult joined = Joined(rig, item);
        Assert.Equal(StateLoadKind.Held, joined.Kind);
        Assert.Contains("could not be read", joined.Reason);
        rig.Store.FailReadFor = null;
        Assert.True(rig.Store.HasSavedState(item, asset));
    }

    [Fact]
    public void WithSaveScriptStateOffTheStoreIsNotRead()
    {
        using var rig = new Rig(saveState: false);
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));
        rig.Rez(item);

        rig.Ready(item, asset);

        Assert.Equal(StateLoadKind.Fresh, Joined(rig, item).Kind);
    }

    [Fact]
    public void AScriptThatIsNotOursIsNeverLookedUp()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));
        rig.Scene.EventManager.TriggerRezScript(LocalId, item, "default { state_entry() {} }", 0, false, Phlox, 0);

        rig.Ready(item, asset);

        Assert.Null(rig.Engine.JoinedStateOf(item));
    }

    [Fact]
    public void TheLogSaysWhenTheCompiledFormCameFromTheCache()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item);

        rig.Ready(item, UUID.Random(), fromCache: true);

        Assert.Contains(rig.Log.Lines, l => l.Contains(item.ToString()) && l.Contains("from the compile cache"));
    }

    [Fact]
    public void ACompiledScriptIsHeldButNotRunning()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item);
        rig.Loader.Ready.Add(item);
        rig.Ready(item, UUID.Random());

        Assert.True(rig.Engine.HasScript(item, out bool running));
        Assert.False(running);
        Assert.False(rig.Engine.GetScriptState(item));
    }

    [Fact]
    public void TheRegionIsToldTheEngineOwnsAScriptItHasStateFor()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));

        Assert.True(rig.Engine.HasSavedScriptState(item, asset));
        Assert.False(rig.Engine.HasSavedScriptState(item, UUID.Random()));
        Assert.False(rig.Engine.HasSavedScriptState(UUID.Random(), asset));
    }

    [Fact]
    public void ADeletedScriptTakesItsRowWithIt()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));
        rig.Rez(item);
        UUID removed = UUID.Zero;
        rig.Engine.OnScriptRemoved += id => removed = id;

        rig.Scene.EventManager.TriggerRemoveScript(LocalId, item); // the prim is not in the scene: the item is gone
        Assert.True(rig.Store.Flush());

        Assert.False(rig.Store.HasSavedState(item, asset));
        Assert.Equal(item, removed);
        Assert.Contains(item, rig.Loader.Disowned);
    }

    [Fact]
    public void TheRemovalOfAScriptThatIsNotOursLeavesItsRow()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));

        rig.Scene.EventManager.TriggerRemoveScript(LocalId, item);
        Assert.True(rig.Store.Flush());

        Assert.True(rig.Store.HasSavedState(item, asset));
    }

    [Fact]
    public void AScriptRemovedAtShutdownKeepsItsRowForTheRestart()
    {
        using var rig = new Rig();
        UUID item = UUID.Random(), asset = UUID.Random();
        rig.SaveRow(item, asset, Record(EngineIdentity.EngineName));
        rig.Rez(item);

        rig.Scene.EventManager.TriggerShutdown();
        rig.Scene.EventManager.TriggerRemoveScript(LocalId, item);
        Assert.True(rig.Store.Flush());

        Assert.True(rig.Store.HasSavedState(item, asset));
    }

    [Fact]
    public void ACompileFailureNoEditorCollectedIsWrittenToTheLog()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item);

        rig.Loader.RaiseFailure(new LoadFailure { LocalID = LocalId, ItemID = item, Errors = new List<string> { "(2,0) Error: bad" } });

        Assert.Contains(rig.Log.Warnings, l => l.Contains(item.ToString()) && l.Contains("(2,0) Error: bad"));
    }

    [Fact]
    public void AFailureOfAScriptThatIsNotOursIsNotLogged()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();

        rig.Loader.RaiseFailure(new LoadFailure { LocalID = LocalId, ItemID = item, Errors = new List<string> { "(1,0) Error: x" } });

        Assert.DoesNotContain(rig.Log.Warnings, l => l.Contains(item.ToString()));
    }
}

/// <summary>
/// The engine as the region server makes it: its own state database and compile cache under ScriptEnginesPath, and none of it
/// when the engine is off. Uses the packaged library, so it runs with the other tests that load it.
/// </summary>
[Collection("native-library")]
public class EngineFoldersTests
{
    private static SLuaEngine Start(string root, bool enabled, bool saveState = true)
    {
        var engine = new SLuaEngine();
        var source = new IniConfigSource();
        IConfig slua = source.AddConfig(EngineIdentity.ConfigSection);
        slua.Set("Enabled", enabled ? "true" : "false");
        slua.Set("ScriptEnginesPath", root);
        slua.Set("SaveScriptState", saveState ? "true" : "false");
        engine.Initialise(source);
        return engine;
    }

    [Fact]
    public void AnEngineThatIsOffMakesNoFolderAndNoDatabase()
    {
        using var dir = new TempFolder();
        string root = System.IO.Path.Combine(dir.Path, "ScriptEngines");
        Scene scene = new SceneHelpers().SetupScene();
        SLuaEngine engine = Start(root, enabled: false);

        engine.AddRegion(scene);
        engine.RemoveRegion(scene);

        Assert.False(System.IO.Directory.Exists(root));
    }

    [PackagedRuntimeFact]
    public void SwitchedOnTheEngineKeepsItsStateDatabaseInItsOwnFolder()
    {
        using var dir = new TempFolder();
        string root = System.IO.Path.Combine(dir.Path, "ScriptEngines");
        Scene scene = new SceneHelpers().SetupScene();
        SLuaEngine engine = Start(root, enabled: true);

        engine.AddRegion(scene);
        engine.RemoveRegion(scene);

        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(root, EngineIdentity.EngineName, "state", "script_state.db")));
    }

    [PackagedRuntimeFact]
    public void WithSaveScriptStateOffNoDatabaseIsMade()
    {
        using var dir = new TempFolder();
        string root = System.IO.Path.Combine(dir.Path, "ScriptEngines");
        Scene scene = new SceneHelpers().SetupScene();
        SLuaEngine engine = Start(root, enabled: true, saveState: false);

        engine.AddRegion(scene);
        engine.RemoveRegion(scene);

        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(root, EngineIdentity.EngineName, "state", "script_state.db")));
    }
    [PackagedRuntimeFact]
    public void TheCompiledFormIsKeptInTheCompileCacheAndReusedAfterARestart()
    {
        using var dir = new TempFolder();
        string root = System.IO.Path.Combine(dir.Path, "ScriptEngines");
        string cache = System.IO.Path.Combine(root, EngineIdentity.EngineName, "cache");
        const string Source = "--!slua\nprint(\"cached\")\n";

        Scene scene = new SceneHelpers().SetupScene();
        SLuaEngine first = Start(root, enabled: true);
        first.AddRegion(scene);
        UUID item = UUID.Random();
        scene.EventManager.TriggerRezScript(7400, item, Source, 0, false, EngineIdentity.EngineName, 0);
        Assert.Empty(first.GetScriptErrors(item));
        Assert.True(first.HasScript(item, out bool running));
        Assert.False(running);
        first.RemoveRegion(scene);
        string entry = Assert.Single(System.IO.Directory.GetFiles(cache, "*.luc"));
        System.DateTime written = System.IO.File.GetLastWriteTimeUtc(entry);

        // The region comes back: the same source compiles from the cache, and the entry is not written again.
        Scene again = new SceneHelpers().SetupScene();
        SLuaEngine second = Start(root, enabled: true);
        second.AddRegion(again);
        UUID other = UUID.Random();
        again.EventManager.TriggerRezScript(7400, other, Source, 0, false, EngineIdentity.EngineName, 0);
        Assert.Empty(second.GetScriptErrors(other));
        Assert.True(second.HasScript(other, out _));
        second.RemoveRegion(again);
        Assert.Equal(entry, Assert.Single(System.IO.Directory.GetFiles(cache, "*.luc")));
        Assert.Equal(written, System.IO.File.GetLastWriteTimeUtc(entry));
    }

    [PackagedRuntimeFact]
    public void WithTheCompileCacheOffNothingIsWrittenToIt()
    {
        using var dir = new TempFolder();
        string root = System.IO.Path.Combine(dir.Path, "ScriptEngines");
        Scene scene = new SceneHelpers().SetupScene();
        var engine = new SLuaEngine();
        var source = new IniConfigSource();
        IConfig slua = source.AddConfig(EngineIdentity.ConfigSection);
        slua.Set("Enabled", "true");
        slua.Set("ScriptEnginesPath", root);
        slua.Set("CompileCache", "false");
        engine.Initialise(source);
        engine.AddRegion(scene);
        UUID item = UUID.Random();
        scene.EventManager.TriggerRezScript(7401, item, "--!slua\nprint(2)\n", 0, false, EngineIdentity.EngineName, 0);
        Assert.Empty(engine.GetScriptErrors(item));
        engine.RemoveRegion(scene);

        Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(root, EngineIdentity.EngineName, "cache")));
    }
}

/// <summary>
/// A compile failure is held for the editor that saved the script; one that no editor collects within the grace is announced
/// once, so the engine can tell someone. The native compiler is replaced by one that always fails.
/// </summary>
public class UnclaimedFailureTests
{
    private static ScriptLoader Loader() =>
        new ScriptLoader(new ScriptCompiler(native: (text, lsl) => throw new SLua.ScriptEngine.Bindings.SluaException("slua_compile", -5, "1: bad")), null, new RecordingLog())
        { UnclaimedGrace = TimeSpan.FromMilliseconds(50) };

    [Fact]
    public void AFailureNoEditorCollectsIsAnnouncedOnceWithItsErrors()
    {
        using ScriptLoader loader = Loader();
        var announced = new System.Collections.Concurrent.ConcurrentQueue<LoadFailure>();
        loader.FailureUnclaimed += f => announced.Enqueue(f);
        UUID item = UUID.Random();

        loader.Post(new LoadRequest { ItemID = item, LocalID = 1, Source = "--!slua\nlocal x = = 1\n" });

        var until = DateTime.UtcNow.AddSeconds(10);
        while (announced.IsEmpty && DateTime.UtcNow < until)
            System.Threading.Thread.Sleep(20);
        LoadFailure failure = Assert.Single(announced);
        Assert.Equal(item, failure.ItemID);
        Assert.Equal("(1,0) Error: bad", Assert.Single(failure.Errors));
    }

    [Fact]
    public void AFailureAnEditorCollectsIsNotAnnounced()
    {
        using ScriptLoader loader = Loader();
        int announced = 0;
        loader.FailureUnclaimed += _ => System.Threading.Interlocked.Increment(ref announced);
        UUID item = UUID.Random();

        loader.Post(new LoadRequest { ItemID = item, LocalID = 1, Source = "--!slua\nlocal x = = 1\n" });
        Assert.Equal("(1,0) Error: bad", Assert.Single(loader.WaitForCompileErrors(item, TimeSpan.FromSeconds(10))));

        System.Threading.Thread.Sleep(500); // a window ten times the grace, to show nothing is announced
        Assert.Equal(0, announced);
    }
}
