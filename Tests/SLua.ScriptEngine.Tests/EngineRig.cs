/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using Xunit;
using System.Globalization;
using SLua.ScriptEngine.Bindings;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace SLua.ScriptEngine.Tests;

/// <summary>Records what the engine tells a script's owner.</summary>
internal sealed class RecordingMessages : IScriptMessages
{
    private readonly List<(uint Local, UUID Item, string Text)> m_shouts = new();
    private readonly List<(uint Local, UUID Item, string[] Errors)> m_failures = new();

    public (uint Local, UUID Item, string Text)[] Shouts { get { lock (m_shouts) return m_shouts.ToArray(); } }
    public (uint Local, UUID Item, string[] Errors)[] Failures { get { lock (m_failures) return m_failures.ToArray(); } }

    public void ShoutError(uint localId, UUID itemId, string text) { lock (m_shouts) m_shouts.Add((localId, itemId, text)); }

    public void CompileFailed(uint localId, UUID itemId, IReadOnlyList<string> errors)
    {
        lock (m_failures) m_failures.Add((localId, itemId, errors.ToArray()));
    }
}

/// <summary>
/// The engine with the real loader, the real scheduler on real threads and the real library, in a test scene: a rez
/// goes in, host functions that count and record come out. The messages to the owner go to a recorder.
/// </summary>
internal sealed class EngineRig : IDisposable
{
    public static readonly UUID Owner = UUID.Random();

    public readonly Scene Scene = new SceneHelpers().SetupScene();
    public readonly RecordingLog Log = new();
    public readonly RecordingMessages Messages = new();
    public readonly ConcurrentDictionary<long, long> Counts = new();
    public readonly SceneObjectGroup Object;
    public readonly LuauEngine Engine;
    public ScriptLoader Loader;
    public LuauScheduler Scheduler;

    private readonly List<string> m_marks = new();

    public EngineRig(Dictionary<string, string> settings = null, Action<SchedulerOptions> tweak = null, double graceMs = 100, NativeCompile compile = null)
    {
        RealLibrary.Load();
        Engine = new LuauEngine(Log, (s, l) => true, null,
            (s, l, scene) => Loader = new ScriptLoader(new ScriptCompiler(null, compile ?? Host.Compile, l, null), new FakeAssets(), l, ownsCompiler: true)
            {
                UnclaimedGrace = TimeSpan.FromMilliseconds(graceMs),
            },
            options =>
            {
                options.VmFactory = MakeVm;
                tweak?.Invoke(options);
                return Scheduler = new LuauScheduler(options);
            },
            Messages);
        var source = new IniConfigSource();
        IConfig config = source.AddConfig(EngineIdentity.ConfigSection);
        config.Set("Enabled", "true");
        if (settings != null)
            foreach (var kv in settings) config.Set(kv.Key, kv.Value);
        Engine.Initialise(source);
        Object = SceneHelpers.AddSceneObject(Scene, "Example Object", Owner);
        Engine.AddRegion(Scene);
    }

    public uint LocalId => Object.RootPart.LocalId;

    private Vm MakeVm()
    {
        var vm = new Vm(Watchdog.Default);
        vm.Register("ossl", "Count", (tag, _) => { Counts.AddOrUpdate(tag, 1, (_, n) => n + 1); return null; });
        vm.Register("ossl", "Mark", (tag, a) => { lock (m_marks) m_marks.Add($"{tag}:{a[0].S}"); return null; });
        vm.Register("ll", "OwnerSay", (tag, a) => { lock (m_marks) m_marks.Add($"{tag}:{a[0].S}"); return null; });
        vm.Register("ossl", "Burn", (tag, a) =>
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(a[0].N / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < end) { }
            return null;
        });
        return vm;
    }

    /// <summary>The marks a script recorded through ossl.Mark.</summary>
    public string[] MarksOf(UUID item)
    {
        SchedulerScript s = Engine.SchedulerScriptFor(item);
        if (s == null) return Array.Empty<string>();
        lock (m_marks) return m_marks.Where(m => m.StartsWith(s.Id + ":")).Select(m => m.Substring(m.IndexOf(':') + 1)).ToArray();
    }

    public long CountOf(UUID item)
    {
        SchedulerScript s = Engine.SchedulerScriptFor(item);
        return s != null && Counts.TryGetValue(s.Id, out long n) ? n : 0;
    }

    public static string Slua(string body) => "//SLua:slua\n" + body;

    public static string Lsl(string body) => "//SLua:lsl\n" + body;

    /// <summary>Adds a script item to the object and tells the region it was rezzed; the Running flag is the item's.</summary>
    public UUID Rez(string source, bool running = true, UUID? item = null, int startParam = 0, bool postOnRez = false, int stateSource = 0)
    {
        UUID id = item ?? UUID.Random();
        TaskInventoryItem existing = Object.RootPart.Inventory.GetInventoryItem(id);
        if (existing == null)
        {
            Object.RootPart.Inventory.AddInventoryItem(new TaskInventoryItem
            {
                ItemID = id,
                AssetID = UUID.Random(),
                Name = "Example Script " + id.ToString().Substring(0, 8),
                OwnerID = Owner,
                Type = (int)OpenMetaverse.AssetType.LSLText,
                InvType = (int)OpenMetaverse.InventoryType.LSL,
                ScriptRunning = running,
            }, false);
        }
        Scene.EventManager.TriggerRezScript(LocalId, id, source, startParam, postOnRez, "InWorldz.Phlox", stateSource);
        return id;
    }

    public TaskInventoryItem ItemOf(UUID item) => Object.RootPart.Inventory.GetInventoryItem(item);

    /// <summary>Waits until the script is made and in the scheduler.</summary>
    public SchedulerScript WaitScheduled(UUID item)
    {
        Assert.True(SchedulerHarness.WaitFor(() => Engine.SchedulerScriptFor(item) != null), "the script reached the scheduler");
        return Engine.SchedulerScriptFor(item);
    }

    public void Dispose() => Engine.RemoveRegion(Scene);
}
