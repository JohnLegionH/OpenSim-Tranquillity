/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Another script engine in the region, as the script functions see one: a script module that is also an
/// <see cref="IScriptEngine"/>. It records the events the SLua engine's script functions offer it, and holds the
/// script items it claims to run so a test can say which scripts it owns.
/// </summary>
internal sealed class FakeOtherEngine : IScriptModule, IScriptEngine
{
    private readonly List<(UUID Item, EventParams Params)> m_scriptPosts = new();
    private readonly List<(uint Local, EventParams Params)> m_objectPosts = new();

    public (UUID Item, EventParams Params)[] ScriptPosts { get { lock (m_scriptPosts) return m_scriptPosts.ToArray(); } }
    public (uint Local, EventParams Params)[] ObjectPosts { get { lock (m_objectPosts) return m_objectPosts.ToArray(); } }

    public bool PostScriptEvent(UUID itemID, EventParams parms) { lock (m_scriptPosts) m_scriptPosts.Add((itemID, parms)); return true; }
    public bool PostObjectEvent(uint localID, EventParams parms) { lock (m_objectPosts) m_objectPosts.Add((localID, parms)); return true; }
    public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value) => false;

    public string Name => "Fake Other Engine";
    public string ScriptEngineName => "Fake Other Engine";
    public Type ReplaceableInterface => null;
    public Scene World { get; private set; }
    public IScriptModule ScriptModule => this;
    public IConfig Config => null;
    public IConfigSource ConfigSource => null;
    public string ScriptEnginePath => string.Empty;
    public string ScriptClassName => string.Empty;
    public string ScriptBaseClassName => string.Empty;
    public string[] ScriptReferencedAssemblies => Array.Empty<string>();
    public ParameterInfo[] ScriptBaseClassParameters => null;

    public event ScriptRemoved OnScriptRemoved { add { } remove { } }
    public event ObjectRemoved OnObjectRemoved { add { } remove { } }

    public void Initialise(IConfigSource source) { }
    public void Close() { }
    public void AddRegion(Scene scene) { World = scene; }
    public void RemoveRegion(Scene scene) { }
    public void RegionLoaded(Scene scene) { }

    public bool PostScriptEvent(UUID itemID, string name, object[] args) => PostScriptEvent(itemID, new EventParams(name, args, null));
    public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
    public bool SuspendScript(UUID itemID) => false;
    public bool ResumeScript(UUID itemID) => false;
    public string GetXMLState(UUID itemID) => string.Empty;
    public bool SetXMLState(UUID itemID, string xml) => false;
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
        totaltime = 0;
        totalmemory = 0;
        return new List<ScriptTopStatsData>();
    }

    public IScriptWorkItem QueueEventHandler(object parms) => null;
    public void CancelScriptEvent(UUID itemID, string eventName) { }
    public DetectParams GetDetectParams(UUID item, int number) => null;
    public void SetMinEventDelay(UUID itemID, double delay) { }
    public int GetStartParameter(UUID itemID) => 0;
    public void SetScriptState(UUID itemID, bool state, bool self) { }
    public void SetState(UUID itemID, string newState) { }
    public void ApiResetScript(UUID itemID) { }
    public void ResetScript(UUID itemID) { }
    public IScriptApi GetApi(UUID itemID, string name) => null;
    public void SleepScript(UUID itemID, int delay) { }
}
