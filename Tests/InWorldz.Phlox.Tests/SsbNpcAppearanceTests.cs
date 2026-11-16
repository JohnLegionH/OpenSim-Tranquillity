/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An NPC's appearance on a region with and without server-side baking.
///
/// <para>
/// An NPC is created from a copy of its owner's appearance (<c>NPCModule.CreateNPC</c>, <c>BotManager.CreateBot</c>),
/// including the owner's baked faces and <c>AppearanceMessage_Version</c> (id 11000, index 251), which the owner's
/// viewer sends as 1 once it is on the server-bake path. The viewer prefers that parameter over the AppearanceData
/// block (<c>resolve_appearance_version</c>), so a 1 with no bake behind it sends the viewer to the appearance
/// service under the NPC's UUID.
/// </para>
///
/// <para>
/// On a server-baking region the region bakes the NPC (<c>ServerSideBakingModule</c>): the copy keeps no baked
/// faces, and the message is an avatar's. On a region without server-side baking nothing bakes the NPC, so the
/// copy keeps the owner's baked faces and the parameter goes out as 0, the message every avatar carried before
/// server-side baking.
/// </para>
/// </summary>
public class SsbNpcAppearanceTests
{
    private readonly ITestOutputHelper _out;
    public SsbNpcAppearanceTests(ITestOutputHelper o) => _out = o;

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

    private static SchedulerHarness NpcScene()
    {
        var h = new SchedulerHarness(cfg =>
        {
            cfg.AddConfig("NPC").Set("Enabled", "true");
            cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            cfg.AddConfig("Chat");
        });
        SceneHelpers.SetupSceneModules(h.Scene, h.Config,
            new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(), new NPCModule(),
            new BasicInventoryAccessModule(), new BotManager(), new ChatModule());
        Assert.NotNull(h.Scene.RequestModuleInterface<IBotManager>());
        return h;
    }

    /// <summary>
    /// What the owner looks like after this region baked them: every baked face points at a stored bake asset,
    /// and the parameter the viewer sends back on the server-bake path is 1
    /// (<c>F32_to_U8(1.0, 0, 255)</c>, <c>llagent.cpp sendAgentSetAppearance</c>).
    /// </summary>
    private static Dictionary<int, UUID> MakeBaked(ScenePresence owner)
    {
        var bakes = new Dictionary<int, UUID>();
        foreach (int face in AvatarAppearance.BAKE_INDICES)
        {
            var id = UUID.Random();
            bakes[face] = id;
            owner.Appearance.Texture.CreateFace((uint)face).TextureID = id;
        }
        var vp = new byte[Math.Max(owner.Appearance.VisualParams?.Length ?? 0, VersionIndex + 1)];
        if (owner.Appearance.VisualParams is { } old) Array.Copy(old, vp, old.Length);
        vp[VersionIndex] = 1;
        owner.Appearance.VisualParams = vp;
        return bakes;
    }

    private static CapturingClient AddObserver(Scene scene)
    {
        var acd = SceneHelpers.GenerateAgentData(UUID.Random());
        var client = new CapturingClient(acd, scene);
        SceneHelpers.AddScenePresence(scene, client, acd);
        return client;
    }

    private static Dictionary<int, UUID> BakedFaces(byte[] textureEntryBytes)
    {
        var te = new Primitive.TextureEntry(textureEntryBytes, 0, textureEntryBytes.Length);
        return AvatarAppearance.BAKE_INDICES.ToDictionary(f => (int)f, f => te.FaceTextures[f]?.TextureID ?? te.DefaultTexture.TextureID);
    }

    // ------------------------------------------------------------------ the clone

    [Fact]
    public void OnAServerBakedRegionAnNpcClonedFromABakedOwnerCarriesNoneOfTheOwnersBakedFaces()
    {
        using var h = NpcScene();
        var region = new ServerSideBakingRegion(true, new CofHandshake());
        h.Scene.RegisterModuleInterface<IServerSideBakingRegion>(region);

        var owner = h.Scene.GetScenePresence(h.AddClient().AgentId);
        MakeBaked(owner);
        region.RecordBake(owner.UUID, 7);

        var bots = h.Scene.RequestModuleInterface<IBotManager>();
        var npcId = bots.CreateBot("Test", "Npc", owner.AbsolutePosition + new Vector3(2, 0, 0), "", UUID.Zero, owner.UUID, true, true, out string reason);
        Assert.True(npcId != UUID.Zero, reason);
        var npc = h.Scene.GetScenePresence(npcId);
        Assert.True(npc.IsNPC);

        foreach (int face in AvatarAppearance.BAKE_INDICES)
            Assert.Null(npc.Appearance.Texture.FaceTextures[face]);
        Assert.Equal(1, npc.Appearance.VisualParams[VersionIndex]);   // the owner's appearance-version byte still comes along
    }

    [Fact]
    public void OnARegionWithoutSsbAnNpcClonedFromABakedOwnerKeepsTheOwnersBakedFaces()
    {
        using var h = NpcScene();
        Assert.Null(h.Scene.RequestModuleInterface<IServerSideBakingRegion>());

        var owner = h.Scene.GetScenePresence(h.AddClient().AgentId);
        var bakes = MakeBaked(owner);

        var bots = h.Scene.RequestModuleInterface<IBotManager>();
        var npcId = bots.CreateBot("Test", "Npc", owner.AbsolutePosition + new Vector3(2, 0, 0), "", UUID.Zero, owner.UUID, true, true, out string reason);
        Assert.True(npcId != UUID.Zero, reason);
        var npc = h.Scene.GetScenePresence(npcId);

        foreach (int face in AvatarAppearance.BAKE_INDICES)
            Assert.Equal(bakes[face], npc.Appearance.Texture.FaceTextures[face]?.TextureID);
    }

    // ------------------------------------------------------------------ the wire

    /// <summary>
    /// On a server-baked region the region bakes the NPC (ServerSideBakingModule), so its message is the one an
    /// avatar's is: with the bake recorded, the AppearanceData block and the parameter both say 1; before the bake
    /// lands, the parameter is not rewritten to hide the NPC from the appearance service.
    /// </summary>
    [Fact]
    public void OnAServerBakedRegionTheNpcsMessageIsAnAvatarsMessage()
    {
        using var h = NpcScene();
        var region = new ServerSideBakingRegion(true, new CofHandshake());
        h.Scene.RegisterModuleInterface<IServerSideBakingRegion>(region);

        var owner = h.Scene.GetScenePresence(h.AddClient().AgentId);
        MakeBaked(owner);
        var observer = AddObserver(h.Scene);

        var bots = h.Scene.RequestModuleInterface<IBotManager>();
        var npcId = bots.CreateBot("Test", "Npc", owner.AbsolutePosition + new Vector3(2, 0, 0), "", UUID.Zero, owner.UUID, true, true, out string reason);
        Assert.True(npcId != UUID.Zero, reason);
        var npc = h.Scene.GetScenePresence(npcId);

        npc.SendAppearanceToAllOtherAgents();
        var before = observer.LastFor(npcId);
        Assert.Equal(-1, before.Cof);
        Assert.Equal(1, before.Params[VersionIndex]);

        region.RecordBake(npcId, 0);
        npc.SendAppearanceToAllOtherAgents();
        var after = observer.LastFor(npcId);
        Assert.Equal(0, after.Cof);
        Assert.Equal(1, after.Params[VersionIndex]);
    }

    /// <summary>
    /// A region with no baking module at all: an NPC is not server-baked there, so its message
    /// says 0 there too, while the owner's stored parameters go out untouched as they always have.
    /// </summary>
    [Fact]
    public void OnARegionWithoutSsbTheNpcSaysZeroAndTheOwnerIsUntouched()
    {
        using var h = NpcScene();
        Assert.Null(h.Scene.RequestModuleInterface<IServerSideBakingRegion>());

        var owner = h.Scene.GetScenePresence(h.AddClient().AgentId);
        MakeBaked(owner);                                            // a stored byte of 1 from a session on an SSB region
        var observer = AddObserver(h.Scene);

        var bots = h.Scene.RequestModuleInterface<IBotManager>();
        var npcId = bots.CreateBot("Test", "Npc", owner.AbsolutePosition + new Vector3(2, 0, 0), "", UUID.Zero, owner.UUID, true, true, out string reason);
        Assert.True(npcId != UUID.Zero, reason);
        var npc = h.Scene.GetScenePresence(npcId);

        owner.SendAppearanceToAllOtherAgents();
        npc.SendAppearanceToAllOtherAgents();
        var forOwner = observer.LastFor(owner.UUID);
        var forNpc = observer.LastFor(npcId);

        Assert.Equal(-1, forOwner.Cof);
        Assert.Same(owner.Appearance.VisualParams, forOwner.Params);  // untouched, not even copied
        Assert.Equal(-1, forNpc.Cof);
        Assert.Equal(0, forNpc.Params[VersionIndex]);
    }
}
