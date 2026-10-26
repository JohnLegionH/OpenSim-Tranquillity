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
    /// The transform N a map must be drawn with once the face transform F is in the mesh coordinates, so that N(F(p)) = M(p) for the map's
    /// transform M. False when N is not a transform a material can state (a shear: the five figures are a turn, then the repeats, then the offset).
    /// </summary>
    public static bool Compose(FaceTransform face, FaceTransform map, out FaceTransform composed)
    {
        composed = FaceTransform.Identity;
        // xform (llface.cpp 734-757) is x -> S R (x - c) + c + o with the turn R (a' = a cos + b sin, b' = -a sin + b cos), S = diag(repeats), c = (0.5, 0.5).
        // N = M after the inverse of F is x -> L (x - c) + c + (o_m - L o_f) with L = S_m R(d) S_f^-1, d the difference of the two turns; a material
        // states L as S_n R_n: the rows of L must be at right angles, with the second the first turned a quarter (a shear otherwise).
        double fu = face.RepeatU, fv = face.RepeatV;
        if (Math.Abs(fu) < 1e-6 || Math.Abs(fv) < 1e-6)
            return false;
        double d = (double)map.Rotation - face.Rotation, c = Math.Cos(d), s = Math.Sin(d);
        double l00 = map.RepeatU * c / fu, l01 = map.RepeatU * s / fv, l10 = -map.RepeatV * s / fu, l11 = map.RepeatV * c / fv;
        double nx = Math.Sqrt(l00 * l00 + l01 * l01);
        if (nx < 1e-9)
            return false;
        double phi = Math.Atan2(l01, l00);
        double ny = -l10 * Math.Sin(phi) + l11 * Math.Cos(phi);
        double ra = l10 + ny * Math.Sin(phi), rb = l11 - ny * Math.Cos(phi);
        double residual = Math.Sqrt(ra * ra + rb * rb);
        if (Math.Abs(ny) < 1e-9 || residual > 1e-3 * (nx + Math.Abs(ny)))
            return false;
        // the turn nearest zero, with the repeats' signs made to match
        if (Math.Abs(phi) > Math.PI / 2)
        {
            phi -= Math.Sign(phi) * Math.PI;
            nx = -nx;
            ny = -ny;
        }
        double ou = map.OffsetU - (l00 * face.OffsetU + l01 * face.OffsetV), ov = map.OffsetV - (l10 * face.OffsetU + l11 * face.OffsetV);
        composed = new FaceTransform((float)nx, (float)ny, (float)ou, (float)ov, (float)phi);
        return true;
    }

    /// <summary>
    /// True when a composed transform is the identity to within what a texture entry and a material lose in storing the same figures (a few
    /// ten-thousandths in each of the five; a repeat of 0.5 doubles the loss).
    /// </summary>
    public static bool NearIdentity(FaceTransform t) =>
        Math.Abs(t.RepeatU - 1) <= IdentityTolerance && Math.Abs(t.RepeatV - 1) <= IdentityTolerance && Math.Abs(t.OffsetU) <= IdentityTolerance
        && Math.Abs(t.OffsetV) <= IdentityTolerance && Math.Abs(t.Rotation) <= IdentityTolerance;

    private const float IdentityTolerance = 5e-4f;

    /// <summary>A transform as the material stores it: each figure in ten-thousandths, rounded.</summary>
    public static FaceTransform AsStored(FaceTransform t)
    {
        static float Q(float v) => (float)(Math.Round(v * (double)Repeat, MidpointRounding.AwayFromZero) / Repeat);
        return new FaceTransform(Q(t.RepeatU), Q(t.RepeatV), Q(t.OffsetU), Q(t.OffsetV), Q(t.Rotation));
    }

    /// <summary>
    /// The material with both maps' repeats set to 1 and offsets and rotations to 0, in the encoding the region writes a legacy material
    /// (FaceMaterial.toLLSDxml, OpenSim.Region.Framework.Scenes), and the id the region derives from those bytes (FaceMaterial.genID: their MD5).
    /// Everything else (the maps, the specular colour and exponent, the environment intensity and the alpha mode and cutoff) is kept.
    /// </summary>
    public static (Guid Id, byte[] Bytes) WithoutMapTransforms(OSDMap map) => WithMapTransforms(map, FaceTransform.Identity, FaceTransform.Identity);

    /// <summary>
    /// The material with the normal map drawn with <paramref name="normal"/> and the specular map with <paramref name="specular"/> (each as the
    /// material stores it, see <see cref="AsStored"/>), everything else kept.
    /// </summary>
    public static (Guid Id, byte[] Bytes) WithMapTransforms(OSDMap map, FaceTransform normal, FaceTransform specular)
    {
        static int Q(float v) => (int)Math.Round(v * (double)Repeat, MidpointRounding.AwayFromZero);
        Color4 specularColor = map["SpecColor"].AsColor4();
        osUTF8 sb = LLSDxmlEncode2.Start(1024, false);
        LLSDxmlEncode2.AddMap(sb);
        LLSDxmlEncode2.AddElem("NormMap", map["NormMap"].AsUUID(), sb);
        LLSDxmlEncode2.AddElem("NormOffsetX", Q(normal.OffsetU), sb);
        LLSDxmlEncode2.AddElem("NormOffsetY", Q(normal.OffsetV), sb);
        LLSDxmlEncode2.AddElem("NormRepeatX", Q(normal.RepeatU), sb);
        LLSDxmlEncode2.AddElem("NormRepeatY", Q(normal.RepeatV), sb);
        LLSDxmlEncode2.AddElem("NormRotation", Q(normal.Rotation), sb);
        LLSDxmlEncode2.AddElem("SpecMap", map["SpecMap"].AsUUID(), sb);
        LLSDxmlEncode2.AddElem("SpecOffsetX", Q(specular.OffsetU), sb);
        LLSDxmlEncode2.AddElem("SpecOffsetY", Q(specular.OffsetV), sb);
        LLSDxmlEncode2.AddElem("SpecRepeatX", Q(specular.RepeatU), sb);
        LLSDxmlEncode2.AddElem("SpecRepeatY", Q(specular.RepeatV), sb);
        LLSDxmlEncode2.AddElem("SpecRotation", Q(specular.Rotation), sb);
        LLSDxmlEncode2.AddArray("SpecColor", sb);
        LLSDxmlEncode2.AddElem((byte)specularColor.R, sb);
        LLSDxmlEncode2.AddElem((byte)specularColor.G, sb);
        LLSDxmlEncode2.AddElem((byte)specularColor.B, sb);
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
