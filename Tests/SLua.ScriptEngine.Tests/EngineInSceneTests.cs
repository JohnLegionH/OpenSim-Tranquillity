/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The engine with scripts in real prims of a scene: a script deleted from its prim, an object taken out of the scene, a
/// script saved again, and a script item that carries the viewer's compile target. The loader is a recorder; the state
/// database is a real SQLite file.
/// </summary>
public class EngineInSceneTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TempFolder Dir = new();
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly RecordingLog Log = new();
        public readonly RecordingLoader Loader = new();
        public readonly SqliteStateStore Store;
        public readonly SLuaEngine Engine;

        public Rig()
        {
            Store = new SqliteStateStore(System.IO.Path.Combine(Dir.Path, "state", "script_state.db"), EngineIdentity.EngineName);
            Engine = new SLuaEngine(Log, (settings, log) => true, Store, (settings, log, scene) => Loader);
            var source = new IniConfigSource();
            source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", "true");
            Engine.Initialise(source);
            Engine.AddRegion(Scene);
        }

        /// <summary>An object in the scene with one SLua script in its root prim, rezzed to the engine, and a saved row for it.</summary>
        public (SceneObjectGroup Group, TaskInventoryItem Item) ObjectWithScript(uint flags = 0, string engine = "SLua")
        {
            SceneObjectGroup group = SceneHelpers.AddSceneObject(Scene);
            SceneObjectPart part = group.RootPart;
            const string Source = "--!slua\nprint(1)\n";
            TaskInventoryItem item = TaskInventoryHelpers.AddScript(Scene.AssetService, part, "script", Source);
            part.Inventory.GetInventoryItem(item.ItemID).Flags = flags; // the item the prim holds
            Scene.EventManager.TriggerRezScript(part.LocalId, item.ItemID, Source, 0, false, engine, 0);
            Store.Save(item.ItemID, item.AssetID, new ScriptStateRecord { Engine = EngineIdentity.EngineName }.Encode());
            Assert.True(Store.Flush());
            return (group, item);
        }

        public void Dispose()
        {
            Engine.RemoveRegion(Scene);
            Store.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public void DeletingAScriptFromAPrimInTheSceneTakesItsRowAndKeepsThePrimsInventoryLocked()
    {
        using var rig = new Rig();
        (SceneObjectGroup group, TaskInventoryItem item) = rig.ObjectWithScript();
        SceneObjectPart part = group.RootPart;

        // A handler after the engine's checks, from another thread, that the prim's inventory is still locked by the removal
        // (TaskInventoryDictionary gives up a write lock when the same thread asks it for a read lock).
        bool readerBlocked = false;
        rig.Scene.EventManager.OnRemoveScript += (localID, itemID) =>
        {
            var reader = Task.Run(() => part.Inventory.GetInventoryItem(UUID.Random()));
            readerBlocked = !reader.Wait(TimeSpan.FromMilliseconds(300)); // a window in which the reader must stay blocked
        };

        Assert.Equal((int)InventoryType.LSL, part.Inventory.RemoveInventoryItem(item.ItemID));
        Assert.True(rig.Store.Flush());

        Assert.True(readerBlocked);
        Assert.False(rig.Store.HasSavedState(item.ItemID, item.AssetID));
    }

    [Fact]
    public void AScriptTakenOutWithItsObjectKeepsItsRowUntilTheObjectLeavesTheScene()
    {
        using var rig = new Rig();
        (SceneObjectGroup group, TaskInventoryItem item) = rig.ObjectWithScript();

        group.RootPart.Inventory.RemoveScriptInstances(true);
        Assert.True(rig.Store.Flush());
        Assert.True(rig.Store.HasSavedState(item.ItemID, item.AssetID));

        rig.Scene.DeleteSceneObject(group, false);
        Assert.True(rig.Store.Flush());
        Assert.False(rig.Store.HasSavedState(item.ItemID, item.AssetID));
    }

    [Fact]
    public void AScriptRezzedAgainAfterItWasTakenOutNoLongerWaitsForItsObject()
    {
        using var rig = new Rig();
        (SceneObjectGroup group, TaskInventoryItem item) = rig.ObjectWithScript();
        SceneObjectPart part = group.RootPart;

        // A save of the script: its instance goes, and the script is rezzed again in the same prim.
        part.Inventory.RemoveScriptInstance(item.ItemID, false);
        Assert.Equal(1, rig.Engine.ObjectsWaitingToLeave);
        rig.Scene.EventManager.TriggerRezScript(part.LocalId, item.ItemID, "--!slua\nprint(1)\n", 0, false, "SLua", 0);

        // The running script's row must not go when its object later leaves for any other reason than this script's removal.
        Assert.Equal(0, rig.Engine.ObjectsWaitingToLeave);
        Assert.True(rig.Store.HasSavedState(item.ItemID, item.AssetID));
    }

    [Theory]
    [InlineData(ScriptCompileTarget.Luau, (uint)ScriptCompileTarget.Luau)]
    [InlineData(ScriptCompileTarget.LslOnLuau, ScriptCompileTargets.LslOnLuauFlag)]
    public void AnItemWithTheViewersCompileTargetIsLoadedWithThatTarget(ScriptCompileTarget target, uint flags)
    {
        using var rig = new Rig();
        (_, TaskInventoryItem item) = rig.ObjectWithScript(flags);

        LoadRequest posted = Assert.Single(rig.Loader.Posted);
        Assert.Equal(item.ItemID, posted.ItemID);
        Assert.Equal(target, posted.Target);
        Assert.Equal(item.AssetID, posted.AssetID);
    }

    [Fact]
    public void AnItemWithACompileTargetThatTheRegionGaveToAnotherEngineIsLetGo()
    {
        using var rig = new Rig();
        (_, TaskInventoryItem item) = rig.ObjectWithScript((uint)ScriptCompileTarget.Luau, engine: "InWorldz.Phlox");

        Assert.Empty(rig.Loader.Posted);
        Assert.Contains(item.ItemID, rig.Loader.Disowned);
    }

    [Fact]
    public void TheLogSaysOnceThatScriptsDoNotRunYet()
    {
        using var rig = new Rig();
        Assert.Single(rig.Log.Warnings, l => l.Contains("does not run them yet"));
    }
}
