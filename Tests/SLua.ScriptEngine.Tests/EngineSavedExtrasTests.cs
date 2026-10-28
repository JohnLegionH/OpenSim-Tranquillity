/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// What Second Life keeps of a script across a restart, a take or a crossing besides its variables: the detected objects of the events
/// waiting in its queue, its repeating sensor, the permissions it was granted and the controls an attachment took. A script
/// restored from its saved state is the same script, not a reset one (the state-entry rule is in the Second Life wiki page "State").
/// </summary>
[Collection("native-library")]
public class EngineSavedExtrasTests
{
    private const int Passive = 4;
    private const int NewRez = 1;

    private static string Db(TempFolder dir) => System.IO.Path.Combine(dir.Path, "state.db");

    private static object[] Sensors(EngineRig rig, UUID item) => rig.Engine.HostServices.AsyncCommands?.SensorRepeatPlugin.GetSerializationData(item) ?? Array.Empty<object>();

    private static bool Marked(EngineRig rig, UUID item, string mark) => SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark));

    [RequiresNativeFact]
    public void TheDetectedObjectsOfAnEventThatWaitedInTheQueueSurviveARestart()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        const string Handler = "LLEvents:on('touch_start', function(n) ossl.Mark('touch:' .. ossl.DetectedCount() .. ':' .. ossl.DetectedName(0) .. ':' .. ossl.DetectedFace(0)) end)\nossl.Mark('start')\n";
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(store: store))
            {
                rig.Rez(EngineRig.Slua(Handler), item: item, asset: asset);
                Assert.True(Marked(rig, item, "start"));
                Assert.True(rig.Engine.SuspendScript(item)); // the touch waits in the queue
                var dp = new DetectParams { Key = UUID.Random(), Name = "Test User" };
                dp.SurfaceTouchArgs = new OpenSim.Framework.SurfaceTouchEventArgs { FaceIndex = 4 };
                Assert.True(rig.Engine.PostObjectEvent(rig.LocalId, new EventParams("touch_start", new object[] { 1 }, new[] { dp })));
            } // the region stops with the touch still queued
            Assert.True(store.Flush());
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(store: store))
        {
            rig.Rez(EngineRig.Slua(Handler), item: item, asset: asset);
            Assert.True(Marked(rig, item, "touch:1:Test User:4"), "the handler read the toucher of the event that waited");
            Assert.DoesNotContain("start", rig.MarksOf(item));
        }
    }

    [RequiresNativeFact]
    public void ARepeatingSensorSurvivesARestartWithoutTheScriptSettingItAgain()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        string repeat = "ll.SensorRepeat('Nobody Here', NULL_KEY, " + Passive + ", 20.0, PI, 30.0)\nossl.Mark('armed')\n";
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(productionFunctions: true, store: store))
            {
                rig.Rez(EngineRig.Slua(repeat), item: item, asset: asset);
                Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length > 0), "the script set its sensor");
            }
            Assert.True(store.Flush());
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(productionFunctions: true, store: store))
        {
            rig.Rez(EngineRig.Slua(repeat), item: item, asset: asset);
            Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, item).Length > 0), "the sensor repeats again");
            Assert.DoesNotContain("armed", rig.MarksOf(item)); // the script's main chunk did not run again
        }
    }

    [RequiresNativeFact]
    public void AGrantedPermissionSurvivesARestart()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        const int Animate = 16;
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(productionFunctions: true, store: store))
            {
                rig.Rez(EngineRig.Slua("ossl.Mark('start')\n"), item: item, asset: asset);
                Assert.True(Marked(rig, item, "start"));
                var granted = rig.ItemOf(item);
                granted.PermsGranter = EngineRig.Owner;
                granted.PermsMask = Animate;
            }
            Assert.True(store.Flush());
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(productionFunctions: true, store: store))
        {
            rig.Rez(EngineRig.Slua("ossl.Mark('start')\n"), item: item, asset: asset); // the new region's item holds no grant of its own
            Assert.True(SchedulerHarness.WaitFor(() => rig.ItemOf(item).PermsGranter == EngineRig.Owner), "the script's permission record gave the grant back");
            Assert.Equal(Animate, rig.ItemOf(item).PermsMask);
        }
    }

    [RequiresNativeFact]
    public void TheControlsAnAttachmentTookAreKeptWhileItHoldsThePermissionAndDroppedWhenItDoesNot()
    {
        using var dir = new TempFolder();
        UUID held = UUID.Random(), heldAsset = UUID.Random(), dropped = UUID.Random(), droppedAsset = UUID.Random();
        const int TakeControls = 4;
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(productionFunctions: true, store: store))
            {
                foreach ((UUID id, UUID a) in new[] { (held, heldAsset), (dropped, droppedAsset) })
                {
                    rig.Rez(EngineRig.Slua("ossl.Mark('start')\n"), item: id, asset: a);
                    Assert.True(Marked(rig, id, "start"));
                    rig.Engine.RealApiFor(id).RuntimeAccess.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.Control] = new object[] { 1, 1, 0 };
                }
                rig.ItemOf(held).PermsGranter = EngineRig.Owner;
                rig.ItemOf(held).PermsMask = TakeControls;
            }
            Assert.True(store.Flush());
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(productionFunctions: true, store: store))
        {
            rig.Rez(EngineRig.Slua("ossl.Mark('start')\n"), item: held, asset: heldAsset);
            rig.Rez(EngineRig.Slua("ossl.Mark('start')\n"), item: dropped, asset: droppedAsset);
            Assert.True(SchedulerHarness.WaitFor(() => rig.ItemOf(held).PermsGranter == EngineRig.Owner), "the grant is back");
            Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.RealApiFor(held)?.RuntimeAccess.ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control) == true),
                "the record of the taken controls is kept: it rests on a grant of PERMISSION_TAKE_CONTROLS");
            rig.WaitScheduled(dropped);
            Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.RealApiFor(dropped) != null));
            Assert.False(rig.Engine.RealApiFor(dropped).RuntimeAccess.ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control),
                "a record with no grant under it goes");
        }
    }

    [RequiresNativeFact]
    public void AScriptAFaultStoppedIsRestoredStoppedAfterARestartWithOneLogLineAndNoMessageToTheOwner()
    {
        using var dir = new TempFolder();
        UUID item = UUID.Random(), asset = UUID.Random();
        const string Faulting = "LLEvents:on('changed', function(c) error('boom') end)\nossl.Mark('start')\n";
        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        {
            using (var rig = new EngineRig(store: store))
            {
                rig.Rez(EngineRig.Slua(Faulting), item: item, asset: asset, stateSource: NewRez);
                Assert.True(Marked(rig, item, "start"));
                rig.PostProbe(item, 1);
                Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.SchedulerScriptFor(item).Killed));
                Assert.True(SchedulerHarness.WaitFor(() => rig.Messages.Shouts.Length == 1), "the first report told the owner");
            }
            Assert.True(store.Flush());
        }

        using (var store = new SqliteStateStore(Db(dir), "SLua"))
        using (var rig = new EngineRig(store: store))
        {
            rig.Rez(EngineRig.Slua(Faulting), item: item, asset: asset, running: false, stateSource: NewRez);
            SchedulerScript s = rig.WaitScheduled(item);
            Assert.True(SchedulerHarness.WaitFor(() => s.Killed));
            Assert.Empty(rig.Messages.Shouts);
            Assert.True(SchedulerHarness.WaitFor(() => rig.Log.Lines.Any(l => l.Contains(item.ToString()) && l.Contains("boom"))), "the log line");
            Assert.Single(rig.Log.Lines, l => l.Contains(item.ToString()) && l.Contains("boom"));
            Assert.False(rig.ItemOf(item).ScriptRunning);
        }
    }
}
