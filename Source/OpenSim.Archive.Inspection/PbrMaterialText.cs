/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse.StructuredData;

namespace OpenSim.Archive.Inspection;

/// <summary>
/// The glTF text inside a PBR material asset. The viewer stores a material as an LLSD map (binary, XML or notation, told apart by the header line) with the keys
/// version, type and data, and loads it only when the version is 1.0 or 1.1, the type is "GLTF 2.0" and data is a string (Firestorm indra/newview/llgltfmateriallist.cpp,
/// onAssetLoadComplete; indra/llprimitive/llgltfmaterial.cpp, ASSET_VERSION, ASSET_TYPE and ACCEPTED_ASSET_VERSIONS). The viewer does not load bare glTF text, but the
/// archive tools have always read it, so it still comes out as it is.
/// </summary>
public static class PbrMaterialText
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const string AssetType = "GLTF 2.0";
    private static readonly string[] AcceptedVersions = { "1.0", "1.1" };

    /// <summary>The glTF text of a material asset, or null when the bytes are neither the viewer's material map nor glTF text.</summary>
    public static byte[] Unwrap(byte[] asset)
    {
        if (asset == null || asset.Length == 0 || asset.Length > MaxBytes)
            return null;
        int first = 0;
        while (first < asset.Length && (asset[first] == ' ' || asset[first] == '\n' || asset[first] == '\r' || asset[first] == '\t'))
            first++;
        if (first < asset.Length && asset[first] == '{')
            return asset;
        try
        {
            if (OSDParser.Deserialize(asset) is not OSDMap map)
                return null;
            if (!map.TryGetValue("version", out OSD version) || Array.IndexOf(AcceptedVersions, version.AsString()) < 0)
                return null;
            if (!map.TryGetValue("type", out OSD type) || type.AsString() != AssetType)
                return null;
            if (!map.TryGetValue("data", out OSD data) || data.Type != OSDType.String)
                return null;
            return System.Text.Encoding.UTF8.GetBytes(data.AsString());
        }
        catch (Exception)
        {
            // bytes that are not a material read as nothing (the LLSD reader throws a library exception of its own for text it cannot parse)
            return null;
        }
    }
}
