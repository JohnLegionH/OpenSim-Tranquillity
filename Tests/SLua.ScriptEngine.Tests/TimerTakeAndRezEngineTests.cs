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
/// An LLTimers timer goes on ticking when its object is taken into inventory and rezzed again, as a timer set with llSetTimerEvent does:
/// the script comes back through its saved state, and the state carries the timer.
/// </summary>
[Collection("native-library")]
public class TimerTakeAndRezEngineTests
{
    private static readonly string Ticks =
        "n = 0" + (char)10 + "ossl.Mark('start')" + (char)10 +
        "LLTimers:every(0.3, function() n = n + 1; ossl.Mark('t' .. n) end)" + (char)10;

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

    private static int Ticked(EngineRig rig, UUID item) =>
        rig.MarksOf(item).Where(m => m.Length > 1 && m[0] == 't' && char.IsDigit(m[1])).Select(m => int.Parse(m.Substring(1))).DefaultIfEmpty(0).Max();

    [RequiresNativeFact]
    public void AnLlTimersTimerKeepsTickingAfterItsObjectIsTakenAndRezzed()
    {
        using var rig = new EngineRig();
        typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(rig.Scene, "InWorldz.Phlox");
        rig.Scene.StackModuleInterface<IScriptModule>(new DefaultEngine());
        rig.Scene.StackModuleInterface<IScriptModule>(rig.Engine);
        var iam = new BasicInventoryAccessModule();
        var config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        SceneHelpers.SetupSceneModules(rig.Scene, config, iam);
        UserAccount user = UserAccountHelpers.CreateUserWithInventory(rig.Scene, EngineRig.Owner);
        IClientAPI client = SceneHelpers.AddScenePresence(rig.Scene, user).ControllingClient;

        TaskInventoryItem script = TaskInventoryHelpers.AddScript(rig.Scene.AssetService, rig.Object.RootPart, UUID.Random(), UUID.Random(), "ticker", EngineRig.Slua(Ticks));
        script.OwnerID = EngineRig.Owner;
        script.ScriptRunning = true;
        UUID before = script.ItemID;
        Assert.True(rig.Object.RootPart.Inventory.CreateScriptInstance(before, 0, false, "InWorldz.Phlox", 0));
        rig.Object.ResumeScripts();
        Assert.True(SchedulerHarness.WaitFor(() => Ticked(rig, before) >= 2), "the timer did not tick before the take");
        int counted = Ticked(rig, before);

        UUID folder = rig.Scene.InventoryService.GetFolderForType(user.PrincipalID, FolderType.Object).ID;
        InventoryItemBase taken = Assert.Single(iam.CopyToInventory(DeRezAction.Take, folder, new List<SceneObjectGroup> { rig.Object }, client, false));
        rig.Scene.DeleteSceneObject(rig.Object, false);
        SceneObjectGroup rezzed = iam.RezObject(client, taken.ID, UUID.Zero, Vector3.Zero, Vector3.Zero, UUID.Zero, 1, false, false, false, UUID.Zero, false);
        Assert.NotNull(rezzed);
        UUID after = Assert.Single(rezzed.RootPart.Inventory.GetInventoryItems(InventoryType.LSL)).ItemID;
        Assert.NotEqual(before, after);

        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(after) != null), "the script did not reach this engine");
        Assert.True(SchedulerHarness.WaitFor(() => Ticked(rig, after) >= counted + 3, 8000),
            $"the timer did not go on ticking from {counted}: " + string.Join(",", rig.MarksOf(after)));
        Assert.DoesNotContain("start", rig.MarksOf(after)); // restored, not started again
    }
}
