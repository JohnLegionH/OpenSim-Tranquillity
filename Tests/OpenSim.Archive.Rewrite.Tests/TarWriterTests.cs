/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenSim.Archive.Inspection;
using OpenSim.Framework.Serialization;
using Xunit;

namespace OpenSim.Archive.Rewrite.Tests;

public class TarWriterTests
{
    private static byte[] Write(Action<TarWriter> fill)
    {
        MemoryStream ms = new MemoryStream();
        using (TarWriter w = new TarWriter(ms, leaveOpen: true))
            fill(w);
        return ms.ToArray();
    }

    private static List<(string name, char type, byte[] data)> ReadWithRepoReader(byte[] tar)
    {
        List<(string, char, byte[])> list = new List<(string, char, byte[])>();
        TarArchiveReader r = new TarArchiveReader(new MemoryStream(tar));
        byte[] data;
        while ((data = r.ReadEntry(out string path, out TarArchiveReader.TarEntryType type)) != null)
            list.Add((path, type == TarArchiveReader.TarEntryType.TYPE_DIRECTORY ? '5' : '0', data));
        return list;
    }

    private static List<(string name, char type, byte[] data)> ReadWithInspectorReader(byte[] tar)
    {
        List<(string, char, byte[])> list = new List<(string, char, byte[])>();
        TarStreamReader r = new TarStreamReader(new MemoryStream(tar));
        TarEntryHeader h;
        while ((h = r.ReadNext()) != null)
            list.Add((h.Name, h.TypeFlag, r.ReadBody()));
        return list;
    }

    [Fact]
    public void ShortName_IsInTheHeaderField_WithoutALongNameRecord()
    {
        byte[] tar = Write(w => w.WriteFile("assets/a.txt", Encoding.ASCII.GetBytes("hello")));
        // header + one data block + two end blocks
        Assert.Equal(512 * 4, tar.Length);
        Assert.Equal("assets/a.txt", Encoding.ASCII.GetString(tar, 0, 12));
        Assert.Equal((byte)'0', tar[156]);
        Assert.Equal("ustar\0" + "00", Encoding.ASCII.GetString(tar, 257, 8));
    }

    [Fact]
    public void NameOf100Bytes_StaysInTheHeader_And101BytesGetsALongNameRecord()
    {
        string n100 = "d/" + new string('a', 98);
        string n101 = "d/" + new string('b', 99);
        byte[] a = Write(w => w.WriteFile(n100, new byte[1]));
        byte[] b = Write(w => w.WriteFile(n101, new byte[1]));
        Assert.Equal(512 * 4, a.Length);                 // header, data, two end blocks
        Assert.Equal(512 * 6, b.Length);                 // L header, L data, header, data, two end blocks
        Assert.Equal("././@LongLink", Encoding.ASCII.GetString(b, 0, 13));
        Assert.Equal((byte)'L', b[156]);
        Assert.Equal(n100, ReadWithRepoReader(a)[0].name);
        Assert.Equal(n101, ReadWithRepoReader(b)[0].name);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(255)]
    [InlineData(512)]
    [InlineData(1500)]
    public void LongNames_RoundTripThroughBothReaders(int length)
    {
        string name = "assets/" + new string('x', length - 7 - 4) + ".txt";
        Assert.Equal(length, name.Length);
        byte[] body = Encoding.ASCII.GetBytes("long name body");
        byte[] tar = Write(w => w.WriteFile(name, body));

        var viaRepo = ReadWithRepoReader(tar);
        Assert.Single(viaRepo);
        Assert.Equal(name, viaRepo[0].name);
        Assert.Equal(body, viaRepo[0].data);

        var viaInspector = ReadWithInspectorReader(tar);
        Assert.Single(viaInspector);
        Assert.Equal(name, viaInspector[0].name);
        Assert.Equal(body, viaInspector[0].data);
    }

    [Fact]
    public void NonAsciiName_IsWrittenAsUtf8_AndReadBackByTheInspectorReader()
    {
        // The repository's own reader decodes names as ASCII, so only the inspector's reader is checked here.
        string name = "objects/" + new string('é', 60) + ".xml";    // 132 bytes in UTF-8, over the 100 limit
        byte[] tar = Write(w => w.WriteFile(name, new byte[3]));
        Assert.Equal(name, ReadWithInspectorReader(tar)[0].name);
    }

    [Fact]
    public void EmptyFile_HasNoDataBlock()
    {
        byte[] tar = Write(w => w.WriteFile("empty.txt", Array.Empty<byte>()));
        Assert.Equal(512 * 3, tar.Length);
        var entries = ReadWithRepoReader(tar);
        Assert.Single(entries);
        Assert.Empty(entries[0].data);
        Assert.Empty(ReadWithInspectorReader(tar)[0].data);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(70000)]
    [InlineData(5 * 1024 * 1024 + 7)]
    public void FileSizes_AreRoundTrippedAndPaddedToBlocks(int size)
    {
        byte[] body = new byte[size];
        new Random(size).NextBytes(body);
        byte[] tar = Write(w => w.WriteFile("assets/big.bin", body));
        Assert.Equal(0, tar.Length % 512);
        Assert.Equal(512 + ((size + 511) / 512) * 512 + 1024, tar.Length);
        Assert.Equal(body, ReadWithRepoReader(tar)[0].data);
        Assert.Equal(body, ReadWithInspectorReader(tar)[0].data);
    }

    [Fact]
    public void Directories_AreTypeFive_WithTrailingSlash()
    {
        byte[] tar = Write(w =>
        {
            w.WriteDirectory("objects");
            w.WriteFile("objects/x.xml", new byte[2]);
        });
        var entries = ReadWithInspectorReader(tar);
        Assert.Equal(("objects/", '5'), (entries[0].name, entries[0].type));
        Assert.Equal(("objects/x.xml", '0'), (entries[1].name, entries[1].type));
        Assert.Equal('5', ReadWithRepoReader(tar)[0].type);
    }

    [Fact]
    public void ManyEntries_KeepTheirOrder()
    {
        string[] names = Enumerable.Range(0, 200).Select(i => (i % 7 == 0 ? "long/" + new string('n', 110) : "s/") + i + ".bin").ToArray();
        byte[] tar = Write(w => { foreach (string n in names) w.WriteFile(n, Encoding.ASCII.GetBytes(n)); });
        Assert.Equal(names, ReadWithRepoReader(tar).Select(e => e.name).ToArray());
        Assert.Equal(names, ReadWithInspectorReader(tar).Select(e => e.name).ToArray());
    }

    [Fact]
    public void SameEntries_GiveTheSameBytes()
    {
        Assert.Equal(Write(w => w.WriteFile("a", new byte[5])), Write(w => w.WriteFile("a", new byte[5])));
    }

    [Fact]
    public void HeaderChecksum_IsValidOctalWithNulAndSpace()
    {
        byte[] tar = Write(w => w.WriteFile("a.txt", new byte[10]));
        long stored = Convert.ToInt64(Encoding.ASCII.GetString(tar, 148, 6), 8);
        Assert.Equal(0, tar[154]);
        Assert.Equal((byte)' ', tar[155]);
        long sum = 0;
        for (int i = 0; i < 512; i++)
            sum += (i >= 148 && i < 156) ? (byte)' ' : tar[i];
        Assert.Equal(sum, stored);
    }

    [Fact]
    public void BodyShorterThanItsSize_IsRefused()
    {
        using TarWriter w = new TarWriter(new MemoryStream(), leaveOpen: true);
        Assert.Throws<EndOfStreamException>(() => w.WriteEntry("a", '0', 100, new MemoryStream(new byte[10])));
    }

    /// <summary>A stream of zeros of a set length, so a multi-gigabyte entry needs no memory.</summary>
    private sealed class ZeroStream : Stream
    {
        private long m_left;
        public ZeroStream(long length) { m_left = length; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, m_left);
            Array.Clear(buffer, offset, n);
            m_left -= n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>Keeps the first bytes written and counts the rest.</summary>
    private sealed class HeadSink : Stream
    {
        public readonly byte[] Head = new byte[1024];
        public long Total;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Total < Head.Length)
                Array.Copy(buffer, offset, Head, Total, Math.Min(count, Head.Length - (int)Total));
            Total += count;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Total;
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    [Fact]
    public void SizeOfEightGigabytesOrMore_UsesBase256_AndTheInspectorReaderParsesIt()
    {
        long size = 9L * 1024 * 1024 * 1024;
        HeadSink sink = new HeadSink();
        using (TarWriter w = new TarWriter(sink, leaveOpen: true))
            w.WriteEntry("assets/huge.bin", '0', size, new ZeroStream(size));
        Assert.Equal(512 + size + 1024, sink.Total);         // size is a whole number of blocks, so no padding
        Assert.Equal(0x80, sink.Head[124]);

        TarStreamReader r = new TarStreamReader(new MemoryStream(sink.Head, 0, 512));
        TarEntryHeader h = r.ReadNext();
        Assert.Equal("assets/huge.bin", h.Name);
        Assert.Equal(size, h.Size);
    }
}
