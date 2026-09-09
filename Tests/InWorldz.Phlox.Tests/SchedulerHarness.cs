using Xunit;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-2e. A whole script engine on a test scene, driven by hand: rez a script the way
/// <c>EventManager.OnRezScript</c> does, then pump <c>DoWork</c> instead of running the master
/// scheduler's thread, so a test is deterministic and cannot hang.
///
/// <para>
/// This is the harness PHLOX-2d named as the gap and could not build in the time it had. Everything
/// before it either compiled a script (<see cref="PhloxCompiler"/>) or asserted the scheduler's
/// source text; neither could see whether a script instance actually runs, which is precisely what
/// 1.1.275 and 1.1.277 both failed to do in world.
/// </para>
/// </summary>
public sealed class SchedulerHarness : IDisposable
{
    public TestScene Scene { get; }
    public PhloxEngine Engine { get; }
    public SceneObjectPart Prim { get; }

    private readonly object m_loader;
    private readonly object m_exe;

    public SchedulerHarness()
    {
        var config = new IniConfigSource();
        var phlox = config.AddConfig("InWorldz.Phlox");
        phlox.Set("Enabled", "true");
        var startup = config.AddConfig("Startup");
        startup.Set("DefaultScriptEngine", "InWorldz.Phlox");

        Scene = new SceneHelpers().SetupScene();

        Engine = new PhloxEngine();
        Engine.Initialise(config);
        Engine.AddRegion(Scene);
        // RegionLoaded needs an IWorldComm; the test scene has none, so one is registered first.
        if (Scene.RequestModuleInterface<IWorldComm>() is null)
            Scene.RegisterModuleInterface<IWorldComm>(NullWorldComm.Create());
        Engine.RegionLoaded(Scene);

        var sog = SceneHelpers.AddSceneObject(Scene, "Phlox test prim", UUID.Random());
        Prim = sog.RootPart;

        // llSay goes Scene.SimChat -> EventManager.OnChatFromWorld (Scene.PacketHandlers.cs:51-85),
        // so the real path is observed rather than a stub API injected into the engine - the engine
        // builds its own LSLSystemAPI inside FinishedLoading and takes no seam for one.
        Scene.EventManager.OnChatFromWorld += (sender, chat) =>
        {
            lock (m_said) m_said.Add(chat.Message ?? string.Empty);
        };

        m_loader = Field(Engine, "m_ScriptLoader");
        m_exe = Field(Engine, "m_ExeScheduler");
        Assert.NotNull(m_loader);
        Assert.NotNull(m_exe);

        // The master scheduler owns a thread; this harness drives DoWork itself instead, so stop it.
        StopMasterThread();
    }

    private static object Field(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private void StopMasterThread()
    {
        var ms = Field(Engine, "m_MasterScheduler");
        ms?.GetType().GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance)?.Invoke(ms, null);
    }

    /// <summary>
    /// Put a script in the prim's inventory and rez it exactly as <c>PhloxEngine.OnRezScript</c>
    /// does. The inventory item is real: <c>PhloxScriptLoader.FindAssetId</c> reads
    /// <c>Prim.Inventory.GetInventoryItem</c> and logs an error and drops the load without one
    /// (<c>PhloxScriptLoader.cs:301-307</c>), so a harness that skips this tests nothing.
    /// </summary>
    /// <summary>
    /// PHLOX-4: rez with BOTH ids pinned. A restore test has to stand a second engine up and rez the
    /// same item and asset, because StateManager.LoadState keys on the item id and discards the row
    /// when the asset id does not match.
    /// </summary>
    public UUID RezScript(string source, UUID assetId, UUID itemId)
    {
        var item = TaskInventoryHelpers.AddScript(
            Scene.AssetService, Prim, itemId, assetId, "script" + (++m_scriptSeq), source);
        var rez = Engine.GetType().GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rez);
        rez!.Invoke(Engine, new object[] { Prim.LocalId, item.ItemID, source, 0, false, Engine.Name, 0 });
        return item.ItemID;
    }

    /// <summary>PHLOX-4: the engine's StateManager, which is internal - reached by reflection.</summary>
    public object StateManagerOf() => Engine.GetType()
        .GetProperty("StateManager", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
        ?.GetValue(Engine);

    /// <summary>PHLOX-4: save this script the way shutdown does, through ScriptUnloaded.</summary>
    public void SaveState(UUID itemId)
    {
        var sm = StateManagerOf();
        Assert.NotNull(sm);
        var interp = InterpreterFor(itemId);
        Assert.NotNull(interp);
        sm.GetType().GetMethod("ScriptUnloaded")!.Invoke(sm, new[] { interp });
    }

    public UUID RezScript(string source, UUID assetId = default)
    {
        var item = TaskInventoryHelpers.AddScript(
            Scene.AssetService, Prim, UUID.Random(), assetId.IsZero() ? UUID.Random() : assetId,
            "script" + (++m_scriptSeq), source);

        var rez = Engine.GetType().GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rez);
        rez!.Invoke(Engine, new object[] { Prim.LocalId, item.ItemID, source, 0, false, Engine.Name, 0 });
        return item.ItemID;
    }

    private int m_scriptSeq;

    /// <summary>
    /// PROPS-1: add a real client to the scene so what the region SENDS can be asserted, not just
    /// what it stores on the part.
    /// </summary>
    public OpenSim.Tests.Common.TestClient AddClient()
    {
        var sp = SceneHelpers.AddScenePresence(Scene, OpenMetaverse.UUID.Random());
        return (OpenSim.Tests.Common.TestClient)sp.ControllingClient;
    }

    /// <summary>Where a load got to: queues, whether an interpreter exists, and what it said.</summary>
    public string Diagnose(UUID itemId)
    {
        int Count(object owner, string field)
        {
            var v = owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner);
            return v is System.Collections.ICollection c ? c.Count : -1;
        }
        var all = Field(m_exe, "m_AllScripts") as System.Collections.IDictionary;
        return $"pendingLoads={Count(m_loader, "m_PendingLoads")} " +
               $"waitingForCompile={Count(m_loader, "m_WaitingForCompile")} " +
               $"loadedScripts={Count(m_loader, "m_LoadedScripts")} " +
               $"allScripts={all?.Count ?? -1} " +
               $"interpreter={(InterpreterFor(itemId) is null ? "none" : "created")} " +
               $"runState={RunStateOf(itemId)} said=[{string.Join(",", Said)}] " +
               $"invItem={(Prim.Inventory.GetInventoryItem(itemId) is null ? "MISSING" : "present")}";
    }

    /// <summary>Pump both schedulers until neither has work, or the budget runs out.</summary>
    public void Pump(int rounds = 200)
    {
        var loaderDoWork = m_loader.GetType().GetMethod("DoWork");
        var exeDoWork = m_exe.GetType().GetMethod("DoWork");

        for (var i = 0; i < rounds; i++)
        {
            var l = loaderDoWork!.Invoke(m_loader, null);
            var e = exeDoWork!.Invoke(m_exe, null);
            if (!Pending(l) && !Pending(e)) { /* keep pumping a little; events can arrive late */ }
            System.Threading.Thread.Sleep(1);
        }
    }

    /// <summary>PHLOX-4: exactly one DoWork on each scheduler - one timeslice, no more.</summary>
    public void PumpOnce()
    {
        m_loader.GetType().GetMethod("DoWork")!.Invoke(m_loader, null);
        m_exe.GetType().GetMethod("DoWork")!.Invoke(m_exe, null);
    }

    /// <summary>
    /// PHLOX-4: put an event straight on the script's OWN queue (ScriptState.EventQueue), which is
    /// what a script that was interrupted mid-event has when it is saved - not the scheduler's
    /// pending list.
    /// </summary>
    public void QueueEventOnScriptState(UUID itemId)
    {
        var interp = InterpreterFor(itemId);
        Assert.NotNull(interp);
        var state = interp.GetType().GetProperty("ScriptState")!.GetValue(interp)!;
        var q = state.GetType().GetField("EventQueue")!.GetValue(state)!;
        var evt = new global::InWorldz.Phlox.VM.PostedEvent
        {
            EventType = global::InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START,
            Args = new object[] { 1 },
        };
        q.GetType().GetMethod("Add")!.Invoke(q, new object[] { evt });
    }

    /// <summary>Pump for a wall-clock duration, so timer cadence can be measured.</summary>
    public void PumpFor(TimeSpan how)
    {
        var loaderDoWork = m_loader.GetType().GetMethod("DoWork");
        var exeDoWork = m_exe.GetType().GetMethod("DoWork");
        var until = DateTime.UtcNow + how;
        while (DateTime.UtcNow < until)
        {
            loaderDoWork!.Invoke(m_loader, null);
            exeDoWork!.Invoke(m_exe, null);
            System.Threading.Thread.Sleep(1);
        }
    }

    private static bool Pending(object workStatus)
        => (bool)(workStatus.GetType().GetField("WorkIsPending")?.GetValue(workStatus)
                  ?? workStatus.GetType().GetProperty("WorkIsPending")?.GetValue(workStatus)
                  ?? false);

    private readonly List<string> m_said = new();

    /// <summary>Everything any script in this scene has said, in order.</summary>
    public IReadOnlyList<string> Said { get { lock (m_said) return m_said.ToArray(); } }

    /// <summary>Whether anything has been said since the last <see cref="ClearSaid"/>.</summary>
    public bool SaidAnything(UUID itemId) { lock (m_said) return m_said.Count > 0; }

    public void ClearSaid(UUID itemId) { lock (m_said) m_said.Clear(); }

    /// <summary>
    /// Touch the prim through the SCENE's own path - <c>EventManager.TriggerObjectGrab</c> into the
    /// engine's <c>OnObjectGrab</c> handler - rather than posting an event straight at the
    /// scheduler. This is the route that was silent in world, and the only one that exercises the
    /// part's event mask.
    /// </summary>
    public void TouchViaScene()
    {
        // A client is required: PhloxEngine.BuildTouchDetectParams reads remoteClient.AgentId
        // without a null check (PhloxEngine.cs:464), and in world there is always one.
        Scene.EventManager.TriggerObjectGrab(
            Prim.LocalId, Prim.LocalId, OpenMetaverse.Vector3.Zero, NullClient.Create(),
            new OpenSim.Framework.SurfaceTouchEventArgs());
    }

    /// <summary>Post a touch_start the way the region does when a resident touches the prim.</summary>
    public void PostTouch(UUID itemId)
    {
        // PhloxExecutionScheduler is internal, so the call is by reflection; the event type is not.
        var evt = new global::InWorldz.Phlox.VM.PostedEvent
        {
            EventType = global::InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START,
            Args = new object[] { 1 },
        };
        m_exe.GetType().GetMethod("PostEvent")!.Invoke(m_exe, new object[] { itemId, evt });
    }

    /// <summary>The live interpreter for an item, or null if the scheduler never created one.</summary>
    public object InterpreterFor(UUID itemId)
    {
        var all = Field(m_exe, "m_AllScripts") as System.Collections.IDictionary;
        return all != null && all.Contains(itemId) ? all[itemId] : null;
    }

    /// <summary>The item's RunState, as a string, or "(no interpreter)".</summary>
    public string RunStateOf(UUID itemId)
    {
        var interp = InterpreterFor(itemId);
        if (interp is null) return "(no interpreter)";
        var state = interp.GetType().GetProperty("ScriptState")?.GetValue(interp);
        if (state is null) return "(no state)";
        var t = state.GetType();
        var v = t.GetProperty("RunState")?.GetValue(state)
                ?? t.GetField("RunState", BindingFlags.Public | BindingFlags.Instance)?.GetValue(state);
        return v?.ToString() ?? "(no RunState)";
    }

    public void Dispose()
    {
        try { StopMasterThread(); } catch { }
        try { Engine.Close(); } catch { }
    }
}

/// <summary>
/// The test scene has no chat module. IWorldComm has a wide surface and none of it matters here, so
/// it is generated rather than written - the same technique the recording ISystemAPI uses.
/// </summary>
internal class NullWorldComm : System.Reflection.DispatchProxy
{
    public static IWorldComm Create() => Create<IWorldComm, NullWorldComm>();

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>A do-nothing IClientAPI, generated: the touch path needs one but reads almost nothing.</summary>
internal class NullClient : System.Reflection.DispatchProxy
{
    public static OpenSim.Framework.IClientAPI Create() => Create<OpenSim.Framework.IClientAPI, NullClient>();

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "get_Name") return "Test Toucher";
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
