/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>The mark that tells an NPC bake from an avatar's bake and from anything a person made.</summary>
public class NpcBakeMarkTests
{
    private static AssetBase Bake(UUID owner, string description, string name = null)
        => new(UUID.Random(), name ?? $"bake:{owner}:head", (sbyte)AssetType.Texture, owner.ToString()) { Description = description };

    [Fact]
    public void AnNpcBakeIsDescribedWithItsHashAndMarked()
    {
        var description = NpcBakeMark.Describe("abc123");

        Assert.StartsWith("abc123", description);
        Assert.True(NpcBakeMark.IsMarked(Bake(UUID.Random(), description).Metadata));
    }

    [Fact]
    public void AnAvatarsBakeCarriesOnlyItsHashAndIsNotMarked()
    {
        Assert.False(NpcBakeMark.IsMarked(Bake(UUID.Random(), "abc123").Metadata));
    }

    [Fact]
    public void ATextureAPersonMadeIsNotMarkedEvenWithTheMarkInItsDescription()
    {
        var mine = Bake(UUID.Random(), NpcBakeMark.Describe("abc123"), name: "My skin");

        Assert.False(NpcBakeMark.IsMarked(mine.Metadata));
    }

    [Fact]
    public void NoMetadataIsNotMarked()
    {
        Assert.False(NpcBakeMark.IsMarked(null));
    }
}

/// <summary>
/// An NPC leaves: its bakes are deleted unless another live NPC holds them, and only an asset this module made and
/// marked is ever deleted. No scene; a fake asset service.
/// </summary>
public class NpcBakeCleanupTests
{
    private static readonly UUID NpcA = UUID.Random();
    private static readonly UUID NpcB = UUID.Random();

    private sealed class Rig
    {
        public readonly FakeAssetService Assets = new();
        public readonly NpcBakeShare Share = new();

        /// <summary>A bake stored the way the orchestrator stores an NPC bake: marked, with its hash, held by the NPC.</summary>
        public UUID Stored(UUID holder, BakeChannel channel, string hash, bool marked = true, string name = null)
        {
            var id = UUID.Random();
            Assets.Put(new AssetBase(id, name ?? $"bake:{holder}:{channel.ToString().ToLowerInvariant()}", (sbyte)AssetType.Texture, holder.ToString())
            {
                Description = marked ? NpcBakeMark.Describe(hash) : hash,
                Data = new byte[] { 1, 2, 3 },
            });
            Share.NoteStored(channel, hash, id);
            Share.Hold(holder, channel, hash, id);
            return id;
        }

        public NpcBakeCleanup Cleanup(bool enabled = true, bool dryRun = false) => new(Share, enabled, dryRun);
    }

    [Fact]
    public void WhenTheOnlyHolderLeavesItsMarkedBakeIsDeleted()
    {
        var rig = new Rig();
        var head = rig.Stored(NpcA, BakeChannel.Head, "h1");

        var result = rig.Cleanup().Run(NpcA, rig.Assets);

        Assert.Equal(new[] { head }, result.Removed);
        Assert.False(rig.Assets.Assets.ContainsKey(head.ToString()));
    }

    [Fact]
    public void ABakeAnotherLiveNpcHoldsIsKeptUntilTheLastHolderLeaves()
    {
        var rig = new Rig();
        var head = rig.Stored(NpcA, BakeChannel.Head, "h1");
        rig.Share.Hold(NpcB, BakeChannel.Head, "h1", head);
        var cleanup = rig.Cleanup();

        var first = cleanup.Run(NpcA, rig.Assets);
        Assert.Empty(first.Removed);
        Assert.True(rig.Assets.Assets.ContainsKey(head.ToString()), "a bake another NPC still holds was deleted");

        var second = cleanup.Run(NpcB, rig.Assets);
        Assert.Equal(new[] { head }, second.Removed);
        Assert.False(rig.Assets.Assets.ContainsKey(head.ToString()));
    }

    [Fact]
    public void EveryChannelOfTheLeavingNpcIsDeleted()
    {
        var rig = new Rig();
        var ids = new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower }
            .Select((ch, i) => rig.Stored(NpcA, ch, "h" + i)).ToList();

        var result = rig.Cleanup().Run(NpcA, rig.Assets);

        Assert.Equal(ids.OrderBy(i => i), result.Removed.OrderBy(i => i));
        Assert.All(ids, id => Assert.False(rig.Assets.Assets.ContainsKey(id.ToString())));
    }

    [Fact]
    public void WithTheCleanupOffNothingIsDeletedAndNothingIsReported()
    {
        var rig = new Rig();
        var head = rig.Stored(NpcA, BakeChannel.Head, "h1");

        var result = rig.Cleanup(enabled: false).Run(NpcA, rig.Assets);

        Assert.Empty(result.Removed);
        Assert.Empty(result.WouldRemove);
        Assert.True(rig.Assets.Assets.ContainsKey(head.ToString()));
        Assert.DoesNotContain(rig.Assets.Ops, o => o.StartsWith("delete "));
    }

    [Fact]
    public void InDryRunNothingIsDeletedAndTheBakesItWouldDeleteAreListed()
    {
        var rig = new Rig();
        var head = rig.Stored(NpcA, BakeChannel.Head, "h1");
        var cleanup = rig.Cleanup(dryRun: true);

        var result = cleanup.Run(NpcA, rig.Assets);

        Assert.Empty(result.Removed);
        Assert.Equal(new[] { head }, result.WouldRemove);
        Assert.Equal(new[] { head }, cleanup.WouldRemove);
        Assert.True(rig.Assets.Assets.ContainsKey(head.ToString()));
        Assert.DoesNotContain(rig.Assets.Ops, o => o.StartsWith("delete "));
    }

    [Fact]
    public void AnAssetWithoutTheMarkIsNeverDeleted()
    {
        var rig = new Rig();
        var plain = rig.Stored(NpcA, BakeChannel.Head, "h1", marked: false);

        var result = rig.Cleanup().Run(NpcA, rig.Assets);

        Assert.Empty(result.Removed);
        Assert.Equal(new[] { plain }, result.Kept);
        Assert.True(rig.Assets.Assets.ContainsKey(plain.ToString()));
    }

    [Fact]
    public void AnAssetThatIsNotNamedAsABakeIsNeverDeletedEvenWithTheMark()
    {
        var rig = new Rig();
        var theirs = rig.Stored(NpcA, BakeChannel.Head, "h1", name: "My skin");

        var result = rig.Cleanup().Run(NpcA, rig.Assets);

        Assert.Empty(result.Removed);
        Assert.Equal(new[] { theirs }, result.Kept);
        Assert.True(rig.Assets.Assets.ContainsKey(theirs.ToString()));
    }

    [Fact]
    public void ABakeAlreadyGoneFromTheStoreIsNotAnError()
    {
        var rig = new Rig();
        var head = rig.Stored(NpcA, BakeChannel.Head, "h1");
        rig.Assets.Remove(head.ToString());

        var result = rig.Cleanup().Run(NpcA, rig.Assets);

        Assert.Empty(result.Removed);
        Assert.DoesNotContain(rig.Assets.Ops, o => o.StartsWith("delete "));
    }

    [Fact]
    public void ABakeThisProcessDidNotStoreIsNeverDeleted()
    {
        var rig = new Rig();
        var fromAnEarlierRun = UUID.Random();
        rig.Assets.Put(new AssetBase(fromAnEarlierRun, $"bake:{NpcA}:head", (sbyte)AssetType.Texture, NpcA.ToString())
            { Description = NpcBakeMark.Describe("h1") });
        rig.Share.Hold(NpcA, BakeChannel.Head, "h1", fromAnEarlierRun);

        var result = rig.Cleanup().Run(NpcA, rig.Assets);

        Assert.Empty(result.Removed);
        Assert.True(rig.Assets.Assets.ContainsKey(fromAnEarlierRun.ToString()));
    }

    [Fact]
    public void ANpcThatHoldsNothingDeletesNothing()
    {
        var rig = new Rig();
        var head = rig.Stored(NpcA, BakeChannel.Head, "h1");

        var result = rig.Cleanup().Run(NpcB, rig.Assets);

        Assert.Empty(result.Removed);
        Assert.True(rig.Assets.Assets.ContainsKey(head.ToString()));
    }

    [Fact]
    public void ARemovedNpcsHoldsAreLetGoEvenWithTheCleanupOffSoACountStaysRight()
    {
        var rig = new Rig();
        var head = rig.Stored(NpcA, BakeChannel.Head, "h1");
        rig.Share.Hold(NpcB, BakeChannel.Head, "h1", head);
        var off = rig.Cleanup(enabled: false);

        off.Run(NpcA, rig.Assets);
        var onlyB = rig.Cleanup().Run(NpcB, rig.Assets);

        Assert.Equal(new[] { head }, onlyB.Removed);
    }
}

/// <summary>The two settings, read from [Appearance].</summary>
public class NpcBakeCleanupSettingsTests
{
    private static ServerSideBakingModule Load(params (string Key, string Value)[] settings)
    {
        var config = new IniConfigSource();
        var section = config.AddConfig(ServerSideBakingModule.ConfigSection);
        foreach (var (k, v) in settings) section.Set(k, v);
        var module = new ServerSideBakingModule();
        module.Initialise(config);
        return module;
    }

    [Fact]
    public void TheCleanupIsOnAndNotADryRunWhenNothingIsSet()
    {
        var module = Load();
        Assert.True(module.NpcBakeCleanupEnabled);
        Assert.False(module.NpcBakeCleanupDryRun);
    }

    [Fact]
    public void TheCleanupIsOnAndNotADryRunWhenOnlyAnotherSettingIsSet()
    {
        var module = Load(("BakeSize", "512"));
        Assert.True(module.NpcBakeCleanupEnabled);
        Assert.False(module.NpcBakeCleanupDryRun);
    }

    [Fact]
    public void NpcBakeCleanupFalseTurnsTheCleanupOff()
    {
        Assert.False(Load(("NpcBakeCleanup", "false")).NpcBakeCleanupEnabled);
    }

    [Fact]
    public void NpcBakeCleanupDryRunTrueTurnsOnTheDryRun()
    {
        var module = Load(("NpcBakeCleanupDryRun", "true"));
        Assert.True(module.NpcBakeCleanupDryRun);
        Assert.True(module.NpcBakeCleanupEnabled);
    }
}
