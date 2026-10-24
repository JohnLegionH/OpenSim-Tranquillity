/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Text;
using OpenMetaverse;

namespace OpenSim.CharacterPublish.Tests;

public class NotecardTests
{
    static EmbeddedItem Item(string name = "A texture") => new(
        new UUID("c0c0c0c0-1111-4222-8333-444444444444"), new UUID("d0d0d0d0-1111-4222-8333-444444444444"),
        PermissionBlock.ForOwner(Fixtures.Creator, Fixtures.Owner, PermissionMasks.CopyModifyTransfer),
        Fixtures.TexA, "texture", "texture", 0, SaleType.Not, 0, name, "desc", 1700000000);

    [Fact]
    public void Writes_the_exact_text_the_viewer_writes_for_a_plain_notecard() =>
        Assert.Equal(
            "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 6\nHello\n}\n",
            NotecardWriter.Write(new NotecardDocument { Text = "Hello\n" }));

    [Fact]
    public void Text_length_counts_utf8_bytes_not_characters()
    {
        var text = "Café 世界"; // 4 + 1 + 3 + 3 bytes... 'é' is 2 bytes, each CJK character 3
        var bytes = NotecardWriter.WriteBytes(new NotecardDocument { Text = text });
        Assert.Contains("Text length " + Encoding.UTF8.GetByteCount(text) + "\n", Encoding.UTF8.GetString(bytes));
        Assert.Equal(12, Encoding.UTF8.GetByteCount(text));
        Assert.Equal(text, NotecardReader.Read(bytes).Text);
    }

    [Fact]
    public void Writes_an_embedded_item_in_the_viewers_legacy_chunk()
    {
        var text = "See " + NotecardWriter.EmbeddedChar(0) + " here";
        var written = NotecardWriter.Write(new NotecardDocument { Text = text, Items = new[] { Item() } });
        var expectedHead =
            "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 1\n{\next char index 0\n" +
            "\tinv_item\t0\n\t{\n" +
            "\t\titem_id\tc0c0c0c0-1111-4222-8333-444444444444\n" +
            "\t\tparent_id\td0d0d0d0-1111-4222-8333-444444444444\n" +
            "\t\tpermissions 0\n\t\t{\n" +
            "\t\t\tbase_mask\t0008e000\n\t\t\towner_mask\t0008e000\n\t\t\tgroup_mask\t00000000\n\t\t\teveryone_mask\t00000000\n\t\t\tnext_owner_mask\t0008e000\n" +
            "\t\t\tcreator_id\t11111111-2222-4333-8444-555555555555\n\t\t\towner_id\t66666666-7777-4888-8999-aaaaaaaaaaaa\n" +
            "\t\t\tlast_owner_id\t11111111-2222-4333-8444-555555555555\n\t\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n" +
            "\t\t}\n" +
            "\t\tasset_id\t0a1b2c3d-4e5f-4a6b-8c7d-8e9fa0b1c2d3\n" +
            "\t\ttype\ttexture\n\t\tinv_type\ttexture\n\t\tflags\t00000000\n" +
            "\t\tsale_info\t0\n\t\t{\n\t\t\tsale_type\tnot\n\t\t\tsale_price\t0\n\t\t}\n" +
            "\t\tname\tA texture|\n\t\tdesc\tdesc|\n\t\tcreation_date\t1700000000\n" +
            "\t}\n}\n}\n";
        Assert.StartsWith(expectedHead, written);
        Assert.EndsWith("}\n", written);

        var back = NotecardReader.Read(Encoding.UTF8.GetBytes(written));
        Assert.Equal(text, back.Text);
        Assert.Equal(Item(), back.Items.Single());
    }

    [Fact]
    public void The_embedded_character_is_u100000_plus_the_index_in_utf8()
    {
        Assert.Equal("\U00100000", NotecardWriter.EmbeddedChar(0));
        Assert.Equal("\U00100003", NotecardWriter.EmbeddedChar(3));
        Assert.Equal(new byte[] { 0xF4, 0x80, 0x80, 0x80 }, Encoding.UTF8.GetBytes(NotecardWriter.EmbeddedChar(0)));
    }

    [Fact]
    public void Several_items_are_numbered_in_order_and_round_trip()
    {
        var items = new[] { Item("one"), Item("two"), Item("three") };
        var text = NotecardWriter.EmbeddedChar(2) + NotecardWriter.EmbeddedChar(0) + "x";
        var written = NotecardWriter.Write(new NotecardDocument { Text = text, Items = items });
        Assert.Contains("count 3\n", written);
        Assert.Contains("ext char index 2\n", written);
        var back = NotecardReader.Read(Encoding.UTF8.GetBytes(written));
        Assert.Equal(items, back.Items);
        Assert.Equal(text, back.Text);
    }

    [Fact]
    public void A_text_that_refers_to_a_missing_item_is_refused() =>
        Assert.Throws<ArgumentException>(() => NotecardWriter.Write(new NotecardDocument { Text = NotecardWriter.EmbeddedChar(0) }));

    [Fact]
    public void An_item_whose_asset_id_the_viewer_would_encrypt_is_refused()
    {
        var restricted = Item() with { Permissions = PermissionBlock.ForOwner(Fixtures.Creator, Fixtures.Owner, PermissionMasks.Copy | PermissionMasks.Move) };
        Assert.Throws<NotSupportedException>(() => NotecardWriter.Write(new NotecardDocument { Items = new[] { restricted } }));
    }

    [Fact]
    public void Item_text_that_breaks_the_line_format_is_refused()
    {
        Assert.Throws<ArgumentException>(() => NotecardWriter.Write(new NotecardDocument { Items = new[] { Item("a|b") } }));
        Assert.Throws<ArgumentException>(() => NotecardWriter.Write(new NotecardDocument { Items = new[] { Item("a\nb") } }));
    }

    [Fact]
    public void The_size_limit_is_the_viewers()
    {
        var atLimit = new string('a', NotecardWriter.MaxTextBytes);
        Assert.Equal(atLimit, NotecardReader.Read(NotecardWriter.WriteBytes(new NotecardDocument { Text = atLimit })).Text);
        Assert.Throws<ArgumentException>(() => NotecardWriter.Write(new NotecardDocument { Text = atLimit + "a" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no final newline")]
    [InlineData("}\n}\n")]
    [InlineData("Text length 5\n")]
    [InlineData("tabs\tand\r\nCRLF\r\n")]
    [InlineData("line one\n\nline three\n")]
    public void Awkward_text_round_trips_byte_for_byte(string text)
    {
        var bytes = NotecardWriter.WriteBytes(new NotecardDocument { Text = text });
        Assert.Equal(text, NotecardReader.Read(bytes).Text);
        Assert.Equal(bytes, NotecardWriter.WriteBytes(NotecardReader.Read(bytes)));
    }

    [Fact]
    public void The_reader_checks_what_the_viewer_checks()
    {
        var good = NotecardWriter.Write(new NotecardDocument { Text = "Hello" });
        byte[] B(string s) => Encoding.UTF8.GetBytes(s);
        Assert.Throws<FormatException>(() => NotecardReader.Read(B("")));
        Assert.Throws<FormatException>(() => NotecardReader.Read(B(good.Replace("version 2", "version 3"))));
        Assert.Throws<FormatException>(() => NotecardReader.Read(B(good.Replace("Text length 5", "Text length 9"))));
        Assert.Throws<FormatException>(() => NotecardReader.Read(B(good.Replace("Text length 5", "Text length -1"))));
        Assert.Throws<FormatException>(() => NotecardReader.Read(B(good.Replace("count 0", "count 1"))));
        Assert.Throws<FormatException>(() => NotecardReader.Read(B(good.TrimEnd('\n').TrimEnd('}'))));
    }

    [Theory]
    [InlineData("}\n")]
    [InlineData("}")]
    [InlineData("}\n\0")]
    public void The_reader_accepts_the_endings_real_writers_produce(string ending)
    {
        var good = NotecardWriter.Write(new NotecardDocument { Text = "Hello" });
        var bytes = Encoding.UTF8.GetBytes(good[..^2] + ending);
        Assert.Equal("Hello", NotecardReader.Read(bytes).Text);
        Assert.Throws<FormatException>(() => NotecardReader.Read(Encoding.UTF8.GetBytes(good[..^2] + "x}\n")));
    }

    [Fact]
    public void The_attribution_style_notecard_is_a_valid_asset()
    {
        var text = "Mesh body: Example Body by Example Author, CC BY 4.0, modified.\nSource: https://example.org/body\nLicence: https://creativecommons.org/licenses/by/4.0/\n";
        var back = NotecardReader.Read(NotecardWriter.WriteBytes(new NotecardDocument { Text = text }));
        Assert.Equal(text, back.Text);
        Assert.Empty(back.Items);
    }
}
