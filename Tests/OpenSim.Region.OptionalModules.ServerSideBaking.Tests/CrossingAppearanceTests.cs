/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// An agent that crosses into a neighbouring region. The region it leaves knows which bake it applied; the region
/// it enters has no record of the agent, so without help its first appearance message to everyone in view goes out
/// with no AppearanceData block, which a viewer reads as an appearance of version 0 with no baked data. The version
/// of the bake therefore travels with the agent's data, and the region it enters describes the carried bakes from
/// the first message on.
/// </summary>
// Process-wide state: SceneHelpers sets MainConsole.Instance and adds scenes to SceneManager. No other class in
// this project touches either, and the tests within one class run one at a time.
public class CrossingAppearanceTests
{
    private const int VersionIndex = AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX;

    /// <summary>An observer whose client records every AvatarAppearance the region sends it.</summary>
    private sealed class CapturingClient : TestClient
    {
        public readonly List<(UUID Agent, byte[] Params, byte[] Te, int Cof)> Appearances = new();
        public CapturingClient(AgentCircuitData acd, Scene scene) : base(acd, scene) { }
        public override void SendAppearance(UUID agentID, byte[] visualParams, byte[] textureEntry, float hover, int cofVersion)
        {
            lock (Appearances) Appearances.Add((agentID, visualParams, textureEntry, cofVersion));
        }
        public override void SendAppearance(UUID agentID, byte[] visualParams, byte[] textureEntry, float hover)
        {
            lock (Appearances) Appearances.Add((agentID, visualParams, textureEntry, -1));
        }
        public (UUID Agent, byte[] Params, byte[] Te, int Cof) LastFor(UUID agent)
        {
            lock (Appearances) return Appearances.Last(a => a.Agent == agent);
        }
    }

    private sealed record Rig(Scene From, ServerSideBakingRegion FromRegion, Scene To, ServerSideBakingRegion ToRegion,
        ScenePresence Mover, CapturingClient Observer);

    private static ServerSideBakingRegion AddModule(Scene scene, bool flag)
    {
        var config = new IniConfigSource();
        var section = config.AddConfig(ServerSideBakingModule.ConfigSection);
        section.Set("ServerSideBaking", flag ? "true" : "false");
        var module = new ServerSideBakingModule();
        SceneHelpers.SetupSceneModules(scene, config, module);
        return module.RegionOf(scene);
    }

    /// <summary>Two regions side by side, a root agent in the first, and an observer standing in the second.</summary>
    private static Rig Setup(bool destinationFlag = true)
    {
        var helpers = new SceneHelpers();
        var from = helpers.SetupScene("From", UUID.Random(), 1000, 1000);
        var to = helpers.SetupScene("To", UUID.Random(), 1001, 1000);

        var mover = SceneHelpers.AddScenePresence(from, UUID.Random());
        mover.Appearance.VisualParams = new byte[253];   // the block a viewer uploads
        var observerAcd = SceneHelpers.GenerateAgentData(UUID.Random());
        var observer = new CapturingClient(observerAcd, to);
        SceneHelpers.AddScenePresence(to, observer, observerAcd);

        var fromRegion = AddModule(from, true);
        var toRegion = AddModule(to, destinationFlag);
        return new Rig(from, fromRegion, to, toRegion, mover, observer);
    }

    /// <summary>What the region it leaves sends for the agent, through the wire form, as the destination receives it.</summary>
    private static AgentData Leave(ScenePresence mover)
    {
        var sent = new AgentData();
        mover.CopyTo(sent, true);
        var back = new AgentData();
        back.Unpack(sent.Pack(new EntityTransferContext()), null, new EntityTransferContext());
        return back;
    }

    private static ScenePresence Arrive(Rig rig, AgentData data)
    {
        var child = SceneHelpers.AddChildScenePresence(rig.To, rig.Mover.UUID);
        child.UpdateChildAgent(data);
        return child;
    }

    [Fact]
    public void TheDepartingAgentDataCarriesTheVersionOfTheBakeTheRegionApplied()
    {
        var rig = Setup();
        rig.FromRegion.RecordBake(rig.Mover.UUID, 9);

        var sent = new AgentData();
        rig.Mover.CopyTo(sent, true);

        Assert.Equal(9, sent.BakedCofVersion);
    }

    [Fact]
    public void AnAgentTheRegionNeverBakedDepartsWithNoBake()
    {
        var rig = Setup();

        var sent = new AgentData();
        rig.Mover.CopyTo(sent, true);

        Assert.Equal(-1, sent.BakedCofVersion);
    }

    [Fact]
    public void TheRegionAnAgentArrivesInKnowsTheCarriedBake()
    {
        var rig = Setup();
        rig.FromRegion.RecordBake(rig.Mover.UUID, 9);

        Arrive(rig, Leave(rig.Mover));

        Assert.Equal(9, rig.ToRegion.BakedCofVersion(rig.Mover.UUID));
    }

    /// <summary>The point: the first appearance message an observer gets for the arrival already has the block.</summary>
    [Fact]
    public void TheFirstAppearanceAnObserverGetsForAnArrivalCarriesTheAppearanceDataBlock()
    {
        var rig = Setup();
        rig.FromRegion.RecordBake(rig.Mover.UUID, 9);

        var arrived = Arrive(rig, Leave(rig.Mover));
        arrived.SendAppearanceToAgentNF(rig.To.GetScenePresence(rig.Observer.AgentId));

        var sent = rig.Observer.LastFor(rig.Mover.UUID);
        Assert.Equal(9, sent.Cof);
        Assert.Equal(1, sent.Params[VersionIndex]);   // block and parameter agree, or the viewer drops the message
    }

    [Fact]
    public void AnAgentThatArrivedWithNoBakeIsSentAsBefore()
    {
        var rig = Setup();

        var arrived = Arrive(rig, Leave(rig.Mover));
        arrived.SendAppearanceToAgentNF(rig.To.GetScenePresence(rig.Observer.AgentId));

        Assert.Equal(-1, rig.ToRegion.BakedCofVersion(rig.Mover.UUID));
        Assert.Equal(-1, rig.Observer.LastFor(rig.Mover.UUID).Cof);
    }

    /// <summary>Where the flag is off the wire stays as it always was (ADR-001): nothing is recorded.</summary>
    [Fact]
    public void ARegionWithServerSideBakingOffRecordsNothingForAnArrival()
    {
        var rig = Setup(destinationFlag: false);
        rig.FromRegion.RecordBake(rig.Mover.UUID, 9);

        var arrived = Arrive(rig, Leave(rig.Mover));
        arrived.SendAppearanceToAgentNF(rig.To.GetScenePresence(rig.Observer.AgentId));

        Assert.Equal(-1, rig.ToRegion.BakedCofVersion(rig.Mover.UUID));
        Assert.Equal(-1, rig.Observer.LastFor(rig.Mover.UUID).Cof);
    }

    /// <summary>The control for the three above: the carried bake is a first record only, and a later bake replaces it.</summary>
    [Fact]
    public void ABakeAfterTheArrivalReplacesTheCarriedVersion()
    {
        var rig = Setup();
        rig.FromRegion.RecordBake(rig.Mover.UUID, 9);
        Arrive(rig, Leave(rig.Mover));

        rig.ToRegion.RecordBake(rig.Mover.UUID, 12);

        Assert.Equal(12, rig.ToRegion.BakedCofVersion(rig.Mover.UUID));
    }
}
