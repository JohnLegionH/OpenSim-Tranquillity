/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace Luau.ScriptEngine
{
    /// <summary>
    /// The engine's own saved script state, keyed by inventory item and asset. The engine answers the region's
    /// saved-state ownership question from it. An engine that disowns a script leaves its row alone, so the script
    /// resumes if it returns with the same asset.
    /// </summary>
    internal interface ISavedStateStore
    {
        /// <summary>Whether a row exists for this item and asset, without consuming it.</summary>
        bool HasSavedState(UUID itemID, UUID assetID);

        /// <summary>Deletes the item's rows. Called when the script is deleted from inventory, never when it is disowned.</summary>
        void Delete(UUID itemID);
    }

    /// <summary>The store until saved state is built: it holds nothing.</summary>
    internal sealed class EmptyStateStore : ISavedStateStore
    {
        public bool HasSavedState(UUID itemID, UUID assetID) => false;
        public void Delete(UUID itemID) { }
    }
}
