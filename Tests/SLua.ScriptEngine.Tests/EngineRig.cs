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
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
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
    public readonly SLuaEngine Engine;
    public ScriptLoader Loader;
    public SLuaScheduler Scheduler;

    private readonly List<string> m_marks = new();
    private readonly List<double> m_phySleeps = new();

    /// <summary>The delay, in seconds, each ossl.PhySleep call asked for.</summary>
    public double[] PhySleeps { get { lock (m_marks) return m_phySleeps.ToArray(); } }

    public EngineRig(Dictionary<string, string> settings = null, Action<SchedulerOptions> tweak = null, double graceMs = 100, NativeCompile compile = null, IPrimEventFlags primFlags = null, bool productionFunctions = false)
    {
        RealLibrary.Load();
        Engine = new SLuaEngine(Log, (s, l) => true, null,
            (s, l, scene) => Loader = new ScriptLoader(new ScriptCompiler(null, compile ?? Host.Compile, l, null), new FakeAssets(), l, ownsCompiler: true)
            {
                UnclaimedGrace = TimeSpan.FromMilliseconds(graceMs),
            },
            options =>
            {
                if (productionFunctions)
                {
                    // The engine's own VM, with its ll functions, plus the marking functions the tests read back.
                    Func<Vm> production = options.VmFactory;
                    options.VmFactory = () => { Vm vm = production(); AddTestFunctions(vm); return vm; };
                }
                else
                    options.VmFactory = MakeVm;
                tweak?.Invoke(options);
                return Scheduler = new SLuaScheduler(options);
            },
            Messages);
        var source = new IniConfigSource();
        IConfig config = source.AddConfig(EngineIdentity.ConfigSection);
        config.Set("Enabled", "true");
        if (settings != null)
            foreach (var kv in settings) config.Set(kv.Key, kv.Value);
        Engine.Initialise(source);
        if (primFlags != null)
            Engine.PrimFlags = primFlags;
        Object = SceneHelpers.AddSceneObject(Scene, "Example Object", Owner);
        Engine.AddRegion(Scene);
    }

    public uint LocalId => Object.RootPart.LocalId;

    private Vm MakeVm()
    {
        var vm = new Vm(Watchdog.Default);
        AddTestFunctions(vm);
        return vm;
    }

    private void AddTestFunctions(Vm vm)
    {
        vm.Register("ossl", "Count", (tag, _) => { Counts.AddOrUpdate(tag, 1, (_, n) => n + 1); return null; });
        vm.Register("ossl", "Mark", (tag, a) => { lock (m_marks) m_marks.Add($"{tag}:{a[0].S}"); return null; });
        vm.Register("ossl", "PhySleep", (tag, _) =>
        {
            SchedulerScript caller = SchedulerScript.Current;
            double before = caller.AskedSeconds;
            Engine.PhySleep();
            lock (m_marks) m_phySleeps.Add(caller.AskedSeconds - before);
            return null;
        });
        vm.Register("ll", "OwnerSay", (tag, a) => { lock (m_marks) m_marks.Add($"{tag}:{a[0].S}"); return null; });
        // What the Detected functions will read: the detected objects of the event being handled, by index.
        vm.Register("ossl", "DetectedCount", (tag, _) => new[] { Value.Int(SchedulerScript.Current.DetectedCount) });
        vm.Register("ossl", "DetectedName", (tag, a) => new[] { Value.Str(SchedulerScript.Current.DetectedAt((int)a[0].I)?.Name ?? "none") });
        vm.Register("ossl", "DetectedKey", (tag, a) => new[] { Value.Str(SchedulerScript.Current.DetectedAt((int)a[0].I)?.Key ?? "none") });
        vm.Register("ossl", "DetectedFace", (tag, a) => new[] { Value.Int(SchedulerScript.Current.DetectedAt((int)a[0].I)?.TouchFace ?? -99) });
        vm.Register("ossl", "Burn", (tag, a) =>
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(a[0].N / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < end) { }
            return null;
        });
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
    public UUID Rez(string source, bool running = true, UUID? item = null, int startParam = 0, bool postOnRez = false, int stateSource = 0, SceneObjectPart part = null)
    {
        part ??= Object.RootPart;
        UUID id = item ?? UUID.Random();
        TaskInventoryItem existing = part.Inventory.GetInventoryItem(id);
        if (existing == null)
        {
            part.Inventory.AddInventoryItem(new TaskInventoryItem
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
        Scene.EventManager.TriggerRezScript(part.LocalId, id, source, startParam, postOnRez, "InWorldz.Phlox", stateSource);
        return id;
    }

    public static readonly UUID ParcelOwner = new UUID("00000000-0000-0000-0000-930000000001");
    public static readonly Vector3 West = new Vector3(64, 64, 25);
    public static readonly Vector3 East = new Vector3(192, 64, 25);

    public LandManagementModule Land;
    public ILandObject WestParcel, EastParcel;

    /// <summary>Two parcels owned by <see cref="ParcelOwner"/>: west (x below 128) allows no outside or group scripts, east allows everything.</summary>
    public void InstallLand()
    {
        Land = new LandManagementModule();
        SceneHelpers.SetupSceneModules(Scene, new IniConfigSource(), Land);
        int half = (int)Constants.RegionSize / 2;
        var west = new LandObject(ParcelOwner, false, Scene);
        west.LandData.Name = "west";
        west.SetLandBitmap(west.GetSquareLandBitmap(0, 0, half, (int)Constants.RegionSize));
        WestParcel = Land.AddLandObject(west);
        WestParcel.LandData.Flags &= ~(uint)(ParcelFlags.AllowOtherScripts | ParcelFlags.AllowGroupScripts);
        var east = new LandObject(ParcelOwner, false, Scene);
        east.LandData.Name = "east";
        east.SetLandBitmap(east.GetSquareLandBitmap(half, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
        EastParcel = Land.AddLandObject(east);
    }

    private int m_tail = 0x9300;

    /// <summary>Adds another object to the scene at a position, owned by <paramref name="owner"/>.</summary>
    public SceneObjectGroup AddObject(string name, Vector3 pos, UUID owner, UUID group = default)
    {
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, owner, name + "-", m_tail);
        m_tail += 16;
        sog.Name = name;
        sog.RootPart.Name = name;
        if (!group.IsZero())
            sog.RootPart.GroupID = group;
        sog.AbsolutePosition = pos;
        Scene.AddNewSceneObject(sog, false);
        return sog;
    }

    /// <summary>The parcel dialog: flags through LandChannel.UpdateLandObject, which raises OnLandObjectAdded.</summary>
    public void SetParcelFlags(ILandObject parcel, bool otherScripts, bool groupScripts, UUID group = default)
    {
        LandData d = parcel.LandData.Copy();
        if (otherScripts) d.Flags |= (uint)ParcelFlags.AllowOtherScripts; else d.Flags &= ~(uint)ParcelFlags.AllowOtherScripts;
        if (groupScripts) d.Flags |= (uint)ParcelFlags.AllowGroupScripts; else d.Flags &= ~(uint)ParcelFlags.AllowGroupScripts;
        if (!group.IsZero()) d.GroupID = group;
        Scene.LandChannel.UpdateLandObject(parcel.LandData.LocalID, d);
    }

    public const string Probe = "default { changed(integer c) { llOwnerSay(\"c\" + (string)c); } }";

    /// <summary>Posts changed(n) to the script; if it runs, it marks "cN".</summary>
    public void PostProbe(UUID item, int n) => Assert.True(Engine.PostScriptEvent(item, "changed", new object[] { n }));

    /// <summary>The script runs: the mark of a probe event arrives.</summary>
    public bool Heard(UUID item, int n) => SchedulerHarness.WaitFor(() => MarksOf(item).Contains("c" + n));

    /// <summary>The script is held: no mark of a probe event arrives in the time a run would take (a real wait: it proves nothing happens).</summary>
    public bool StaysSilent(UUID item, int n, int ms = 400)
    {
        Thread.Sleep(ms);
        return !MarksOf(item).Contains("c" + n);
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
