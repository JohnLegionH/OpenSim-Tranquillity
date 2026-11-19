/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

#nullable disable

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using SceneScriptEvents = OpenSim.Region.Framework.Scenes.scriptEvents;

namespace OpenSim.Region.ScriptEngine.Yengine.Tests;

/// <summary>
/// The game_control event in YEngine (Second Life wiki: "game_control"). YEngine numbers its events by the bit the
/// scene keeps for each in a part's event mask, and refuses to start when the two lists disagree, so a new event
/// needs the same number in both. These tests cover the numbering, compiling a handler, and carrying the event's data
/// from the post to the running script. Delivery to a running script in a region is checked on a test region.
/// Each test builds its own engine object and instance, so the class runs in parallel with the others.
/// </summary>
public class YEngineGameControlTests
{
    [Fact]
    public void GameControlIsEventCode47AndTheTableHolds48Codes()
    {
        Assert.True(Enum.TryParse("game_control", out ScriptEventCode code), "ScriptEventCode has no game_control");
        Assert.Equal(47, (int)code);
        Assert.Equal(48, (int)ScriptEventCode.Size);
        Assert.Equal(1UL << 47, (ulong)SceneScriptEvents.game_control);
    }

    [Fact]
    public void EveryEventCodeHasTheSameNameAsTheSceneEventAtThatBit()
    {
        // The check Yengine.Initialise makes before it starts; a mismatch turns the engine off.
        for (int i = 0; i < (int)ScriptEventCode.Size; i++)
        {
            string mine = Enum.GetName(typeof(ScriptEventCode), i);
            string scene = Enum.GetName(typeof(SceneScriptEvents), 1UL << i);
            Assert.True(mine == scene, $"code {i}: YEngine says '{mine}', the scene says '{scene}'");
        }
    }

    // -- compiling a handler ---------------------------------------------------------------------------------

    // The compiler's tables (the script API's functions and constants, the instance super type) are process-wide and
    // are filled in by the engine's one-time start-up step; compiling takes this lock so tests do not race on them.
    private static readonly object s_compilerGate = new();
    private static bool s_compilerReady;

    private static void EnsureCompilerReady()
    {
        if (s_compilerReady)
            return;
        IConfigSource config = new IniConfigSource();
        config.AddConfig("YEngine").Set("Enabled", "false");
        Yengine engine = new();
        engine.Initialise(config);
        Set(engine, "m_Scene", new SceneHelpers().SetupScene());
        typeof(Yengine).GetMethod("OneTimeLateInitialization", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(engine, null);
        s_compilerReady = true;
    }

    private static ScriptObjCode Compile(string source, out List<string> errors)
    {
        lock (s_compilerGate)
        {
            EnsureCompilerReady();
            return CompileLocked(source, out errors);
        }
    }

    private static ScriptObjCode CompileLocked(string source, out List<string> errors)
    {
        List<string> errs = new();
        TokenBegin begin = TokenBegin.Construct("test", null, (t, m) => errs.Add(m), source, out string hash);
        errors = errs;
        if (begin is null)
            return null;
        TokenScript script = ScriptReduce.Reduce(begin);
        if (script is null)
            return null;
        using MemoryStream ms = new();
        using (BinaryWriter writer = new(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            if (!ScriptCodeGen.CodeGen(script, writer, hash))
                return null;
        }
        ms.Position = 0;
        using BinaryReader reader = new(ms);
        return new ScriptObjCode(reader, null, null);
    }

    [Fact]
    public void AScriptWithAGameControlHandlerCompilesAndHasTheHandlerInColumn47()
    {
        ScriptObjCode code = Compile(
            "default { state_entry() { } game_control(key id, integer button_levels, list axes) { llSay(0, (string)button_levels); } }",
            out List<string> errors);

        Assert.True(code is not null, string.Join("; ", errors));
        Assert.NotNull(code.scriptEventHandlerTable[0, 47]);
    }

    [Fact]
    public void AGameControlHandlerMayDeclareFewerArguments()
    {
        ScriptObjCode code = Compile("default { game_control(key id) { } }", out List<string> errors);

        Assert.True(code is not null, string.Join("; ", errors));
        Assert.NotNull(code.scriptEventHandlerTable[0, 47]);
    }

    [Fact]
    public void AGameControlHandlerWithWrongArgumentTypesDoesNotCompile()
    {
        ScriptObjCode code = Compile("default { game_control(integer id, integer b, list axes) { } }", out _);

        Assert.Null(code);
    }

    [Fact]
    public void AScriptCanCallTheThreeModeFunctions()
    {
        ScriptObjCode code = Compile(
            "default { game_control(key id, integer b, list axes) { integer m = llGetGameControlMode(id); integer bb = llGetGameControlModeButtons(id); list a = llGetGameControlModeAxes(id); } }",
            out List<string> errors);

        Assert.True(code is not null, string.Join("; ", errors));
    }

    // -- carrying the event data -----------------------------------------------------------------------------

    private sealed class Rig
    {
        public Yengine Engine;
        public XMRInstance Instance;
        public UUID ItemID = UUID.Random();
    }

    private static void Set(object target, string field, object value)
    {
        FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(f);
        f.SetValue(target, value);
    }

    private static Rig MakeRig()
    {
        Rig rig = new();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("YEngine").Set("Enabled", "false");
        rig.Engine = new Yengine();
        rig.Engine.Initialise(config);
        Set(rig.Engine, "m_Enabled", true);

        rig.Instance = new XMRInstance();
        rig.Instance.m_ItemID = rig.ItemID;
        bool[] have = (bool[])typeof(XMRInstance)
            .GetField("m_HaveEventHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rig.Instance);
        if (have.Length > 47)
            have[47] = true;

        var dict = (Dictionary<UUID, XMRInstance>)typeof(Yengine)
            .GetField("m_InstancesDict", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rig.Engine);
        dict[rig.ItemID] = rig.Instance;
        return rig;
    }

    [Fact]
    public void ThePostedEventKeepsItsArgumentsAndItsData()
    {
        Rig rig = MakeRig();
        GameControlSnapshot.TryCreate(5u, new float[] { 1, 0, 0, 0, 0, 0 }, GameControlSnapshot.ModeAvatar, 3u,
            new float[] { 0, 1, 0, 0, 0 }, out GameControlSnapshot snapshot);
        object[] args = { UUID.Random().ToString(), 5, new object[] { 1f, 0f, 0f, 0f, 0f, 0f } };

        bool posted = ((IScriptModule)rig.Engine).PostScriptEvent(rig.ItemID, "game_control", args, snapshot);

        Assert.True(posted);
        Assert.Single(rig.Instance.m_EventQueue);
        EventParams queued = rig.Instance.m_EventQueue.First.Value;
        Assert.Equal("game_control", queued.EventName);
        Assert.Equal(3, queued.Params.Length);
        Assert.Equal(args[0], queued.Params[0]);
        Assert.Equal(5, queued.Params[1]);
        // The list argument reaches the handler as an LSL list of floats.
        LSL_Types.list axes = Assert.IsType<LSL_Types.list>(queued.Params[2]);
        Assert.Equal(6, axes.Length);
        Assert.Equal(1.0, (double)(LSL_Types.LSLFloat)axes.Data[0]);
        Assert.Same(snapshot, queued.EventData);
    }

    [Fact]
    public void TheEngineAnswersTheDataOfTheEventTheScriptIsRunning()
    {
        Rig rig = MakeRig();
        GameControlSnapshot.TryCreate(5u, new float[6], GameControlSnapshot.ModeCursor, 1u,
            new float[6], out GameControlSnapshot snapshot);

        Assert.Null(((IScriptEngine)rig.Engine).GetEventData(rig.ItemID));

        rig.Instance.EventData = snapshot;

        Assert.Same(snapshot, ((IScriptEngine)rig.Engine).GetEventData(rig.ItemID));
        Assert.Null(((IScriptEngine)rig.Engine).GetEventData(UUID.Random()));
    }

    [Fact]
    public void AnEventPostedWithoutDataCarriesNone()
    {
        Rig rig = MakeRig();

        bool posted = ((IScriptModule)rig.Engine).PostScriptEvent(rig.ItemID, "game_control",
            new object[] { "k", 0, new object[0] });

        Assert.True(posted);
        Assert.Null(rig.Instance.m_EventQueue.First.Value.EventData);
    }
}
