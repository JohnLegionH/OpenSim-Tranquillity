/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// A region takes a viewer's GameControlData message only when game control is enabled for it and the sender
    /// is a root agent in it; the result is one scene event carrying the agent id and the validated snapshot.
    /// Anything else is dropped without a log line.
    ///
    /// <para>The quiet-drop test swaps the process-wide logger factory (<see cref="LogCapture"/>), so the class
    /// joins the collection the other logger-swapping scene tests use and runs serially with them.</para>
    /// </summary>
    [Collection("process-logger")]
    public class GameControlSceneTests : OpenSimTestCase
    {
        private static GameControlDataPacket Packet(UUID agentId, byte sequence, short axis0 = 0, byte mode = 0)
        {
            var p = new GameControlDataPacket();
            p.AgentData.AgentID = agentId;
            p.AgentData.SessionID = UUID.Random();
            p.AgentData.Packet = sequence;
            p.AgentData.ActionMode = mode;
            p.Axes = new[] { new GameControlDataPacket.AxesBlock { Value = axis0 } };
            p.Buttons = new[] { new GameControlDataPacket.ButtonsBlock { Flags = 0x1u } };
            p.ModeAxes = Array.Empty<GameControlDataPacket.ModeAxesBlock>();
            p.ModeButtons = Array.Empty<GameControlDataPacket.ModeButtonsBlock>();
            return p;
        }

        private sealed class Received
        {
            public readonly List<(UUID Agent, GameControlSnapshot Snapshot)> Events = new();
            public void On(UUID agent, GameControlSnapshot snapshot) => Events.Add((agent, snapshot));
        }

        private static (Scene scene, Received received, UUID agentId) EnabledSceneWithRootAgent()
        {
            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene();
            scene.GameControlEnabled = true;
            Received received = new();
            scene.EventManager.OnGameControlData += received.On;
            UUID agentId = UUID.Random();
            SceneHelpers.AddScenePresence(scene, agentId);
            return (scene, received, agentId);
        }

        [Fact]
        public void AnEnabledRegionRaisesOneEventWithTheAgentAndTheValidatedState()
        {
            var (scene, received, agentId) = EnabledSceneWithRootAgent();

            scene.HandleGameControlData(agentId, Packet(agentId, 1, axis0: 32767, mode: 1));

            var raised = Assert.Single(received.Events);
            Assert.Equal(agentId, raised.Agent);
            Assert.Equal(1f, raised.Snapshot.RawAxes[0]);
            Assert.Equal(0x1u, raised.Snapshot.RawButtons);
            Assert.Equal(GameControlSnapshot.ModeMouselook, raised.Snapshot.Mode);
        }

        [Fact]
        public void ARegionWithGameControlOffDropsEverything()
        {
            var (scene, received, agentId) = EnabledSceneWithRootAgent();
            scene.HandleGameControlData(agentId, Packet(agentId, 1, axis0: 100));
            Assert.Single(received.Events);                    // control: it works while enabled

            scene.GameControlEnabled = false;
            scene.HandleGameControlData(agentId, Packet(agentId, 2, axis0: 200));

            Assert.Single(received.Events);
        }

        [Fact]
        public void GameControlIsOffByDefault()
        {
            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene();
            Received received = new();
            scene.EventManager.OnGameControlData += received.On;
            UUID agentId = UUID.Random();
            SceneHelpers.AddScenePresence(scene, agentId);

            scene.HandleGameControlData(agentId, Packet(agentId, 1, axis0: 100));

            Assert.False(scene.GameControlEnabled);
            Assert.Empty(received.Events);

            scene.GameControlEnabled = true;                                        // control: it works once on
            scene.HandleGameControlData(agentId, Packet(agentId, 2, axis0: 100));
            Assert.Single(received.Events);
        }

        [Fact]
        public void TheConfigSwitchTurnsItOnForTheScene()
        {
            IniConfigSource config = new();
            config.AddConfig("GameControl").Set("Enabled", "true");

            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene("Example Region", UUID.Random(), 1000, 1000, config);

            Assert.True(scene.GameControlEnabled);
        }

        [Fact]
        public void ASenderWithNoPresenceInTheRegionIsDropped()
        {
            var (scene, received, agentId) = EnabledSceneWithRootAgent();
            UUID stranger = UUID.Random();

            scene.HandleGameControlData(stranger, Packet(stranger, 1, axis0: 100));
            Assert.Empty(received.Events);

            scene.HandleGameControlData(agentId, Packet(agentId, 1, axis0: 100));   // control
            Assert.Single(received.Events);
        }

        [Fact]
        public void AChildAgentIsDropped()
        {
            var (scene, received, rootId) = EnabledSceneWithRootAgent();
            UUID childId = UUID.Random();
            SceneHelpers.AddChildScenePresence(scene, childId);

            scene.HandleGameControlData(childId, Packet(childId, 1, axis0: 100));
            Assert.Empty(received.Events);

            scene.HandleGameControlData(rootId, Packet(rootId, 1, axis0: 100));     // control
            Assert.Single(received.Events);
        }

        [Fact]
        public void AMessageNamingAnotherAgentIsDropped()
        {
            var (scene, received, agentId) = EnabledSceneWithRootAgent();
            UUID other = UUID.Random();

            scene.HandleGameControlData(agentId, Packet(other, 1, axis0: 100));
            Assert.Empty(received.Events);

            scene.HandleGameControlData(agentId, Packet(agentId, 2, axis0: 100));   // control
            Assert.Single(received.Events);
        }

        [Fact]
        public void ARepeatedStateRaisesNoSecondEvent()
        {
            var (scene, received, agentId) = EnabledSceneWithRootAgent();

            scene.HandleGameControlData(agentId, Packet(agentId, 1, axis0: 100));
            scene.HandleGameControlData(agentId, Packet(agentId, 2, axis0: 100));
            scene.HandleGameControlData(agentId, Packet(agentId, 3, axis0: 100));
            Assert.Single(received.Events);

            scene.HandleGameControlData(agentId, Packet(agentId, 4, axis0: 200));
            Assert.Equal(2, received.Events.Count);
        }

        [Fact]
        public void EachAgentHasItsOwnState()
        {
            var (scene, received, first) = EnabledSceneWithRootAgent();
            UUID second = UUID.Random();
            SceneHelpers.AddScenePresence(scene, second);

            scene.HandleGameControlData(first, Packet(first, 1, axis0: 100));
            scene.HandleGameControlData(second, Packet(second, 1, axis0: 100));   // same sequence and state, other agent

            Assert.Equal(2, received.Events.Count);
            Assert.Equal(new[] { first, second }, received.Events.Select(e => e.Agent).ToArray());
        }

        [Fact]
        public void AFailingSubscriberDoesNotStopTheHandlerOrOtherSubscribers()
        {
            var (scene, received, agentId) = EnabledSceneWithRootAgent();
            scene.EventManager.OnGameControlData += (_, _) => throw new InvalidOperationException("subscriber failed");
            Received later = new();
            scene.EventManager.OnGameControlData += later.On;

            scene.HandleGameControlData(agentId, Packet(agentId, 1, axis0: 100));

            Assert.Single(received.Events);
            Assert.Single(later.Events);
        }

        [Fact]
        public void DroppedMessagesWriteNothingToTheLog()
        {
            var (scene, received, agentId) = EnabledSceneWithRootAgent();
            UUID stranger = UUID.Random();

            using LogCapture log = new();
            scene.HandleGameControlData(stranger, Packet(stranger, 1, axis0: 100));          // no presence
            scene.HandleGameControlData(agentId, Packet(UUID.Random(), 1, axis0: 100));      // wrong agent
            scene.HandleGameControlData(agentId, Packet(agentId, 2, axis0: 100));            // passes
            scene.HandleGameControlData(agentId, Packet(agentId, 3, axis0: 100));            // resend
            scene.GameControlEnabled = false;
            scene.HandleGameControlData(agentId, Packet(agentId, 4, axis0: 200));            // disabled

            Assert.Single(received.Events);
            Assert.DoesNotContain(log.Messages, m => m.Contains("game", StringComparison.OrdinalIgnoreCase));
        }
    }
}
