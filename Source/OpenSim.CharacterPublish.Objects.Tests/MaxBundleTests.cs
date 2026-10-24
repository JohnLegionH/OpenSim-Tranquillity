/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;
using System.Text;
using OpenSim.CharacterPublish;
using OpenSimNGC.Appearance.Baking;
using Xunit.Abstractions;

namespace OpenSim.CharacterPublish.Objects.Tests;

/// <summary>
/// Assembles a bundle of a real character from a description file and writes it to a folder for inspection. Skipped
/// unless PUB_BUNDLE_DESCRIPTION names a description file and PUB_BUNDLE_OUT a folder. Nothing is uploaded.
/// </summary>
public class MaxBundleTests
{
    readonly ITestOutputHelper _out;
    public MaxBundleTests(ITestOutputHelper output) => _out = output;

    sealed class BundleFactAttribute : FactAttribute
    {
        public BundleFactAttribute()
        {
            var d = Environment.GetEnvironmentVariable("PUB_BUNDLE_DESCRIPTION");
            if (string.IsNullOrEmpty(d) || !File.Exists(d) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PUB_BUNDLE_OUT")))
                Skip = "set PUB_BUNDLE_DESCRIPTION to a character description file and PUB_BUNDLE_OUT to a folder; the character's files are private and not committed.";
        }
    }

    [BundleFact]
    public void AssemblesTheCharacterWritesItAndTheFolderValidatesAgain()
    {
        var description = Environment.GetEnvironmentVariable("PUB_BUNDLE_DESCRIPTION")!;
        var outDir = Environment.GetEnvironmentVariable("PUB_BUNDLE_OUT")!;
        var owner = new UUID("66666666-7777-4888-8999-aaaaaaaaaaaa");
        var creator = new UUID("11111111-2222-4333-8444-555555555555");

        var a = OutfitAssembler.AssembleFromFile(description, new PublishContext(owner, creator, 1, 1_790_000_000));
        foreach (var p in a.Validation.Problems) _out.WriteLine(p.ToString());
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        a.WriteTo(outDir);

        // Assembling again, with the lower levels generated again, gives the same ids and bytes.
        var second = OutfitAssembler.AssembleFromFile(description, new PublishContext(owner, creator, 1, 1_790_000_000));
        Assert.Equal(BundleFiles.ToJson(a.Bundle), BundleFiles.ToJson(second.Bundle));
        foreach (var (id, bytes) in a.Content) Assert.Equal(bytes, second.Content[id]);

        foreach (var x in a.Bundle.Assets.Where(x => x.AssetType == BundleTypes.AssetMesh))
        {
            var model = OpenSim.MeshAsset.MeshCodec.Decode(a.Content[x.Id]);
            for (var i = 0; i < 4; i++)
                _out.WriteLine($"mesh {x.File} lod {i}: faces {model.Lod(i)?.Faces.Count}, triangles {model.Lod(i)?.Faces.Sum(f => f.TriangleCount)}");
            _out.WriteLine($"mesh {x.File}: rigged {model.Skin != null}, lods generated {model.LodsGenerated}, repeated {model.LodsRepeated}");
        }
        _out.WriteLine($"folder {a.Bundle.OutfitFolder.Name}, {a.Bundle.Items.Count} items, {a.Bundle.Assets.Count} assets");
        foreach (var it in a.Bundle.Items) _out.WriteLine($"item {it.Role,-13} asset {it.AssetType,2} inv {it.InvType,2} flags {it.Flags:x2} {it.Name}");
        foreach (var x in a.Bundle.Assets) _out.WriteLine($"asset {x.Id} type {x.AssetType,2} {x.Length,8} bytes {x.File}");

        var again = BundleFiles.ValidateFolder(outDir);
        Assert.True(again.Ok, again.ToString());
    }

    /// <summary>
    /// The viewer hides a system part only when an attachment face shows that part's bake, and this body has no hair face, so
    /// the resident's own hair would show under it. The bundle therefore carries a hair wearable (the worn hair is replaced,
    /// one per type) whose hair texture is the grid's fully transparent texture: a bald hair.
    /// </summary>
    [BundleFact]
    public void TheCharacterCarriesABaldHairSoTheResidentsOwnNeverShowsUnderTheBody()
    {
        var description = Environment.GetEnvironmentVariable("PUB_BUNDLE_DESCRIPTION")!;
        var a = OutfitAssembler.AssembleFromFile(description,
            new PublishContext(new UUID("66666666-7777-4888-8999-aaaaaaaaaaaa"), new UUID("11111111-2222-4333-8444-555555555555"), 1, 1_790_000_000));
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        Assert.Equal(new[] { "shape", "skin", "eyes", "hair", "attachment-1", "notecard" }, a.Bundle.Items.Select(i => i.Role));

        var item = a.Bundle.Items.Single(i => i.Role == "hair");
        Assert.Equal((uint)WearableKind.Hair, item.Flags);
        var hair = WearableReader.Read(Encoding.UTF8.GetString(a.Content[item.AssetId]));
        Assert.Equal(WearableKind.Hair, hair.Type);
        Assert.Equal(BakeConstants.InvisibleTexture, hair.Textures[(int)TextureSlot.Hair]);
        Assert.Contains(BakeConstants.InvisibleTexture, a.Bundle.ExternalAssets);
    }
}
