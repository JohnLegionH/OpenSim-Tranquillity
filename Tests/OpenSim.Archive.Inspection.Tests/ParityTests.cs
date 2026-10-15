/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Framework.Serialization.External;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>The inspector's own readers against the repository's serializers, field by field.</summary>
[Collection(SceneCollection.Name)]
public class ParityTests : OpenSimTestCase
{
    private static SceneObjectGroup RichObject(string name, int tail, TestScene scene)
    {
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, Bob, name + "-", tail);
        sog.Name = name;
        int i = 0;
        foreach (SceneObjectPart p in sog.Parts)
        {
            i++;
            p.CreatorID = i == 1 ? Alice : i == 2 ? Bob : Carol;
            p.OwnerID = i == 3 ? Dave : Alice;
            p.LastOwnerID = Bob;
            p.GroupID = i == 2 ? Group1 : UUID.Zero;
            p.CreationDate = 1500000000 + i * 1000;
            p.BaseMask = 0x7fffffff;
            p.OwnerMask = 0x7ffff000 + (uint)i;
            p.GroupMask = 0x8000 + (uint)i;
            p.EveryoneMask = 0x4000 + (uint)i;
            p.NextOwnerMask = 0x82000;
            if (i == 2)
                p.CreatorData = "http://grid.example.org:8002/;Carol Maker";
            p.Description = "part " + i;
        }

        TaskInventoryItem item = AddPrimItem(sog.RootPart, "Item <&> one", UUID.Random(), AssetType.LSLText, Carol, Dave, 1400000001, "http://grid.example.org:8002/;Carol Maker");
        item.GroupID = Group1;
        item.GroupPermissions = 0x1234;
        item.EveryonePermissions = 0x2345;
        AddPrimItem(sog.Parts[1], "Item two", UUID.Random(), AssetType.Notecard, Alice, Alice, 1400000002);
        return sog;
    }

    private static void SamePart(SceneObjectPart real, PartRecord mine, bool trimmedByReader = false)
    {
        Assert.Equal(real.UUID, mine.PartId);
        Assert.Equal(real.Name, mine.Name);
        Assert.Equal(real.CreatorID, mine.Creator.Id);
        Assert.Equal(real.CreatorData ?? "", mine.Creator.Data);
        Assert.Equal(real.OwnerID, mine.OwnerId);
        Assert.Equal(real.LastOwnerID, mine.LastOwnerId);
        Assert.Equal(real.GroupID, mine.GroupId);
        Assert.Equal(real.RezzerID, mine.RezzerId);
        // the original-form reader trims the base, owner, group and everyone masks of child parts
        // (SceneObjectPart.TrimPermissions); the file holds the value as written, which is what the inspector reports
        if (!trimmedByReader)
        {
            Assert.Equal(real.BaseMask, mine.Masks.Base);
            Assert.Equal(real.OwnerMask, mine.Masks.Owner);
            Assert.Equal(real.GroupMask, mine.Masks.Group);
            Assert.Equal(real.EveryoneMask, mine.Masks.Everyone);
        }
        Assert.Equal(real.NextOwnerMask, mine.Masks.NextOwner);
        Assert.Equal(real.CreationDate, mine.CreationDate);
        Assert.Equal(real.LinkNum, mine.LinkNum);
    }

    private static void SameItems(SceneObjectPart real, ParsedPart mine)
    {
        List<TaskInventoryItem> items = real.TaskInventory.GetItems();
        Assert.Equal(items.Count, mine.Items.Count);
        foreach (TaskInventoryItem ri in items)
        {
            ItemRecord mi = mine.Items.Single(x => x.ItemId == ri.ItemID);
            Assert.Equal(ri.Name, mi.Name);
            Assert.Equal(ri.AssetID, mi.AssetId);
            Assert.Equal(ri.Type, mi.AssetType);
            Assert.Equal(ri.InvType, mi.InvType);
            Assert.Equal(ri.CreatorID, mi.Creator.Id);
            Assert.Equal(ri.CreatorData ?? "", mi.Creator.Data);
            Assert.Equal(ri.OwnerID, mi.OwnerId);
            Assert.Equal(ri.LastOwnerID, mi.LastOwnerId);
            Assert.Equal(ri.GroupID, mi.GroupId);
            Assert.Equal(ri.BasePermissions, mi.Masks.Base);
            Assert.Equal(ri.CurrentPermissions, mi.Masks.Owner);
            Assert.Equal(ri.GroupPermissions, mi.Masks.Group);
            Assert.Equal(ri.EveryonePermissions, mi.Masks.Everyone);
            Assert.Equal(ri.NextPermissions, mi.Masks.NextOwner);
            Assert.Equal((int)ri.CreationDate, mi.CreationDate);
        }
    }

    private static void SameGroup(SceneObjectGroup real, ParsedGroup mine, bool original = false)
    {
        Assert.Equal(real.RootPart.UUID, mine.RootId);
        Assert.Equal(real.Parts.Length, mine.Parts.Count);
        foreach (SceneObjectPart rp in real.Parts)
        {
            ParsedPart mp = mine.Parts.Single(p => p.Part.PartId == rp.UUID);
            SamePart(rp, mp.Part, original && rp.UUID != real.RootPart.UUID);
            SameItems(rp, mp);
            Assert.Equal(rp.UUID == real.RootPart.UUID, mp.Part.IsRoot);
        }
    }

    [Fact]
    public void Xml2Form_PartsAndPrimItems()
    {
        TestScene scene = NewScene().Scene;
        SceneObjectGroup sog = RichObject("Rich", 0x900, scene);
        scene.AddNewSceneObject(sog, false);

        string xml = SceneObjectSerializer.ToXml2Format(sog);
        SceneObjectGroup real = SceneObjectSerializer.FromXml2Format(xml);
        Assert.NotNull(real);
        ParsedObject mine = ObjectXmlParser.Parse(Utf8(xml));

        ParsedGroup g = Assert.Single(mine.Groups);
        SameGroup(real, g);
        Assert.Equal("Rich", g.Name);
    }

    [Fact]
    public void OriginalForm_PartsAndPrimItems()
    {
        TestScene scene = NewScene().Scene;
        SceneObjectGroup sog = RichObject("Rich2", 0xa00, scene);
        scene.AddNewSceneObject(sog, false);

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(sog);
        SceneObjectGroup real = SceneObjectSerializer.FromOriginalXmlFormat(xml);
        Assert.NotNull(real);
        ParsedObject mine = ObjectXmlParser.Parse(Utf8(xml));

        SameGroup(real, Assert.Single(mine.Groups), true);
    }

    [Fact]
    public void CoalescedForm_EveryObject()
    {
        TestScene scene = NewScene().Scene;
        SceneObjectGroup a = RichObject("CoA", 0xb00, scene);
        SceneObjectGroup b = RichObject("CoB", 0xc00, scene);
        CoalescedSceneObjects coa = new CoalescedSceneObjects(UUID.Zero, a, b);
        string xml = CoalescedSceneObjectsSerializer.ToXml(coa);

        Assert.True(CoalescedSceneObjectsSerializer.TryFromXml(xml, out CoalescedSceneObjects real));
        ParsedObject mine = ObjectXmlParser.Parse(Utf8(xml));

        Assert.True(mine.Coalesced);
        Assert.Equal(real.Objects.Count, mine.Groups.Count);
        foreach (SceneObjectGroup rg in real.Objects)
            SameGroup(rg, mine.Groups.Single(g => g.RootId == rg.RootPart.UUID), true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IarItems_EqualWhatTheRepoSerializerReads(bool withHome)
    {
        TestScene scene = NewScene().Scene;
        UserAccountHelpers.CreateUserWithInventory(scene, new UserAccount(Alice) { FirstName = "Ann", LastName = "Builder" }, "pw");

        InventoryItemBase item = new InventoryItemBase
        {
            ID = UUID.Random(),
            Name = "Odd <name> & more",
            Description = "d",
            AssetID = UUID.Random(),
            AssetType = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard,
            Folder = UUID.Random(),
            Owner = Bob,
            CreatorId = Alice.ToString(),
            CreationDate = 1511111111,
            BasePermissions = 0x7fffffff,
            CurrentPermissions = 0x7ffff000,
            NextPermissions = 0x82000,
            EveryOnePermissions = 0x4000,
            GroupPermissions = 0x8000,
            GroupID = Group1,
            GroupOwned = true,
            Flags = 3
        };

        Dictionary<string, object> options = new Dictionary<string, object>();
        if (withHome)
            options["home"] = "http://grid.example.org:8002";
        string xml = UserInventoryItemSerializer.Serialize(item, options, scene.UserAccountService);

        InventoryItemBase real = UserInventoryItemSerializer.Deserialize(xml);
        ItemRecord mine = MetadataParsers.ParseIarItem(Utf8(xml));

        Assert.Equal(real.Name, mine.Name);
        Assert.Equal(real.ID, mine.ItemId);
        Assert.Equal(real.InvType, mine.InvType);
        Assert.Equal(real.AssetType, mine.AssetType);
        Assert.Equal(real.AssetID, mine.AssetId);
        Assert.Equal(real.Owner, mine.OwnerId);
        Assert.Equal(real.GroupID, mine.GroupId);
        Assert.Equal(real.GroupOwned, mine.GroupOwned);
        Assert.Equal(real.CreationDate, mine.CreationDate);
        Assert.Equal(real.CreatorData ?? "", mine.Creator.Data);
        Assert.Equal((uint)real.BasePermissions, mine.Masks.Base);
        Assert.Equal((uint)real.CurrentPermissions, mine.Masks.Owner);
        Assert.Equal((uint)real.EveryOnePermissions, mine.Masks.Everyone);
        Assert.Equal((uint)real.NextPermissions, mine.Masks.NextOwner);

        // the creator field: a UUID the repo reads as CreatorId, or an OSPA name it keeps as the string
        if (UUID.TryParse(real.CreatorId, out UUID creatorId))
            Assert.Equal(creatorId, mine.Creator.Id);
        else
            Assert.Equal(real.CreatorId, mine.Creator.Raw);
        Assert.Equal(real.CreatorIdAsUuid == UUID.Zero ? UUID.Zero : real.CreatorIdAsUuid, mine.Creator.Id == UUID.Zero ? UUID.Zero : mine.Creator.Id);
        if (!withHome)
            Assert.Equal("Ann Builder", mine.Creator.Name);
    }

    [Fact]
    public void ArchiveConstants_TypeExtensionsAreTheOnesTheRepoWrites()
    {
        // every asset type extension the saver can write is one the inspector recognises as that type
        foreach (KeyValuePair<string, sbyte> kv in ArchiveConstants.EXTENSION_TO_ASSET_TYPE)
        {
            UUID id = UUID.Random();
            byte[] oar = Oar(new[] { ("assets/" + id + kv.Key, new byte[10]) });
            ArchiveReport r = Inspect(oar);
            AssetRecord a = Assert.Single(r.Assets);
            Assert.True(a.Recognised, kv.Key);
            Assert.Equal((int)kv.Value, a.AssetType);
        }
    }

    private static byte[] Oar(IEnumerable<(string, byte[])> entries) => HandMade.Oar(entries);

    // ------------------------------------------------------------------ reference parity with UuidGatherer

    [Fact]
    public void WellKnownList_IsTheOneUuidGathererSkips()
    {
        FieldInfo f = typeof(UuidGatherer).GetField("ToSkip", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        HashSet<UUID> theirs = (HashSet<UUID>)f.GetValue(null);
        Assert.Equal(theirs.Count, WellKnownAssets.Ids.Count);
        Assert.True(theirs.SetEquals(WellKnownAssets.Ids));
    }

    [Fact]
    public void ReferencedAssets_EqualWhatUuidGathererGathers()
    {
        OarScene s = NewScene();
        TestScene scene = s.Scene;
        IAssetService assets = scene.AssetService;

        UUID face0 = UUID.Parse("5a000000-0000-4000-8000-000000000001");
        UUID faceDefault = UUID.Parse("5a000000-0000-4000-8000-000000000002");
        UUID sculpt = UUID.Parse("5a000000-0000-4000-8000-000000000003");
        UUID sound = UUID.Parse("5a000000-0000-4000-8000-000000000004");
        UUID collision = UUID.Parse("5a000000-0000-4000-8000-000000000005");
        UUID particle = UUID.Parse("5a000000-0000-4000-8000-000000000006");
        UUID inScript = UUID.Parse("5a000000-0000-4000-8000-000000000007");
        UUID boxedTexture = UUID.Parse("5a000000-0000-4000-8000-000000000008");
        UUID missingFace = UUID.Parse("5a000000-0000-4000-8000-000000000009");
        UUID scriptAsset = UUID.Parse("5a000000-0000-4000-8000-00000000000a");
        UUID boxAsset = UUID.Parse("5a000000-0000-4000-8000-00000000000b");
        UUID missingScript = UUID.Parse("5a000000-0000-4000-8000-00000000000c");
        UUID anim = UUID.Parse("5a000000-0000-4000-8000-00000000000d");
        UUID notecardAsset = UUID.Parse("5a000000-0000-4000-8000-00000000000e");
        UUID inNotecard = UUID.Parse("5a000000-0000-4000-8000-00000000000f");

        foreach (UUID t in new[] { face0, faceDefault, sculpt, collision, particle, inScript, boxedTexture, inNotecard })
            StoreAsset(assets, t, AssetType.Texture, new byte[64], Alice);
        StoreAsset(assets, sound, AssetType.Sound, new byte[64], Alice);
        StoreAsset(assets, anim, AssetType.Animation, new byte[64], Alice);
        StoreAsset(assets, scriptAsset, AssetType.LSLText, Utf8("default { state_entry() { llSetTexture(\"" + inScript + "\", 0); } }"), Alice);
        StoreAsset(assets, notecardAsset, AssetType.Notecard, Utf8(string.Join("NL",
            "Linden text version 2", "{", "LLEmbeddedItems version 1", "{", "count 1", "{", "ext char index 0", "TABinv_item 0", "TAB{",
            "TABTABitem_idTAB" + UUID.Random(), "TABTABparent_idTAB" + UUID.Random(),
            "TABTABpermissions 0", "TABTAB{", "TABTABTABbase_maskTAB7fffffff", "TABTAB}",
            "TABTABasset_permissions 0", "TABTAB{", "TABTABTABbase_maskTAB7fffffff", "TABTAB}",
            "TABTABasset_idTAB" + inNotecard, "TABTABtypeTABtexture", "TABTABinv_typeTABtexture", "TABTABflagsTAB00000000",
            "TABTABsale_infoTAB0", "TABTAB{", "TABTABTABsale_typeTABnot", "TABTABTABsale_priceTAB10", "TABTAB}",
            "TABTABnameTABx|", "TABTABdescTABy|", "TABTABcreation_dateTAB1", "TAB}", "}", "Text length 1", " ", "}", "").Replace("NL", "\n").Replace("TAB", "\t")), Alice);

        SceneObjectGroup boxed = SceneHelpers.CreateSceneObject(1, Bob, "Boxed-", 0x800);
        boxed.RootPart.Shape.Textures = new Primitive.TextureEntry(boxedTexture);
        scene.AssetService.Store(AssetHelpers.CreateAsset(boxAsset, boxed));

        SceneObjectGroup a = AddObject(scene, "Everything", Alice, Alice, 2, 0xd00);
        Primitive.TextureEntry te = new Primitive.TextureEntry(faceDefault);
        te.CreateFace(0).TextureID = face0;
        te.CreateFace(1).TextureID = missingFace;      // named by a face, not in the asset service
        a.RootPart.Shape.Textures = te;
        a.RootPart.Shape.SculptTexture = sculpt;
        a.RootPart.Shape.SculptType = 1;
        a.RootPart.Sound = sound;
        a.RootPart.CollisionSound = collision;
        a.RootPart.ParticleSystem = new Primitive.ParticleSystem { Texture = particle, PartStartColor = Color4.White, PartEndColor = Color4.White }.GetBytes();
        AddPrimItem(a.RootPart, "Script", scriptAsset, AssetType.LSLText, Alice, Alice);
        AddPrimItem(a.RootPart, "Boxed", boxAsset, AssetType.Object, Alice, Alice);
        AddPrimItem(a.RootPart, "Notes", notecardAsset, AssetType.Notecard, Alice, Alice);
        AddPrimItem(a.RootPart, "Absent script", missingScript, AssetType.LSLText, Alice, Alice);
        AddPrimItem(a.Parts[1], "Anim", anim, AssetType.Animation, Alice, Alice);

        // what the saver's gatherer collects for the scene
        HashSet<UUID> gathered = new HashSet<UUID>();
        UuidGatherer gatherer = new UuidGatherer(assets);
        foreach (SceneObjectGroup sog in new[] { a })
        {
            gatherer.AddForInspection(sog);
            gatherer.GatherAll();
        }
        gathered.UnionWith(gatherer.GatheredUuids.Keys);

        // what the inspector says the archive refers to
        ArchiveReport r = Inspect(SaveOar(s));
        HashSet<UUID> tool = new HashSet<UUID>(r.CertainReferencedAssetIds);
        tool.UnionWith(r.ReferencedAssetIds.Where(r.PresentAssetIds.Contains));

        // known difference, see DESIGN.md: the gatherer drops an inventory item whose asset it cannot fetch,
        // the inspector keeps it and reports it as missing. Nothing else may differ.
        HashSet<UUID> onlyTool = new HashSet<UUID>(tool);
        onlyTool.ExceptWith(gathered);
        HashSet<UUID> onlyGatherer = new HashSet<UUID>(gathered);
        onlyGatherer.ExceptWith(tool);

        Assert.Empty(onlyGatherer);
        Assert.Equal(new[] { missingScript }, onlyTool.ToArray());
        Assert.Contains(missingScript, r.MissingAssets);
        Assert.Contains(missingFace, r.MissingAssets);          // both name it; only the inspector says it is gone
        Assert.Contains(missingFace, gathered);
        Assert.Contains(inNotecard, tool);
        Assert.Contains(inScript, tool);
        Assert.Contains(boxedTexture, tool);
    }
}
