/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using InWorldz.Phlox.Glue;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Phlox.ScriptEngine;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The functions that act on another script of the same prim by name work on a script whatever engine runs it, as in SL, where
/// llResetOtherScript (https://wiki.secondlife.com/wiki/LlResetOtherScript), llSetScriptState
/// (https://wiki.secondlife.com/wiki/LlSetScriptState) and llGetScriptState (https://wiki.secondlife.com/wiki/LlGetScriptState)
/// do not depend on how the named script was compiled; osResetAllScripts resets every other script of the prim.
/// Both directions, with the region's script functions (LSLSystemAPI) the engines share:
/// an SLua script calls the function on a script another engine runs (the stand-in engine holds the item and records what it is
/// asked), and a script of another engine calls it on an SLua script (the function host answers as an engine that does not run
/// the item, and the real SLua engine runs the target).
/// </summary>
[Collection("native-library")]
public class CrossEngineScriptControlTests
{
    private static readonly string[] NoEvents = Array.Empty<string>();

    /// <summary>Adds a script item called <paramref name="name"/> to the rig's object, in the Running state given.</summary>
    private static TaskInventoryItem AddItem(EngineRig rig, string name, bool running = true)
    {
        var item = new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = UUID.Random(),
            Name = name,
            OwnerID = EngineRig.Owner,
            Type = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            ScriptRunning = running,
        };
        rig.Object.RootPart.Inventory.AddInventoryItem(item, false);
        return item;
    }

    private static FakeOtherEngine OtherEngineHolding(EngineRig rig, UUID item, bool running = true)
    {
        var other = new FakeOtherEngine();
        rig.Scene.StackModuleInterface<IScriptModule>(other);
        other.AddRegion(rig.Scene);
        other.Hold(item, running);
        return other;
    }

    // ---- an SLua script on a script another engine runs ----

    [RequiresNativeFact]
    public void AnSLuaScriptsResetOtherScriptResetsAScriptAnotherEngineRuns()
    {
        using var rig = new EngineRig(productionFunctions: true);
        TaskInventoryItem victim = AddItem(rig, "victim");
        FakeOtherEngine other = OtherEngineHolding(rig, victim.ItemID);

        rig.Rez(EngineRig.Slua("ll.ResetOtherScript('victim')"));

        Assert.True(SchedulerHarness.WaitFor(() => other.Resets.Contains(victim.ItemID)), "the engine that runs the script was not asked to reset it");
        Assert.Single(other.Resets);
    }

    [RequiresNativeFact]
    public void AnSLuaScriptsSetScriptStateStopsAndStartsAScriptAnotherEngineRuns()
    {
        using var rig = new EngineRig(productionFunctions: true);
        TaskInventoryItem victim = AddItem(rig, "victim");
        FakeOtherEngine other = OtherEngineHolding(rig, victim.ItemID);

        UUID caller = rig.Rez(EngineRig.Slua("LLEvents:on('changed', function(c) ll.SetScriptState('victim', c) end)\nossl.Mark('ready')\n"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(caller).Contains("ready")));
        rig.Engine.PostScriptEvent(caller, "changed", new object[] { 0 });
        Assert.True(SchedulerHarness.WaitFor(() => other.RunStates.Contains((victim.ItemID, false))), "no stop reached the other engine");
        rig.Engine.PostScriptEvent(caller, "changed", new object[] { 1 });
        Assert.True(SchedulerHarness.WaitFor(() => other.RunStates.Contains((victim.ItemID, true))), "no start reached the other engine");
    }

    [RequiresNativeFact]
    public void AnSLuaScriptsGetScriptStateAsksTheEngineThatRunsTheScript()
    {
        using var rig = new EngineRig(productionFunctions: true);
        // The engine says the script runs; the item's flag still says it does not (a fault stops a script without the flag).
        TaskInventoryItem victim = AddItem(rig, "victim", running: false);
        OtherEngineHolding(rig, victim.ItemID, running: true);

        UUID caller = rig.Rez(EngineRig.Slua("ossl.Mark('state=' .. tostring(ll.GetScriptState('victim')))"));

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(caller).Any(m => m.StartsWith("state="))), "the script never answered");
        Assert.Equal("state=true", rig.MarksOf(caller).Single(m => m.StartsWith("state=")));
    }

    [RequiresNativeFact]
    public void AnSLuaScriptsResetAllScriptsResetsAScriptAnotherEngineRuns()
    {
        using var rig = new EngineRig(productionFunctions: true);
        TaskInventoryItem victim = AddItem(rig, "victim");
        FakeOtherEngine other = OtherEngineHolding(rig, victim.ItemID);

        UUID caller = rig.Rez(EngineRig.Slua("LLEvents:on('changed', function(c) ossl.ResetAllScripts(0) end)\nossl.Mark('ready')\n"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(caller).Contains("ready")));
        rig.Engine.PostScriptEvent(caller, "changed", new object[] { 1 });

        Assert.True(SchedulerHarness.WaitFor(() => other.Resets.Contains(victim.ItemID)), "the engine that runs the script was not asked to reset it");
    }

    // ---- a script of another engine on an SLua script ----

    /// <summary>
    /// The function host of an engine other than SLua: forwards everything to the SLua engine's host except what tells the
    /// script functions who runs a script. This engine runs nothing in the region, and its reset requests are noted.
    /// </summary>
    internal class OtherEnginesHost : DispatchProxy
    {
        private IScriptHostServices m_real;
        private FakeOtherEngine m_self;

        public static IScriptHostServices Create(IScriptHostServices real, FakeOtherEngine self)
        {
            IScriptHostServices host = DispatchProxy.Create<IScriptHostServices, OtherEnginesHost>();
            var proxy = (OtherEnginesHost)(object)host;
            proxy.m_real = real;
            proxy.m_self = self;
            return host;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "get_ScriptModule": return m_self;
                case "get_ScriptEngine": return m_self;
                case "HasScript": args[1] = false; return false;
                case "ApiResetScript": m_self.ApiResetScript((UUID)args[0]); return null;
                case "ResetScript": m_self.ResetScript((UUID)args[0]); return null;
                default:
                    try { return method.Invoke(m_real, args); }
                    catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
            }
        }
    }

    /// <summary>A region with the SLua engine running one script, and the script functions as a script of another engine has them.</summary>
    private sealed class OtherCaller : IDisposable
    {
        public readonly EngineRig Rig = new(productionFunctions: true);
        public readonly FakeOtherEngine Self = new();
        public readonly UUID Target;
        public readonly string TargetName;
        private readonly LSLSystemAPI m_api;

        public OtherCaller(string targetSource)
        {
            Rig.Scene.StackModuleInterface<IScriptModule>(Self);
            Self.AddRegion(Rig.Scene);
            Target = Rig.Rez(targetSource);
            TargetName = Rig.ItemOf(Target).Name;
            Assert.True(SchedulerHarness.WaitFor(() => Rig.MarksOf(Target).Contains("start")), "the target did not start");
            IScriptHostServices host = OtherEnginesHost.Create(Rig.Engine.HostServices, Self);
            m_api = new LSLSystemAPI(host, Rig.Object.RootPart, Rig.LocalId, UUID.Random())
            {
                RuntimeAccess = new EngineScriptAccess(new SchedulerScript(0, 0, new byte[0], ScriptLanguage.Slua)),
            };
        }

        public void Call(Action<LSLSystemAPI> call)
        {
            var ctx = new SyscallContext(UUID.Random(), SyscallContext.NextSeq());
            ctx.Enter();
            try { call(m_api); }
            finally { SyscallContext.Exit(); }
        }

        public int Starts => Rig.MarksOf(Target).Count(m => m == "start");

        public void Dispose() => Rig.Dispose();
    }

    private static readonly string Target = EngineRig.Slua("LLEvents:on('changed', function(c) ossl.Mark('changed=' .. c) end)\nossl.Mark('start')\n");

    [RequiresNativeFact]
    public void AScriptOfAnotherEnginesResetOtherScriptResetsAnSLuaScript()
    {
        using var c = new OtherCaller(Target);
        c.Call(api => api.llResetOtherScript(c.TargetName));
        Assert.True(SchedulerHarness.WaitFor(() => c.Starts == 2), "the SLua script was not reset: " + string.Join("|", c.Rig.MarksOf(c.Target)));
        Assert.Empty(c.Self.Resets);   // nothing was left to the caller's own engine, which does not run the script
    }

    [RequiresNativeFact]
    public void AScriptOfAnotherEnginesSetScriptStateStopsAndStartsAnSLuaScript()
    {
        using var c = new OtherCaller(Target);
        c.Call(api => api.llSetScriptState(c.TargetName, 0));
        Assert.True(SchedulerHarness.WaitFor(() => !c.Rig.Engine.GetScriptState(c.Target)), "the SLua script was not stopped");
        Assert.False(c.Rig.ItemOf(c.Target).ScriptRunning);
        c.Rig.Engine.PostScriptEvent(c.Target, "changed", new object[] { 1 });
        Thread.Sleep(500);   // a real wait: a stopped script handles nothing in the time a run would take
        Assert.DoesNotContain("changed=1", c.Rig.MarksOf(c.Target));

        c.Call(api => api.llSetScriptState(c.TargetName, 1));
        Assert.True(SchedulerHarness.WaitFor(() => c.Rig.Engine.GetScriptState(c.Target)), "the SLua script was not started");
        Assert.True(c.Rig.ItemOf(c.Target).ScriptRunning);
    }

    [RequiresNativeFact]
    public void AScriptOfAnotherEnginesGetScriptStateAsksTheSLuaEngine()
    {
        using var c = new OtherCaller(Target);
        int running = 0;
        c.Call(api => running = api.llGetScriptState(c.TargetName));
        Assert.Equal(1, running);

        // The engine holds the script (a fault, a pause) while the item's flag says it runs: the engine's answer wins.
        Assert.True(c.Rig.Engine.SuspendScript(c.Target));
        Assert.True(c.Rig.ItemOf(c.Target).ScriptRunning);
        c.Call(api => running = api.llGetScriptState(c.TargetName));
        Assert.Equal(0, running);
    }

    [RequiresNativeFact]
    public void AScriptOfAnotherEnginesResetAllScriptsResetsAnSLuaScript()
    {
        using var c = new OtherCaller(Target);
        c.Call(api => api.osResetAllScripts(0));
        Assert.True(SchedulerHarness.WaitFor(() => c.Starts == 2), "the SLua script was not reset: " + string.Join("|", c.Rig.MarksOf(c.Target)));
    }

    // ---- what already crosses ----

    [RequiresNativeFact]
    public void AScriptOfAnotherEnginesLlRemoveInventoryRemovesARunningSLuaScript()
    {
        using var c = new OtherCaller(Target);
        c.Call(api => api.llRemoveInventory(c.TargetName));
        Assert.True(SchedulerHarness.WaitFor(() => c.Rig.Engine.SchedulerScriptFor(c.Target) == null), "the SLua script still runs");
        Assert.Null(c.Rig.ItemOf(c.Target));
    }

    [RequiresNativeFact]
    public void AnSLuaScriptsLlRemoveInventoryRemovesAScriptAnotherEngineRuns()
    {
        using var rig = new EngineRig(productionFunctions: true);
        TaskInventoryItem victim = AddItem(rig, "victim");
        var other = new FakeOtherEngine();
        rig.Scene.StackModuleInterface<IScriptModule>(other);
        var removed = new List<UUID>();
        rig.Scene.EventManager.OnRemoveScript += (local, item) => { lock (removed) removed.Add(item); };

        rig.Rez(EngineRig.Slua("ll.RemoveInventory('victim')"));

        Assert.True(SchedulerHarness.WaitFor(() => { lock (removed) return removed.Contains(victim.ItemID); }), "the region did not tell the engines the script was removed");
        Assert.Null(rig.ItemOf(victim.ItemID));
    }
}
