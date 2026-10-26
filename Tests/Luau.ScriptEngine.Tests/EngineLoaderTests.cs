/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Luau.ScriptEngine.Bindings;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>A loader that records what the engine asks of it and answers from a table.</summary>
internal sealed class RecordingLoader : IScriptLoader
{
    public readonly List<LoadRequest> Posted = new();
    public readonly List<UUID> Disowned = new();
    public readonly Dictionary<UUID, List<string>> Answers = new();
    public bool Disposed;
    private long m_serial;

    public long Post(LoadRequest request) { lock (Posted) Posted.Add(request); return ++m_serial; }
    public void Disown(UUID itemID) { lock (Disowned) Disowned.Add(itemID); }
    public List<string> WaitForCompileErrors(UUID itemID, TimeSpan timeout) => Answers.TryGetValue(itemID, out var a) ? a : null;
    public bool IsLoading(UUID itemID) => false;
    public readonly HashSet<UUID> Ready = new();
    public bool IsReady(UUID itemID) => Ready.Contains(itemID);
    public void Dispose() => Disposed = true;
    public event Action<ReadyScript> ScriptReady;
    public event Action<LoadFailure> FailureUnclaimed;
    public void RaiseReady(ReadyScript ready) => ScriptReady?.Invoke(ready);
    public void RaiseFailure(LoadFailure failure) => FailureUnclaimed?.Invoke(failure);
}

/// <summary>What the engine does with a script it owns, with the loader replaced by a recorder.</summary>
public class EngineLoaderTests
{
    private const string Phlox = "InWorldz.Phlox", Ours = "NGC.Luau";
    private const uint LocalId = 7100;

    private sealed class Rig
    {
        public Scene Scene = new SceneHelpers().SetupScene();
        public RecordingLog Log = new();
        public RecordingLoader Loader = new();
        public LuauEngine Engine;
        public int FactoryCalls;

        public Rig(bool enabled = true)
        {
            Engine = new LuauEngine(Log, (settings, log) => true, null, (settings, log, scene) => { FactoryCalls++; return Loader; });
            var source = new IniConfigSource();
            source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", enabled ? "true" : "false");
            Engine.Initialise(source);
            Engine.AddRegion(Scene);
        }

        public void Rez(UUID item, string script, string selectedEngine) =>
            Scene.EventManager.TriggerRezScript(LocalId, item, script, 0, false, selectedEngine, 0);
    }

    [Fact]
    public void AnAcceptedScriptIsPostedToTheLoaderWithItsTextAndPrim()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:slua\nprint(1)", Phlox);
        LoadRequest r = Assert.Single(rig.Loader.Posted);
        Assert.Equal(item, r.ItemID);
        Assert.Equal(LocalId, r.LocalID);
        Assert.Equal("//NGC.Luau:slua\nprint(1)", r.Source);
    }

    [Fact]
    public void AScriptForAnotherEngineIsNeverPostedAndTheLoaderLetsGoOfIt()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "default { state_entry() {} }", Phlox);
        Assert.Empty(rig.Loader.Posted);
        Assert.Equal(new[] { item }, rig.Loader.Disowned);
    }

    [Fact]
    public void AScriptThatMovesToAnotherEngineIsDisownedInTheLoader()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:\ndefault {}", Phlox);
        rig.Rez(item, "//InWorldz.Phlox:\ndefault {}", Phlox);
        Assert.Single(rig.Loader.Posted);
        Assert.Contains(item, rig.Loader.Disowned);
    }

    [Fact]
    public void GetScriptErrorsAnswersFromTheLoaderInTheEditorsFormat()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:slua\nx = = 1", Phlox);
        rig.Loader.Answers[item] = new List<string> { "(2,0) Error: bad" };

        var errors = rig.Engine.GetScriptErrors(item);

        Assert.Equal(new object[] { "(2,0) Error: bad" }, errors.Cast<object>().ToArray());
    }

    [Fact]
    public void GetScriptErrorsAnswersNothingForAScriptThatIsNotOurs()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "default {}", Phlox);
        rig.Loader.Answers[item] = new List<string> { "(1,0) Error: should never be asked" };

        Assert.Empty(rig.Engine.GetScriptErrors(item));
        Assert.Empty(rig.Engine.GetScriptErrors(UUID.Random())); // an item never seen
    }

    [Fact]
    public void HasScriptAnswersTrueForAReadyScriptThatIsNotRunning()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:slua\nprint(1)", Phlox);
        rig.Loader.Ready.Add(item);

        Assert.True(rig.Engine.HasScript(item, out bool running));
        Assert.False(running); // compiled and held ready, not started
    }

    [Fact]
    public void HasScriptAnswersFalseWhileTheScriptIsStillLoading()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:slua\nprint(1)", Phlox);

        Assert.False(rig.Engine.HasScript(item, out bool running));
        Assert.False(running);
    }

    [Fact]
    public void HasScriptAnswersFalseForAScriptThatIsNotOurs()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "default {}", Phlox);
        rig.Loader.Ready.Add(item); // even if the loader holds something under the id

        Assert.False(rig.Engine.HasScript(item, out _));
        Assert.False(rig.Engine.HasScript(UUID.Random(), out _));
    }

    [Fact]
    public void ALoaderThatKnowsNothingOfTheItemGivesAnEmptyAnswer()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:slua\nprint(1)", Phlox); // the recorder answers null: no load of it
        Assert.Empty(rig.Engine.GetScriptErrors(item));
    }

    [Fact]
    public void RemovingTheRegionDisposesTheLoader()
    {
        var rig = new Rig();
        rig.Engine.RemoveRegion(rig.Scene);
        Assert.True(rig.Loader.Disposed);
    }

    [Fact]
    public void ADisabledEngineBuildsNoLoader()
    {
        var rig = new Rig(enabled: false);
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:slua\nprint(1)", Phlox);
        Assert.Equal(0, rig.FactoryCalls);
        Assert.Empty(rig.Loader.Posted);
    }
}

/// <summary>The engine with the real loader and the real library: a rez to GetScriptErrors, end to end.</summary>
[Collection("native-library")]
public class EngineErrorsTests
{
    private const string Phlox = "InWorldz.Phlox";
    private const uint LocalId = 7200;

    private sealed class Rig : IDisposable
    {
        public Scene Scene = new SceneHelpers().SetupScene();
        public LuauEngine Engine;
        public ScriptLoader Loader;

        public Rig()
        {
            RealLibrary.Load();
            var log = new RecordingLog();
            Engine = new LuauEngine(log, (settings, l) => true, null, (settings, l, scene) =>
                Loader = new ScriptLoader(new ScriptCompiler(), new FakeAssets(), l, 128 * 1024, () => new Vm(Watchdog.Resident)));
            var source = new IniConfigSource();
            source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", "true");
            Engine.Initialise(source);
            Engine.AddRegion(Scene);
        }

        public void Rez(UUID item, string script) =>
            Scene.EventManager.TriggerRezScript(LocalId, item, script, 0, false, Phlox, 0);

        public void Dispose() => Engine.RemoveRegion(Scene);
    }

    [RequiresNativeFact]
    public void ACompileErrorInOurScriptReachesTheCallerOfGetScriptErrors()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:slua\nlocal a = 1\nlocal b = = 2\n");
        string error = Assert.IsType<string>(Assert.Single(rig.Engine.GetScriptErrors(item).Cast<object>()));
        Assert.StartsWith("(3,0) Error: ", error);
    }

    [RequiresNativeFact]
    public void AGoodScriptGivesAnEmptyListAndIsHeldReady()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:\ndefault\n{\n    state_entry()\n    {\n        llSay(0, \"hi\");\n    }\n}\n");
        Assert.Empty(rig.Engine.GetScriptErrors(item));
        Assert.True(rig.Loader.IsReady(item));
    }

    [RequiresNativeFact]
    public void AnotherEnginesScriptGetsNothingFromUsEvenWithABadBodyAndWeNeverCompileIt()
    {
        using var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//InWorldz.Phlox:\nthis is not a script at all (((");
        Assert.Empty(rig.Engine.GetScriptErrors(item));
        Assert.False(rig.Loader.IsLoading(item));
        Assert.False(rig.Loader.IsReady(item));
    }
}
