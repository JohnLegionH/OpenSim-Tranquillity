/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;
using OpenSim.CharacterPublish.Objects;

namespace OpenSim.CharacterPublish.Services.Tests;

/// <summary>
/// The proof on a real character's bundle. The character's files are private and not in the repository, so these are skipped
/// unless PUB_MAX_BUNDLE names an assembled bundle folder (and, for the two-resident run, PUB_MAX_DESCRIPTION its description file).
/// </summary>
[Collection(ScratchGridCollection.Name)]
public class MaxPublishTests
{
    readonly ScratchGrid g;
    public MaxPublishTests(ScratchGrid grid) => g = grid;

    sealed class BundleFolderFactAttribute : FactAttribute
    {
        public BundleFolderFactAttribute()
        {
            var d = Environment.GetEnvironmentVariable("PUB_MAX_BUNDLE");
            if (string.IsNullOrEmpty(d) || !File.Exists(Path.Combine(d, BundleFiles.ManifestName)))
                Skip = "set PUB_MAX_BUNDLE to an assembled bundle folder; the character's files are private and not committed.";
        }
    }

    sealed class DescriptionFactAttribute : FactAttribute
    {
        public DescriptionFactAttribute()
        {
            var d = Environment.GetEnvironmentVariable("PUB_MAX_DESCRIPTION");
            if (string.IsNullOrEmpty(d) || !File.Exists(d))
                Skip = "set PUB_MAX_DESCRIPTION to the character description file; the character's files are private and not committed.";
        }
    }

    [BundleFolderFact]
    public void TheAssembledBundleIsPublishedFromItsFolderAndAgainIsAlreadyPublished()
    {
        var loaded = BundleFiles.Read(Environment.GetEnvironmentVariable("PUB_MAX_BUNDLE")!);
        var r = g.NewResident(withId: loaded.Bundle.OwnerId);
        var before = g.Snapshot(r.Id);
        var inv = g.NewInventory(); var ast = g.NewAssets();

        var res = new PublishService(ast, inv).Publish(loaded, r.Id);

        Verify.Published(g, r, loaded.Bundle, loaded.Content, res, before);
        Assert.Empty(inv.Forbidden);
        Assert.Equal(5, loaded.Bundle.Items.Count); // shape, skin, eyes, the body, the credits notecard
        var afterFirst = g.Snapshot(r.Id);

        var inv2 = g.NewInventory(); var ast2 = g.NewAssets();
        var again = new PublishService(ast2, inv2).Publish(loaded, r.Id);

        Assert.True(again.AlreadyPublished, again.Summary);
        Assert.Empty(inv2.ItemsAdded); Assert.Empty(inv2.FoldersAdded); Assert.Empty(ast2.Stored);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(afterFirst, g.Snapshot(r.Id)));
    }

    [DescriptionFact]
    public void TwoResidentsShareTheMeshAndAMidwayFailureIsCompletedByARetry()
    {
        var description = Environment.GetEnvironmentVariable("PUB_MAX_DESCRIPTION")!;
        var ra = g.NewResident(); var rb = g.NewResident();
        var a = OutfitAssembler.AssembleFromFile(description, new PublishContext(ra.Id, BundleFixtures.Creator, 1, BundleFixtures.Date));
        var b = OutfitAssembler.AssembleFromFile(description, new PublishContext(rb.Id, BundleFixtures.Creator, 1, BundleFixtures.Date));
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        Assert.True(b.Validation.Ok, b.Validation.ToString());
        var beforeA = g.Snapshot(ra.Id); var beforeB = g.Snapshot(rb.Id);

        // resident A: the third item write fails, then the retry completes
        var inv1 = g.NewInventory();
        inv1.FailAddItem = (n, _) => n == 3;
        var broken = new PublishService(g.NewAssets(), inv1).Publish(a.Bundle, a.Content, ra.Id);
        Assert.True(broken.Partial);
        Assert.Equal(2, broken.ItemsCreated.Count);
        var inv2 = g.NewInventory();
        var fixedUp = new PublishService(g.NewAssets(), inv2).Publish(a.Bundle, a.Content, ra.Id);
        Verify.Published(g, ra, a.Bundle, a.Content, fixedUp, beforeA);
        Assert.Empty(fixedUp.ItemsCreated.Intersect(broken.ItemsCreated));
        Assert.Empty(inv2.Forbidden);

        // resident B: the mesh (the big asset) is already stored by A's publish; everything that names an owner is B's own
        var astB = g.NewAssets();
        var resB = new PublishService(astB, g.NewInventory()).Publish(b.Bundle, b.Content, rb.Id);
        Verify.Published(g, rb, b.Bundle, b.Content, resB, beforeB);
        var mesh = a.Bundle.Assets.Single(x => x.AssetType == BundleTypes.AssetMesh);
        Assert.Equal(mesh.Id, b.Bundle.Assets.Single(x => x.AssetType == BundleTypes.AssetMesh).Id);
        Assert.Contains(mesh.Id, resB.AssetsAlreadyPresent);
        Assert.DoesNotContain(mesh.Id, astB.Stored);
        Assert.Empty(a.Bundle.Items.Select(i => i.Id).Intersect(b.Bundle.Items.Select(i => i.Id)));
        foreach (var role in new[] { "shape", "skin", "eyes", "attachment-1" })
            Assert.NotEqual(a.Bundle.Items.Single(i => i.Role == role).AssetId, b.Bundle.Items.Single(i => i.Role == role).AssetId);
        Assert.NotEqual(resB.OutfitFolderId, fixedUp.OutfitFolderId);
    }
}
