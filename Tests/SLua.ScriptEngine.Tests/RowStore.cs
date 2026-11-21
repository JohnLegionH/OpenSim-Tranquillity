/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace SLua.ScriptEngine.Tests;

/// <summary>A saved-state store holding one row, counting the deletes it is asked for.</summary>
internal sealed class RowStore : ISavedStateStore
{
    private readonly UUID m_item;
    private readonly UUID m_asset;
    private int m_deletes;

    public RowStore(UUID item, UUID asset) { m_item = item; m_asset = asset; }

    public int Deletes => Volatile.Read(ref m_deletes);

    public bool HasSavedState(UUID itemID, UUID assetID) => itemID == m_item && assetID == m_asset && Deletes == 0;
    public void Delete(UUID itemID) => Interlocked.Increment(ref m_deletes);
}
