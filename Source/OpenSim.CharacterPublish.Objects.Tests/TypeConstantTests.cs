/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;
using OpenSim.CharacterPublish;

namespace OpenSim.CharacterPublish.Objects.Tests;

/// <summary>
/// The light project holds its own copies of the type numbers (it cannot take the library that names them);
/// these tests keep each equal to the library's and to the viewer's.
/// </summary>
public class TypeConstantTests
{
    [Fact]
    public void AssetTypeNumbersAreTheLibrarys()
    {
        Assert.Equal((int)AssetType.Clothing, BundleTypes.AssetClothing);
        Assert.Equal((int)AssetType.Object, BundleTypes.AssetObject);
        Assert.Equal((int)AssetType.Notecard, BundleTypes.AssetNotecard);
        Assert.Equal((int)AssetType.Bodypart, BundleTypes.AssetBodypart);
        Assert.Equal((int)AssetType.Link, BundleTypes.AssetLink);
        Assert.Equal((int)AssetType.Mesh, BundleTypes.AssetMesh);
    }

    [Fact]
    public void InventoryTypeNumbersAreTheLibrarys()
    {
        Assert.Equal((int)InventoryType.Notecard, BundleTypes.InvNotecard);
        Assert.Equal((int)InventoryType.Attachment, BundleTypes.InvAttachment);
        Assert.Equal((int)InventoryType.Wearable, BundleTypes.InvWearable);
    }

    [Fact]
    public void FolderTypeNumbersAreTheLibrarys()
    {
        Assert.Equal((int)FolderType.CurrentOutfit, BundleTypes.FolderCurrentOutfit);
        Assert.Equal((int)FolderType.Outfit, BundleTypes.FolderOutfit);
        Assert.Equal((int)FolderType.MyOutfits, BundleTypes.FolderMyOutfits);
    }

    [Fact]
    public void TheHighestAttachmentPointIsTheRegions() =>
        Assert.Equal((int)AttachmentPoint.LastValid, ObjectCheck.MaxAttachmentPoint);

    [Fact]
    public void TheMeshSculptTypeIsTheLibrarys() =>
        Assert.Equal((int)SculptType.Mesh, ObjectCheck.SculptTypeMesh);

    [Fact]
    public void WearableTypesMapToBodypartOrClothingAsTheRegionDoes()
    {
        for (var t = 0; t <= 16; t++)
            Assert.Equal(t <= 3 ? BundleTypes.AssetBodypart : BundleTypes.AssetClothing, BundleTypes.AssetTypeOfWearable(t));
    }
}
