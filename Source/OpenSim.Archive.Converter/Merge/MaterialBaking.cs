/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>
/// Makes the legacy material a merged face names. A face that bakes its texture transform into the mesh coordinates draws its normal and
/// specular maps from those same coordinates, so the maps' own repeats, offsets and rotations must not be applied a second time: the new
/// material is the old one with them taken out.
/// </summary>
internal static class MaterialBaking
{
    private const int Repeat = 10000;

    /// <summary>
    /// The material with both maps' repeats set to 1 and offsets and rotations to 0, in the encoding the region writes a legacy material
    /// (FaceMaterial.toLLSDxml, OpenSim.Region.Framework.Scenes), and the id the region derives from those bytes (FaceMaterial.genID: their MD5).
    /// Everything else (the maps, the specular colour and exponent, the environment intensity and the alpha mode and cutoff) is kept.
    /// </summary>
    public static (Guid Id, byte[] Bytes) WithoutMapTransforms(OSDMap map)
    {
        Color4 specular = map["SpecColor"].AsColor4();
        osUTF8 sb = LLSDxmlEncode2.Start(1024, false);
        LLSDxmlEncode2.AddMap(sb);
        LLSDxmlEncode2.AddElem("NormMap", map["NormMap"].AsUUID(), sb);
        LLSDxmlEncode2.AddElem("NormOffsetX", 0, sb);
        LLSDxmlEncode2.AddElem("NormOffsetY", 0, sb);
        LLSDxmlEncode2.AddElem("NormRepeatX", Repeat, sb);
        LLSDxmlEncode2.AddElem("NormRepeatY", Repeat, sb);
        LLSDxmlEncode2.AddElem("NormRotation", 0, sb);
        LLSDxmlEncode2.AddElem("SpecMap", map["SpecMap"].AsUUID(), sb);
        LLSDxmlEncode2.AddElem("SpecOffsetX", 0, sb);
        LLSDxmlEncode2.AddElem("SpecOffsetY", 0, sb);
        LLSDxmlEncode2.AddElem("SpecRepeatX", Repeat, sb);
        LLSDxmlEncode2.AddElem("SpecRepeatY", Repeat, sb);
        LLSDxmlEncode2.AddElem("SpecRotation", 0, sb);
        LLSDxmlEncode2.AddArray("SpecColor", sb);
        LLSDxmlEncode2.AddElem((byte)specular.R, sb);
        LLSDxmlEncode2.AddElem((byte)specular.G, sb);
        LLSDxmlEncode2.AddElem((byte)specular.B, sb);
        LLSDxmlEncode2.AddElem(255, sb);
        LLSDxmlEncode2.AddEndArray(sb);
        LLSDxmlEncode2.AddElem("SpecExp", (byte)map["SpecExp"].AsUInteger(), sb);
        LLSDxmlEncode2.AddElem("EnvIntensity", (byte)map["EnvIntensity"].AsUInteger(), sb);
        LLSDxmlEncode2.AddElem("DiffuseAlphaMode", (byte)map["DiffuseAlphaMode"].AsUInteger(), sb);
        LLSDxmlEncode2.AddElem("AlphaMaskCutoff", (byte)map["AlphaMaskCutoff"].AsUInteger(), sb);
        LLSDxmlEncode2.AddEndMap(sb);
        byte[] bytes = LLSDxmlEncode2.EndToBytes(sb);
        using MD5 md5 = MD5.Create();
        return (new UUID(md5.ComputeHash(bytes), 0).Guid, bytes);
    }
}
