/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Tests.Common;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>An OAR written by the repository's own saver, read back by the inspector.</summary>
[Collection(SceneCollection.Name)]
public class OarEndToEndTests : OpenSimTestCase
{
    internal sealed class Fixture
    {
        public OarScene S;
        public UUID Tex1, Tex2, Tex3, ScriptAsset, BoxA, BoxB, Notecard;
        public SceneObjectGroup Chair, Table, Lamp;
        public byte[] Oar;
        public ArchiveReport Report;
    }

    internal static Fixture Build(Dictionary<string, object> saveOptions = null)
    {
        Fixture f = new Fixture { S = NewScene() };
        TestScene scene = f.S.Scene;

        f.Tex1 = UUID.Parse("11111111-aaaa-4000-8000-000000000001");
        f.Tex2 = UUID.Parse("11111111-aaaa-4000-8000-000000000002");
        f.Tex3 = UUID.Parse("11111111-aaaa-4000-8000-000000000003");
        f.ScriptAsset = UUID.Parse("22222222-aaaa-4000-8000-000000000001");
        f.Notecard = UUID.Parse("22222222-aaaa-4000-8000-000000000002");
        f.BoxA = UUID.Parse("33333333-aaaa-4000-8000-000000000001");
        f.BoxB = UUID.Parse("33333333-aaaa-4000-8000-000000000002");

        StoreAsset(scene.AssetService, f.Tex1, AssetType.Texture, new byte[200], Alice);
        StoreAsset(scene.AssetService, f.Tex2, AssetType.Texture, new byte[300], Bob);
        StoreAsset(scene.AssetService, f.Tex3, AssetType.Texture, new byte[400], Dave);
        StoreAsset(scene.AssetService, f.ScriptAsset, AssetType.LSLText,
            Utf8("default { state_entry() { llSetTexture(\"" + f.Tex2 + "\", ALL_SIDES); } }"), Carol);
        StoreAsset(scene.AssetService, f.Notecard, AssetType.Notecard, Utf8("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 5\nhello}\n"), Alice);

        // a boxed object holding a second boxed object
        SceneObjectGroup boxB = SceneHelpers.CreateSceneObject(1, Carol, "BoxB-", 0x500);
        boxB.Name = "Box B";
        boxB.RootPart.CreatorID = Carol;
        scene.AssetService.Store(AssetHelpers.CreateAsset(f.BoxB, boxB));

        SceneObjectGroup boxA = SceneHelpers.CreateSceneObject(2, Bob, "BoxA-", 0x400);
        boxA.Name = "Box A";
        foreach (SceneObjectPart p in boxA.Parts) p.CreatorID = Bob;
        AddPrimItem(boxA.RootPart, "Box B item", f.BoxB, AssetType.Object, Bob, Bob, 1400000000);
        scene.AssetService.Store(AssetHelpers.CreateAsset(f.BoxA, boxA));

        f.Chair = AddObject(scene, "Chair", Alice, Alice, 3, 0x100, creationDate: 1600000000);
        AddPrimItem(f.Chair.RootPart, "Chair script", f.ScriptAsset, AssetType.LSLText, Carol, Alice, 1610000000);
        AddPrimItem(f.Chair.RootPart, "Box A item", f.BoxA, AssetType.Object, Dave, Alice, 1620000000);

        f.Table = AddObject(scene, "Table", Bob, Alice, 1, 0x200, creationDate: 1630000000);
        Primitive.TextureEntry te = new Primitive.TextureEntry(f.Tex1);
        te.CreateFace(0).TextureID = f.Tex2;
        f.Table.RootPart.Shape.Textures = te;

        f.Lamp = AddObject(scene, "Lamp", Dave, Bob, 1, 0x300, "http://grid.example.org:8002/;Dave Builder", 1500000000);

        scene.RegionInfo.RegionSettings.TerrainTexture1 = f.Tex3;

        ILandObject parcel = scene.LandChannel.AllParcels()[0];
        parcel.LandData.Name = "Alice Park";
        parcel.LandData.OwnerID = Alice;
        parcel.LandData.GroupID = Group1;

        f.Oar = SaveOar(f.S, saveOptions);
        f.Report = Inspect(f.Oar);
        return f;
    }

    [Fact]
    public void ControlFileAndCounts()
    {
        Fixture f = Build();
        ArchiveReport r = f.Report;

        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.Equal(ArchiveKind.Oar, r.Archive.Kind);
        Assert.Equal("0.8", r.Archive.Version);
        Assert.True(r.Archive.AssetsIncluded);
        Assert.True(r.Archive.ControlFileFirst);
        Assert.False(r.Archive.MultiRegion);
        Assert.Single(r.Archive.Regions);
        Assert.Equal(f.S.Scene.RegionInfo.RegionID, r.Archive.Regions[0].Id);
        Assert.Equal(256f, r.Archive.Regions[0].SizeX);
        Assert.True(r.Archive.Compressed == false);   // the saver writes a plain tar to the stream it is given

        Assert.Equal(3, r.LinksetCount);
        Assert.Equal(5, r.PartCount);
        Assert.Equal(2, r.PrimItemCount);
        Assert.Equal(2, r.BoxedObjectCount);
        Assert.Equal(2, r.BoxedLinksetCount);       // Box A and Box B
        Assert.Equal(3, r.BoxedPartCount);
        Assert.Equal(1, r.BoxedItemCount);
        Assert.Equal(1, r.ParcelCount);
    }

    [Fact]
    public void LinksetsPartsAndOwners()
    {
        ArchiveReport r = Build().Report;

        LinksetRecord chair = r.Linksets.Single(l => l.Name == "Chair");
        Assert.Equal(3, chair.PrimCount);
        Assert.Equal(RecordContext.Archive, chair.Context);
        Assert.Equal(new Vector3(20 + (0x100 & 0x3f), 30, 25), new Vector3(chair.X, chair.Y, chair.Z));

        PartRecord root = r.Parts.Single(p => p.Name == "Chair" && p.Context == RecordContext.Archive);
        Assert.True(root.IsRoot);
        Assert.Equal(Alice, root.Creator.Id);
        Assert.Equal(Alice, root.OwnerId);
        Assert.Equal(Alice, root.LastOwnerId);
        Assert.Equal(1600000000, root.CreationDate);
        Assert.Equal(chair.LinksetId, root.LinksetId);
        Assert.NotNull(root.Masks.Base);
        Assert.NotNull(root.Masks.NextOwner);

        PartRecord table = r.Parts.Single(p => p.Name == "Table");
        Assert.Equal(Bob, table.Creator.Id);
        Assert.Equal(Alice, table.OwnerId);

        PartRecord lamp = r.Parts.Single(p => p.Name == "Lamp");
        Assert.Equal(Dave, lamp.Creator.Id);
        Assert.True(lamp.Creator.HasData);
        Assert.Equal("http://grid.example.org:8002/", lamp.Creator.Home);
        Assert.Equal("Dave Builder", lamp.Creator.Name);
        Assert.Equal(1500000000, lamp.CreationDate);
        Assert.Equal(Bob, lamp.OwnerId);

        Assert.Equal(2, r.Owners.Count(o => o.Id == Alice || o.Id == Bob));
    }

    [Fact]
    public void PrimInventoryItemsAndBoxedObjects()
    {
        Fixture f = Build();
        ArchiveReport r = f.Report;

        ItemRecord script = r.Items.Single(i => i.Name == "Chair script");
        Assert.Equal(ItemSource.PrimInventory, script.Source);
        Assert.Equal(Carol, script.Creator.Id);
        Assert.Equal(Alice, script.OwnerId);
        Assert.Equal(f.ScriptAsset, script.AssetId);
        Assert.Equal((int)AssetType.LSLText, script.AssetType);
        Assert.Equal(1610000000, script.CreationDate);
        Assert.Equal("Chair", script.PartName);
        Assert.Equal(r.Linksets.Single(l => l.Name == "Chair").LinksetId, script.LinksetId);
        Assert.Equal(RecordContext.Archive, script.Context);

        ItemRecord boxItem = r.Items.Single(i => i.Name == "Box A item");
        Assert.Equal(Dave, boxItem.Creator.Id);
        Assert.Equal(f.BoxA, boxItem.AssetId);

        BoxedObjectRecord a = r.BoxedObjects.Single(b => b.AssetId == f.BoxA);
        BoxedObjectRecord b2 = r.BoxedObjects.Single(b => b.AssetId == f.BoxB);
        Assert.Equal(1, a.Depth);
        Assert.Equal(2, b2.Depth);
        Assert.Equal(1, a.HolderCount);
        Assert.Equal(1, b2.HolderCount);
        Assert.Equal(2, a.PartCount);
        Assert.Equal(1, a.ItemCount);
        Assert.Equal(Bob, a.RootCreator.Id);
        Assert.Equal(Carol, b2.RootCreator.Id);

        ItemRecord inner = r.Items.Single(i => i.Name == "Box B item");
        Assert.Equal(RecordContext.Boxed, inner.Context);
        Assert.Equal(f.BoxA, inner.BoxedAssetId);
        Assert.Equal(1400000000, inner.CreationDate);

        // a boxed object's contents count toward its creators
        PartyTotals bob = r.Creators.Single(c => c.Id == Bob);
        Assert.Equal(1 + 2, bob.Parts);          // Table, and both parts of Box A
        PartyTotals carol = r.Creators.Single(c => c.Id == Carol);
        Assert.Equal(1, carol.Parts);            // the part of Box B
        Assert.Equal(1, carol.Items);            // the script item
        Assert.Equal(1, bob.BoxedObjects);       // Box A's root part was made by Bob
        Assert.Equal(1, carol.BoxedObjects);
    }

    [Fact]
    public void AssetsReferencesAndWhoMadeTheUsers()
    {
        Fixture f = Build();
        ArchiveReport r = f.Report;

        Assert.Contains(f.Tex1, r.PresentAssetIds);
        Assert.True(r.MissingAssetCount == 0, "missing: " + string.Join(",", r.MissingAssets));
        Assert.Contains(f.Tex1, r.CertainReferencedAssetIds);   // default texture of the table
        Assert.Contains(f.Tex2, r.CertainReferencedAssetIds);   // face 0 of the table
        Assert.Contains(f.Tex3, r.CertainReferencedAssetIds);   // terrain texture 1
        Assert.Contains(f.Tex2, r.ReferencedAssetIds);

        AssetUse tex1 = r.AssetsWithoutCreator.Single(u => u.AssetId == f.Tex1);
        Assert.False(tex1.Uncertain);
        Assert.Contains(tex1.Users, u => u.CreatorId == Bob);   // the table's maker

        AssetUse tex2 = r.AssetsWithoutCreator.Single(u => u.AssetId == f.Tex2);
        Assert.Contains(tex2.Users, u => u.CreatorId == Bob);   // face texture on the table
        Assert.Contains(tex2.Users, u => u.CreatorId == Carol); // named in the text of the script Carol made

        Assert.Contains(r.RegionSettings, s => s.TerrainTextures.Contains(f.Tex3));
        Assert.Contains(r.AssetsWithoutCreator, u => u.AssetId == f.Tex3);

        AssetTypeSummary textures = r.AssetsByType.Single(t => t.AssetType == (int)AssetType.Texture);
        Assert.Equal(4, textures.Count);          // the three above and the default prim texture every plain prim names
        Assert.Equal(964, textures.Bytes);
        Assert.Equal(7, r.AssetCount);            // 4 textures, the script, Box A and Box B; the notecard is in no object so it is not saved
    }

    [Fact]
    public void ParcelsAndRegionSettings()
    {
        ArchiveReport r = Build().Report;
        ParcelRecord p = Assert.Single(r.Parcels);
        Assert.Equal("Alice Park", p.Name);
        Assert.Equal(Alice, p.OwnerId);
        Assert.False(p.IsGroupOwned);
        Assert.Equal(Group1, p.GroupId);
        RegionSettingsRecord s = Assert.Single(r.RegionSettings);
        Assert.Equal("Test Region", s.RegionName);
    }

    [Fact]
    public void SavedWithoutAssets_SaysSoAndListsNothingMissing()
    {
        Fixture f = Build(new Dictionary<string, object> { ["noassets"] = true });
        ArchiveReport r = f.Report;

        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.Equal(false, r.Archive.AssetsIncluded);
        Assert.Equal(0, r.AssetCount);
        Assert.Equal(0, r.MissingAssetCount);                // none are expected when the archive says so
        Assert.Equal(3, r.LinksetCount);
        Assert.Equal(0, r.BoxedObjectCount);                 // no object assets, so no boxed objects to read
        Assert.Contains(f.Tex1, r.ReferencedAssetIds);       // the table still names it
        Assert.Contains(f.Tex3, r.ReferencedAssetIds);       // and the terrain still names its texture
    }

    [Fact]
    public void MultiRegion_Version10_EveryRegionAndItsObjects()
    {
        SceneManager.Instance.Scenes.Clear();
        OarScene a = NewScene("Region A");
        OarScene b = NewScene("Region B", x: 1001);
        SceneManager.Instance.Add(a.Scene);
        SceneManager.Instance.Add(b.Scene);
        AddObject(a.Scene, "In A", Alice, Alice, 1, 0x100);
        AddObject(b.Scene, "In B", Bob, Bob, 2, 0x200);

        byte[] oar = SaveOar(a, new Dictionary<string, object> { ["all"] = true });
        ArchiveReport r = Inspect(oar);
        SceneManager.Instance.Scenes.Clear();

        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.Equal(ArchiveKind.Oar, r.Archive.Kind);
        Assert.Equal("1.0", r.Archive.Version);
        Assert.True(r.Archive.MultiRegion);
        Assert.Equal(2, r.Archive.Regions.Count);
        Assert.Contains(r.Archive.Regions, x => x.Id == a.Scene.RegionInfo.RegionID && x.SizeX == 256f && x.Dir.Length > 0);
        Assert.Contains(r.Archive.Regions, x => x.Id == b.Scene.RegionInfo.RegionID);

        LinksetRecord inA = r.Linksets.Single(l => l.Name == "In A");
        LinksetRecord inB = r.Linksets.Single(l => l.Name == "In B");
        Assert.NotEqual(inA.RegionDir, inB.RegionDir);
        Assert.Equal(a.Scene.RegionInfo.RegionID, inA.RegionId);
        Assert.Equal(b.Scene.RegionInfo.RegionID, inB.RegionId);
        Assert.Equal(2, inB.PrimCount);
        Assert.Equal(2, r.RegionSettings.Count);
        Assert.Equal(2, r.ParcelCount);
    }
}
