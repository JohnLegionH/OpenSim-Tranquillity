/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;

namespace OpenSim.Archive.Rewrite;

/// <summary>
/// Writes a tar stream in POSIX ustar format. A name longer than 100 bytes is carried in a preceding GNU 'L'
/// ("././@LongLink") record, which the repository's tar readers understand (the record holds the name without a trailing NUL, as the repository's own
/// writer writes it); PAX records are not written. Names are UTF-8; the repository's reader decodes names as ASCII.
/// A size of 8 GiB or more uses the GNU base-256 size field. Headers are fixed (mode, owner, time), so the same
/// entries always give the same bytes.
/// </summary>
public sealed class TarWriter : IDisposable
{
    private const int BlockSize = 512;
    private const int NameFieldSize = 100;
    private const long MaxOctalSize = 0x1FFFFFFFFL;   // 8 GiB - 1: the largest size 11 octal digits hold

    private readonly Stream m_stream;
    private readonly bool m_leaveOpen;
    private bool m_finished;

    public long EntriesWritten { get; private set; }

    public TarWriter(Stream stream, bool leaveOpen = false)
    {
        m_stream = stream;
        m_leaveOpen = leaveOpen;
    }

    /// <summary>Writes a directory entry. A trailing '/' is added to the name when it is missing.</summary>
    public void WriteDirectory(string name)
    {
        if (!name.EndsWith("/", StringComparison.Ordinal))
            name += "/";
        WriteEntry(name, '5', 0, Stream.Null);
    }

    public void WriteFile(string name, byte[] data) => WriteFile(name, '0', data);

    /// <summary>Writes a file entry with the given type flag ('0' regular file; '\0' is written as '0').</summary>
    public void WriteFile(string name, char typeFlag, byte[] data)
    {
        if (typeFlag == '\0')
            typeFlag = '0';
        using MemoryStream ms = new MemoryStream(data, false);
        WriteEntry(name, typeFlag, data.Length, ms);
    }

    /// <summary>Writes an entry whose body is read from a stream; exactly <paramref name="size"/> bytes must come out of it.</summary>
    public void WriteEntry(string name, char typeFlag, long size, Stream body)
    {
        if (m_finished)
            throw new InvalidOperationException("the tar stream is already finished");
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > NameFieldSize)
        {
            // GNU long name record. The data is the full name with no trailing NUL, as the repository's own writer
            // writes it: its reader takes the whole record as the name.
            WriteHeader(Encoding.ASCII.GetBytes("././@LongLink"), 'L', nameBytes.Length);
            m_stream.Write(nameBytes, 0, nameBytes.Length);
            WritePadding(nameBytes.Length);
        }

        WriteHeader(nameBytes, typeFlag, size);
        CopyBody(body, size, name);
        WritePadding(size);
        EntriesWritten++;
    }

    /// <summary>Writes the two zero blocks that end an archive. Called once; further writes are refused.</summary>
    public void Finish()
    {
        if (m_finished)
            return;
        m_finished = true;
        m_stream.Write(new byte[BlockSize * 2], 0, BlockSize * 2);
        m_stream.Flush();
    }

    public void Dispose()
    {
        Finish();
        if (!m_leaveOpen)
            m_stream.Dispose();
    }

    private void WriteHeader(byte[] nameBytes, char typeFlag, long size)
    {
        byte[] h = new byte[BlockSize];
        // A name over 100 bytes goes in the field cut short; the reader takes the full name from the 'L' record.
        Buffer.BlockCopy(nameBytes, 0, h, 0, Math.Min(nameBytes.Length, NameFieldSize));

        bool dir = typeFlag == '5';
        PutOctal(h, 100, 8, dir ? 0x1ED : 0x1A4);   // mode 0755 for directories, 0644 for the rest
        PutOctal(h, 108, 8, 0);                      // uid
        PutOctal(h, 116, 8, 0);                      // gid
        if (size <= MaxOctalSize)
            PutOctal(h, 124, 12, size);
        else
        {
            // GNU base-256: high bit set in the first byte, then the size big-endian.
            h[124] = 0x80;
            for (int i = 0; i < 8; i++)
                h[135 - i] = (byte)(size >> (8 * i));
        }
        PutOctal(h, 136, 12, 0);                     // modification time: fixed so output is repeatable
        h[156] = (byte)typeFlag;
        Encoding.ASCII.GetBytes("ustar").CopyTo(h, 257);   // magic "ustar" NUL
        h[263] = (byte)'0';                                 // version "00"
        h[264] = (byte)'0';

        for (int i = 148; i < 156; i++)
            h[i] = (byte)' ';
        long sum = 0;
        foreach (byte b in h)
            sum += b;
        // Checksum: six octal digits, NUL, space.
        string s = Convert.ToString(sum, 8).PadLeft(6, '0');
        Encoding.ASCII.GetBytes(s).CopyTo(h, 148);
        h[154] = 0;
        h[155] = (byte)' ';

        m_stream.Write(h, 0, BlockSize);
    }

    private static void PutOctal(byte[] h, int offset, int length, long value)
    {
        // length-1 octal digits then a NUL
        string s = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        Encoding.ASCII.GetBytes(s).CopyTo(h, offset);
        h[offset + length - 1] = 0;
    }

    private void CopyBody(Stream body, long size, string name)
    {
        byte[] buffer = new byte[Math.Min(size, 80 * 1024) > 0 ? (int)Math.Min(size, 80 * 1024) : 1];
        long remaining = size;
        while (remaining > 0)
        {
            int n = body.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n <= 0)
                throw new EndOfStreamException("the body of '" + name + "' ended " + remaining + " bytes before its declared size");
            m_stream.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private void WritePadding(long size)
    {
        int pad = (int)((BlockSize - size % BlockSize) % BlockSize);
        if (pad > 0)
            m_stream.Write(new byte[pad], 0, pad);
    }
}
