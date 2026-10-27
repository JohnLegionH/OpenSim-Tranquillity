/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using InWorldz.Phlox.Glue;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The scheduler harness with the generated ll bindings registered in every VM and served by a real LSLSystemAPI built on the
/// engine's own host services, in a region with one object. Each script gets an API of its own (its item id, its script access).
/// </summary>
internal sealed class EngineApiRig : IDisposable
{
    public readonly Scene Scene;
    public readonly SceneObjectGroup Object;
    public readonly EngineHostServices Host;
    public readonly SchedulerHarness H;
    public readonly ConcurrentQueue<string> BackendCalls = new();

    private readonly ConcurrentDictionary<long, (LSLSystemAPI Api, UUID Item)> m_apis = new();

    private readonly ILSLSystemAPI m_fallback;

    /// <param name="fallback">Serves the functions the real API does not (the router sends them here); null runs every function on the real API.</param>
    public EngineApiRig(ILSLSystemAPI fallback, params (string Key, string Value)[] settings) : this(settings)
    {
        m_fallback = fallback;
    }

    public EngineApiRig(params (string Key, string Value)[] settings)
    {
        var source = new IniConfigSource();
        IConfig c = source.AddConfig(EngineIdentity.ConfigSection);
        c.Set("Enabled", "true");
        foreach ((string key, string value) in settings) c.Set(key, value);

        Scene = new SceneHelpers().SetupScene();
        Object = SceneHelpers.AddSceneObject(Scene, "Example Object", UUID.Random());
        Host = new EngineHostServices(SLuaSettings.Read(source, new RecordingLog()), Scene, source, null, new EngineHostBackend
        {
            SetTimer = (item, sec) => BackendCalls.Enqueue("SetTimer " + sec),
        });
        H = new SchedulerHarness(configure: o =>
        {
            Func<Bindings.Vm> inner = o.VmFactory;
            o.VmFactory = () =>
            {
                Bindings.Vm vm = inner();
                LlBindings.Register(vm, ContextOf);
                return vm;
            };
        });
    }

    private ScriptContext ContextOf(SchedulerScript script)
    {
        (LSLSystemAPI api, UUID item) = m_apis.GetOrAdd(script.Id, _ =>
        {
            UUID id = UUID.Random();
            return (new LSLSystemAPI(Host, Object.RootPart, Object.RootPart.LocalId, id) { RuntimeAccess = new EngineScriptAccess(script) }, id);
        });
        return new ScriptContext(script, m_fallback == null ? api : ApiRouter.Create(api, m_fallback), item);
    }

    /// <summary>A notecard item in the object, so the notecard calls find one by name.</summary>
    public void AddNotecard(string name)
    {
        Object.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = UUID.Random(),
            Name = name,
            Type = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard,
            OwnerID = Object.OwnerID,
            CreatorID = Object.OwnerID,
            ParentPartID = Object.RootPart.UUID,
        }, false);
    }

    /// <summary>
    /// The delay Phlox's own code asks for on a call: every ScriptSleep of the call added up inside a SyscallContext, the way
    /// Phlox reads it for a call that runs off the script's thread (SyscallContext, and the Phlox tests' ApiCallRig.Accounted).
    /// </summary>
    public int PhloxDelayMs(Func<LSLSystemAPI, object> call)
    {
        UUID item = UUID.Random();
        var api = new LSLSystemAPI(Host, Object.RootPart, Object.RootPart.LocalId, item)
        {
            RuntimeAccess = new EngineScriptAccess(new SchedulerScript(0, 0, new byte[0], ScriptLanguage.Slua)),
        };
        var ctx = new SyscallContext(item, SyscallContext.NextSeq());
        ctx.Enter();
        try { call(api); }
        finally { SyscallContext.Exit(); }
        return ctx.DelayMs;
    }

    /// <summary>
    /// Runs the script to its first sleep and returns how long it sleeps, in seconds on the scheduler's clock; 0 when it did not sleep.
    /// </summary>
    public double FirstSleep(string source, bool lsl = false)
    {
        SchedulerScript s = H.AddAndStart(source, lsl);
        H.Pump();
        return s.State == ScriptRunState.Sleeping ? s.SleepUntil - H.Clock.Now : 0;
    }

    public void Dispose() => H.Dispose();
}
