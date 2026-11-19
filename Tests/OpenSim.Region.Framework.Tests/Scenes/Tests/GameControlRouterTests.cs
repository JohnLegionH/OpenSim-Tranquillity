/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse.Packets;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.GameControl;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// The router turns the region's game-controller events into game_control posts. The SL wiki says the input goes
    /// only to scripts the avatar has granted PERMISSION_GAME_CONTROL (granted without a dialog to a script in an
    /// attachment or in an object the avatar sits on), so a script gets a state when its item holds the bit granted by
    /// that avatar: found in the avatar's worn attachments, in the object it sits on, and among the scripts an engine
    /// reported a grant for (the remote-control case). The post goes through IScriptModule, with the snapshot as the
    /// event's data.
    ///
    /// <para>Runs with the other logger-swapping scene tests only because it shares their helper style; it changes
    /// nothing process-wide, so it runs in parallel.</para>
    /// </summary>
    public class GameControlRouterTests : OpenSimTestCase
    {
        private const int GameControl = 0x100000;

        /// <summary>An IScriptModule that knows the items it is told of and records every post.</summary>
        public class RecordingScripts : DispatchProxy
        {
            public readonly HashSet<UUID> Known = new();
            public readonly List<(UUID Item, string Name, object[] Args, object Data)> Posts = new();

            public static IScriptModule Create(out RecordingScripts rec)
            {
                var p = Create<IScriptModule, RecordingScripts>();
                rec = (RecordingScripts)(object)p;
                return p;
            }

            protected override object Invoke(MethodInfo m, object[] args)
            {
                switch (m.Name)
                {
                    case "HasScript":
                        args[1] = true;
                        return Known.Contains((UUID)args[0]);
                    case "PostScriptEvent":
                        lock (Posts)
                            Posts.Add(((UUID)args[0], (string)args[1], (object[])args[2], args.Length > 3 ? args[3] : null));
                        return true;
                }
                var rt = m.ReturnType;
                return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
            }
        }

        private sealed class Rig
        {
            public Scene Scene;
            public GameControlRouterModule Module;
            public RecordingScripts Engine;
            public int Posts { get { lock (Engine.Posts) return Engine.Posts.Count; } }
            public List<(UUID Item, string Name, object[] Args, object Data)> All { get { lock (Engine.Posts) return Engine.Posts.ToList(); } }
        }

        private static Rig NewRig(bool enabled = true)
        {
            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene();
            scene.GameControlEnabled = enabled;
            scene.RegisterModuleInterface(RecordingScripts.Create(out RecordingScripts engine));
            var module = new GameControlRouterModule();
            SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), module);
            return new Rig { Scene = scene, Module = module, Engine = engine };
        }

        private static ScenePresence AddAgent(Rig r) => SceneHelpers.AddScenePresence(r.Scene, UUID.Random());

        /// <summary>A script item in <paramref name="part"/>, known to the engine, holding a grant from <paramref name="granter"/>.</summary>
        private static UUID AddScript(Rig r, SceneObjectPart part, UUID granter, int mask)
        {
            UUID itemId = UUID.Random();
            TaskInventoryItem item = TaskInventoryHelpers.AddScript(r.Scene.AssetService, part, itemId, UUID.Random(), "script", "default{}");
            item.PermsGranter = granter;
            item.PermsMask = mask;
            r.Engine.Known.Add(itemId);
            return itemId;
        }

        private static SceneObjectGroup Wear(Rig r, ScenePresence sp)
        {
            SceneObjectGroup sog = SceneHelpers.AddSceneObject(r.Scene, "Worn", sp.UUID);
            sog.AttachedAvatar = sp.UUID;
            sog.IsAttachment = true;
            sog.AttachmentPoint = 3;
            sp.AddAttachment(sog);
            return sog;
        }

        private static void SitOn(ScenePresence sp, SceneObjectPart seat)
        {
            sp.AbsolutePosition = seat.ParentGroup.AbsolutePosition + new Vector3(1, 1, 0);
            sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, seat.UUID, Vector3.Zero);
            Assert.Equal(seat.ParentGroup, sp.ParentPart?.ParentGroup);
        }

        private static int s_sequence;

        /// <summary>Sends a state for <paramref name="agent"/>; each call differs from the last, so none is dropped as a resend.</summary>
        private static void Send(Scene scene, UUID agent, short axis0)
        {
            var p = new GameControlDataPacket();
            p.AgentData.AgentID = agent;
            p.AgentData.SessionID = UUID.Random();
            p.AgentData.Packet = (byte)(++s_sequence % 120);
            p.AgentData.ActionMode = 0;
            p.Axes = new[] { new GameControlDataPacket.AxesBlock { Value = axis0 } };
            p.Buttons = new[] { new GameControlDataPacket.ButtonsBlock { Flags = 0x5u } };
            p.ModeAxes = Array.Empty<GameControlDataPacket.ModeAxesBlock>();
            p.ModeButtons = Array.Empty<GameControlDataPacket.ModeButtonsBlock>();
            scene.HandleGameControlData(agent, p);
        }

        [Fact]
        public void AWornScriptWithTheGrantGetsTheWearersStateWithTheSnapshotAsEventData()
        {
            var r = NewRig();
            var wearer = AddAgent(r);
            var worn = Wear(r, wearer);
            UUID item = AddScript(r, worn.RootPart, wearer.UUID, GameControl);

            Send(r.Scene, wearer.UUID, 16384);

            var post = Assert.Single(r.All);
            Assert.Equal(item, post.Item);
            Assert.Equal("game_control", post.Name);
            Assert.Equal(3, post.Args.Length);
            Assert.Equal(wearer.UUID.ToString(), post.Args[0].ToString());
            Assert.Equal(0x5, Convert.ToInt32(post.Args[1]));
            var axes = Assert.IsType<object[]>(post.Args[2]);
            Assert.Equal(6, axes.Length);
            Assert.Equal(16384 / 32767f, Convert.ToSingle(axes[0]), 4);
            var snapshot = Assert.IsType<GameControlSnapshot>(post.Data);
            Assert.Equal(0x5u, snapshot.RawButtons);
        }

        [Fact]
        public void AWornScriptWithoutTheGrantGetsNothing()
        {
            var r = NewRig();
            var wearer = AddAgent(r);
            var worn = Wear(r, wearer);
            UUID granted = AddScript(r, worn.RootPart, wearer.UUID, GameControl);          // control
            AddScript(r, worn.RootPart, wearer.UUID, 0x4);                                 // another bit only
            AddScript(r, worn.RootPart, UUID.Zero, 0);                                     // none

            Send(r.Scene, wearer.UUID, 100);

            Assert.Equal(granted, Assert.Single(r.All).Item);
        }

        [Fact]
        public void AScriptInTheSeatGetsOnlyItsOwnSittersInputWhenTwoAvatarsSit()
        {
            var r = NewRig();
            var a = AddAgent(r);
            var b = AddAgent(r);
            SceneObjectGroup seat = SceneHelpers.AddSceneObject(r.Scene, "Seat", a.UUID);
            seat.AbsolutePosition = new Vector3(128, 128, 30);
            SceneObjectPart second = SceneHelpers.CreateSceneObjectPart("second", UUID.Random(), a.UUID);
            seat.AddPart(second);
            SitOn(a, seat.RootPart);
            SitOn(b, seat.RootPart);
            UUID forA = AddScript(r, seat.RootPart, a.UUID, GameControl);
            UUID forB = AddScript(r, second, b.UUID, GameControl);

            Send(r.Scene, a.UUID, 100);
            Assert.Equal(forA, Assert.Single(r.All).Item);

            Send(r.Scene, b.UUID, 200);
            var all = r.All;
            Assert.Equal(2, all.Count);
            Assert.Equal(forB, all[1].Item);
            Assert.Equal(b.UUID.ToString(), all[1].Args[0].ToString());
        }

        [Fact]
        public void AScriptElsewhereGetsTheInputOfTheAvatarWhoGrantedItOnceItsEngineReportsTheGrant()
        {
            var r = NewRig();
            var granter = AddAgent(r);
            SceneObjectGroup remote = SceneHelpers.AddSceneObject(r.Scene, "Remote", UUID.Random());
            UUID item = AddScript(r, remote.RootPart, granter.UUID, GameControl);

            Send(r.Scene, granter.UUID, 100);
            Assert.Equal(0, r.Posts);                        // not found until the grant is reported

            r.Scene.EventManager.TriggerScriptPermissionsGranted(remote.RootPart.UUID, item, granter.UUID, GameControl);
            Send(r.Scene, granter.UUID, 200);

            Assert.Equal(item, Assert.Single(r.All).Item);
        }

        [Fact]
        public void ARevokedGrantStopsTheDelivery()
        {
            var r = NewRig();
            var granter = AddAgent(r);
            SceneObjectGroup remote = SceneHelpers.AddSceneObject(r.Scene, "Remote", UUID.Random());
            UUID item = AddScript(r, remote.RootPart, granter.UUID, GameControl);
            r.Scene.EventManager.TriggerScriptPermissionsGranted(remote.RootPart.UUID, item, granter.UUID, GameControl);
            Send(r.Scene, granter.UUID, 100);
            Assert.Single(r.All);                            // control: it was delivered while granted

            remote.RemoveScriptsPermissions(granter, GameControl);
            Send(r.Scene, granter.UUID, 200);

            Assert.Single(r.All);
        }

        [Fact]
        public void AnotherAvatarsInputIsNotDeliveredToAScriptThatAnAvatarGranted()
        {
            var r = NewRig();
            var granter = AddAgent(r);
            var other = AddAgent(r);
            SceneObjectGroup remote = SceneHelpers.AddSceneObject(r.Scene, "Remote", UUID.Random());
            UUID item = AddScript(r, remote.RootPart, granter.UUID, GameControl);
            r.Scene.EventManager.TriggerScriptPermissionsGranted(remote.RootPart.UUID, item, granter.UUID, GameControl);

            Send(r.Scene, other.UUID, 100);
            Assert.Equal(0, r.Posts);

            Send(r.Scene, granter.UUID, 100);                // control: the granter's own input arrives
            Assert.Equal(item, Assert.Single(r.All).Item);
        }

        [Fact]
        public void AScriptThatQualifiesTwiceGetsEachStateOnce()
        {
            var r = NewRig();
            var wearer = AddAgent(r);
            var worn = Wear(r, wearer);
            UUID item = AddScript(r, worn.RootPart, wearer.UUID, GameControl);
            r.Scene.EventManager.TriggerScriptPermissionsGranted(worn.RootPart.UUID, item, wearer.UUID, GameControl);
            r.Scene.EventManager.TriggerScriptPermissionsGranted(worn.RootPart.UUID, item, wearer.UUID, GameControl);

            Send(r.Scene, wearer.UUID, 100);
            Send(r.Scene, wearer.UUID, 200);

            Assert.Equal(2, r.Posts);
            Assert.All(r.All, p => Assert.Equal(item, p.Item));
        }

        [Fact]
        public void ADisabledRegionPostsNothing()
        {
            var r = NewRig();
            var wearer = AddAgent(r);
            var worn = Wear(r, wearer);
            AddScript(r, worn.RootPart, wearer.UUID, GameControl);
            Send(r.Scene, wearer.UUID, 100);
            Assert.Single(r.All);                            // control: enabled, it posts

            r.Scene.GameControlEnabled = false;
            Send(r.Scene, wearer.UUID, 200);

            Assert.Single(r.All);
        }

        // ── the module ────────────────────────────────────────────────────────────

        [Fact]
        public void AModuleForARegionWithGameControlOnSubscribesToItsEvents()
        {
            Assert.True(NewRig(enabled: true).Module.Active);
        }

        [Fact]
        public void AModuleForARegionWithGameControlOffSubscribesToNothing()
        {
            var r = NewRig(enabled: false);
            Assert.False(r.Module.Active);

            var wearer = AddAgent(r);
            var worn = Wear(r, wearer);
            AddScript(r, worn.RootPart, wearer.UUID, GameControl);
            r.Scene.GameControlEnabled = true;               // the scene would raise the event now; the module is not listening
            Send(r.Scene, wearer.UUID, 100);

            Assert.Equal(0, r.Posts);
            Assert.Equal(0, r.Module.GrantIndexCount(wearer.UUID));
        }

        [Fact]
        public void AModuleThatIsRemovedFromItsRegionStopsListening()
        {
            var r = NewRig();
            var wearer = AddAgent(r);
            var worn = Wear(r, wearer);
            AddScript(r, worn.RootPart, wearer.UUID, GameControl);
            Send(r.Scene, wearer.UUID, 100);
            Assert.Single(r.All);                            // control: it listens

            r.Module.RemoveRegion(r.Scene);
            Send(r.Scene, wearer.UUID, 200);

            Assert.False(r.Module.Active);
            Assert.Single(r.All);
        }

        // ── the grant index: restart and arrival ──────────────────────────────────

        /// <summary>An object that is not in the scene yet, with a script item that holds a grant from <paramref name="granter"/>.</summary>
        private static (SceneObjectGroup Group, UUID Item) ObjectHoldingAGrant(Rig r, UUID granter, int mask = GameControl)
        {
            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random(), "Arriving", 0x7a);
            UUID item = AddScript(r, sog.RootPart, granter, mask);
            return (sog, item);
        }

        [Fact]
        public void AnObjectThatArrivesHoldingAGrantIsHeardWithoutTheScriptAskingAgain()
        {
            var r = NewRig();
            var granter = AddAgent(r);
            var (arriving, item) = ObjectHoldingAGrant(r, granter.UUID);

            Send(r.Scene, granter.UUID, 100);
            Assert.Equal(0, r.Posts);                        // control: not in the region yet

            Assert.True(r.Scene.AddNewSceneObject(arriving, false));
            Send(r.Scene, granter.UUID, 200);

            Assert.Equal(1, r.Module.GrantIndexCount(granter.UUID));
            Assert.Equal(item, Assert.Single(r.All).Item);
        }

        [Fact]
        public void AnObjectAlreadyInTheRegionWhenTheModuleStartsIsHeardWithoutTheScriptAskingAgain()
        {
            // A region start: the objects are loaded, and an engine has put each script's saved grant on its item.
            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene();
            scene.GameControlEnabled = true;
            scene.RegisterModuleInterface(RecordingScripts.Create(out RecordingScripts engine));
            var r = new Rig { Scene = scene, Engine = engine };
            var granter = AddAgent(r);
            var (existing, item) = ObjectHoldingAGrant(r, granter.UUID);
            Assert.True(scene.AddNewSceneObject(existing, false));

            r.Module = new GameControlRouterModule();
            SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), r.Module);
            Send(scene, granter.UUID, 100);

            Assert.Equal(1, r.Module.GrantIndexCount(granter.UUID));
            Assert.Equal(item, Assert.Single(r.All).Item);
        }

        [Fact]
        public void AnObjectThatLeavesTheRegionIsNoLongerIndexedOrHeard()
        {
            var r = NewRig();
            var granter = AddAgent(r);
            var (arriving, _) = ObjectHoldingAGrant(r, granter.UUID);
            Assert.True(r.Scene.AddNewSceneObject(arriving, false));
            Send(r.Scene, granter.UUID, 100);
            Assert.Single(r.All);                            // control: heard while it is here
            Assert.Equal(1, r.Module.GrantIndexCount(granter.UUID));

            r.Scene.DeleteSceneObject(arriving, false);
            Send(r.Scene, granter.UUID, 200);

            Assert.Equal(0, r.Module.GrantIndexCount(granter.UUID));
            Assert.Single(r.All);
        }

        [Fact]
        public void AGrantThatEndsIsDroppedFromTheIndex()
        {
            var r = NewRig();
            var granter = AddAgent(r);
            var (arriving, item) = ObjectHoldingAGrant(r, granter.UUID);
            Assert.True(r.Scene.AddNewSceneObject(arriving, false));
            Assert.Equal(1, r.Module.GrantIndexCount(granter.UUID));   // control

            arriving.RemoveScriptsPermissions(granter, GameControl);

            Assert.Equal(0, r.Module.GrantIndexCount(granter.UUID));
            Send(r.Scene, granter.UUID, 100);
            Assert.Equal(0, r.Posts);
        }

        [Fact]
        public void AGrantThatPassesToAnotherAvatarMovesToThatAvatarsIndex()
        {
            var r = NewRig();
            var first = AddAgent(r);
            var second = AddAgent(r);
            var (arriving, item) = ObjectHoldingAGrant(r, first.UUID);
            Assert.True(r.Scene.AddNewSceneObject(arriving, false));
            Assert.Equal(1, r.Module.GrantIndexCount(first.UUID));     // control

            TaskInventoryItem held = arriving.RootPart.Inventory.GetInventoryItem(item);
            held.PermsGranter = second.UUID;
            r.Scene.EventManager.TriggerScriptPermissionsGranted(arriving.RootPart.UUID, item, second.UUID, GameControl);

            Assert.Equal(0, r.Module.GrantIndexCount(first.UUID));
            Assert.Equal(1, r.Module.GrantIndexCount(second.UUID));
        }

        [Fact]
        public void AGrantWithoutTheGameControlBitIsNotIndexed()
        {
            var r = NewRig();
            var granter = AddAgent(r);
            var (arriving, _) = ObjectHoldingAGrant(r, granter.UUID, mask: 0x4);
            Assert.True(r.Scene.AddNewSceneObject(arriving, false));

            Assert.Equal(0, r.Module.GrantIndexCount(granter.UUID));
        }

        // ── stand-up: only the grants the seat gave end ────────────────────────────

        private static TaskInventoryItem SeatedWithAGrant(Rig r, ScenePresence sp, int mask, int silent, out SceneObjectGroup seat)
        {
            seat = SceneHelpers.AddSceneObject(r.Scene, "Seat", sp.UUID);
            seat.AbsolutePosition = new Vector3(128, 128, 30);
            SitOn(sp, seat.RootPart);
            UUID item = AddScript(r, seat.RootPart, sp.UUID, mask);
            TaskInventoryItem held = seat.RootPart.Inventory.GetInventoryItem(item);
            held.SilentPermsMask = silent;
            r.Scene.EventManager.TriggerScriptPermissionsGranted(seat.RootPart.UUID, item, sp.UUID, mask);
            return held;
        }

        [Fact]
        public void StandingUpEndsAGameControlGrantTheSeatGaveAutomaticallyAndTheScriptHearsNoMore()
        {
            var r = NewRig();
            var sitter = AddAgent(r);
            TaskInventoryItem held = SeatedWithAGrant(r, sitter, GameControl | 0x4, silent: GameControl | 0x4, out _);
            Send(r.Scene, sitter.UUID, 100);
            Assert.Single(r.All);                            // control: heard while sitting

            sitter.StandUp();
            Send(r.Scene, sitter.UUID, 200);

            Assert.Equal(0, held.PermsMask);
            Assert.Equal(UUID.Zero, held.PermsGranter);
            Assert.Single(r.All);
        }

        [Fact]
        public void StandingUpKeepsAGameControlGrantTheAvatarAnsweredThroughTheDialogAndTheScriptStillHearsThem()
        {
            var r = NewRig();
            var sitter = AddAgent(r);
            // The seat's controls were given automatically; game control was asked for with the dialog.
            TaskInventoryItem held = SeatedWithAGrant(r, sitter, GameControl | 0x4, silent: 0x4, out _);
            Send(r.Scene, sitter.UUID, 100);
            Assert.Single(r.All);                            // control: heard while sitting

            sitter.StandUp();
            Send(r.Scene, sitter.UUID, 200);

            Assert.Equal(GameControl, held.PermsMask);       // the controls went (as before), the dialog grant stayed
            Assert.Equal(sitter.UUID, held.PermsGranter);
            Assert.Equal(2, r.Posts);
        }

        [Fact]
        public void AGrantThatEndsOnStandUpOnlyForTheSitterWhoGaveIt()
        {
            var r = NewRig();
            var a = AddAgent(r);
            var b = AddAgent(r);
            SceneObjectGroup seat = SceneHelpers.AddSceneObject(r.Scene, "Seat", a.UUID);
            seat.AbsolutePosition = new Vector3(128, 128, 30);
            SitOn(a, seat.RootPart);
            SitOn(b, seat.RootPart);
            UUID forA = AddScript(r, seat.RootPart, a.UUID, GameControl);
            UUID forB = AddScript(r, seat.RootPart, b.UUID, GameControl);
            seat.RootPart.Inventory.GetInventoryItem(forA).SilentPermsMask = GameControl;
            seat.RootPart.Inventory.GetInventoryItem(forB).SilentPermsMask = GameControl;

            a.StandUp();

            Assert.Equal(0, seat.RootPart.Inventory.GetInventoryItem(forA).PermsMask);
            Assert.Equal(GameControl, seat.RootPart.Inventory.GetInventoryItem(forB).PermsMask);
        }

        [Fact]
        public void AScriptThatAvatarGrantedThroughTheDialogToTheObjectTheyAreSittingOnAndTheViewerRevokesIsStillRevoked()
        {
            // The silent-only rule belongs to stand-up and detach; an explicit revoke ends whatever kind it is.
            var r = NewRig();
            var sitter = AddAgent(r);
            TaskInventoryItem held = SeatedWithAGrant(r, sitter, GameControl, silent: 0, out SceneObjectGroup seat);

            seat.RemoveScriptsPermissions(sitter, GameControl);

            Assert.Equal(0, held.PermsMask);
        }

        [Fact]
        public void AScriptTheEngineDoesNotRunIsSkippedAndTheOthersStillGetTheState()
        {
            var r = NewRig();
            var wearer = AddAgent(r);
            var worn = Wear(r, wearer);
            UUID running = AddScript(r, worn.RootPart, wearer.UUID, GameControl);
            UUID notRunning = AddScript(r, worn.RootPart, wearer.UUID, GameControl);
            r.Engine.Known.Remove(notRunning);

            Send(r.Scene, wearer.UUID, 100);

            Assert.Equal(running, Assert.Single(r.All).Item);
        }
    }
}
