/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using InWorldz.Phlox.Glue;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Measures what the warm-up costs and saves: the time the region's start takes, the time the warm-up itself takes, and the
/// time of the first call of a few served ll functions through the bindings, with the warm-up on or off. The first call in a
/// process is the cold one, so each mode is run in a process of its own. It runs only when SLUA_WARMUP_MEASURE_OUT names a
/// file to append a tab-separated row to; SLUA_WARMUP_MODE is "on" or "off".
/// </summary>
[Collection("native-library")]
public class WarmUpMeasurementTests
{
    public sealed class MeasureFactAttribute : FactAttribute
    {
        public MeasureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_WARMUP_MEASURE_OUT")))
                Skip = "SLUA_WARMUP_MEASURE_OUT is not set; the measurement runs only on request";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SLUA_SHIM_PATH")))
                Skip = "SLUA_SHIM_PATH is not set";
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    [MeasureFact]
    public void FirstCallsAndRegionStart()
    {
        string mode = Environment.GetEnvironmentVariable("SLUA_WARMUP_MODE") ?? "on";
        RealLibrary.Load();
        var log = new RecordingLog();
        var source = new IniConfigSource();
        IConfig config = source.AddConfig(EngineIdentity.ConfigSection);
        config.Set("Enabled", "true");
        config.Set("WarmUp", mode == "on" ? "true" : "false");
        Scene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup obj = SceneHelpers.AddSceneObject(scene, "Example Object", UUID.Random());
        var engine = new SLuaEngine(log, (s, l) => true, null,
            (s, l, sc) => new ScriptLoader(new ScriptCompiler(null, Host.Compile, l, null), new FakeAssets(), l, ownsCompiler: true),
            o => new SLuaScheduler(o), new RecordingMessages());
        engine.Initialise(source);

        long t0 = Stopwatch.GetTimestamp();
        engine.AddRegion(scene);
        long addRegion = Stopwatch.GetTimestamp() - t0;

        long warm = 0;
        if (mode == "on")
        {
            Assert.True(SchedulerHarness.WaitFor(() => ApiWarmUp.IsDone, 120000));
            warm = Stopwatch.GetTimestamp() - t0 - addRegion;
        }

        var script = new SchedulerScript(0, 0, new byte[0], ScriptLanguage.Slua);
        var item = UUID.Random();
        var context = new ScriptContext(script, engine.CreateScriptApi(script, item, obj.RootPart.LocalId, UnavailableApi.Create()), item);
        var calls = new (string Name, Value[] Args)[]
        {
            ("GetObjectName", new Value[0]), ("GetPos", new Value[0]), ("GetOwner", new Value[0]), ("GetScale", new Value[0]),
            ("StringLength", new[] { Value.Str("abc") }), ("Abs", new[] { Value.Int(-3) }), ("GetUnixTime", new Value[0]),
        };
        var first = new List<string>();
        long firstSum = 0, secondSum = 0;
        foreach ((string name, Value[] args) in calls)
        {
            BoundFunction f = LlBindings.All.First(b => b.Name == name);
            long a = Run(f, context, item, args);
            long b = Run(f, context, item, args);
            firstSum += a;
            secondSum += b;
            first.Add(name + "=" + Ms(a).ToString("F3", CultureInfo.InvariantCulture) + "/" + Ms(b).ToString("F3", CultureInfo.InvariantCulture));
        }

        string row = string.Join("\t", mode,
            Ms(addRegion).ToString("F1", CultureInfo.InvariantCulture),
            Ms(warm).ToString("F1", CultureInfo.InvariantCulture),
            Ms(firstSum).ToString("F2", CultureInfo.InvariantCulture),
            Ms(secondSum).ToString("F2", CultureInfo.InvariantCulture),
            ApiWarmUp.Prepared.ToString(CultureInfo.InvariantCulture),
            string.Join(" ", first));
        File.AppendAllText(Environment.GetEnvironmentVariable("SLUA_WARMUP_MEASURE_OUT"), row + "\n");
        engine.RemoveRegion(scene);
    }

    private static long Run(BoundFunction f, ScriptContext context, UUID item, Value[] args)
    {
        var call = new SyscallContext(item, SyscallContext.NextSeq());
        call.Enter();
        long t = Stopwatch.GetTimestamp();
        try { f.Call(context, args); }
        finally { SyscallContext.Exit(); }
        return Stopwatch.GetTimestamp() - t;
    }
}
