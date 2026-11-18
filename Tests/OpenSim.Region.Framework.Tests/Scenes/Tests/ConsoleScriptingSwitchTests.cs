/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// While the console switch "debug scene set scripting false" is in effect, a script that is rezzed or added is not
/// started; switching it on again starts the scripts that were held back. The switch is a debug switch: it does not
/// write the saved region setting. The engine here only records the scripts it is asked to start.
/// No process-wide state: these run in parallel.
/// </summary>
public class ConsoleScriptingSwitchTests : OpenSimTestCase
{
    private TestScene m_scene;
    private RecordingEngine m_engine;

    private void Setup()
    {
        m_engine = new RecordingEngine();
        SceneHelpers sh = new();
        m_scene = sh.SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, new IniConfigSource(), m_engine);
    }

    private SceneObjectGroup NewObject(string name = "box") =>
        SceneHelpers.AddSceneObject(m_scene, 1, TestHelpers.ParseTail(1), name, 0x10);

    private TaskInventoryItem NewScript(SceneObjectGroup group, string name = "script") =>
        TaskInventoryHelpers.AddScript(m_scene.AssetService, group.RootPart, name, "default { state_entry() { } }");

    [Fact]
    public void AScriptAddedToAnObjectIsNotStartedWhileTheSwitchIsOff()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup box = NewObject();
        TaskInventoryItem script = NewScript(box);
        m_scene.ScriptsEnabled = false;

        bool started = box.RootPart.Inventory.CreateScriptInstance(script, 0, false, string.Empty, 0);

        Assert.False(started);
        Assert.Empty(m_engine.Started);
    }

    [Fact]
    public void TheScriptsOfARezzedObjectAreNotStartedWhileTheSwitchIsOff()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup box = NewObject();
        NewScript(box, "one");
        NewScript(box, "two");
        m_scene.ScriptsEnabled = false;

        int attempted = box.CreateScriptInstances(0, false, string.Empty, 0);

        Assert.Equal(0, attempted);
        Assert.Empty(m_engine.Started);
    }

    [Fact]
    public void SwitchingScriptsOnStartsTheScriptsThatCameInWhileTheyWereOff()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup box = NewObject();
        m_scene.ScriptsEnabled = false;
        TaskInventoryItem late = NewScript(box, "late");

        m_scene.ScriptsEnabled = true;

        Assert.Equal(new[] { late.ItemID }, m_engine.Started);
    }

    [Fact]
    public void ANewScriptStartsAgainOnceTheSwitchIsOn()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup box = NewObject();
        TaskInventoryItem script = NewScript(box);
        m_scene.ScriptsEnabled = false;
        m_scene.ScriptsEnabled = true;
        m_engine.Started.Clear();

        bool started = box.RootPart.Inventory.CreateScriptInstance(script, 0, false, string.Empty, 0);

        Assert.True(started);
        Assert.Equal(new[] { script.ItemID }, m_engine.Started);
    }

    [Fact]
    public void TheConsoleSwitchDoesNotChangeTheSavedRegionSetting()
    {
        TestHelpers.InMethod();
        Setup();
        Assert.False(m_scene.RegionInfo.RegionSettings.DisableScripts);

        m_scene.ScriptsEnabled = false;
        Assert.False(m_scene.ScriptsEnabled);
        Assert.False(m_scene.RegionInfo.RegionSettings.DisableScripts);

        m_scene.ScriptsEnabled = true;
        Assert.True(m_scene.ScriptsEnabled);
        Assert.False(m_scene.RegionInfo.RegionSettings.DisableScripts);
    }

    [Fact]
    public void ASavedDisableScriptsSettingStillStopsScriptsWithTheSwitchOn()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup box = NewObject();
        TaskInventoryItem script = NewScript(box);
        m_scene.RegionInfo.RegionSettings.DisableScripts = true;

        bool started = box.RootPart.Inventory.CreateScriptInstance(script, 0, false, string.Empty, 0);

        Assert.False(started);
        Assert.Empty(m_engine.Started);
    }

    /// <summary>An engine that records the scripts the scene asks it to start.</summary>
    private sealed class RecordingEngine : IScriptModule
    {
        public List<UUID> Started { get; } = new();

        public string Name => "RecordingEngine";
        public string ScriptEngineName => "RecordingEngine";
        public Type ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void Close() { }
        public void AddRegion(Scene scene)
        {
            scene.StackModuleInterface<IScriptModule>(this);
            scene.EventManager.OnRezScript += (localID, itemID, script, startParam, postOnRez, engine, stateSource) => Started.Add(itemID);
        }
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => true;
        public bool SuspendScript(UUID itemID) => false;
        public bool ResumeScript(UUID itemID) => false;
        public event ScriptRemoved OnScriptRemoved { add { } remove { } }
        public event ObjectRemoved OnObjectRemoved { add { } remove { } }
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public ArrayList GetScriptErrors(UUID itemID) => new();
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public bool GetScriptState(UUID itemID) => false;
        public void SaveAllState() { }
        public void StartProcessing() { }
        public float GetScriptExecutionTime(List<UUID> itemIDs) => 0f;
        public int GetScriptsMemory(List<UUID> itemIDs) => 0;
        public Dictionary<uint, float> GetObjectScriptsExecutionTimes() => new();
        public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out float totaltime, out float totalmemory)
        {
            totaltime = 0f;
            totalmemory = 0f;
            return new List<ScriptTopStatsData>();
        }
    }
}
