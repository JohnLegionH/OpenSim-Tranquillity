/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;
using OpenSim.CharacterPublish;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;

namespace OpenSim.CharacterPublish.Objects.Tests;

/// <summary>The object we write, read back by the region's own deserializer.</summary>
public class AttachmentObjectTests
{
    static readonly UUID Mesh = new("11111111-2222-3333-4444-555555555555");
    static readonly UUID Creator = new("aaaaaaaa-0000-4000-8000-000000000001");
    static readonly UUID Owner = new("bbbbbbbb-0000-4000-8000-000000000002");

    internal static PermissionBlock Perms(uint mask = PermissionMasks.CopyModifyTransfer) => PermissionBlock.ForOwner(Creator, Owner, mask);

    internal static AttachmentObjectSpec Spec(int faces = 3, Dictionary<int, BakeChannel>? map = null, AttachmentPoint point = AttachmentPoint.Chest,
        string name = "Example Body", string description = "Example mesh body, modified.") =>
        new(Mesh, faces, map ?? new Dictionary<int, BakeChannel> { [0] = BakeChannel.Head, [1] = BakeChannel.Upper, [2] = BakeChannel.Lower },
            point, new Vector3(0.62f, 0.31f, 1.78f), Perms(), name, description);

    internal static SceneObjectGroup Read(string xml) => SceneObjectSerializer.FromOriginalXmlFormat(xml);

    [Fact]
    public void EveryFieldComesBackAsWritten()
    {
        var spec = Spec();
        var grp = Read(AttachmentObjectWriter.Write(spec));
        var part = grp.RootPart;

        Assert.Single(grp.Parts);
        Assert.Equal((uint)AttachmentPoint.Chest, grp.AttachmentPoint);
        Assert.Equal((byte)AttachmentPoint.Chest, part.Shape.State);
        Assert.Equal((byte)AttachmentPoint.Chest, part.Shape.LastAttachPoint);
        Assert.Equal(Vector3.Zero, part.AttachedPos);
        Assert.Equal(Vector3.Zero, part.GroupPosition);
        Assert.Equal(Vector3.Zero, part.OffsetPosition);

        Assert.True(part.Shape.SculptEntry);
        Assert.Equal((byte)SculptType.Mesh, part.Shape.SculptType);
        Assert.Equal(Mesh, part.Shape.SculptTexture);
        Assert.Equal(9, part.Shape.PCode);
        Assert.Equal(3, part.GetNumberOfSides());

        var te = part.Shape.Textures;
        Assert.Equal(BakeChannelIds.Of(BakeChannel.Head), te.GetFace(0).TextureID);
        Assert.Equal(BakeChannelIds.Of(BakeChannel.Upper), te.GetFace(1).TextureID);
        Assert.Equal(BakeChannelIds.Of(BakeChannel.Lower), te.GetFace(2).TextureID);

        Assert.Equal(spec.Size, part.Scale);
        Assert.Equal(spec.Permissions.BaseMask, part.BaseMask);
        Assert.Equal(spec.Permissions.OwnerMask, part.OwnerMask);
        Assert.Equal(spec.Permissions.GroupMask, part.GroupMask);
        Assert.Equal(spec.Permissions.EveryoneMask, part.EveryoneMask);
        Assert.Equal(spec.Permissions.NextOwnerMask, part.NextOwnerMask);
        Assert.Equal(Creator, part.CreatorID);
        Assert.Equal(Owner, part.OwnerID);
        Assert.Equal("Example Body", part.Name);
        Assert.Equal("Example mesh body, modified.", part.Description);
        Assert.Equal(0, part.CreationDate);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void FaceCountComesBack(int faces)
    {
        var map = Enumerable.Range(0, faces).ToDictionary(i => i, i => (BakeChannel)(i % 3));
        var part = Read(AttachmentObjectWriter.Write(Spec(faces, map))).RootPart;
        Assert.Equal(faces, part.GetNumberOfSides());
        for (var i = 0; i < faces; i++)
            Assert.Equal(BakeChannelIds.Of((BakeChannel)(i % 3)), part.Shape.Textures.GetFace((uint)i).TextureID);
    }

    [Fact]
    public void AFaceWithNoChannelKeepsTheWhiteTexture()
    {
        var part = Read(AttachmentObjectWriter.Write(Spec(3, new Dictionary<int, BakeChannel> { [1] = BakeChannel.Eyes }))).RootPart;
        Assert.Equal(Primitive.TextureEntry.WHITE_TEXTURE, part.Shape.Textures.GetFace(0).TextureID);
        Assert.Equal(BakeChannelIds.Of(BakeChannel.Eyes), part.Shape.Textures.GetFace(1).TextureID);
        Assert.Equal(Primitive.TextureEntry.WHITE_TEXTURE, part.Shape.Textures.GetFace(2).TextureID);
    }

    [Theory]
    [InlineData(AttachmentPoint.Chest)] [InlineData(AttachmentPoint.Skull)] [InlineData(AttachmentPoint.Pelvis)]
    [InlineData(AttachmentPoint.LeftHand)] [InlineData(AttachmentPoint.Stomach)] [InlineData(AttachmentPoint.LastValid)]
    public void AttachmentPointComesBack(AttachmentPoint point)
    {
        var grp = Read(AttachmentObjectWriter.Write(Spec(point: point)));
        Assert.Equal((uint)point, grp.AttachmentPoint);
    }

    [Fact]
    public void SameInputGivesSameBytes()
    {
        Assert.Equal(AttachmentObjectWriter.Write(Spec()), AttachmentObjectWriter.Write(Spec()));
    }

    [Fact]
    public void ADifferentOwnerGetsADifferentPartId()
    {
        var a = Read(AttachmentObjectWriter.Write(Spec())).RootPart.UUID;
        var other = Spec() with { Permissions = PermissionBlock.ForOwner(Creator, new UUID("cccccccc-0000-4000-8000-000000000003"), PermissionMasks.CopyModifyTransfer) };
        Assert.NotEqual(a, Read(AttachmentObjectWriter.Write(other)).RootPart.UUID);
        Assert.False(a.IsZero());
    }

    [Fact]
    public void AnExplicitPartIdIsKept()
    {
        var id = new UUID("dddddddd-0000-4000-8000-000000000004");
        Assert.Equal(id, Read(AttachmentObjectWriter.Write(Spec() with { PartId = id })).RootPart.UUID);
    }

    [Fact]
    public void TheRootElementsAreTheRegionsOwn()
    {
        var xml = AttachmentObjectWriter.Write(Spec());
        Assert.StartsWith("<SceneObjectGroup><RootPart><SceneObjectPart", xml);
        Assert.EndsWith("</SceneObjectPart></RootPart><OtherParts /></SceneObjectGroup>", xml);
    }

    [Theory]
    [InlineData("type 5")] [InlineData("parameters")] [InlineData("textures 3 of them")] [InlineData("  type\tx")]
    public void ADescriptionThatStartsWithASectionWordIsRefused(string description)
    {
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(description: description)));
        Assert.False(SafeDescription.IsSafe(description));
    }

    [Theory]
    [InlineData("")] [InlineData("typed by hand")] [InlineData("Mesh body: Example, CC BY 4.0, modified.")] [InlineData("texturesque")] [InlineData("a type 5")]
    public void OtherDescriptionsAreAccepted(string description)
    {
        Assert.True(SafeDescription.IsSafe(description));
        Assert.Equal(description, Read(AttachmentObjectWriter.Write(Spec(description: description))).RootPart.Description);
    }

    [Fact]
    public void BadInputIsRefused()
    {
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(point: AttachmentPoint.Default)));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(point: (AttachmentPoint)(AttachmentPoint.LastValid + 1))));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(faces: 0, map: new())));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(faces: 9, map: new())));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(faces: 2, map: new() { [2] = BakeChannel.Head })));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(name: "")));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(name: new string('n', 64))));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec(description: new string('d', 128))));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec() with { Size = new Vector3(0, 1, 1) }));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec() with { MeshAssetId = UUID.Zero }));
        Assert.Throws<ArgumentException>(() => AttachmentObjectWriter.Write(Spec() with { Permissions = PermissionBlock.ForOwner(UUID.Zero, Owner, 1) }));
    }

    [Fact]
    public void TheBakeIdsAreTheViewersAndDistinct()
    {
        // llcommon/indra_constants.cpp IMG_USE_BAKED_*
        var expected = new Dictionary<BakeChannel, string>
        {
            [BakeChannel.Head] = "5a9f4a74-30f2-821c-b88d-70499d3e7183", [BakeChannel.Upper] = "ae2de45c-d252-50b8-5c6e-19f39ce79317",
            [BakeChannel.Lower] = "24daea5f-0539-cfcf-047f-fbc40b2786ba", [BakeChannel.Eyes] = "52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a",
            [BakeChannel.Skirt] = "43529ce8-7faa-ad92-165a-bc4078371687", [BakeChannel.Hair] = "09aac1fb-6bce-0bee-7d44-caac6dbb6c63",
            [BakeChannel.LeftArm] = "ff62763f-d60a-9855-890b-0c96f8f8cd98", [BakeChannel.LeftLeg] = "8e915e25-31d1-cc95-ae08-d58a47488251",
            [BakeChannel.Aux1] = "9742065b-19b5-297c-858a-29711d539043", [BakeChannel.Aux2] = "03642e83-2bd1-4eb9-34b4-4c47ed586d2d",
            [BakeChannel.Aux3] = "edd51b77-fc10-ce7a-4b3d-011dfc349e4f",
        };
        Assert.Equal(Enum.GetValues<BakeChannel>().Length, expected.Count);
        foreach (var (ch, id) in expected)
        {
            Assert.Equal(new UUID(id), BakeChannelIds.Of(ch));
            Assert.Equal(ch, BakeChannelIds.ChannelOf(new UUID(id)));
        }
        Assert.Equal(expected.Count, expected.Values.Distinct().Count());
        Assert.Null(BakeChannelIds.ChannelOf(Primitive.TextureEntry.WHITE_TEXTURE));
    }
}
