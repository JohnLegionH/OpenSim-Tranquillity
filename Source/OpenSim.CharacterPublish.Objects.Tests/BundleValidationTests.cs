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
using N = System.Numerics;
using static OpenSim.CharacterPublish.Objects.Tests.BundleFixtures;

namespace OpenSim.CharacterPublish.Objects.Tests;

/// <summary>Each kind of defect fails validation, with a reason that names it.</summary>
public class BundleValidationTests
{
    static Edit Fresh() => new(Assemble());

    static void Fails(ValidationResult r, string code, string reasonPart)
    {
        Assert.False(r.Ok, "the bundle validated, but it should have failed with " + code);
        Assert.Contains(r.Problems, p => p.Code == code && p.Message.Contains(reasonPart, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheUntouchedFixtureValidates() => Assert.True(Fresh().Validate().Ok);

    // ---- the defects a publish bundle must refuse: a missing mesh, a script, a wrong flag, a bad description, a dangling id

    [Fact]
    public void AMissingMeshFails()
    {
        var e = Fresh();
        var meshId = e.Bundle.Assets.First(a => a.AssetType == BundleTypes.AssetMesh).Id;
        e.Content.Remove(meshId);
        e.Bundle = e.Bundle with { Assets = e.Bundle.Assets.Where(a => a.Id != meshId).ToList() };
        Fails(e.Validate(), "dangling", "not in the bundle");
    }

    [Fact]
    public void AMeshListedButWithoutItsBytesFails()
    {
        var e = Fresh();
        var meshId = e.Bundle.Assets.First(a => a.AssetType == BundleTypes.AssetMesh).Id;
        e.Content.Remove(meshId);
        Fails(e.Validate(), "missing-content", "no bytes");
    }

    [Fact]
    public void AnObjectWithAScriptFails()
    {
        var e = Fresh();
        e.ReplaceText("attachment-1", t => t.Replace("</SceneObjectPart>",
            "<TaskInventory><TaskInventoryItem><Name>script</Name><Type>10</Type></TaskInventoryItem></TaskInventory></SceneObjectPart>"));
        Fails(e.Validate(), "object", "task inventory");
    }

    [Fact]
    public void AnObjectFlaggedScriptedFails()
    {
        var e = Fresh();
        e.ReplaceText("attachment-1", t => t.Replace("<Flags>None</Flags>", "<Flags>Scripted</Flags>"));
        Fails(e.Validate(), "object", "scripted");
    }

    [Fact]
    public void AWrongWearableFlagFails()
    {
        var e = Fresh();
        e.SetItem("skin", i => i with { Flags = 0 }); // a skin whose flags say shape
        Fails(e.Validate(), "flags", "wearable type");
    }

    [Fact]
    public void AWearableWithTheWrongAssetTypeFails()
    {
        var e = Fresh();
        e.SetItem("hair", i => i with { AssetType = BundleTypes.AssetClothing });
        Assert.False(e.Validate().Ok);
    }

    [Fact]
    public void ABadDescriptionFails()
    {
        var e = Fresh();
        e.SetItem("attachment-1", i => i with { Description = "parameters of the body" });
        Fails(e.Validate(), "description", "section keyword");
    }

    [Fact]
    public void ADescriptionInsideAWearableThatOpensWithAKeywordFails()
    {
        var e = Fresh();
        // The text is changed past the writer (which refuses it), and the item made to agree, so only the safe-description rule can object.
        e.ReplaceText("eyes", t => t.Replace("\n" + Credit + "\n", "\ntextures are below\n"));
        e.SetItem("eyes", i => i with { Description = "textures are below" });
        Fails(e.Validate(), "description", "section keyword");
    }

    [Fact]
    public void ADanglingTextureIdFails()
    {
        var e = Fresh();
        e.Bundle = e.Bundle with { ExternalAssets = e.Bundle.ExternalAssets.Where(x => x != Iris).ToList() };
        Fails(e.Validate(), "dangling", "texture slot 3");
    }

    [Fact]
    public void AnItemWhoseAssetIsNotInTheBundleFails()
    {
        var e = Fresh();
        e.SetItem("notecard", i => i with { AssetId = new UUID("99999999-8888-4777-8666-555555555555") });
        Fails(e.Validate(), "dangling", "not in the bundle");
    }

    // ---- the rest of the checks

    [Fact]
    public void AnIdThatIsNotDerivedFromTheContentFails()
    {
        var e = Fresh();
        var id = e.Item("notecard").AssetId;
        e.Content[id] = Encoding.UTF8.GetBytes("tampered");
        var r = e.Validate();
        Fails(r, "asset-id", "not derived from the content");
    }

    [Fact]
    public void AMeshThatDoesNotDecodeFails()
    {
        var e = Fresh();
        var mesh = e.Bundle.Assets.First(a => a.AssetType == BundleTypes.AssetMesh);
        var junk = Encoding.UTF8.GetBytes("this is not a mesh asset at all");
        var id = ContentId.OfBytes(junk);
        e.Content.Remove(mesh.Id);
        e.Content[id] = junk;
        e.Bundle = e.Bundle with { Assets = e.Bundle.Assets.Select(a => a.Id == mesh.Id ? a with { Id = id, Length = junk.Length } : a).ToList() };
        // the object still names the old mesh id, so the junk is found as an unreferenced asset and the object as dangling;
        // point the object at the junk to reach the decoder
        e.ReplaceText("attachment-1", t => t.Replace(mesh.Id.ToString(), id.ToString()));
        Fails(e.Validate(), "mesh", "does not decode");
    }

    [Fact]
    public void AnObjectWithMoreThanOnePartFails()
    {
        var e = Fresh();
        e.ReplaceText("attachment-1", t => t.Replace("<OtherParts />", "<OtherParts><SceneObjectPart /></OtherParts>"));
        Fails(e.Validate(), "object", "more than one part");
    }

    [Fact]
    public void AnObjectThatIsNotAMeshFails()
    {
        var e = Fresh();
        e.ReplaceText("attachment-1", t => t.Replace("<SculptType>5</SculptType>", "<SculptType>1</SculptType>"));
        Fails(e.Validate(), "object", "not mesh");
    }

    [Fact]
    public void AnObjectAtAnInvalidAttachmentPointFails()
    {
        var e = Fresh();
        e.ReplaceText("attachment-1", t => t.Replace("<State>40</State>", "<State>200</State>"));
        Fails(e.Validate(), "object", "attachment point");
    }

    [Fact]
    public void AnObjectOfTheWrongSizeFails()
    {
        var e = Fresh();
        e.ReplaceText("attachment-1", t => t.Replace("<X>0.62</X>", "<X>3</X>"));
        Fails(e.Validate(), "object", "NormalizedScale");
    }

    [Fact]
    public void AnObjectBiggerThanThePrimLimitFails()
    {
        var e = Fresh();
        e.ReplaceText("attachment-2", t => t.Replace("<X>0.2</X>", "<X>500</X>"));
        Fails(e.Validate(), "object", "outside 0..64");
    }

    [Fact]
    public void AnObjectThatIsNotXmlFails()
    {
        var e = Fresh();
        e.ReplaceContent("attachment-1", _ => Encoding.UTF8.GetBytes("<SceneObjectGroup><RootPart>"));
        Fails(e.Validate(), "object", "not well-formed");
    }

    [Fact]
    public void AnObjectWithADoctypeFails()
    {
        var e = Fresh();
        e.ReplaceContent("attachment-1", _ => Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"b\">]><SceneObjectGroup/>"));
        Fails(e.Validate(), "object", "not well-formed");
    }

    [Fact]
    public void ObjectMasksThatDifferFromTheItemsFail()
    {
        var e = Fresh();
        e.SetItem("attachment-1", i => i with { NextPermissions = 0 });
        Fails(e.Validate(), "permissions", "masks inside the object");
    }

    [Fact]
    public void WearableMasksThatDifferFromTheItemsFail()
    {
        var e = Fresh();
        e.SetItem("shape", i => i with { EveryonePermissions = 0, GroupPermissions = 0, CurrentPermissions = PermissionMasks.Copy });
        Fails(e.Validate(), "permissions", "masks inside the wearable");
    }

    [Fact]
    public void AMaskWithBitsTheBaseLacksFails()
    {
        var e = Fresh();
        e.SetItem("notecard", i => i with { BasePermissions = PermissionMasks.Copy });
        Fails(e.Validate(), "permissions", "base mask lacks");
    }

    [Fact]
    public void ACreatorThatIsNotTheBundlesFails()
    {
        var e = Fresh();
        e.SetItem("notecard", i => i with { CreatorId = Owner });
        Fails(e.Validate(), "permissions", "creator differs");
    }

    [Fact]
    public void ATwoShapeOutfitFails()
    {
        var e = Fresh();
        var shape = e.Item("shape");
        e.Bundle = e.Bundle with { Items = e.Bundle.Items.Append(shape with { Id = ContentId.Derive("second"), Role = "shape-2", Name = "Second" }).ToList() };
        Fails(e.Validate(), "item", "second wearable");
    }

    [Fact]
    public void ANotecardThatDoesNotParseFails()
    {
        var e = Fresh();
        e.ReplaceContent("notecard", _ => Encoding.UTF8.GetBytes("not a notecard\n"));
        Fails(e.Validate(), "notecard", "does not parse");
    }

    [Fact]
    public void AWearableThatDoesNotParseFails()
    {
        var e = Fresh();
        e.ReplaceContent("skin", _ => Encoding.UTF8.GetBytes("LLWearable version 22\nbroken\n"));
        Fails(e.Validate(), "wearable", "does not parse");
    }

    [Fact]
    public void AnOutfitFolderOfTheWrongTypeOrParentFails()
    {
        var e = Fresh();
        e.Bundle = e.Bundle with { OutfitFolder = e.Bundle.OutfitFolder with { Type = BundleTypes.FolderMyOutfits } };
        Fails(e.Validate(), "folder", "not Outfit");
        e = Fresh();
        e.Bundle = e.Bundle with { OutfitFolder = e.Bundle.OutfitFolder with { Parent = "root" } };
        Fails(e.Validate(), "folder", "parent");
    }

    [Fact]
    public void ACurrentLookPlanWithoutTheDateOrWithTheWrongSourceFails()
    {
        var e = Fresh();
        e.Bundle = e.Bundle with { CurrentLook = e.Bundle.CurrentLook! with { FolderName = "Previous look" } };
        Fails(e.Validate(), "plan", "date");
        e = Fresh();
        e.Bundle = e.Bundle with { CurrentLook = e.Bundle.CurrentLook! with { SourceFolderType = BundleTypes.FolderOutfit } };
        Fails(e.Validate(), "plan", "not Current Outfit");
    }

    [Fact]
    public void AnAssetNothingUsesFails()
    {
        var e = Fresh();
        var junk = Encoding.UTF8.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 1\nx}\n");
        var id = ContentId.OfBytes(junk);
        e.Content[id] = junk;
        e.Bundle = e.Bundle with { Assets = e.Bundle.Assets.Append(new BundleAsset(id, BundleTypes.AssetNotecard, id + ".notecard", junk.Length, "extra")).ToList() };
        Fails(e.Validate(), "unreferenced", "not used");
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/dir.txt")]
    [InlineData("C:evil")]
    [InlineData("bundle.json")]
    [InlineData("")]
    public void AFileNameThatIsNotAPlainNameFails(string file)
    {
        var e = Fresh();
        var a = e.Bundle.Assets[0];
        e.Bundle = e.Bundle with { Assets = e.Bundle.Assets.Select(x => x.Id == a.Id ? x with { File = file } : x).ToList() };
        Fails(e.Validate(), "asset-file", "plain file name");
    }

    [Fact]
    public void LimitsOnCountAndSizeAreEnforced()
    {
        var a = Assemble();
        Assert.False(BundleValidator.Validate(a.Bundle, a.Content, new BundleLimits(MaxAssets: 3)).Ok);
        var r = BundleValidator.Validate(a.Bundle, a.Content, new BundleLimits(MaxAssetBytes: 100));
        Assert.Contains(r.Problems, p => p.Code == "limit");
    }

    [Fact]
    public void AFolderWithAFileTheManifestDoesNotListFails()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"), "stray");
        Assemble().WriteTo(folder);
        File.WriteAllText(Path.Combine(folder, "extra.bin"), "x");
        var r = BundleFiles.ValidateFolder(folder);
        Assert.Contains(r.Problems, p => p.Code == "unlisted-file" && p.Where == "extra.bin");
    }

    [Fact]
    public void ReadingAManifestWithAPathInAFileNameIsRefusedBeforeAnyFileIsTouched()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"), "traversal");
        var a = Assemble();
        a.WriteTo(folder);
        var json = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "bundle.json")));
        var file = a.Bundle.Assets[0].File;
        File.WriteAllText(Path.Combine(folder, "bundle.json"), json.Replace(file, "../" + file));
        Assert.Throws<InvalidDataException>(() => BundleFiles.Read(folder));
    }

    [Fact]
    public void ABundleWithProblemsWillNotBeWrittenToDisk()
    {
        var e = Fresh();
        e.SetItem("skin", i => i with { Flags = 0 });
        var bad = new AssembledBundle(e.Bundle, e.Content, e.Validate());
        Assert.Throws<InvalidOperationException>(() => bad.WriteTo(Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"), "bad")));
    }
}
