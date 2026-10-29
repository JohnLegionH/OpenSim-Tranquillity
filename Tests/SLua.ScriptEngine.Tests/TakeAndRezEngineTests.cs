/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A script this engine holds through saved state stays with this engine after its object is taken into inventory and rezzed
/// again. The take is the core's own (BasicInventoryAccessModule.CopyToInventory, which serializes the object with each running
/// script's state) and so is the rez (RezObject: new ids for every item, then CreateScriptInstances), so the script comes back
/// under a new item id and the state it carried places it (Scene.ResolveScriptEngine). The region's default engine is another
/// engine, which holds nothing. A script started fresh would run its main chunk again; one that was restored counts on.
/// </summary>
[Collection("native-library")]
public class TakeAndRezEngineTests
{
    private const string Counts =
        "n = (n or 0) + 1\nossl.Mark('start')\n" +
        "LLEvents:on('changed', function(c) n = n + 1; ossl.Mark('changed' .. c .. 'n' .. n) end)\n";

    /// <summary>The region's default engine: it holds no script and claims none.</summary>
    private sealed class DefaultEngine : MockScriptEngine, IScriptModule
    {
        public new string ScriptEngineName => "InWorldz.Phlox";
        public new bool ResumeScript(UUID itemID) => false;
        public new bool SuspendScript(UUID itemID) => false;
        public new bool GetScriptState(UUID itemID) => false;
        public new bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public new ArrayList GetScriptErrors(UUID itemID) => new();
        public new string GetXMLState(UUID itemID) => string.Empty;
        public new bool SetXMLState(UUID itemID, string xml) => false;
    }

    /// <summary>The script's count when its changed(<paramref name="probe"/>) event ran, or 0 if it has not.</summary>
    private static int CountAfter(EngineRig rig, UUID item, int probe)
    {
        string prefix = "changed" + probe + "n";
        string mark = rig.MarksOf(item).FirstOrDefault(m => m.StartsWith(prefix));
        return mark == null ? 0 : int.Parse(mark.Substring(prefix.Length));
    }

    [RequiresNativeFact]
    public void AnSLuaScriptKeepsItsEngineAndItsCountWhenItsObjectIsTakenAndRezzed()
    {
        using var rig = new EngineRig();
        typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(rig.Scene, "InWorldz.Phlox");
        rig.Scene.StackModuleInterface<IScriptModule>(new DefaultEngine());
        rig.Scene.StackModuleInterface<IScriptModule>(rig.Engine);   // the region lists the engine, as it does the engines it loads
        var iam = new BasicInventoryAccessModule();
        var config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        SceneHelpers.SetupSceneModules(rig.Scene, config, iam);
        UserAccount user = UserAccountHelpers.CreateUserWithInventory(rig.Scene, EngineRig.Owner);
        IClientAPI client = SceneHelpers.AddScenePresence(rig.Scene, user).ControllingClient;

        TaskInventoryItem script = TaskInventoryHelpers.AddScript(rig.Scene.AssetService, rig.Object.RootPart, UUID.Random(), UUID.Random(), "counter", EngineRig.Slua(Counts));
        script.OwnerID = EngineRig.Owner;
        script.ScriptRunning = true;
        UUID before = script.ItemID;
        Assert.True(rig.Object.RootPart.Inventory.CreateScriptInstance(before, 0, false, "InWorldz.Phlox", 0));
        rig.Object.ResumeScripts();
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(before).Contains("start")));
        rig.PostProbe(before, 1);
        Assert.True(SchedulerHarness.WaitFor(() => CountAfter(rig, before, 1) > 0));
        rig.PostProbe(before, 2);
        Assert.True(SchedulerHarness.WaitFor(() => CountAfter(rig, before, 2) > 0));
        int counted = CountAfter(rig, before, 2);   // the region sends a changed event of its own too, so the count is read, not assumed

        UUID folder = rig.Scene.InventoryService.GetFolderForType(user.PrincipalID, FolderType.Object).ID;
        InventoryItemBase taken = Assert.Single(iam.CopyToInventory(DeRezAction.Take, folder, new List<SceneObjectGroup> { rig.Object }, client, false));
        Assert.NotNull(taken);
        rig.Scene.DeleteSceneObject(rig.Object, false);
        SceneObjectGroup rezzed = iam.RezObject(client, taken.ID, UUID.Zero, Vector3.Zero, Vector3.Zero, UUID.Zero, 1, false, false, false, UUID.Zero, false);
        Assert.NotNull(rezzed);
        UUID after = Assert.Single(rezzed.RootPart.Inventory.GetInventoryItems(InventoryType.LSL)).ItemID;

        Assert.NotEqual(before, after);   // a real take: the id changed
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(after) != null), "the script did not reach this engine");
        rig.PostProbe(after, 3);
        Assert.True(SchedulerHarness.WaitFor(() => CountAfter(rig, after, 3) > 0), "no change event reached the script: " + string.Join(",", rig.MarksOf(after)));
        Assert.True(CountAfter(rig, after, 3) > counted,
            $"the count did not go on from {counted}: " + string.Join(",", rig.MarksOf(after)));
        Assert.DoesNotContain("start", rig.MarksOf(after));   // restored, not started again
    }
}
