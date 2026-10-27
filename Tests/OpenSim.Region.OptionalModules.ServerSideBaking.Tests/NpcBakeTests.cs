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
    public void AnNpcThatLeavesHandsBackTheBakesNobodyElseHolds()
    {
        var share = new NpcBakeShare();
        var asset = UUID.Random();
        share.NoteStored(BakeChannel.Head, HashOne, asset);
        share.Hold(NpcA, BakeChannel.Head, HashOne, asset);

        var released = share.Release(NpcA);

        Assert.Equal(new[] { asset }, released);
        Assert.False(share.TryFind(BakeChannel.Head, HashOne, out _), "a bake handed back for deletion is still offered for reuse");
    }

    [Fact]
    public void AnNpcThatLeavesKeepsTheBakesAnotherNpcStillHolds()
    {
        var share = new NpcBakeShare();
        var asset = UUID.Random();
        share.NoteStored(BakeChannel.Head, HashOne, asset);
        share.Hold(NpcA, BakeChannel.Head, HashOne, asset);
        share.Hold(NpcB, BakeChannel.Head, HashOne, asset);

        Assert.Empty(share.Release(NpcA));
        Assert.True(share.TryFind(BakeChannel.Head, HashOne, out var found));
        Assert.Equal(asset, found);
        Assert.Equal(new[] { asset }, share.Release(NpcB));
    }

    [Fact]
    public void AnNpcThatLeavesDoesNotHandBackABakeThisProcessDidNotStore()
    {
        var share = new NpcBakeShare();
        share.Hold(NpcA, BakeChannel.Head, HashOne, UUID.Random());   // read from a stored index, never stored here

        Assert.Empty(share.Release(NpcA));
    }

    [Fact]
    public void ReleasingTheSameNpcTwiceHandsBackNothingTheSecondTime()
    {
        var share = new NpcBakeShare();
        var asset = UUID.Random();
        share.NoteStored(BakeChannel.Head, HashOne, asset);
        share.Hold(NpcA, BakeChannel.Head, HashOne, asset);
        share.Release(NpcA);

        Assert.Empty(share.Release(NpcA));
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

    /// <summary>The fake avatar service with a gate on <c>GetAvatar</c>, which a bake calls first (to read its index).</summary>
    private sealed class GatedAvatarService : IAvatarService
    {
        private readonly FakeAvatarService m_inner;
        public readonly ManualResetEventSlim Open = new(true);
        public GatedAvatarService(FakeAvatarService inner) { m_inner = inner; }
        public AvatarData GetAvatar(UUID userID) { Open.Wait(TimeSpan.FromSeconds(60)); return m_inner.GetAvatar(userID); }
        public bool SetAvatar(UUID userID, AvatarData avatar) => m_inner.SetAvatar(userID, avatar);
        public AvatarAppearance GetAppearance(UUID userID) => m_inner.GetAppearance(userID);
        public bool SetAppearance(UUID userID, AvatarAppearance appearance) => m_inner.SetAppearance(userID, appearance);
        public bool ResetAvatar(UUID userID) => m_inner.ResetAvatar(userID);
        public bool SetItems(UUID userID, string[] names, string[] values) => m_inner.SetItems(userID, names, values);
        public bool RemoveItems(UUID userID, string[] names) => m_inner.RemoveItems(userID, names);
    }

    private sealed record Rig(Scene Scene, ServerSideBakingModule Module, ServerSideBakingRegion Region,
        FakeAvatarService Avatars, NPCModule Npcs, ScenePresence Owner, GatedAvatarService Gate);

    private static Rig Setup(bool serverBaking = true, bool cleanup = true, bool dryRun = false, bool bots = false)
    {
        var config = new IniConfigSource();
        config.AddConfig("NPC").Set("Enabled", "true");
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        var appearance = config.AddConfig(ServerSideBakingModule.ConfigSection);
        appearance.Set("ServerSideBaking", serverBaking ? "true" : "false");
        appearance.Set("BakeSize", "512");
        if (!cleanup) appearance.Set("NpcBakeCleanup", "false");
        if (dryRun) appearance.Set("NpcBakeCleanupDryRun", "true");

        var scene = new SceneHelpers().SetupScene("npc bake scene", UUID.Random(), 1000, 1000, 256, 256, config);
        var avatars = new FakeAvatarService();
        var gate = new GatedAvatarService(avatars);
        scene.RegisterModuleInterface<IAvatarService>(gate);

        // The test scene's asset store does not answer AssetsExist (it reports every asset missing), and the reuse
        // check asks it. A fake that does stands in; the scene caches its asset service, so that field is reset.
        scene.UnregisterModuleInterface(scene.RequestModuleInterface<IAssetService>());
        scene.RegisterModuleInterface<IAssetService>(new FakeAssetService());
        typeof(Scene).GetField("m_AssetService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(scene, null);

        var module = new ServerSideBakingModule();
        var npcs = new NPCModule();
        var modules = new List<object> { new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(),
            new BasicInventoryAccessModule(), npcs, module };
        if (bots) modules.Add(new BotManager());
        SceneHelpers.SetupSceneModules(scene, config, modules.ToArray());

        var owner = SceneHelpers.AddScenePresence(scene, UUID.Random());
        owner.Appearance.Wearables = Wear(scene, owner.UUID, 200, 170, 140);
        return new Rig(scene, module, module.RegionOf(scene), avatars, npcs, owner, gate);
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

    [Fact]
    public async Task AnNpcNeverCarriesAnotherAvatarsBakedFacesWhileItsOwnBakeIsPending()
    {
        var rig = Setup();
        await BakeOwner(rig);
        Assert.NotEmpty(BakeOrchestratorFaces(rig.Owner));

        rig.Gate.Open.Reset();   // holds every bake at its first avatar-service read
        try
        {
            var npcId = MakeNpc(rig, new AvatarAppearance(rig.Owner.Appearance, true));
            var npc = rig.Scene.GetScenePresence(npcId);
            Assert.Empty(BakeOrchestratorFaces(npc));
            Assert.Equal(-1, rig.Region.BakedCofVersion(npcId));
        }
        finally { rig.Gate.Open.Set(); }
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

        WaitFor(() => { var h = Indexed(rig, npcId, BakeChannel.Head); return !h.IsZero() && h != before; }, "the NPC's new bake");
        var after = Indexed(rig, npcId, BakeChannel.Head);
        WaitFor(() => Face(rig.Scene.GetScenePresence(npcId), BakeChannel.Head) == after, "the NPC's face to name the new bake");
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

    // ------------------------------------------------------------------ cleanup: an NPC goes, its bakes go

    private static (UUID Npc, List<UUID> Bakes) MadeAndBaked(Rig rig, AvatarAppearance look, string last = "Npc")
    {
        var npcId = MakeNpc(rig, look, last);
        WaitFor(() => rig.Region.BakedCofVersion(npcId) >= 0, "the NPC's bake");
        var npc = rig.Scene.GetScenePresence(npcId);
        var bakes = Enum.GetValues<BakeChannel>().Select(ch => Face(npc, ch)).Where(id => !id.IsZero()).Distinct().ToList();
        Assert.NotEmpty(bakes);
        return (npcId, bakes);
    }

    [Fact]
    public void AnNpcBakeIsStoredWithItsHashAndTheNpcMark()
    {
        var rig = Setup();
        var (npcId, bakes) = MadeAndBaked(rig, Look(rig, 200, 170, 140));

        foreach (var bake in bakes)
            Assert.True(NpcBakeMark.IsMarked(rig.Scene.AssetService.GetMetadata(bake.ToString())), $"bake {bake} of {npcId} is not marked as an NPC bake");
    }

    [Fact]
    public async Task AnOwnersBakeIsStoredWithoutTheNpcMark()
    {
        var rig = Setup();
        await BakeOwner(rig);

        foreach (var bake in BakeOrchestratorFaces(rig.Owner))
            Assert.False(NpcBakeMark.IsMarked(rig.Scene.AssetService.GetMetadata(bake.ToString())), "an avatar's bake carries the NPC mark");
    }

    [Fact]
    public async Task RemovingAnNpcDeletesItsBakesAndLeavesTheOwnersAlone()
    {
        var rig = Setup();
        await BakeOwner(rig);
        var ownerBakes = BakeOrchestratorFaces(rig.Owner);
        var (npcId, bakes) = MadeAndBaked(rig, new AvatarAppearance(rig.Owner.Appearance, true));

        rig.Npcs.DeleteNPC(npcId, rig.Scene);

        WaitFor(() => bakes.All(b => !Exists(rig, b)), "the removed NPC's bakes to go");
        Assert.All(ownerBakes, b => Assert.True(Exists(rig, b), "removing an NPC deleted the owner's bake"));
    }

    [Fact]
    public async Task RemovingOneOfTwoNpcsInOneOutfitKeepsTheSharedBakesUntilTheSecondGoes()
    {
        var rig = Setup();
        var look = Look(rig, 20, 90, 200);
        var (first, bakes) = MadeAndBaked(rig, new AvatarAppearance(look, true), "One");
        var (second, secondBakes) = MadeAndBaked(rig, new AvatarAppearance(look, true), "Two");
        Assert.Equal(bakes.OrderBy(b => b), secondBakes.OrderBy(b => b));

        rig.Npcs.DeleteNPC(first, rig.Scene);
        await Task.Delay(1500);   // a "did not happen" window: the shared bakes must stay
        Assert.All(bakes, b => Assert.True(Exists(rig, b), "a bake the second NPC still holds was deleted"));

        rig.Npcs.DeleteNPC(second, rig.Scene);
        WaitFor(() => bakes.All(b => !Exists(rig, b)), "the shared bakes to go with the last NPC");
    }

    [Fact]
    public async Task WithTheCleanupOffRemovingAnNpcDeletesNothing()
    {
        var rig = Setup(cleanup: false);
        var (npcId, bakes) = MadeAndBaked(rig, Look(rig, 200, 170, 140));

        rig.Npcs.DeleteNPC(npcId, rig.Scene);
        await Task.Delay(1500);   // a "did not happen" window

        Assert.All(bakes, b => Assert.True(Exists(rig, b), "the cleanup is off and a bake was deleted"));
        Assert.Empty(rig.Module.NpcCleanup.Removed);
    }

    [Fact]
    public void InDryRunRemovingAnNpcDeletesNothingAndListsWhatItWouldDelete()
    {
        var rig = Setup(dryRun: true);
        var (npcId, bakes) = MadeAndBaked(rig, Look(rig, 200, 170, 140));

        rig.Npcs.DeleteNPC(npcId, rig.Scene);

        WaitFor(() => bakes.All(b => rig.Module.NpcCleanup.WouldRemove.Contains(b)), "the dry run to list the NPC's bakes");
        Assert.All(bakes, b => Assert.True(Exists(rig, b), "a dry run deleted a bake"));
        Assert.Empty(rig.Module.NpcCleanup.Removed);
    }

    [Fact]
    public void ARegionShuttingDownCleansUpTheNpcsStillThere()
    {
        var rig = Setup();
        var (_, bakes) = MadeAndBaked(rig, Look(rig, 200, 170, 140));

        rig.Scene.EventManager.TriggerSceneShuttingDown(rig.Scene);

        WaitFor(() => bakes.All(b => !Exists(rig, b)), "the bakes of the NPCs left at shutdown to go");
    }

    [Fact]
    public void ARegionShuttingDownInDryRunListsTheBakesOfTheNpcsStillThere()
    {
        var rig = Setup(dryRun: true);
        var (_, bakes) = MadeAndBaked(rig, Look(rig, 200, 170, 140));

        rig.Scene.EventManager.TriggerSceneShuttingDown(rig.Scene);

        WaitFor(() => bakes.All(b => rig.Module.NpcCleanup.WouldRemove.Contains(b)), "the dry run to list the bakes at shutdown");
        Assert.All(bakes, b => Assert.True(Exists(rig, b)));
    }
    // ------------------------------------------------------------------ a bot made from a notecard while its owner is away

    [Fact]
    public void ABotMadeFromANotecardWhileItsOwnerIsAwayIsBakedFromTheNotecardsWearables()
    {
        var rig = Setup(bots: true);
        var host = SceneHelpers.AddSceneObject(rig.Scene, "outfit box", UUID.Random()).RootPart;
        Assert.NotNull(AppearanceNotecard.Save(host, rig.Scene.AssetService, "Priest", Look(rig, 200, 170, 140), includeHuds: false));
        Assert.True(AppearanceNotecard.TryLoad(host, rig.Scene.AssetService, "Priest", out var outfit, out var why), why);
        var away = UUID.Random();   // an owner with no presence in the region
        Assert.Null(rig.Scene.GetScenePresence(away));
        var bots = rig.Scene.RequestModuleInterface<IBotManager>();

        var id = bots.CreateBotWithAppearance(rig.Scene, "Priest", "Bot", new Vector3(128, 128, 30), outfit, UUID.Zero, away,
            owned: true, senseAsAgent: true, botDoor: true, out var reason);

        Assert.True(id.IsNotZero(), "no bot was made: " + reason);
        Assert.True(bots.IsBot(id));
        var bot = rig.Scene.GetScenePresence(id);
        Assert.Equal(outfit.Wearables[(int)WearableType.Skin][0], bot.Appearance.Wearables[(int)WearableType.Skin][0]);
        WaitFor(() => rig.Region.BakedCofVersion(id) >= 0, "the bot's bake");
        foreach (var ch in new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower })
        {
            var face = Face(bot, ch);
            Assert.NotEqual(UUID.Zero, face);
            Assert.True(Exists(rig, face), $"{ch}: the bake is not in the asset store");
            Assert.Equal(face, Indexed(rig, id, ch));
        }
    }
}
