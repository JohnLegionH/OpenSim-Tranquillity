/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish.Tests;

public class TerseFloatTests
{
    // Cases from the viewer's own comments (llwearable.cpp:745-777) and the edges of its algorithm.
    [Theory]
    [InlineData(1.2f, "1.2")]
    [InlineData(24f, "24")]
    [InlineData(0.59f, ".59")]
    [InlineData(-0.59f, "-.59")]
    [InlineData(0f, "0")]
    [InlineData(0.5f, ".5")]
    [InlineData(0.1f, ".1")]
    [InlineData(1f, "1")]
    [InlineData(-1f, "-1")]
    [InlineData(100f, "100")]
    [InlineData(10.5f, "10.5")]
    [InlineData(-1.5f, "-1.5")]
    [InlineData(255f, "255")]
    [InlineData(0.004f, "0")]
    [InlineData(0.995f, "1")]
    [InlineData(-0.001f, "-0")]
    [InlineData(1e-10f, "0")]
    [InlineData(123.456f, "123.46")]
    public void Matches_the_viewers_terse_form(float value, string expected) =>
        Assert.Equal(expected, WearableFormat.TerseFloat(value));

    [Fact]
    public void Negative_zero_keeps_its_sign_as_printf_does() =>
        Assert.Equal("-0", WearableFormat.TerseFloat(-0f));

    [Fact]
    public void A_tie_rounds_to_even_on_the_exact_binary_value()
    {
        // 0.125 is exact in binary, so "%.2f" sees a tie. .NET rounds it to even, as a C runtime with exact conversion does;
        // the viewer's own result depends on the C runtime it is built with and is not confirmed. Slider values are two
        // decimals in practice, so no real weight is a tie. This pins the behaviour.
        Assert.Equal(".12", WearableFormat.TerseFloat(0.125f));
        Assert.Equal("-.12", WearableFormat.TerseFloat(-0.125f));
        Assert.Equal(".38", WearableFormat.TerseFloat(0.375f));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Refuses_a_value_the_viewer_cannot_read_back(float value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WearableFormat.TerseFloat(value));

    [Fact]
    public void Ignores_the_current_culture()
    {
        var odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        odd.NumberFormat.NumberDecimalSeparator = ",";
        odd.NumberFormat.NegativeSign = "~";
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = odd;
            Assert.Equal("-.59", WearableFormat.TerseFloat(-0.59f));
            Assert.Equal("1.2", WearableFormat.TerseFloat(1.2f));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}

public class WearableWriterTests
{
    const string ExpectedEyes =
        "LLWearable version 22\n" +
        "Test Eyes\n" +
        "A fixture wearable\n" +
        "\tpermissions 0\n" +
        "\t{\n" +
        "\t\tbase_mask\t0008e000\n" +
        "\t\towner_mask\t0008e000\n" +
        "\t\tgroup_mask\t00000000\n" +
        "\t\teveryone_mask\t00000000\n" +
        "\t\tnext_owner_mask\t0008e000\n" +
        "\t\tcreator_id\t11111111-2222-4333-8444-555555555555\n" +
        "\t\towner_id\t66666666-7777-4888-8999-aaaaaaaaaaaa\n" +
        "\t\tlast_owner_id\t11111111-2222-4333-8444-555555555555\n" +
        "\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n" +
        "\t}\n" +
        "\tsale_info\t0\n" +
        "\t{\n" +
        "\t\tsale_type\tnot\n" +
        "\t\tsale_price\t10\n" +
        "\t}\n" +
        "type 3\n" +
        "parameters 7\n" +
        "1 0\n" +
        "2 .59\n" +
        "3 -.59\n" +
        "4 1.2\n" +
        "5 24\n" +
        "6 -1\n" +
        "77 .5\n" +
        "textures 3\n" +
        "3 0a1b2c3d-4e5f-4a6b-8c7d-8e9fa0b1c2d3\n" +
        "8 1a2b3c4d-5e6f-4a7b-8c8d-9eaf0b1c2d3e\n" +
        "44 2a3b4c5d-6e7f-4a8b-8c9d-aeb00c1d2e3f\n";

    [Fact]
    public void Writes_the_exact_text_the_viewer_writes() =>
        Assert.Equal(ExpectedEyes, WearableWriter.Write(Fixtures.Wearable(WearableKind.Eyes)));

    [Fact]
    public void Uses_only_newlines_and_ends_with_one_and_no_nul()
    {
        var bytes = WearableWriter.WriteBytes(Fixtures.Wearable(WearableKind.Shape));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.DoesNotContain((byte)0, bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
    }

    [Fact]
    public void Writes_parameters_and_textures_in_ascending_order_whatever_the_insertion_order()
    {
        var d = new WearableDocument
        {
            Permissions = Fixtures.Perms, Type = WearableKind.Skin,
            Parameters = new SortedDictionary<int, float>(),
            Textures = new SortedDictionary<int, UUID>(),
        };
        d.Parameters[900] = 1; d.Parameters[-3] = 2; d.Parameters[10] = 3;
        d.Textures[6] = Fixtures.TexA; d.Textures[0] = Fixtures.TexB;
        var lines = WearableWriter.Write(d).Split('\n');
        var p = Array.IndexOf(lines, "parameters 3");
        Assert.Equal(new[] { "-3 2", "10 3", "900 1" }, lines[(p + 1)..(p + 4)]);
        var t = Array.IndexOf(lines, "textures 2");
        Assert.StartsWith("0 ", lines[t + 1]);
        Assert.StartsWith("6 ", lines[t + 2]);
    }

    [Theory]
    [MemberData(nameof(AllKindData))]
    public void Every_wearable_type_round_trips_through_our_reader(WearableKind kind)
    {
        var d = Fixtures.Wearable(kind);
        var text = WearableWriter.Write(d);
        var back = WearableReader.Read(text);
        Assert.True(d.ContentEquals(back), $"type {kind} changed on the round trip");
        Assert.Equal(text, WearableWriter.Write(back));
    }

    [Fact]
    public void Group_owned_and_sale_fields_survive()
    {
        var d = new WearableDocument
        {
            Name = "Sale", Permissions = new PermissionBlock(PermissionMasks.All, 0x7fffbfff, 1, 2, 3, Fixtures.Creator, Fixtures.Group, Fixtures.Owner, Fixtures.Group, true),
            SaleType = SaleType.Copy, SalePrice = 250, Type = WearableKind.Skin,
        };
        var text = WearableWriter.Write(d);
        Assert.Contains("\t\tgroup_owned\t1\n", text);
        Assert.Contains("\t\tsale_type\tcopy\n", text);
        Assert.True(d.ContentEquals(WearableReader.Read(text)));
    }

    [Fact]
    public void Round_trips_under_an_unusual_current_culture()
    {
        var odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        odd.NumberFormat.NumberDecimalSeparator = ",";
        odd.NumberFormat.NegativeSign = "~";
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = odd;
            var d = Fixtures.Wearable(WearableKind.Shape);
            var text = WearableWriter.Write(d);
            Assert.Equal(ExpectedShapeParameters, text.Split('\n').SkipWhile(l => l != "parameters 7").Skip(1).Take(7).ToArray());
            Assert.True(d.ContentEquals(WearableReader.Read(text)));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void Non_ascii_names_are_written_as_utf8()
    {
        var d = new WearableDocument { Name = "Mädchen 世界", Permissions = Fixtures.Perms, Type = WearableKind.Shape };
        var bytes = WearableWriter.WriteBytes(d);
        Assert.Equal(d.Name, WearableReader.Read(System.Text.Encoding.UTF8.GetString(bytes)).Name);
        Assert.NotEqual(0xEF, bytes[0]); // no byte order mark
    }

    [Fact]
    public void An_empty_name_and_description_keep_their_lines()
    {
        var text = WearableWriter.Write(new WearableDocument { Permissions = Fixtures.Perms, Type = WearableKind.Hair });
        Assert.StartsWith("LLWearable version 22\n\n\n\tpermissions 0\n", text);
        var back = WearableReader.Read(text);
        Assert.Equal("", back.Name);
        Assert.Equal("", back.Description);
    }

    [Fact]
    public void Refuses_what_the_viewer_would_refuse_to_read()
    {
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Name = "a\nb", Permissions = Fixtures.Perms, Type = WearableKind.Shape }));
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Description = "a\rb", Permissions = Fixtures.Perms, Type = WearableKind.Shape }));
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Name = new string('x', 2048), Permissions = Fixtures.Perms, Type = WearableKind.Shape }));
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Permissions = Fixtures.Perms, Type = WearableKind.Invalid }));
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Permissions = Fixtures.Perms, Type = (WearableKind)17 }));
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Permissions = Fixtures.Perms, Type = WearableKind.Skin, Textures = new() { [45] = Fixtures.TexA } }));
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Permissions = Fixtures.Perms, Type = WearableKind.Skin, Textures = new() { [-1] = Fixtures.TexA } }));
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Permissions = Fixtures.Perms, Type = WearableKind.Skin, SalePrice = -1 }));

        var many = new SortedDictionary<int, float>();
        for (var i = 0; i <= WearableFormat.MaxParameters; i++) many[i] = 0;
        Assert.Throws<ArgumentException>(() => WearableWriter.Write(new WearableDocument { Permissions = Fixtures.Perms, Type = WearableKind.Shape, Parameters = many }));
    }

    [Fact]
    public void The_limits_themselves_are_accepted_and_read_back()
    {
        var prms = new SortedDictionary<int, float>();
        for (var i = 0; i < WearableFormat.MaxParameters; i++) prms[i] = 0.25f;
        var texs = new SortedDictionary<int, UUID>();
        for (var i = 0; i < WearableFormat.TextureSlotCount; i++) texs[i] = Fixtures.TexA;
        var d = new WearableDocument { Name = new string('n', 2047), Permissions = Fixtures.Perms, Type = WearableKind.Shape, Parameters = prms, Textures = texs };
        Assert.True(d.ContentEquals(WearableReader.Read(WearableWriter.Write(d))));
    }

    [Fact]
    public void The_reader_is_as_strict_as_the_viewer()
    {
        var good = WearableWriter.Write(Fixtures.Wearable(WearableKind.Shape));
        Assert.Throws<FormatException>(() => WearableReader.Read(""));
        Assert.Throws<FormatException>(() => WearableReader.Read("Not a wearable\n"));
        Assert.Throws<FormatException>(() => WearableReader.Read(good.Replace("type 0\n", "type 99\n")));
        Assert.Throws<FormatException>(() => WearableReader.Read(good.Replace("parameters 7\n", "parameters 8\n")));
        Assert.Throws<FormatException>(() => WearableReader.Read(good.Replace("textures 3\n", "textures 4\n")));
        Assert.Throws<FormatException>(() => WearableReader.Read(good.Replace("permissions 0", "permissions 1")));
        Assert.Throws<FormatException>(() => WearableReader.Read(good.Replace("44 2a3b4c5d-6e7f-4a8b-8c9d-aeb00c1d2e3f", "45 2a3b4c5d-6e7f-4a8b-8c9d-aeb00c1d2e3f")));
    }

    [Fact]
    public void The_reader_tolerates_blank_lines_and_a_trailing_nul_as_the_viewer_does()
    {
        var d = Fixtures.Wearable(WearableKind.Skin);
        var text = WearableWriter.Write(d).Replace("type 1\n", "\n\ntype 1\n") + "\0";
        Assert.True(d.ContentEquals(WearableReader.Read(text)));
    }

    static readonly string[] ExpectedShapeParameters = { "1 0", "2 .59", "3 -.59", "4 1.2", "5 24", "6 -1", "77 .5" };

    public static IEnumerable<object[]> AllKindData() => Fixtures.AllKinds.Select(k => new object[] { k });
}

public class WearableFactoryTests
{
    [Fact]
    public void The_default_shape_carries_every_shape_parameter_at_its_avatar_lad_default()
    {
        var lad = AvatarLad.Embedded;
        var shape = WearableFactory.Shape("Default Shape", "", Fixtures.Perms);
        var expected = lad.Params.Values.Where(p => p.Wearable == "shape").ToDictionary(p => p.Id, p => p.Default);
        Assert.Equal(expected.Count, shape.Parameters.Count);
        Assert.True(shape.Parameters.Count > 100);
        foreach (var (id, v) in expected) Assert.Equal(v, shape.Parameters[id]);
        Assert.Empty(shape.Textures);
        Assert.Equal(WearableKind.Shape, shape.Type);
    }

    [Fact]
    public void Skin_eyes_and_hair_use_the_texture_slots_the_viewer_uses()
    {
        var skin = WearableFactory.Skin("S", "", Fixtures.Perms, Fixtures.TexA, Fixtures.TexB, Fixtures.TexC);
        Assert.Equal(new[] { 0, 5, 6 }, skin.Textures.Keys.ToArray());
        Assert.Equal(Fixtures.TexA, skin.Textures[0]);
        Assert.Equal(Fixtures.TexB, skin.Textures[5]);
        Assert.Equal(Fixtures.TexC, skin.Textures[6]);

        var eyes = WearableFactory.Eyes("E", "", Fixtures.Perms, Fixtures.TexA);
        Assert.Equal(new[] { 3 }, eyes.Textures.Keys.ToArray());

        Assert.Equal(new[] { 4 }, WearableFactory.Hair("H", "", Fixtures.Perms, Fixtures.TexA).Textures.Keys.ToArray());
        Assert.Empty(WearableFactory.Hair("H", "", Fixtures.Perms).Textures);
    }

    [Fact]
    public void Every_type_gets_only_its_own_parameters()
    {
        var lad = AvatarLad.Embedded;
        foreach (var kind in Fixtures.AllKinds)
        {
            var d = WearableFactory.CreateDefault(kind, "x", "", Fixtures.Perms);
            var owner = kind.ToString().ToLowerInvariant();
            Assert.All(d.Parameters.Keys, id => Assert.Equal(owner, lad.Params[id].Wearable));
            // Defaults need not be two-decimal numbers; the file keeps the viewer's two decimals.
            var back = WearableReader.Read(WearableWriter.Write(d));
            Assert.Equal(d.Parameters.Keys, back.Parameters.Keys);
            foreach (var (id, v) in d.Parameters)
                Assert.Equal(float.Parse(WearableFormat.TerseFloat(v), CultureInfo.InvariantCulture), back.Parameters[id]);
        }
    }
}
