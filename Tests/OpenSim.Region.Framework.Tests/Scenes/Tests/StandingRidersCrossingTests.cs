/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// An object that crosses into a neighbouring region takes the avatars standing on it along (their soft link, see
/// <see cref="SoftLink"/>), the way it already takes the seated ones. Two regions run in one process; the crossing
/// thread runs inline so each step is finished when the call returns.
/// </summary>
public class StandingRidersCrossingTests : OpenSimTestCase
{
    private static readonly Vector3 PlatformStart = new(128, 10, 2);

    // the platform's origin leaves the region by a tad, and arrives 0.2 m inside the neighbour
    private static readonly Vector3 PlatformTarget = new(128, -0.05f, 2);

    private readonly List<(ScenePresence rider, Vector3 offset)> m_riders = new();

    private TestScene m_sceneA;
    private TestScene m_sceneB;
    private SceneObjectGroup m_platform;
    private UUID m_platformId;

    public StandingRidersCrossingTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private void SetupTwoRegions()
    {
        EntityTransferModule etmA = new();
        EntityTransferModule etmB = new();
        LocalSimulationConnectorModule lscm = new();

        IConfigSource config = new IniConfigSource();
        IConfig modulesConfig = config.AddConfig("Modules");
        modulesConfig.Set("EntityTransferModule", etmA.Name);
        modulesConfig.Set("SimulationServices", lscm.Name);
        IConfig entityTransferConfig = config.AddConfig("EntityTransfer");

        // a single threaded test cannot wait for the destination's callback
        entityTransferConfig.Set("wait_for_callback", false);

        SceneHelpers sh = new();
        m_sceneA = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), 1000, 1000);
        m_sceneB = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), 1000, 999);

        SceneHelpers.SetupSceneModules(new Scene[] { m_sceneA, m_sceneB }, config, lscm);
        SceneHelpers.SetupSceneModules(m_sceneA, config, new CapabilitiesModule(), etmA);
        SceneHelpers.SetupSceneModules(m_sceneB, config, new CapabilitiesModule(), etmB);

        m_platform = SceneHelpers.AddSceneObject(m_sceneA, 1, TestHelpers.ParseTail(0x1), "", 0x2);
        m_platformId = m_platform.UUID;
        m_platform.AbsolutePosition = PlatformStart;
    }

    /// <summary>An avatar in the first region at <paramref name="offset"/> from the platform's origin.</summary>
    private ScenePresence AddAvatar(int userTail, Vector3 offset, bool linked)
    {
        UUID userId = TestHelpers.ParseTail(userTail);
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(userId);
        // the helper gives every agent the same circuit code, and two avatars cannot share one
        acd.circuitcode = (uint)(1000 + userTail);
        TestClient tc = new(acd, m_sceneA);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, new List<TestClient>());

        ScenePresence sp = SceneHelpers.AddScenePresence(m_sceneA, tc, acd);
        sp.AbsolutePosition = PlatformStart + offset;
        m_riders.Add((sp, offset));

        if (linked)
        {
            sp.RideOn.Update(0, m_platformId, true);
            sp.RideOn.Update(RideOnLimits.StartMs, m_platformId, true);
            Assert.True(sp.RideOn.IsLinked);
        }

        return sp;
    }

    private void CrossPlatform()
    {
        m_sceneA.Update(4);
        m_sceneB.Update(4);

        // the engine carries a standing avatar along with its platform: do the same here
        foreach ((ScenePresence rider, Vector3 offset) in m_riders)
            rider.AbsolutePosition = PlatformTarget + offset;

        m_sceneA.SceneGraph.UpdatePrimGroupPosition(m_platform.LocalId, PlatformTarget, m_riders[0].rider.ControllingClient);
        m_sceneA.Update(4);
        m_sceneB.Update(4);
    }

    private ScenePresence Arrive(UUID userId)
    {
        ScenePresence sp = m_sceneB.GetScenePresence(userId);
        Assert.NotNull(sp);
        ((TestClient)sp.ControllingClient).CompleteMovement();
        m_sceneA.Update(4);
        m_sceneB.Update(4);
        return sp;
    }

    [Fact]
    public void A_standing_rider_crosses_with_the_object_and_arrives_at_the_same_offset()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        Vector3 offset = new(0.5f, 0f, 0.1f);
        ScenePresence rider = AddAvatar(0x11, offset, linked: true);
        Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.0f);
        rider.Rotation = facing;

        CrossPlatform();

        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platformId));
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        Assert.NotNull(arrived);

        Assert.True(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.False(rider.RideOn.IsLinked);
        ScenePresence riderB = Arrive(rider.UUID);

        Assert.False(riderB.IsChildAgent);
        Vector3 expected = arrived.AbsolutePosition + offset;
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, expected), 0f, 0.05f);
        Assert.InRange(Quaternion.Dot(riderB.Rotation, facing), 0.999f, 1.001f);
    }

    [Fact]
    public void Two_standing_riders_each_keep_their_own_offset()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        Vector3 offset1 = new(0.5f, 0f, 0.1f);
        Vector3 offset2 = new(-0.5f, 0f, 0.1f);
        ScenePresence rider1 = AddAvatar(0x11, offset1, linked: true);
        ScenePresence rider2 = AddAvatar(0x12, offset2, linked: true);

        CrossPlatform();

        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        Assert.NotNull(arrived);
        ScenePresence r1 = Arrive(rider1.UUID);
        ScenePresence r2 = Arrive(rider2.UUID);

        Assert.InRange(Vector3.Distance(r1.AbsolutePosition, arrived.AbsolutePosition + offset1), 0f, 0.05f);
        Assert.InRange(Vector3.Distance(r2.AbsolutePosition, arrived.AbsolutePosition + offset2), 0f, 0.05f);
    }

    [Fact]
    public void An_avatar_that_is_not_linked_stays_behind()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence bystander = AddAvatar(0x11, new Vector3(0.5f, 0f, 0.1f), linked: false);

        CrossPlatform();

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platformId));
        Assert.False(m_sceneA.GetScenePresence(bystander.UUID).IsChildAgent);
        Assert.True(m_sceneB.GetScenePresence(bystander.UUID)?.IsChildAgent ?? true);
    }

    [Fact]
    public void A_link_to_an_avatar_that_moved_away_without_reports_is_dropped_at_the_crossing()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        // well outside the object's bounds plus the position gate, and no contact report since
        float far = 0.25f + RideOnLimits.PositionGate + 5f;
        ScenePresence gone = AddAvatar(0x11, new Vector3(far, 0f, 0.1f), linked: true);

        CrossPlatform();

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platformId));
        Assert.False(gone.RideOn.IsLinked);
        Assert.False(m_sceneA.GetScenePresence(gone.UUID).IsChildAgent);
        Assert.True(m_sceneB.GetScenePresence(gone.UUID)?.IsChildAgent ?? true);
    }

    [Fact]
    public void A_rider_inside_the_position_gate_next_to_the_object_is_still_carried()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        float near = 0.25f + RideOnLimits.PositionGate - 0.1f;
        ScenePresence edge = AddAvatar(0x11, new Vector3(near, 0f, 0.1f), linked: true);

        CrossPlatform();

        Assert.True(m_sceneA.GetScenePresence(edge.UUID).IsChildAgent);
    }

    [Fact]
    public void The_carried_flag_and_the_offset_survive_the_agent_data_wire_format()
    {
        UUID group = UUID.Random();
        AgentData sent = new()
        {
            AgentID = UUID.Random(),
            Position = new Vector3(1, 2, 3),
            CrossingFlags = (byte)(1 | AgentData.CrossingCarried),
            CarriedByGroup = group,
            CarriedOffset = new Vector3(0.5f, -1.5f, 0.25f)
        };

        AgentData received = new();
        received.Unpack(sent.Pack(new EntityTransferContext()), null, new EntityTransferContext());

        Assert.Equal(sent.CrossingFlags, received.CrossingFlags);
        Assert.Equal(group, received.CarriedByGroup);
        Assert.Equal(sent.CarriedOffset, received.CarriedOffset);

        AgentData plain = new() { AgentID = UUID.Random(), CrossingFlags = 1 };
        AgentData plainReceived = new();
        plainReceived.Unpack(plain.Pack(new EntityTransferContext()), null, new EntityTransferContext());
        Assert.Equal(UUID.Zero, plainReceived.CarriedByGroup);
    }
}
