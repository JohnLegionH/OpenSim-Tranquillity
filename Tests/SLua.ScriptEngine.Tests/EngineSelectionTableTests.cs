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
/// Every header form against the engines loaded, the headerless setting and the region's default engine, with the
/// engine each must reach (Scene.ResolveScriptEngine, with the real SLua engine and stand-ins for Phlox and YEngine
/// that carry Phlox's name and header alias). Then the saved-state cases: a script keeps the engine that holds its state.
/// </summary>
public class EngineSelectionTableTests
{
    private const string Phlox = "InWorldz.Phlox", YEngine = "YEngine", Ours = "SLua";

    private const string Lsl = "default { state_entry() {} }";
    private const string Lua = "print(1)";

    /// <summary>Stands in for Phlox: its engine name, its "Phlox" alias, no claims, and saved state when told.</summary>
    private sealed class PhloxStandIn : MockScriptEngine, IScriptModule, IScriptStateProvider, IScriptEngineSelection
    {
        public new string ScriptEngineName => Phlox;
        public IReadOnlyList<string> HeaderAliases { get; } = new[] { "Phlox" };
        public bool ClaimsScript(string scriptSource) => false;
        public HashSet<UUID> HoldsStateFor { get; } = new();
        public bool HasSavedScriptState(UUID itemID, UUID assetID) => HoldsStateFor.Contains(assetID);
    }

    private sealed class YEngineStandIn : MockScriptEngine, IScriptModule
    {
        public new string ScriptEngineName => YEngine;
    }

    private static (Scene scene, PhloxStandIn phlox) Region(bool slua, bool claimHeaderless, ISavedStateStore store = null, bool yengine = true)
    {
        Scene scene = new SceneHelpers().SetupScene();
        var phlox = new PhloxStandIn();
        scene.StackModuleInterface<IScriptModule>(phlox);
        if (yengine)
            scene.StackModuleInterface<IScriptModule>(new YEngineStandIn());
        var engine = store is null ? new SLuaEngine(new RecordingLog(), (settings, log) => true)
                                   : new SLuaEngine(new RecordingLog(), (settings, log) => true, store);
        var source = new IniConfigSource();
        IConfig section = source.AddConfig(EngineIdentity.ConfigSection);
        section.Set("Enabled", slua ? "true" : "false");
        section.Set("ClaimHeaderlessScripts", claimHeaderless ? "true" : "false");
        engine.Initialise(source);
        engine.AddRegion(scene);
        return (scene, phlox);
    }

    // script source; default engine; SLua enabled; headerless setting on; engine that must get the script
    public static TheoryData<string, string, bool, bool, string> Table()
    {
        var rows = new TheoryData<string, string, bool, bool, string>();
        foreach (string defaultEngine in new[] { Phlox, YEngine, Ours })
        {
            foreach (bool headerless in new[] { false, true })
            {
                // headers that name an engine always win over the default and the setting
                foreach (string source in new[] { "//SLua:\n" + Lsl, "//SLua:LSL\n" + Lsl, "//SLua:slua\n" + Lua, "//SLua:SLua\n" + Lua, "// SLua :slua\n" + Lua })
                    rows.Add(source, defaultEngine, true, headerless, Ours);
                foreach (string source in new[] { "//Phlox:\n" + Lsl, "//Phlox:slua\n" + Lua, "//InWorldz.Phlox:\n" + Lsl, "//InWorldz.Phlox:slua\n" + Lua })
                    rows.Add(source, defaultEngine, true, headerless, Phlox);
                rows.Add("//YEngine:\n" + Lsl, defaultEngine, true, headerless, YEngine);

                // --!slua goes to the SLua engine when it is enabled, whatever the setting
                rows.Add("--!slua\n" + Lua, defaultEngine, true, headerless, Ours);
                rows.Add("  --!SLUA\n" + Lua, defaultEngine, true, headerless, Ours);

                // no header: the default engine, unless the setting hands such scripts to the SLua engine
                string headerlessTarget = headerless || defaultEngine == Ours ? Ours : defaultEngine;
                foreach (string source in new[] { Lsl, "-- plain Lua\n" + Lua, "// an ordinary comment\n" + Lsl, "//Unknown:\n" + Lsl,
                                                  "//SLua\n" + Lsl, "//Phlox\n" + Lsl, "//slua:\n" + Lua, "//phlox:\n" + Lsl, "--!lua\n" + Lua })
                    rows.Add(source, defaultEngine, true, headerless, headerlessTarget);
            }

            // the SLua engine is not enabled: nothing is claimed and its header names nobody
            foreach (string source in new[] { "--!slua\n" + Lua, Lsl, "//SLua:\n" + Lsl, "-- plain Lua\n" + Lua })
                rows.Add(source, defaultEngine == Ours ? Phlox : defaultEngine, false, true, defaultEngine == Ours ? Phlox : defaultEngine);
            rows.Add("//Phlox:\n" + Lsl, YEngine, false, false, Phlox);
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void TheRegionGivesTheScriptToTheExpectedEngine(string source, string defaultEngine, bool slua, bool claimHeaderless, string expected)
    {
        var (scene, _) = Region(slua, claimHeaderless);
        Assert.Equal(expected, scene.ResolveScriptEngine(defaultEngine, source)?.ScriptEngineName);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void TheSluaEngineOwnsExactlyTheScriptsTheRegionGaveIt(string source, string defaultEngine, bool slua, bool claimHeaderless, string expected)
    {
        // Every engine is offered every rez with the engine the region chose; this engine keeps the script only when that is itself.
        if (!slua)
            return;
        var (scene, _) = Region(slua, claimHeaderless);
        string chosen = scene.ResolveScriptEngine(defaultEngine, source)?.ScriptEngineName;
        string[] loaded = scene.RequestModuleInterfaces<IScriptModule>().Select(m => m.ScriptEngineName).ToArray();
        Assert.Equal(expected == Ours, EngineHeader.Owner(source, chosen, loaded) == Ours);
    }

    [Fact]
    public void ARunningScriptKeepsTheEngineThatHoldsItsSavedState()
    {
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() };
        string script = "--!slua\n" + Lua;

        // a Phlox SLua script that Phlox holds state for stays on Phlox, with the SLua engine enabled and the setting on
        var (scene, phlox) = Region(slua: true, claimHeaderless: true);
        Assert.Equal(Ours, scene.ResolveScriptEngine(Phlox, script, item)?.ScriptEngineName);   // without state it would move
        phlox.HoldsStateFor.Add(item.AssetID);
        Assert.Equal(Phlox, scene.ResolveScriptEngine(Phlox, script, item)?.ScriptEngineName);
        Assert.Equal(Phlox, scene.ResolveScriptEngine(Phlox, Lsl, item)?.ScriptEngineName);

        // edited, it has a new asset and no state: it moves
        var edited = new TaskInventoryItem { ItemID = item.ItemID, AssetID = UUID.Random() };
        Assert.Equal(Ours, scene.ResolveScriptEngine(Phlox, script, edited)?.ScriptEngineName);
    }

    [Fact]
    public void AScriptTheSluaEngineHoldsStateForStaysWithItWhateverTheDefaultAndTheSetting()
    {
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() };
        var (scene, _) = Region(slua: true, claimHeaderless: false, store: new RowStore(item.ItemID, item.AssetID));

        Assert.Equal(Ours, scene.ResolveScriptEngine(Phlox, Lsl, item)?.ScriptEngineName);
        Assert.Equal(Ours, scene.ResolveScriptEngine(YEngine, Lua, item)?.ScriptEngineName);
        Assert.Equal(Phlox, scene.ResolveScriptEngine(Phlox, Lsl, new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() })?.ScriptEngineName);
    }

    [Fact]
    public void ACrossingWithNoSourceGoesByItsSavedStateAlone()
    {
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random() };
        var (scene, phlox) = Region(slua: true, claimHeaderless: true);

        Assert.Equal(Phlox, scene.ResolveScriptEngine(Phlox, string.Empty, item)?.ScriptEngineName);   // nothing to claim: the default
        phlox.HoldsStateFor.Add(item.AssetID);
        Assert.Equal(Phlox, scene.ResolveScriptEngine(Ours, string.Empty, item)?.ScriptEngineName);

        var (slScene, _) = Region(slua: true, claimHeaderless: true, store: new RowStore(item.ItemID, item.AssetID));
        Assert.Equal(Ours, slScene.ResolveScriptEngine(Phlox, string.Empty, item)?.ScriptEngineName);
    }
}
