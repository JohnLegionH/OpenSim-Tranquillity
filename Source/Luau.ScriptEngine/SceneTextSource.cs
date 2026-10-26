/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace Luau.ScriptEngine
{
    /// <summary>Fetches script text from the region's asset service; only script text assets count.</summary>
    internal sealed class SceneTextSource : IScriptTextSource
    {
        private readonly IAssetService m_assets;

        public SceneTextSource(IAssetService assets) => m_assets = assets;

        public void Fetch(UUID assetId, Action<string> done)
        {
            if (m_assets == null)
            {
                done(null);
                return;
            }
            m_assets.Get(assetId.ToString(), this, (id, sender, asset) =>
                done(asset != null && asset.Type == (sbyte)AssetType.LSLText ? Utils.BytesToString(asset.Data) : null));
        }
    }
}
