/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Services.AvatarService;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// The bakes an NPC and its neighbours hold between them: which asset serves which (channel, input hash), who holds
/// it, and which asset may be deleted once nobody holds it. No scene.
/// </summary>
public class NpcBakeShareTests
{
    private static readonly UUID NpcA = UUID.Random();
    private static readonly UUID NpcB = UUID.Random();
    private const string HashOne = "hash-one";
    private const string HashTwo = "hash-two";

    [Fact]
    public void ABakeNobodyStoredIsNotFound()
    {
        var share = new NpcBakeShare();

        Assert.False(share.TryFind(BakeChannel.Head, HashOne, out var id));
        Assert.Equal(UUID.Zero, id);
    }

    [Fact]
    public void AStoredBakeIsFoundByItsChannelAndHashOnly()
    {
        var share = new NpcBakeShare();
        var asset = UUID.Random();

        share.NoteStored(BakeChannel.Head, HashOne, asset);

        Assert.True(share.TryFind(BakeChannel.Head, HashOne, out var found));
        Assert.Equal(asset, found);
        Assert.False(share.TryFind(BakeChannel.Head, HashTwo, out _));
        Assert.False(share.TryFind(BakeChannel.Upper, HashOne, out _));
    }

    [Fact]
    public void MovingToANewBakeReturnsTheOldOneWhenNobodyElseHoldsIt()
    {
        var share = new NpcBakeShare();
        var first = UUID.Random();
        var second = UUID.Random();
        share.NoteStored(BakeChannel.Head, HashOne, first);
        share.NoteStored(BakeChannel.Head, HashTwo, second);
        Assert.Equal(UUID.Zero, share.Hold(NpcA, BakeChannel.Head, HashOne, first));

        var released = share.Hold(NpcA, BakeChannel.Head, HashTwo, second);

        Assert.Equal(first, released);
        Assert.False(share.TryFind(BakeChannel.Head, HashOne, out _), "a bake that was handed back for deletion is still offered for reuse");
        Assert.True(share.TryFind(BakeChannel.Head, HashTwo, out var kept));
        Assert.Equal(second, kept);
    }

    [Fact]
    public void ABakeAnotherNpcStillHoldsIsNotHandedBack()
    {
        var share = new NpcBakeShare();
        var shared = UUID.Random();
        var other = UUID.Random();
        share.NoteStored(BakeChannel.Head, HashOne, shared);
        share.NoteStored(BakeChannel.Head, HashTwo, other);
        share.Hold(NpcA, BakeChannel.Head, HashOne, shared);
        share.Hold(NpcB, BakeChannel.Head, HashOne, shared);

        Assert.Equal(UUID.Zero, share.Hold(NpcA, BakeChannel.Head, HashTwo, other));
        Assert.True(share.TryFind(BakeChannel.Head, HashOne, out var stillThere));
        Assert.Equal(shared, stillThere);

        Assert.Equal(shared, share.Hold(NpcB, BakeChannel.Head, HashTwo, other));
    }

    [Fact]
    public void ABakeThisProcessDidNotStoreIsNeverHandedBack()
    {
        var share = new NpcBakeShare();
        var fromAnIndex = UUID.Random();
        var fresh = UUID.Random();
        share.NoteStored(BakeChannel.Head, HashTwo, fresh);
        share.Hold(NpcA, BakeChannel.Head, HashOne, fromAnIndex);   // read from a stored index, never stored here

        Assert.Equal(UUID.Zero, share.Hold(NpcA, BakeChannel.Head, HashTwo, fresh));
    }

    [Fact]
    public void AnNpcThatLeavesDeletesNothingAndItsBakesStayAvailable()
    {
        var share = new NpcBakeShare();
        var asset = UUID.Random();
        share.NoteStored(BakeChannel.Head, HashOne, asset);
        share.Hold(NpcA, BakeChannel.Head, HashOne, asset);

        share.Drop(NpcA);

        Assert.True(share.TryFind(BakeChannel.Head, HashOne, out var found));
        Assert.Equal(asset, found);
    }
}

/// <summary>
/// NPCs on a server-baked region get their own bakes, under their own ids, stored and served as an avatar's are.
/// Real scene, real NPC module and avatar factory, the baking module, a fake avatar service.
/// </summary>
// Process-wide state: SceneHelpers sets MainConsole.Instance and adds the scene to SceneManager. No other class in
// this project touches either, and the tests within one class run one at a time.
public class NpcBakeTests
{
    private sealed class TestableAppearanceService : AppearanceService
    {
        public TestableAppearanceService(IAvatarService avatars, IAssetService assets)
            : base(new IniConfigSource(), avatars, assets) { }
    }

    private sealed record Rig(Scene Scene, ServerSideBakingModule Module, ServerSideBakingRegion Region,
        FakeAvatarService Avatars, NPCModule Npcs, ScenePresence Owner);

    private static Rig Setup(bool serverBaking = true)
    {
        var config = new IniConfigSource();
        config.AddConfig("NPC").Set("Enabled", "true");
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        var appearance = config.AddConfig(ServerSideBakingModule.ConfigSection);
        appearance.Set("ServerSideBaking", serverBaking ? "true" : "false");
        appearance.Set("BakeSize", "512");

        var scene = new SceneHelpers().SetupScene("npc bake scene", UUID.Random(), 1000, 1000, 256, 256, config);
        var avatars = new FakeAvatarService();
        scene.RegisterModuleInterface<IAvatarService>(avatars);

        var module = new ServerSideBakingModule();
        var npcs = new NPCModule();
        SceneHelpers.SetupSceneModules(scene, config, new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(),
            new BasicInventoryAccessModule(), npcs, module);

        var owner = SceneHelpers.AddScenePresence(scene, UUID.Random());
        owner.Appearance.Wearables = Wear(scene, owner.UUID, 200, 170, 140);
        return new Rig(scene, module, module.RegionOf(scene), avatars, npcs, owner);
    }

    /// <summary>A shape and a skin whose paint is the given colour; a different colour is a different look.</summary>
    private static AvatarWearable[] Wear(Scene scene, UUID creator, byte r, byte g, byte b)
    {
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();

        void Put(UUID id, string name, AssetType type, byte[] data)
            => scene.AssetService.Store(new AssetBase(id, name, (sbyte)type, creator.ToString()) { Data = data });

        var skinTex = UUID.Random();
        Put(skinTex, "skin", AssetType.Texture, BomAuxChannelTests.Texture(r, g, b));

        var shape = UUID.Random();
        Put(shape, "Shape", AssetType.Bodypart,
            Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Shape, "Shape", new Dictionary<TextureSlot, UUID>())));
        wearables[(int)WearableType.Shape].Add(UUID.Random(), shape);

        var skin = UUID.Random();
        Put(skin, "Skin", AssetType.Bodypart, Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Skin, "Skin",
            new Dictionary<TextureSlot, UUID>
            {
                [TextureSlot.HeadBodypaint] = skinTex, [TextureSlot.UpperBodypaint] = skinTex, [TextureSlot.LowerBodypaint] = skinTex,
            })));
        wearables[(int)WearableType.Skin].Add(UUID.Random(), skin);
        return wearables;
    }

    private static AvatarAppearance Look(Rig rig, byte r, byte g, byte b)
    {
        var look = new AvatarAppearance(rig.Owner.Appearance, true);
        look.Wearables = Wear(rig.Scene, rig.Owner.UUID, r, g, b);
        return look;
    }

    private static UUID MakeNpc(Rig rig, AvatarAppearance look, string last = "Npc")
        => rig.Npcs.CreateNPC("Test", last, new Vector3(128, 128, 30), rig.Owner.UUID, true, rig.Scene, look);

    private static void WaitFor(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            Thread.Sleep(50);
        }
    }

    private static UUID Face(ScenePresence sp, BakeChannel ch)
        => sp.Appearance.Texture.FaceTextures[BakeOrchestrator.FaceOf(ch)]?.TextureID ?? UUID.Zero;

    private static UUID Indexed(Rig rig, UUID agent, BakeChannel ch)
        => BakeIndex.Read(rig.Avatars, agent).TryGet(ch, out var stored) ? stored.AssetId : UUID.Zero;

    private static bool Exists(Rig rig, UUID asset) => !asset.IsZero() && rig.Scene.AssetService.AssetsExist(new[] { asset.ToString() })[0];

    /// <summary>The owner is baked first, as a logged-in avatar is, so the clone carries real bake ids.</summary>
    private static async Task BakeOwner(Rig rig)
    {
        var outcome = await rig.Module.BakeAsync(rig.Owner, BakeReason.Login, CancellationToken.None);
        Assert.True(outcome.Count(ChannelStatus.Baked) > 0, "the owner's outfit baked nothing");
    }

    // ------------------------------------------------------------------ rulings 1 and 6: the NPC's own bake

    [Fact]
    public async Task AnNpcOnAServerBakedRegionIsBakedUnderItsOwnId()
    {
        var rig = Setup();
        await BakeOwner(rig);
        var ownerHead = Face(rig.Owner, BakeChannel.Head);

        var npcId = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true));
        var npc = rig.Scene.GetScenePresence(npcId);
        Assert.True(npc.IsNPC);

        WaitFor(() => rig.Region.BakedCofVersion(npcId) >= 0, "the NPC's bake");

        var head = Indexed(rig, npcId, BakeChannel.Head);
        Assert.NotEqual(UUID.Zero, head);
        Assert.NotEqual(ownerHead, head);
        Assert.Equal(head, Face(npc, BakeChannel.Head));
        Assert.True(Exists(rig, head));
    }

    [Fact]
    public async Task EveryFaceOfTheNpcNamesABakeItOwnsNotTheOwners()
    {
        var rig = Setup();
        await BakeOwner(rig);
        var ownerBakes = BakeOrchestratorFaces(rig.Owner);

        var npcId = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true));
        var npc = rig.Scene.GetScenePresence(npcId);
        WaitFor(() => rig.Region.BakedCofVersion(npcId) >= 0, "the NPC's bake");

        var npcIndex = BakeIndex.Read(rig.Avatars, npcId);
        foreach (var ch in Enum.GetValues<BakeChannel>())
        {
            var face = Face(npc, ch);
            if (face.IsZero()) continue;
            Assert.DoesNotContain(face, ownerBakes);
            Assert.True(npcIndex.TryGet(ch, out var stored) && stored.AssetId == face, $"{ch}: the face is not in the NPC's own index");
        }
    }

    private static List<UUID> BakeOrchestratorFaces(ScenePresence sp)
        => Enum.GetValues<BakeChannel>().Select(ch => Face(sp, ch)).Where(id => !id.IsZero()).ToList();

    [Fact]
    public async Task AnNpcOnARegionWithoutServerSideBakingIsNotBaked()
    {
        var rig = Setup(serverBaking: false);
        var owner = rig.Owner;
        var cloned = new AvatarAppearance(owner.Appearance, true);

        var npcId = MakeNpc(rig, cloned);
        await Task.Delay(1500);   // a "did not happen" window: nothing may bake the NPC

        Assert.Equal(-1, rig.Region.BakedCofVersion(npcId));
        Assert.False(rig.Avatars.Records.ContainsKey(npcId), "an avatar record was written for an NPC on a region with baking off");
    }

    [Fact]
    public async Task TheAppearanceServiceAnswersForTheNpcAndRefusesTheOwnersAsset()
    {
        var rig = Setup();
        await BakeOwner(rig);
        var ownerHead = Face(rig.Owner, BakeChannel.Head);
        var npcId = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true));
        WaitFor(() => rig.Region.BakedCofVersion(npcId) >= 0, "the NPC's bake");
        var npcHead = Face(rig.Scene.GetScenePresence(npcId), BakeChannel.Head);
        var service = new TestableAppearanceService(rig.Avatars, rig.Scene.AssetService);

        var served = service.GetBake(npcId, "head", npcHead);

        Assert.NotNull(served);
        Assert.True(served.Data.Length > 0);
        Assert.Null(service.GetBake(npcId, "head", ownerHead));
    }

    // ------------------------------------------------------------------ ruling 2: the owner no longer reaches the NPC

    [Fact]
    public async Task AnOwnersRebakeLeavesTheNpcsBakesAlone()
    {
        var rig = Setup();
        await BakeOwner(rig);
        var npcId = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true));
        var npc = rig.Scene.GetScenePresence(npcId);
        WaitFor(() => rig.Region.BakedCofVersion(npcId) >= 0, "the NPC's bake");
        var npcHead = Face(npc, BakeChannel.Head);
        Assert.True(Exists(rig, npcHead));

        rig.Owner.Appearance.Wearables = Wear(rig.Scene, rig.Owner.UUID, 20, 90, 200);
        var outcome = await rig.Module.BakeAsync(rig.Owner, BakeReason.CofChanged, CancellationToken.None);
        Assert.True(outcome.Count(ChannelStatus.Baked) > 0 && outcome.Superseded.Count > 0, "the owner's outfit change superseded nothing");

        Assert.Equal(npcHead, Face(npc, BakeChannel.Head));
        Assert.True(Exists(rig, npcHead), "the owner's rebake deleted a bake the NPC uses");
        Assert.Equal(npcHead, Indexed(rig, npcId, BakeChannel.Head));
    }

    // ------------------------------------------------------------------ ruling 1: appearance changes, and sharing

    [Fact]
    public async Task AnAppearanceChangeBakesTheNpcAgainAndSupersedesTheOldBake()
    {
        var rig = Setup();
        await BakeOwner(rig);
        var npcId = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true));
        var npc = rig.Scene.GetScenePresence(npcId);
        WaitFor(() => rig.Region.BakedCofVersion(npcId) >= 0, "the NPC's bake");
        var before = Face(npc, BakeChannel.Head);

        Assert.True(rig.Npcs.SetNPCAppearance(npcId, Look(rig, 20, 90, 200), rig.Scene));

        WaitFor(() => { var h = Face(rig.Scene.GetScenePresence(npcId), BakeChannel.Head); return !h.IsZero() && h != before; }, "the NPC's new bake");
        var after = Face(rig.Scene.GetScenePresence(npcId), BakeChannel.Head);
        Assert.Equal(after, Indexed(rig, npcId, BakeChannel.Head));
        Assert.True(Exists(rig, after));
        WaitFor(() => !Exists(rig, before), "the superseded bake to go");
    }

    [Fact]
    public async Task TwoNpcsInOneOutfitReuseTheSameBakes()
    {
        var rig = Setup();
        await BakeOwner(rig);
        var look = new AvatarAppearance(rig.Owner.Appearance, true);
        var first = MakeNpc(rig, look, "One");
        WaitFor(() => rig.Region.BakedCofVersion(first) >= 0, "the first NPC's bake");

        var second = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true), "Two");
        WaitFor(() => rig.Region.BakedCofVersion(second) >= 0, "the second NPC's bake");

        foreach (var ch in new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower })
        {
            var a = Indexed(rig, first, ch);
            Assert.NotEqual(UUID.Zero, a);
            Assert.Equal(a, Indexed(rig, second, ch));
        }
    }

    [Fact]
    public async Task ChangingOneNpcKeepsTheBakesTheOtherStillUsesAndDeletesThemWhenNoneDo()
    {
        var rig = Setup();
        await BakeOwner(rig);
        var one = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true), "One");
        WaitFor(() => rig.Region.BakedCofVersion(one) >= 0, "the first NPC's bake");
        var two = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true), "Two");
        WaitFor(() => rig.Region.BakedCofVersion(two) >= 0, "the second NPC's bake");
        var shared = Face(rig.Scene.GetScenePresence(one), BakeChannel.Head);
        Assert.Equal(shared, Face(rig.Scene.GetScenePresence(two), BakeChannel.Head));

        Assert.True(rig.Npcs.SetNPCAppearance(one, Look(rig, 20, 90, 200), rig.Scene));
        WaitFor(() => Face(rig.Scene.GetScenePresence(one), BakeChannel.Head) != shared, "the first NPC's new bake");

        Assert.True(Exists(rig, shared), "a bake the second NPC uses was deleted when the first NPC changed");
        var service = new TestableAppearanceService(rig.Avatars, rig.Scene.AssetService);
        Assert.NotNull(service.GetBake(two, "head", shared));

        Assert.True(rig.Npcs.SetNPCAppearance(two, Look(rig, 90, 20, 60), rig.Scene));
        WaitFor(() => Face(rig.Scene.GetScenePresence(two), BakeChannel.Head) != shared, "the second NPC's new bake");
        WaitFor(() => !Exists(rig, shared), "the bake no NPC uses to go");
    }
}
