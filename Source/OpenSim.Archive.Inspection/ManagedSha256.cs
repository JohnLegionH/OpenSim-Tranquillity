/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Buffers.Binary;

namespace OpenSim.Archive.Inspection;

/// <summary>
/// SHA-256 as specified in FIPS 180-4, written in managed code. System.Security.Cryptography.SHA256 calls OpenSSL on
/// Linux, so a self-contained program that uses it fails to start on a system without libssl. The digests are the same.
/// </summary>
public static class ManagedSha256
{
    // FIPS 180-4 defines both tables from the first 64 primes: K is the first 32 bits of the fractional part of each cube root,
    // the initial hash value is the same for each square root of the first 8 primes.
    private static readonly uint[] K = FractionBits(64, 3.0);
    private static readonly uint[] InitialHash = FractionBits(8, 2.0);

    private static uint[] FractionBits(int count, double root)
    {
        uint[] bits = new uint[count];
        int found = 0;
        for (int n = 2; found < count; n++)
        {
            bool prime = true;
            for (int d = 2; d * d <= n && prime; d++)
                prime = n % d != 0;
            if (!prime)
                continue;
            double r = Math.Pow(n, 1.0 / root);
            bits[found++] = (uint)((r - Math.Floor(r)) * 4294967296.0);
        }
        return bits;
    }

    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        uint[] h = (uint[])InitialHash.Clone();
        uint[] w = new uint[64];

        int full = data.Length / 64;
        for (int i = 0; i < full; i++)
            Block(data.Slice(i * 64, 64), h, w);

        // padding: 0x80, zeros, then the bit length as a big-endian 64-bit number, to a multiple of 64 bytes
        int rest = data.Length - full * 64;
        Span<byte> tail = stackalloc byte[128];
        tail.Clear();
        data.Slice(full * 64, rest).CopyTo(tail);
        tail[rest] = 0x80;
        int tailLength = rest < 56 ? 64 : 128;
        BinaryPrimitives.WriteUInt64BigEndian(tail.Slice(tailLength - 8, 8), (ulong)data.Length * 8);
        for (int i = 0; i < tailLength; i += 64)
            Block(tail.Slice(i, 64), h, w);

        byte[] digest = new byte[32];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4, 4), h[i]);
        return digest;
    }

    private static void Block(ReadOnlySpan<byte> block, uint[] h, uint[] w)
    {
        for (int t = 0; t < 16; t++)
            w[t] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(t * 4, 4));
        for (int t = 16; t < 64; t++)
        {
            uint s0 = uint.RotateRight(w[t - 15], 7) ^ uint.RotateRight(w[t - 15], 18) ^ (w[t - 15] >> 3);
            uint s1 = uint.RotateRight(w[t - 2], 17) ^ uint.RotateRight(w[t - 2], 19) ^ (w[t - 2] >> 10);
            w[t] = w[t - 16] + s0 + w[t - 7] + s1;
        }

        uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int t = 0; t < 64; t++)
        {
            uint t1 = hh + (uint.RotateRight(e, 6) ^ uint.RotateRight(e, 11) ^ uint.RotateRight(e, 25)) + ((e & f) ^ (~e & g)) + K[t] + w[t];
            uint t2 = (uint.RotateRight(a, 2) ^ uint.RotateRight(a, 13) ^ uint.RotateRight(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
            hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }

        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }
}
