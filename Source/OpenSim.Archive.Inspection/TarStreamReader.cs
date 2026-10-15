/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;

namespace OpenSim.Archive.Inspection;

/// <summary>The tar stream is damaged: a header does not check out or the data stops inside an entry.</summary>
public class TarFormatException : Exception
{
    public string EntryName { get; }
    public TarFormatException(string message, string entryName = null) : base(message) { EntryName = entryName; }
}

/// <summary>A configured safety limit was reached; the message names the entry being read.</summary>
public class ArchiveLimitException : Exception
{
    public string EntryName { get; }
    public ArchiveLimitException(string message, string entryName = null) : base(message) { EntryName = entryName; }
}

public sealed class TarEntryHeader
{
    public string Name { get; set; }
    public long Size { get; set; }
    public char TypeFlag { get; set; }
    public bool IsDirectory => TypeFlag == '5' || (Name != null && Name.EndsWith("/", StringComparison.Ordinal));
    public bool IsFile => !IsDirectory && (TypeFlag == '0' || TypeFlag == '\0' || TypeFlag == '7');
}

/// <summary>
/// Forward-only tar reader. Reads v7, ustar, GNU long-name ('L') and PAX ('x', 'g') headers, sizes beyond
/// 8 GiB (base-256), and UTF-8 names. A body is either read into a byte array (with a limit set by the caller)
/// or skipped through one reused buffer, so memory does not grow with the size of an entry that is skipped.
/// </summary>
public sealed class TarStreamReader
{
    private const int BlockSize = 512;
    private const int MaxLongNameBytes = 64 * 1024;

    private readonly Stream m_stream;
    private readonly byte[] m_block = new byte[BlockSize];
    private readonly byte[] m_skipBuffer = new byte[80 * 1024];
    private TarEntryHeader m_current;
    private long m_bodyRemaining;
    private long m_paddingRemaining;

    public long EntriesRead { get; private set; }
    /// <summary>Bytes taken from the (decompressed) tar stream so far.</summary>
    public long BytesRead { get; private set; }

    public TarStreamReader(Stream stream)
    {
        m_stream = stream;
    }

    /// <summary>Name of the entry being read, or of the last one read.</summary>
    public string CurrentName => m_current?.Name;

    /// <summary>Returns the next file or directory header, or null at the end of the archive.</summary>
    /// <exception cref="TarFormatException">The header is damaged or the stream ends inside an entry.</exception>
    public TarEntryHeader ReadNext()
    {
        // Whatever the caller did not take of the previous entry is skipped here.
        if (m_current != null && (m_bodyRemaining > 0 || m_paddingRemaining > 0))
            SkipBody();

        string longName = null;
        Dictionary<string, string> pax = null;

        while (true)
        {
            int got = ReadBlock(m_block);
            if (got == 0)
                return null;   // clean end of stream without end blocks
            if (got < BlockSize)
                throw new TarFormatException("archive ends inside a tar header", m_current?.Name);

            if (IsZeroBlock(m_block))
                return null;   // end-of-archive marker

            if (!ChecksumOk(m_block))
                throw new TarFormatException("tar header checksum does not match (after entry '" + m_current?.Name + "')", m_current?.Name);

            char type = (char)m_block[156];
            long size = ParseSize(m_block, 124, 12);
            string name = ParseName(m_block);

            if (type == 'L' || type == 'K' || type == 'x' || type == 'g')
            {
                if (size < 0 || size > MaxLongNameBytes)
                    throw new TarFormatException("tar extension record is too large", name);
                byte[] data = new byte[size];
                ReadExactly(data, name);
                SkipPadding(size, name);

                if (type == 'L')
                    longName = TrimNul(Encoding.UTF8.GetString(data));
                else if (type == 'x')
                    pax = ParsePax(data, pax);
                continue;
            }

            if (longName != null)
                name = longName;
            if (pax != null)
            {
                if (pax.TryGetValue("path", out string p))
                    name = p;
                if (pax.TryGetValue("size", out string s) && long.TryParse(s, out long ps))
                    size = ps;
            }

            m_current = new TarEntryHeader { Name = name, Size = size, TypeFlag = type };
            m_bodyRemaining = size;
            m_paddingRemaining = (BlockSize - (size % BlockSize)) % BlockSize;
            EntriesRead++;
            return m_current;
        }
    }

    /// <summary>Reads the whole body of the current entry. The caller has already checked the size against its limit.</summary>
    public byte[] ReadBody()
    {
        if (m_current == null)
            throw new InvalidOperationException("no current entry");
        if (m_bodyRemaining > int.MaxValue / 2)
            throw new ArchiveLimitException("entry '" + m_current.Name + "' is too large to hold in memory", m_current.Name);

        byte[] data = new byte[m_bodyRemaining];
        ReadExactly(data, m_current.Name);
        m_bodyRemaining = 0;
        SkipPadding(m_current.Size, m_current.Name);
        m_paddingRemaining = 0;
        return data;
    }

    /// <summary>Passes over the rest of the current entry without holding it.</summary>
    public void SkipBody()
    {
        if (m_current == null)
            return;

        long remaining = m_bodyRemaining + m_paddingRemaining;
        while (remaining > 0)
        {
            int want = (int)Math.Min(m_skipBuffer.Length, remaining);
            int n = m_stream.Read(m_skipBuffer, 0, want);
            if (n <= 0)
                throw new TarFormatException("archive ends inside entry '" + m_current.Name + "'", m_current.Name);
            BytesRead += n;
            remaining -= n;
        }
        m_bodyRemaining = 0;
        m_paddingRemaining = 0;
    }

    private void SkipPadding(long size, string entry)
    {
        long pad = (BlockSize - (size % BlockSize)) % BlockSize;
        while (pad > 0)
        {
            int n = m_stream.Read(m_skipBuffer, 0, (int)Math.Min(m_skipBuffer.Length, pad));
            if (n <= 0)
                throw new TarFormatException("archive ends inside entry '" + entry + "'", entry);
            BytesRead += n;
            pad -= n;
        }
    }

    private int ReadBlock(byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = m_stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
                break;
            total += n;
        }
        BytesRead += total;
        return total;
    }

    private void ReadExactly(byte[] buffer, string entry)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = m_stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
                throw new TarFormatException("archive ends inside entry '" + entry + "'", entry);
            total += n;
        }
        BytesRead += total;
    }

    private static bool IsZeroBlock(byte[] b)
    {
        for (int i = 0; i < b.Length; i++)
            if (b[i] != 0)
                return false;
        return true;
    }

    private static bool ChecksumOk(byte[] b)
    {
        long stored = ParseOctal(b, 148, 8, out bool ok);
        if (!ok)
            return false;
        long unsigned = 0, signed = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            byte v = (i >= 148 && i < 156) ? (byte)' ' : b[i];
            unsigned += v;
            signed += (sbyte)v;
        }
        return stored == unsigned || stored == signed;
    }

    private static string ParseName(byte[] b)
    {
        string name = TrimNul(Encoding.UTF8.GetString(b, 0, 100));
        // ustar: the directory part of a long name goes in the prefix field.
        if (b[257] == 'u' && b[258] == 's' && b[259] == 't' && b[260] == 'a' && b[261] == 'r')
        {
            string prefix = TrimNul(Encoding.UTF8.GetString(b, 345, 155));
            if (prefix.Length > 0)
                name = prefix + "/" + name;
        }
        return name;
    }

    private static string TrimNul(string s)
    {
        int i = s.IndexOf('\0');
        return i >= 0 ? s.Substring(0, i) : s;
    }

    private static long ParseSize(byte[] b, int offset, int length)
    {
        if ((b[offset] & 0x80) != 0)
        {
            // base-256, big-endian, first byte's high bit is the marker
            long v = b[offset] & 0x7F;
            for (int i = 1; i < length; i++)
            {
                if (v > (long.MaxValue >> 8))
                    throw new TarFormatException("tar entry size is not valid");
                v = (v << 8) | b[offset + i];
            }
            return v;
        }

        long size = ParseOctal(b, offset, length, out bool ok);
        if (!ok)
            throw new TarFormatException("tar entry size is not valid octal");
        return size;
    }

    private static long ParseOctal(byte[] b, int offset, int length, out bool ok)
    {
        ok = true;
        long v = 0;
        int i = offset, end = offset + length;
        while (i < end && (b[i] == ' ' || b[i] == 0))
            i++;
        bool any = false;
        for (; i < end; i++)
        {
            byte c = b[i];
            if (c == ' ' || c == 0)
                break;
            if (c < '0' || c > '7')
            {
                ok = false;
                return 0;
            }
            if (v > (long.MaxValue >> 3))
            {
                ok = false;
                return 0;
            }
            v = (v << 3) | (long)(c - '0');
            any = true;
        }
        if (!any)
            return 0;
        return v;
    }

    private static Dictionary<string, string> ParsePax(byte[] data, Dictionary<string, string> into)
    {
        into ??= new Dictionary<string, string>();
        int pos = 0;
        while (pos < data.Length)
        {
            int space = Array.IndexOf(data, (byte)' ', pos);
            if (space < 0)
                break;
            if (!int.TryParse(Encoding.ASCII.GetString(data, pos, space - pos), out int recLen) || recLen <= 0 || pos + recLen > data.Length)
                break;
            string rec = Encoding.UTF8.GetString(data, space + 1, pos + recLen - space - 2);
            int eq = rec.IndexOf('=');
            if (eq > 0)
                into[rec.Substring(0, eq)] = rec.Substring(eq + 1);
            pos += recLen;
        }
        return into;
    }
}

/// <summary>Counts the bytes read from the file under a decompressor, so the compression ratio is known.</summary>
public sealed class CountingStream : Stream
{
    private readonly Stream m_inner;
    public long BytesRead { get; private set; }

    public CountingStream(Stream inner) { m_inner = inner; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = m_inner.Read(buffer, offset, count);
        BytesRead += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        int n = m_inner.Read(buffer);
        BytesRead += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => m_inner.Length;
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
