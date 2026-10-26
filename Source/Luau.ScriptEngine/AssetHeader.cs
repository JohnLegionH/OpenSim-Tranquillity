/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace Luau.ScriptEngine
{
    /// <summary>What the host reads of a compiled asset's header: its flavour and, for LSL, which events each state handles.</summary>
    internal static class AssetHeader
    {
        /// <summary>
        /// Reads the header at the front of a compiled asset. False when the bytes are not an asset of this runtime.
        /// <paramref name="stateMasks"/> has one entry per LSL state, bit (event number - 1) set for each event the
        /// state handles; it is empty for SLua.
        /// </summary>
        internal static bool TryRead(byte[] asset, out bool isLsl, out ulong[] stateMasks)
        {
            isLsl = false;
            stateMasks = null;
            if (asset == null)
                return false;

            // Layout (the runtime's BytecodeHeader, little endian): tag "LUAU", major, minor, then a section of
            // u32 length holding: u8 isLsl, u32 api version, u32 state count, one u64 mask per state, u32 charged size.
            int pos = 0;
            if (asset.Length < 12 || asset[0] != (byte)'L' || asset[1] != (byte)'U' || asset[2] != (byte)'A' || asset[3] != (byte)'U')
                return false;
            pos = 12;
            if (!ReadU32(asset, ref pos, out uint sectionLength) || sectionLength > asset.Length - pos)
                return false;
            int end = pos + (int)sectionLength;
            if (pos + 1 > end)
                return false;
            bool lsl = asset[pos++] != 0;
            if (!ReadU32(asset, ref pos, out _) || !ReadU32(asset, ref pos, out uint states))
                return false;
            if (states > (uint)(end - pos) / 8)
                return false;
            var masks = new ulong[states];
            for (int i = 0; i < masks.Length; i++)
            {
                ReadU32(asset, ref pos, out uint low);
                ReadU32(asset, ref pos, out uint high);
                masks[i] = low | ((ulong)high << 32);
            }
            isLsl = lsl;
            stateMasks = masks;
            return true;
        }

        private static bool ReadU32(byte[] data, ref int pos, out uint value)
        {
            value = 0;
            if (data.Length - pos < 4)
                return false;
            value = data[pos] | ((uint)data[pos + 1] << 8) | ((uint)data[pos + 2] << 16) | ((uint)data[pos + 3] << 24);
            pos += 4;
            return true;
        }
    }
}
