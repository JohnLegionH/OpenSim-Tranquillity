/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Archive.GltfExport;

/// <summary>Reads a sculpt map from the bytes of a JPEG 2000 texture asset.</summary>
public static class SculptMapReader
{
    /// <summary>The map's pixels, or null with the reason in <paramref name="problem"/>.</summary>
    public static SculptMap Decode(byte[] jpeg2000, out string problem)
    {
        if (jpeg2000 == null || jpeg2000.Length == 0)
        {
            problem = "the sculpt map has no data";
            return null;
        }
        return TextureDecoder.DecodeRgb(new AssetBlob { Type = (int)AssetType.Texture, Bytes = jpeg2000 }, out problem);
    }
}
