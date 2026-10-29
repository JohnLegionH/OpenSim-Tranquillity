/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Which engine the region gives a script to (Scene.ResolveScriptEngine), with this engine, Phlox and YEngine loaded
/// and Phlox the default. The region's own header parse needs no change to route to this engine: its name is the
/// token. Scripts without our header never come to us.
/// </summary>
public class SelectionTests
{
    private const string Phlox = "InWorldz.Phlox", YEngine = "YEngine", Ours = "SLua";

    private sealed class OtherEngine : MockScriptEngine, IScriptModule
    {
        public OtherEngine(string name) { ScriptEngineName = name; }
        public new string ScriptEngineName { get; }
    }

    private static Scene RegionWithAllThree()
    {
        Scene scene = new SceneHelpers().SetupScene();
        scene.StackModuleInterface<IScriptModule>(new OtherEngine(Phlox));
        scene.StackModuleInterface<IScriptModule>(new OtherEngine(YEngine));
        var engine = new SLuaEngine(new RecordingLog(), (settings, log) => true);
        var source = new IniConfigSource();
        IConfig slua = source.AddConfig(EngineIdentity.ConfigSection);
        slua.Set("Enabled", "true");
        slua.Set("WarmUp", "false"); // the API warm-up is process-wide and not under test here (see WarmUpIsolationTests)
        engine.Initialise(source);
        engine.AddRegion(scene);
        return scene;
    }

    // first line (or whole source), the region's default engine, the engine that must get the script
    public static TheoryData<string, string, string> Table => new()
    {
        { "//SLua:\ndefault { state_entry() {} }", Phlox, Ours },
        { "//SLua:lsl\ndefault { state_entry() {} }", Phlox, Ours },
        { "//SLua:slua\nprint(1)", Phlox, Ours },
        { "//SLua:LSL\ndefault { state_entry() {} }", Phlox, Ours },
        { "//SLua:SLua\nprint(1)", Phlox, Ours },
        { "// SLua :slua\nprint(1)", Phlox, Ours },
        { "//SLua:\ndefault {}", YEngine, Ours },
        { "//InWorldz.Phlox:\ndefault {}", Phlox, Phlox },
        { "//InWorldz.Phlox:slua\nprint(1)", Phlox, Phlox },
        { "//YEngine:\ndefault {}", Phlox, YEngine },
        { "--!slua\nprint(1)", Phlox, Phlox },                       // no header: the default engine, never us
        { "-- plain Lua\nprint(1)", Phlox, Phlox },
        { "default { state_entry() {} }", Phlox, Phlox },
        { "// an ordinary comment\ndefault {}", Phlox, Phlox },
        { "//SLua\ndefault {}", Phlox, Phlox },                  // no colon
        { "//SLua:", Phlox, Phlox },                             // no newline
        { "default { state_entry() {} }", Ours, Ours },              // an operator who made us the default
        { "--!slua\nprint(1)", Ours, Ours },
        { "//InWorldz.Phlox:\ndefault {}", Ours, Phlox },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void TheRegionGivesTheScriptToTheRightEngine(string source, string defaultEngine, string expected)
    {
        Scene scene = RegionWithAllThree();
        Assert.Equal(expected, scene.ResolveScriptEngine(defaultEngine, source)?.ScriptEngineName);
    }

    [Fact]
    public void ALowerCaseHeaderIsNotOursUnlessTheRegionChoseUs()
    {
        // The region compares engine names ordinally (Scene.ResolveScriptEngine), and so does the engine's own test.
        // A "//slua:" script therefore reaches us only when we are the default engine, or by saved state.
        const string source = "//slua:\nprint(1)";
        Scene scene = RegionWithAllThree();
        Assert.Equal(Phlox, scene.ResolveScriptEngine(Phlox, source)?.ScriptEngineName);
        Assert.Equal(Ours, scene.ResolveScriptEngine(Ours, source)?.ScriptEngineName);
        Assert.Equal(Phlox, EngineHeader.Owner(source, Phlox, new[] { Phlox, YEngine, Ours }));   // the engine agrees with the region: not ours
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void OurOwnershipTestAgreesWithTheRegion(string source, string defaultEngine, string expected)
    {
        string[] loaded = { Phlox, YEngine, Ours };
        Assert.Equal(expected == Ours, EngineHeader.Owner(source, defaultEngine, loaded) == Ours);
    }

    [Fact]
    public void ASavedRowForAScriptWithoutOurHeaderStillRoutesToUsWhenWeHoldIt()
    {
        // Saved-state routing: the engine holding a row for the item and asset gets the script, whatever the default.
        Scene scene = new SceneHelpers().SetupScene();
        scene.StackModuleInterface<IScriptModule>(new OtherEngine(Phlox));
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() };
        var engine = new SLuaEngine(new RecordingLog(), (settings, log) => true, new RowStore(item.ItemID, item.AssetID));
        var source = new IniConfigSource();
        IConfig slua = source.AddConfig(EngineIdentity.ConfigSection);
        slua.Set("Enabled", "true");
        slua.Set("WarmUp", "false"); // the API warm-up is process-wide and not under test here (see WarmUpIsolationTests)
        engine.Initialise(source);
        engine.AddRegion(scene);

        Assert.Equal(Ours, scene.ResolveScriptEngine(Phlox, "default {}", item)?.ScriptEngineName);
        Assert.Equal(Phlox, scene.ResolveScriptEngine(Phlox, "default {}", new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() })?.ScriptEngineName);
    }
}
