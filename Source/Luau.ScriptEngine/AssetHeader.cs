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
            return false;
        }
    }
}
