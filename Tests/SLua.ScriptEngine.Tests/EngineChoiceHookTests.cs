/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The region's engine-choice hooks, as Phlox implements them (PhloxEngine.OnRezScript, DiscardSavedScriptState):
/// a choice saved with the item (a viewer's Luau target or a saved engine, ScriptCompileTargets.CarriesChoice) places the
/// script ahead of its first line, so the engine the region chose runs it and no other; a change of engine drops the state
/// this engine saved, so an old copy cannot come back; and a viewer's Lua compile target ("luau", "lsl-luau") is this
/// engine's (IScriptEngineSelection.AcceptsCompileTarget), compiled as the language the target names.
/// </summary>
public class EngineChoiceHookTests
{
    private const string Phlox = "InWorldz.Phlox", Ours = "SLua";

    /// <summary>Another engine loaded in the region, so a first line can name it.</summary>
    private sealed class OtherEngine : MockScriptEngine, IScriptModule
    {
        public OtherEngine(string name) { ScriptEngineName = name; }
        public new string ScriptEngineName { get; }
    }

    private sealed class Rig
    {
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly RecordingLog Log = new();
        public readonly SLuaEngine Engine;
        public readonly SceneObjectPart Part;
        public readonly RecordingLoader Loader = new();

        public Rig(ISavedStateStore store = null, bool enabled = true)
        {
            Scene.StackModuleInterface<IScriptModule>(new OtherEngine(Phlox));
            Engine = new SLuaEngine(Log, (settings, log) => true, store, (settings, log, scene) => Loader);
            var source = new IniConfigSource();
            IConfig slua = source.AddConfig(EngineIdentity.ConfigSection);
            slua.Set("Enabled", enabled ? "true" : "false");
            slua.Set("WarmUp", "false"); // the API warm-up is process-wide and not under test here (see WarmUpIsolationTests)
            Engine.Initialise(source);
            Engine.AddRegion(Scene);
            Part = SceneHelpers.AddSceneObject(Scene, "Example Object", UUID.Random()).RootPart;
        }

        public UUID Add(uint flags, UUID? item = null, UUID? asset = null)
        {
            UUID id = item ?? UUID.Random();
            Part.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = id,
                AssetID = asset ?? UUID.Random(),
                Name = "Example Script " + id.ToString().Substring(0, 8),
                Type = (int)AssetType.LSLText,
                InvType = (int)InventoryType.LSL,
                Flags = flags,
                ScriptRunning = true,
            }, false);
            return id;
        }

        public void Rez(UUID item, string script, string selectedEngine) =>
            Scene.EventManager.TriggerRezScript(Part.LocalId, item, script, 0, false, selectedEngine, 0);
    }

    /// <summary>A store with no rows that notes what the engine asks of it.</summary>
    private sealed class RecordingStore : ISavedStateStore
    {
        public readonly List<string> Calls = new();
        public bool HasSavedState(UUID itemID, UUID assetID) => false;
        public void Delete(UUID itemID) { lock (Calls) Calls.Add("delete " + itemID); }
        public void Discard(UUID itemID) { lock (Calls) Calls.Add("discard " + itemID); }
        public void Loading(UUID itemID) { lock (Calls) Calls.Add("loading " + itemID); }
    }

    private static uint Chose(int engine) => ScriptEngineChoices.WithChoice(0, engine);
    private static uint Target(ScriptCompileTarget target) => ScriptCompileTargets.WithTarget(0, target);

    // ---- CarriesChoice: a saved choice places the script ahead of its first line ----

    [Fact]
    public void AScriptWithOurHeaderWhoseSavedEngineIsPhloxIsNotOurs()
    {
        var rig = new Rig();
        UUID item = rig.Add(Chose(ScriptEngineChoices.Phlox));
        rig.Rez(item, "//SLua:LSL\ndefault { state_entry() {} }", Phlox);
        Assert.False(rig.Engine.IsTracking(item));
        Assert.False(rig.Engine.HasScript(item, out _));
    }

    [Fact]
    public void AScriptWithOurHeaderThatWeRanIsLetGoWhenTheRegionRestartsItUnderASavedPhloxChoice()
    {
        var rig = new Rig();
        UUID item = rig.Add(0);
        rig.Rez(item, "//SLua:LSL\ndefault { state_entry() {} }", Phlox);
        Assert.True(rig.Engine.IsTracking(item));

        // osSetScriptEngine("...", "Phlox"): the region saves the choice and starts the script again with Phlox chosen
        rig.Part.Inventory.GetInventoryItem(item).Flags = Chose(ScriptEngineChoices.Phlox);
        rig.Rez(item, "//SLua:LSL\ndefault { state_entry() {} }", Phlox);
        Assert.False(rig.Engine.IsTracking(item));
    }

    [Fact]
    public void ALuaTargetTheRegionGaveUsIsOursWhateverItsFirstLineNames()
    {
        var rig = new Rig();
        UUID item = rig.Add(Target(ScriptCompileTarget.Luau));
        rig.Rez(item, "//InWorldz.Phlox:\nprint(1)", Ours);
        Assert.True(rig.Engine.IsTracking(item));
    }

    [Fact]
    public void ALuaTargetTheRegionGaveAnotherEngineIsNotOursEvenWithOurHeader()
    {
        var rig = new Rig();
        UUID item = rig.Add(Target(ScriptCompileTarget.LslOnLuau));
        rig.Rez(item, "//SLua:LSL\ndefault { state_entry() {} }", Phlox);
        Assert.False(rig.Engine.IsTracking(item));
    }

    [Fact]
    public void AScriptWithNoSavedChoiceIsStillPlacedByItsHeader()
    {
        var rig = new Rig();
        UUID item = rig.Add(0);
        rig.Rez(item, "//SLua:LSL\ndefault { state_entry() {} }", Phlox);
        Assert.True(rig.Engine.IsTracking(item));
    }

    // ---- DiscardSavedScriptState: a change of engine drops this engine's saved state ----

    [Fact]
    public void DiscardingTheStateOfAScriptDeletesItsRowAndItIsNoLongerReported()
    {
        UUID item = UUID.Random(), asset = UUID.Random();
        var store = new RowStore(item, asset);
        var rig = new Rig(store);
        Assert.True(rig.Engine.HasSavedScriptState(item, asset));

        ((IScriptStateProvider)rig.Engine).DiscardSavedScriptState(item);

        Assert.Equal(1, store.Deletes);
        Assert.False(rig.Engine.HasSavedScriptState(item, asset));
    }

    [Fact]
    public void ASwitchAwayAsksTheStoreToDiscardTheItemNotOnlyToDeleteItsRow()
    {
        var store = new RecordingStore();
        var rig = new Rig(store);
        UUID item = UUID.Random();
        ((IScriptStateProvider)rig.Engine).DiscardSavedScriptState(item);
        Assert.Equal(new[] { "discard " + item }, store.Calls);
    }

    [Fact]
    public void TheStoreIsToldWhenTheEngineTakesAnItemAgainAndNotWhenAnotherEngineDoes()
    {
        var store = new RecordingStore();
        var rig = new Rig(store);
        UUID ours = rig.Add(0), theirs = rig.Add(Chose(ScriptEngineChoices.Phlox));
        rig.Rez(ours, "//SLua:LSL\ndefault { state_entry() {} }", Phlox);
        rig.Rez(theirs, "//SLua:LSL\ndefault { state_entry() {} }", Phlox);
        Assert.Contains("loading " + ours, store.Calls);
        Assert.DoesNotContain("loading " + theirs, store.Calls);
    }

    [Fact]
    public void ASaveOfADiscardedItemIsDroppedUntilTheItemLoadsHereAgain()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(System.IO.Path.Combine(dir.Path, "state.db"), "SLua");
        store.Save(item, asset, new byte[] { 1 });
        Assert.True(store.Flush());

        store.Discard(item);
        store.Save(item, asset, new byte[] { 2 });   // a timed save of the copy from before the switch, made after the discard
        Assert.False(store.HasSavedState(item, asset));
        Assert.True(store.Flush());
        Assert.False(store.HasSavedState(item, asset));
        Assert.Null(store.Load(item, asset));

        store.Loading(item);
        store.Save(item, asset, new byte[] { 3 });
        Assert.True(store.Flush());
        Assert.Equal(new byte[] { 3 }, store.Load(item, asset));
    }

    [Fact]
    public void AfterADiscardTheStoreReportsNoStateWhileTheRemovalIsStillBeingWritten()
    {
        using var dir = new TempFolder();
        string db = System.IO.Path.Combine(dir.Path, "state.db");
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(db, "SLua");
        store.Save(item, asset, new byte[] { 1, 2, 3 });
        Assert.True(store.Flush());
        Assert.True(store.HasSavedState(item, asset));

        // Hold the writer: every store's writes take this lock, so the removal queued below cannot be written yet.
        object writerLock = typeof(SqliteStateStore).GetField("s_writerLock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        lock (writerLock)
        {
            store.Delete(item);
            Assert.False(store.HasSavedState(item, asset));
            Assert.False(store.HasSavedState(item, UUID.Zero));
            Assert.Null(store.Load(item, asset));
            Assert.Null(store.Load(item, UUID.Zero));
        }
        Assert.True(store.Flush());
        Assert.False(store.HasSavedState(item, asset));
    }

    [Fact]
    public void AStateSavedAfterADiscardIsReportedAgain()
    {
        using var dir = new TempFolder();
        string db = System.IO.Path.Combine(dir.Path, "state.db");
        UUID item = UUID.Random(), asset = UUID.Random();
        using var store = new SqliteStateStore(db, "SLua");
        store.Save(item, asset, new byte[] { 1 });
        store.Delete(item);
        store.Save(item, asset, new byte[] { 2 });
        Assert.True(store.HasSavedState(item, asset));
        Assert.True(store.Flush());
        Assert.Equal(new byte[] { 2 }, store.Load(item, asset));
    }

    // ---- AcceptsCompileTarget: a viewer's Lua compile target picks this engine ----

    [Theory]
    [InlineData(ScriptCompileTarget.Luau, true)]
    [InlineData(ScriptCompileTarget.LslOnLuau, true)]
    [InlineData(ScriptCompileTarget.Unspecified, false)]
    public void TheEngineTakesBothLuaCompileTargets(ScriptCompileTarget target, bool accepted)
    {
        var rig = new Rig();
        Assert.Equal(accepted, ((IScriptEngineSelection)rig.Engine).AcceptsCompileTarget(target));
    }

    [Fact]
    public void AnEngineThatIsNotEnabledTakesNoCompileTarget()
    {
        var rig = new Rig(enabled: false);
        Assert.False(((IScriptEngineSelection)rig.Engine).AcceptsCompileTarget(ScriptCompileTarget.Luau));
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau)]
    [InlineData(ScriptCompileTarget.Unspecified)]
    public void TheItemsCompileTargetReachesTheLoader(ScriptCompileTarget target)
    {
        var rig = new Rig();
        UUID item = rig.Add(Target(target));
        rig.Rez(item, "default { state_entry() {} }", Ours);
        LoadRequest posted;
        lock (rig.Loader.Posted) posted = rig.Loader.Posted.Last(r => r.ItemID == item);
        Assert.Equal(target, posted.Target);
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau)]
    public void TheRegionGivesAScriptWithALuaTargetToThisEngine(ScriptCompileTarget target)
    {
        var rig = new Rig();
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random(), Flags = Target(target) };
        Assert.Equal(Ours, rig.Scene.ResolveScriptEngine(Phlox, "default { state_entry() {} }", item)?.ScriptEngineName);
    }
}

/// <summary>The language a compile target names decides how the script is compiled, whatever its text looks like.</summary>
[Collection("native-library")]
public class CompileTargetLanguageTests
{
    private static CompileResult Compile(string source, ScriptCompileTarget target)
    {
        RealLibrary.Load();
        return new ScriptCompiler().Compile(source, target);
    }

    [RequiresNativeFact]
    public void ALuaTargetCompilesTextWithoutAMarkerAsSLua()
    {
        CompileResult r = Compile("local n = 1\nll.Say(0, tostring(n))\n", ScriptCompileTarget.Luau);
        Assert.True(r.Success, string.Join(" | ", r.EditorErrors()));
        Assert.Equal(ScriptLanguage.Slua, r.Language);
    }

    [RequiresNativeFact]
    public void ALuaTargetBlanksAFirstLineThatNamesAnotherEngine()
    {
        CompileResult r = Compile("//InWorldz.Phlox:\nll.Say(0, 'x')\n", ScriptCompileTarget.Luau);
        Assert.True(r.Success, string.Join(" | ", r.EditorErrors()));
        Assert.Equal(ScriptLanguage.Slua, r.Language);
    }

    [RequiresNativeFact]
    public void AnLslOnLuaTargetCompilesAsLsl()
    {
        CompileResult r = Compile("default\n{\n    state_entry()\n    {\n        llSay(0, \"hello\");\n    }\n}\n", ScriptCompileTarget.LslOnLuau);
        Assert.True(r.Success, string.Join(" | ", r.EditorErrors()));
        Assert.Equal(ScriptLanguage.Lsl, r.Language);
    }

    [RequiresNativeFact]
    public void NoTargetLeavesTheLanguageToTheText()
    {
        Assert.Equal(ScriptLanguage.Lsl, Compile("default\n{\n    state_entry()\n    {\n    }\n}\n", ScriptCompileTarget.Unspecified).Language);
    }
}
