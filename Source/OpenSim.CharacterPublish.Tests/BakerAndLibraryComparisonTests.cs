/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish.Tests;

/// <summary>The server-side baking module's own parser must read every wearable we write, to the same values.</summary>
public class BakingParserTests
{
    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_bakers_parser_reads_every_type_with_identical_values(WearableKind kind)
    {
        var d = Fixtures.Wearable(kind);
        var parsed = WearableParser.Parse(WearableWriter.Write(d));

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(d.Name, parsed.Name);
        Assert.Equal(d.Parameters.Count, parsed.Params.Count);
        foreach (var (id, v) in d.Parameters) Assert.Equal(v, parsed.Params[id]);
        Assert.Equal(d.Textures.Count, parsed.Textures.Count);
        foreach (var (slot, id) in d.Textures) Assert.Equal(id, parsed.Textures[(TextureSlot)slot]);
    }

    [Fact]
    public void The_default_shape_skin_eyes_and_hair_parse_to_the_values_the_file_holds()
    {
        var docs = new[]
        {
            WearableFactory.Shape("Shape", "", Fixtures.Perms),
            WearableFactory.Skin("Skin", "", Fixtures.Perms, Fixtures.TexA, Fixtures.TexB, Fixtures.TexC),
            WearableFactory.Eyes("Eyes", "", Fixtures.Perms, Fixtures.TexA),
            WearableFactory.Hair("Hair", "", Fixtures.Perms, Fixtures.TexB),
        };
        foreach (var d in docs)
        {
            var parsed = WearableParser.Parse(WearableWriter.Write(d));
            Assert.Equal(d.Parameters.Count, parsed.Params.Count);
            // The file keeps the viewer's two decimals, so the expected value is the default rounded that way.
            foreach (var (id, v) in d.Parameters)
                Assert.Equal(float.Parse(WearableFormat.TerseFloat(v), CultureInfo.InvariantCulture), parsed.Params[id]);
            Assert.Equal(d.Textures.Select(t => ((TextureSlot)t.Key, t.Value)).OrderBy(t => (int)t.Item1),
                parsed.Textures.Select(t => (t.Key, t.Value)).OrderBy(t => (int)t.Key));
        }
    }

    [Fact]
    public void A_description_that_looks_like_a_section_does_not_confuse_the_bakers_parser()
    {
        // The baker's parser skips the description by scanning for section keywords; a description is free text.
        var d = Fixtures.Wearable(WearableKind.Skin);
        var tricky = new WearableDocument
        {
            Name = d.Name, Description = "type 9", Permissions = d.Permissions, Type = d.Type, Parameters = d.Parameters, Textures = d.Textures,
        };
        var parsed = WearableParser.Parse(WearableWriter.Write(tricky));
        // The 'type 9' line is read as the type until the real 'type' line overrides it, so the result is still right.
        Assert.Equal(WearableKind.Skin, parsed.Kind);
        Assert.Equal(d.Parameters.Count, parsed.Params.Count);
    }

    public static IEnumerable<object[]> Kinds() => Fixtures.AllKinds.Select(k => new object[] { k });
}

/// <summary>
/// Why the writers are our own: the OpenMetaverse asset classes the tree already references do not produce the viewer's
/// text byte for byte. These tests pin the three differences found, so a library update that changes them shows up.
/// </summary>
public class OpenMetaverseComparisonTests
{
    static AssetBodypart LibraryEyes(string name, string description, params (int Id, float Weight)[] parameters)
    {
        var a = new AssetBodypart
        {
            Name = name, Description = description, WearableType = OpenMetaverse.WearableType.Eyes,
            ForSale = OpenMetaverse.SaleType.Not, SalePrice = 10,
            Creator = Fixtures.Creator, Owner = Fixtures.Owner, LastOwner = Fixtures.Creator, Group = UUID.Zero,
            Permissions = new Permissions
            {
                BaseMask = (PermissionMask)0x8e000, OwnerMask = (PermissionMask)0x8e000, GroupMask = 0,
                EveryoneMask = 0, NextOwnerMask = (PermissionMask)0x8e000,
            },
        };
        foreach (var (id, w) in parameters) a.Params[id] = w;
        a.Textures[AvatarTextureIndex.EyesIris] = Fixtures.TexA;
        a.Encode();
        return a;
    }

    static WearableDocument OursEyes(string name, string description, params (int Id, float Weight)[] parameters)
    {
        return new WearableDocument
        {
            Name = name, Description = description, Permissions = Fixtures.Perms, Type = WearableKind.Eyes, SalePrice = 10,
            Parameters = new SortedDictionary<int, float>(parameters.ToDictionary(p => p.Id, p => p.Weight)),
            Textures = new SortedDictionary<int, UUID> { [3] = Fixtures.TexA },
        };
    }

    [Fact]
    public void With_no_description_and_sorted_parameters_the_library_text_is_ours_plus_a_nul_byte()
    {
        var lib = LibraryEyes("Nm", "", (1, 0.25f), (2, 0.59f), (3, -0.59f), (4, 1.2f), (5, 24f));
        var ours = WearableWriter.Write(OursEyes("Nm", "", (1, 0.25f), (2, 0.59f), (3, -0.59f), (4, 1.2f), (5, 24f)));
        Assert.Equal(ours + "\0", Encoding.UTF8.GetString(lib.AssetData));
    }

    [Fact]
    public void The_library_drops_the_description()
    {
        var lib = Encoding.UTF8.GetString(LibraryEyes("Nm", "A description", (1, 0.5f)).AssetData);
        Assert.DoesNotContain("A description", lib);
        Assert.StartsWith("LLWearable version 22\nNm\n\n\tpermissions 0\n", lib);
    }

    [Fact]
    public void The_library_writes_parameters_in_insertion_order_where_the_viewer_sorts()
    {
        var lib = Encoding.UTF8.GetString(LibraryEyes("Nm", "", (99, 0.5f), (98, 0.1f)).AssetData).Split('\n');
        var p = Array.IndexOf(lib, "parameters 2");
        Assert.Equal(new[] { "99 .5", "98 .1" }, lib[(p + 1)..(p + 3)]);
    }

    [Fact]
    public void The_library_writes_a_zero_weight_as_dot_zero_zero_where_the_viewer_writes_zero()
    {
        var lib = Encoding.UTF8.GetString(LibraryEyes("Nm", "", (1, 0f)).AssetData).Split('\n');
        Assert.Contains("1 .00", lib);
        Assert.Contains("1 0", WearableWriter.Write(OursEyes("Nm", "", (1, 0f))).Split('\n'));
    }

    [Fact]
    public void The_library_notecard_is_ours_plus_a_nul_byte_for_text_without_embedded_items()
    {
        var text = "h\u00e9llo\nworld";
        var n = new AssetNotecard { BodyText = text };
        n.Encode();
        var lib = Encoding.UTF8.GetString(n.AssetData);
        Assert.Equal(NotecardWriter.Write(new NotecardDocument { Text = text }) + "\0", lib);
    }
}
