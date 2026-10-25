/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Text;
using OpenMetaverse;
using OpenSim.CharacterPublish;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSimNGC.Appearance.Baking;
using N = System.Numerics;
using static OpenSim.CharacterPublish.Objects.Tests.BundleFixtures;

namespace OpenSim.CharacterPublish.Objects.Tests;

public class OutfitAssemblerTests
{
    static SceneObjectGroup ReadObject(AssembledBundle a, string role)
    {
        var item = a.Bundle.Items.Single(i => i.Role == role);
        return SceneObjectSerializer.FromOriginalXmlFormat(Encoding.UTF8.GetString(a.Content[item.AssetId]));
    }

    [Fact]
    public void ABundleFromFixturesValidatesAndHoldsEveryKindOfPiece()
    {
        var a = Assemble();
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        Assert.Equal(new[] { "shape", "skin", "eyes", "hair", "attachment-1", "attachment-2", "notecard" }, a.Bundle.Items.Select(i => i.Role));
        // two meshes, two objects, four wearables, one notecard
        Assert.Equal(9, a.Bundle.Assets.Count);
        Assert.Equal(new[] { BundleTypes.AssetBodypart, BundleTypes.AssetBodypart, BundleTypes.AssetBodypart, BundleTypes.AssetBodypart, BundleTypes.AssetObject, BundleTypes.AssetObject, BundleTypes.AssetNotecard },
            a.Bundle.Items.Select(i => i.AssetType));
        Assert.Equal(BundleTypes.FolderOutfit, a.Bundle.OutfitFolder.Type);
        Assert.Equal("Example #1", a.Bundle.OutfitFolder.Name);
        Assert.Equal(new[] { SkinHead, SkinUpper, SkinLower, Iris, HairTexture }, a.Bundle.ExternalAssets);
    }

    // ---- the attachment point (body at Avatar Center, separate head at Skull) and the size (the mesh's NormalizedScale)

    [Fact]
    public void ABodyIsWornAtAvatarCenterAndASeparateHeadAtSkull()
    {
        var a = Assemble();
        Assert.Equal(40u, ReadObject(a, "attachment-1").AttachmentPoint);
        Assert.Equal(2u, ReadObject(a, "attachment-2").AttachmentPoint);
        Assert.Equal((AttachmentPoint)40, OutfitAssembler.DefaultPoint(PartRole.Body));
        Assert.Equal((AttachmentPoint)2, OutfitAssembler.DefaultPoint(PartRole.Head));
    }

    [Fact]
    public void AnExplicitPointInTheDescriptionOverridesTheRole()
    {
        var d = Description();
        d = d with { Parts = d.Parts.Select((p, i) => i == 0 ? p with { AttachmentPoint = 1 } : p).ToList() };
        Assert.Equal(1u, ReadObject(Assemble(d), "attachment-1").AttachmentPoint);
    }

    [Fact]
    public void AnAttachmentPointOutsideTheRegionsRangeIsRefused()
    {
        var d = Description();
        d = d with { Parts = d.Parts.Select((p, i) => i == 0 ? p with { AttachmentPoint = 99 } : p).ToList() };
        Assert.Throws<ArgumentException>(() => Assemble(d));
    }

    [Fact]
    public void TheObjectSizeIsTheMeshsNormalizedScale()
    {
        var a = Assemble(files: Files(Mesh(3, new N.Vector3(1.5f, 0.4f, 1.9f)), Mesh(2, new N.Vector3(0.2f, 0.22f, 0.25f), rigged: false)));
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        var body = ReadObject(a, "attachment-1").RootPart.Scale;
        Assert.Equal(1.5f, body.X); Assert.Equal(0.4f, body.Y); Assert.Equal(1.9f, body.Z);
        var head = ReadObject(a, "attachment-2").RootPart.Scale;
        Assert.Equal(0.2f, head.X); Assert.Equal(0.22f, head.Y); Assert.Equal(0.25f, head.Z);
    }

    [Fact]
    public void AnUnriggedMeshWithoutANormalizedScaleIsRefusedWithAReason()
    {
        var e = Assert.Throws<ArgumentException>(() => Assemble(files: Files(Mesh(3, rigged: false, normalizedScale: false))));
        Assert.Contains("NormalizedScale", e.Message);
    }

    /// <summary>
    /// A rigged mesh uploaded from Collada may carry no per-face NormalizedScale: the size is in its bind shape matrix, whose scale
    /// is the model's size (Ruth2's own uploads: object size 0.2876 x 1.7708 x 1.7141, bind shape scale the same). The object takes it.
    /// </summary>
    [Fact]
    public void ARiggedMeshWithoutANormalizedScaleTakesItsBindShapeScaleAsTheObjectSize()
    {
        var a = Assemble(files: Files(Mesh(3, rigged: true, normalizedScale: false, bindShapeScale: new N.Vector3(0.3f, 1.8f, 1.7f)),
            Mesh(2, new N.Vector3(0.2f, 0.22f, 0.25f), rigged: false)));
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        var body = ReadObject(a, "attachment-1").RootPart.Scale;
        Assert.Equal(0.3f, body.X, 4); Assert.Equal(1.8f, body.Y, 4); Assert.Equal(1.7f, body.Z, 4);
    }

    // ---- the safe-description rule is enforced in the wearable writer (LightProjectTests); the assembler passes the credit through it

    [Fact]
    public void AnUnsafeCreditIsRefusedBeforeAnyWearableIsWritten()
    {
        var d = Description();
        d = d with { Attribution = d.Attribution with { Credit = "parameters 3, see the notecard" } };
        var e = Assert.Throws<ArgumentException>(() => Assemble(d));
        Assert.Contains("description", e.Message);
    }

    // ---- the pieces

    [Fact]
    public void EveryItemCarriesTheCreditCreatorOwnerPermissionsAndFlags()
    {
        var a = Assemble();
        foreach (var it in a.Bundle.Items)
        {
            Assert.Equal(Creator, it.CreatorId);
            Assert.Equal(Owner, it.OwnerId);
            Assert.Equal(Date, it.CreationDate);
            Assert.Equal(PermissionMasks.CopyModifyTransfer, it.BasePermissions);
            Assert.Equal(PermissionMasks.CopyModifyTransfer, it.CurrentPermissions);
            Assert.Equal(PermissionMasks.CopyModifyTransfer, it.NextPermissions);
            Assert.Equal(0u, it.GroupPermissions);
            Assert.Equal(0u, it.EveryonePermissions);
            if (it.Role != "notecard") Assert.Equal(Credit, it.Description);
        }
        // the wearable type is the low byte of the flags: shape 0, skin 1, hair 2, eyes 3
        Assert.Equal(0u, a.Bundle.Items.Single(i => i.Role == "shape").Flags);
        Assert.Equal(1u, a.Bundle.Items.Single(i => i.Role == "skin").Flags);
        Assert.Equal(3u, a.Bundle.Items.Single(i => i.Role == "eyes").Flags);
        Assert.Equal(2u, a.Bundle.Items.Single(i => i.Role == "hair").Flags);
        Assert.All(a.Bundle.Items.Where(i => i.AssetType == BundleTypes.AssetBodypart), i => Assert.Equal(BundleTypes.InvWearable, i.InvType));
        Assert.All(a.Bundle.Items.Where(i => i.Role.StartsWith("attachment")), i => Assert.Equal(BundleTypes.InvAttachment, i.InvType));
    }

    [Fact]
    public void TheCreditIsInTheItemsTheObjectsAndTheNotecard()
    {
        var a = Assemble();
        foreach (var role in new[] { "attachment-1", "attachment-2" })
            Assert.Equal(Credit, ReadObject(a, role).RootPart.Description);
        var skin = WearableReader.Read(Encoding.UTF8.GetString(a.Content[a.Bundle.Items.Single(i => i.Role == "skin").AssetId]));
        Assert.Equal(Credit, skin.Description);
        var note = NotecardReader.Read(a.Content[a.Bundle.Items.Single(i => i.Role == "notecard").AssetId]);
        Assert.Equal(NotecardText, note.Text);
    }

    [Fact]
    public void TheBakeChannelsReachTheObjectsFaces()
    {
        var a = Assemble();
        var body = ReadObject(a, "attachment-1").RootPart;
        var te = new Primitive.TextureEntry(body.Shape.TextureEntry, 0, body.Shape.TextureEntry.Length);
        Assert.Equal(BakeChannelIds.Of(BakeChannel.Head), te.FaceTextures[0].TextureID);
        Assert.Equal(BakeChannelIds.Of(BakeChannel.Upper), te.FaceTextures[1].TextureID);
        Assert.Equal(BakeChannelIds.Of(BakeChannel.Lower), te.FaceTextures[2].TextureID);
    }

    [Fact]
    public void TheShapeValuesAreTheDefaultsWithTheGivenOnesChanged()
    {
        var a = Assemble();
        var shape = WearableReader.Read(Encoding.UTF8.GetString(a.Content[a.Bundle.Items.Single(i => i.Role == "shape").AssetId]));
        var defaults = WearableFactory.Shape("x", "y", PermissionBlock.ForOwner(Creator, Owner, PermissionMasks.CopyModifyTransfer));
        Assert.Equal(defaults.Parameters.Count, shape.Parameters.Count);
        Assert.Equal(0.4f, shape.Parameters[1]);
        Assert.Equal(0.25f, shape.Parameters[80]);
        Assert.Equal(defaults.Parameters[2], shape.Parameters[2]);
    }

    [Fact]
    public void AnUnknownShapeParameterABadFaceOrAChannelNameIsRefused()
    {
        var d = Description();
        var bad = d with { Wearables = d.Wearables! with { Shape = new ShapeDescription(Values: new() { ["99999"] = 1f }) } };
        Assert.Throws<ArgumentException>(() => Assemble(bad));
        var badFace = d with { Parts = d.Parts.Select((p, i) => i == 0 ? p with { Faces = new() { ["7"] = "head" } } : p).ToList() };
        Assert.Throws<ArgumentException>(() => Assemble(badFace));
        var badChannel = d with { Parts = d.Parts.Select((p, i) => i == 0 ? p with { Faces = new() { ["0"] = "chin" } } : p).ToList() };
        Assert.Throws<ArgumentException>(() => Assemble(badChannel));
    }

    [Fact]
    public void WithoutHairTheBundleHasNoHairSoTheResidentKeepsTheirOwn()
    {
        var a = Assemble(Description(hair: false));
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        Assert.DoesNotContain(a.Bundle.Items, i => i.Role == "hair");
        Assert.DoesNotContain(HairTexture, a.Bundle.ExternalAssets);
    }

    [Fact]
    public void TheCurrentLookIsAPlanWithTheDateInTheName()
    {
        var a = Assemble();
        var plan = a.Bundle.CurrentLook!;
        Assert.Equal(BundleTypes.FolderOutfit, plan.FolderType);
        Assert.Equal(BundleTypes.FolderCurrentOutfit, plan.SourceFolderType);
        Assert.Equal(BundleTypes.AssetLink, plan.LinkAssetType);
        Assert.Equal("2026-09-21", DateTimeOffset.FromUnixTimeSeconds(Date).UtcDateTime.ToString("yyyy-MM-dd"));
        Assert.Contains("2026-09-21", plan.FolderName);
        Assert.Null(Assemble(ctx: Context() with { SaveCurrentLook = false }).Bundle.CurrentLook);
    }

    [Fact]
    public void EachPublishNumberGivesADifferentFolderAndDifferentItemIdsButTheSameAssets()
    {
        var one = Assemble(ctx: Context(1));
        var two = Assemble(ctx: Context(2));
        Assert.Equal("Example #2", two.Bundle.OutfitFolder.Name);
        Assert.NotEqual(one.Bundle.OutfitFolder.Id, two.Bundle.OutfitFolder.Id);
        Assert.Empty(one.Bundle.Items.Select(i => i.Id).Intersect(two.Bundle.Items.Select(i => i.Id)));
        Assert.Equal(one.Bundle.Assets.Select(x => x.Id), two.Bundle.Assets.Select(x => x.Id));
    }

    [Fact]
    public void TwoPartsOnTheSameMeshStoreItOnce()
    {
        var d = Description();
        d = d with { Parts = new List<PartDescription> { d.Parts[0], d.Parts[0] with { Name = "Second", AttachmentPoint = 1 } } };
        var a = Assemble(d);
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        Assert.Equal(1, a.Bundle.Assets.Count(x => x.AssetType == BundleTypes.AssetMesh));
    }

    [Fact]
    public void ADescriptionIsReadFromJson()
    {
        var json = """
        { "name": "Example",
          "attribution": { "credit": "Credit line", "notecardText": "Full text\n" },
          "parts": [ { "name": "Example Body", "role": "body", "mesh": "body.llmesh", "faces": { "0": "head" } },
                     { "name": "Example Head", "role": "head", "mesh": "head.llmesh" } ],
          "wearables": { "shape": { "values": { "1": 0.5 } },
                         "skin": { "head": "0a1b2c3d-4e5f-4a6b-8c7d-8e9fa0b1c2d3", "upper": "1a2b3c4d-5e6f-4a7b-8c8d-9eaf0b1c2d3e", "lower": "2a3b4c5d-6e7f-4a8b-8c9d-aeb00c1d2e3f" },
                         "eyes": { "iris": "3a4b5c6d-7e8f-4a9b-8cad-bfc10d1e2f30" } } }
        """;
        var d = CharacterDescription.Parse(json);
        Assert.Equal(PartRole.Head, d.Parts[1].Role);
        var a = OutfitAssembler.Assemble(d, Context(), Files());
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        Assert.Equal(new[] { "shape", "skin", "eyes", "attachment-1", "attachment-2", "notecard" }, a.Bundle.Items.Select(i => i.Role));
    }

    [Fact]
    public void ASkinWithNoTexturesShowsItsSkinColourValues()
    {
        var json = """
        { "name": "Example",
          "attribution": { "credit": "Credit line", "notecardText": "Full text\n" },
          "parts": [ { "name": "Example Body", "role": "body", "mesh": "body.llmesh", "faces": { "0": "head" } } ],
          "wearables": { "skin": { "name": "Plain Skin", "values": { "111": 0.35, "110": 0.03 } } } }
        """;
        var a = OutfitAssembler.Assemble(CharacterDescription.Parse(json), Context(), Files());
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        var skin = WearableReader.Read(Encoding.UTF8.GetString(a.Content[a.Bundle.Items.Single(i => i.Role == "skin").AssetId]));
        Assert.Empty(skin.Textures);                  // no paint: the baker shows the skin colour over the grain
        Assert.Equal(0.35f, skin.Parameters[111]);    // pigment
        Assert.Equal(0.03f, skin.Parameters[110]);    // ruddiness
        Assert.Equal(0f, skin.Parameters[108]);       // rainbow colour keeps its default
        Assert.Empty(a.Bundle.ExternalAssets);

        // a parameter that is not one of the skin's is refused, as for the shape
        var bad = json.Replace("\"111\": 0.35", "\"1\": 0.35");
        Assert.Throws<ArgumentException>(() => OutfitAssembler.Assemble(CharacterDescription.Parse(bad), Context(), Files()));
    }

    static byte[] GridMesh(int n)
    {
        var f = new OpenSim.MeshAsset.MeshFace { NormalizedScale = new N.Vector3(1, 1, 1) };
        var pos = new List<N.Vector3>(); var uv = new List<N.Vector2>(); var nor = new List<N.Vector3>(); var idx = new List<int>();
        for (var y = 0; y <= n; y++)
            for (var x = 0; x <= n; x++) { pos.Add(new N.Vector3(x / (float)n, y / (float)n, MathF.Sin(x * 0.4f) * 0.1f)); uv.Add(new N.Vector2(x / (float)n, y / (float)n)); nor.Add(N.Vector3.UnitZ); }
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++) { var a = y * (n + 1) + x; idx.AddRange(new[] { a, a + 1, a + n + 1, a + 1, a + n + 2, a + n + 1 }); }
        f.Positions = pos.ToArray(); f.Uvs = uv.ToArray(); f.Normals = nor.ToArray(); f.Indices = idx.ToArray();
        var m = new OpenSim.MeshAsset.MeshAssetModel { High = new OpenSim.MeshAsset.MeshLod() };
        m.High.Faces.Add(f);
        return OpenSim.MeshAsset.MeshCodec.Encode(m);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("character")]
    [InlineData("auto")]
    public void LowerLevelsOfDetailAreGeneratedOnRequestAndRepeatably(string lods)
    {
        var d = Description();
        d = d with { Parts = new List<PartDescription> { new("Grid", PartRole.Body, "grid.llmesh", Lods: lods) } };
        Func<string, byte[]> files = _ => GridMesh(40);
        var a = OutfitAssembler.Assemble(d, Context(), files);
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        var mesh = OpenSim.MeshAsset.MeshCodec.Decode(a.Content[a.Bundle.Assets.Single(x => x.AssetType == BundleTypes.AssetMesh).Id]);
        var tris = Enumerable.Range(0, 4).Select(i => mesh.Lod(i)!.Faces.Sum(f => f.TriangleCount)).ToArray();
        Assert.Equal(3200, tris[3]);
        Assert.True(tris[0] < tris[1] && tris[1] < tris[2] && tris[2] < tris[3], string.Join(",", tris));
        var b = OutfitAssembler.Assemble(d, Context(), files);
        Assert.Equal(a.Bundle.Assets.Select(x => x.Id), b.Bundle.Assets.Select(x => x.Id));
    }

    [Fact]
    public void ALodsNameThatIsNotAProfileIsRefused()
    {
        var d = Description();
        d = d with { Parts = d.Parts.Select((p, i) => i == 0 ? p with { Lods = "fancy" } : p).ToList() };
        Assert.Throws<ArgumentException>(() => Assemble(d));
    }

    [Fact]
    public void ABundleNeedsItsAttributionText()
    {
        var d = Description() with { Attribution = new AttributionDescription(Credit) };
        Assert.Throws<ArgumentException>(() => Assemble(d));
    }

    // ---- determinism and the folder on disk

    [Fact]
    public void AssemblingTwiceGivesByteIdenticalBundles()
    {
        var a = Assemble();
        var b = Assemble();
        Assert.Equal(BundleFiles.ToJson(a.Bundle), BundleFiles.ToJson(b.Bundle));
        Assert.Equal(a.Content.Keys.Order(), b.Content.Keys.Order());
        foreach (var (id, bytes) in a.Content) Assert.Equal(bytes, b.Content[id]);

        var scratch = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
        var one = Path.Combine(scratch, "one");
        var two = Path.Combine(scratch, "two");
        // The scratch folders sit under the test output folder; the tests never delete anything.
        a.WriteTo(one);
        b.WriteTo(two);
        var files1 = Directory.GetFiles(one).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(files1, Directory.GetFiles(two).Select(Path.GetFileName).Order().ToList());
        foreach (var f in files1) Assert.Equal(File.ReadAllBytes(Path.Combine(one, f!)), File.ReadAllBytes(Path.Combine(two, f!)));
        Assert.True(BundleFiles.ValidateFolder(one).Ok, BundleFiles.ValidateFolder(one).ToString());
        Assert.True(BundleFiles.ValidateFolder(two).Ok);
    }
}
