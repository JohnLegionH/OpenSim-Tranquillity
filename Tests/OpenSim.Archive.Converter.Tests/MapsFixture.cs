/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Archives whose faces name legacy materials with normal and specular maps. Two objects of eight parts each (boxes and cylinders, turned
/// and sized differently); in every part
///   face 0 is default-mapped, material A, and its own transform is the one both maps of A carry;
///   face 1 is planar, material B (a normal map only), face transform equal to the map's;
///   face 2 is planar, material A2 (the maps of A with another transform), face transform equal to that one: it is the same material as A once
///          the transforms are taken out;
///   face 3 is default-mapped, material M whose normal map is drawn with a transform that is a shear relative to the face transform (a different turn on an uneven scale), so it cannot be baked.
/// The face transforms are the values a texture entry stores (written and read back), and the material figures are those values rounded to
/// the steps a material stores, so the two differ by what the two storage formats lose, as they do in a real archive.
/// </summary>
internal static class MapsFixture
{
    public static readonly Guid Diffuse = Guid.Parse("21111111-2222-4333-8444-555555555555");
    public static readonly Guid NormalMap = Guid.Parse("22111111-2222-4333-8444-555555555555");
    public static readonly Guid SpecularMap = Guid.Parse("23111111-2222-4333-8444-555555555555");
    public static readonly Guid MaterialA = Guid.Parse("31111111-2222-4333-8444-555555555555");
    public static readonly Guid MaterialB = Guid.Parse("32111111-2222-4333-8444-555555555555");
    public static readonly Guid MaterialA2 = Guid.Parse("33111111-2222-4333-8444-555555555555");
    public static readonly Guid MaterialM = Guid.Parse("34111111-2222-4333-8444-555555555555");
    public const int ObjectCount = 2, PartsPerObject = 8;

    public static readonly FaceTransform TransformA = AsStored(new FaceTransform(2f, 3f, 0.25f, -0.5f, 0.3f));
    public static readonly FaceTransform TransformB = AsStored(new FaceTransform(2f, 0.5f, -0.125f, 0.0625f, 1.2f));
    public static readonly FaceTransform TransformA2 = AsStored(new FaceTransform(1.5f, 1.5f, 0.5f, 0.25f, 0f));
    public static readonly FaceTransform FaceM = AsStored(new FaceTransform(2f, 3f, 0.1f, 0.2f, 0.4f));
    public static readonly FaceTransform NormalM = AsStored(new FaceTransform(3f, 2f, 0.1f, 0.2f, 1.0f));

    /// <summary>What a texture entry keeps of a transform: written and read back.</summary>
    public static FaceTransform AsStored(FaceTransform t)
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(UUID.Zero);
        te.DefaultTexture.RepeatU = t.RepeatU; te.DefaultTexture.RepeatV = t.RepeatV;
        te.DefaultTexture.OffsetU = t.OffsetU; te.DefaultTexture.OffsetV = t.OffsetV; te.DefaultTexture.Rotation = t.Rotation;
        byte[] bytes = te.GetBytes();
        Primitive.TextureEntryFace f = new Primitive.TextureEntry(bytes, 0, bytes.Length).DefaultTexture;
        return new FaceTransform(f.RepeatU, f.RepeatV, f.OffsetU, f.OffsetV, f.Rotation);
    }

    /// <summary>The legacy material asset: the maps and the transform each is drawn with, in the steps (ten thousandths) a material stores.</summary>
    public static byte[] MaterialXml(Guid normalMap, FaceTransform normal, Guid specularMap, FaceTransform specular)
    {
        OSDMap m = new OSDMap();
        static int Q(float v) => (int)Math.Round(v * 10000.0, MidpointRounding.AwayFromZero);
        m["NormMap"] = new UUID(normalMap);
        m["NormOffsetX"] = Q(normal.OffsetU); m["NormOffsetY"] = Q(normal.OffsetV);
        m["NormRepeatX"] = Q(normal.RepeatU); m["NormRepeatY"] = Q(normal.RepeatV); m["NormRotation"] = Q(normal.Rotation);
        m["SpecMap"] = new UUID(specularMap);
        m["SpecOffsetX"] = Q(specular.OffsetU); m["SpecOffsetY"] = Q(specular.OffsetV);
        m["SpecRepeatX"] = Q(specular.RepeatU); m["SpecRepeatY"] = Q(specular.RepeatV); m["SpecRotation"] = Q(specular.Rotation);
        OSDArray color = new OSDArray { 255, 255, 255, 255 };
        m["SpecColor"] = color;
        m["SpecExp"] = 51; m["EnvIntensity"] = 0; m["DiffuseAlphaMode"] = 0; m["AlphaMaskCutoff"] = 0;
        return OSDParser.SerializeLLSDXmlBytes(m);
    }

    public static Dictionary<string, byte[]> Assets(int normalMapSize = 512, int specularMapSize = 256, int diffuseSize = 1024) => new Dictionary<string, byte[]>
    {
        ["assets/" + Diffuse + "_texture.jp2"] = TextureKit.Solid(diffuseSize, diffuseSize, withAlphaChannel: false),
        ["assets/" + NormalMap + "_texture.jp2"] = TextureKit.Solid(normalMapSize, normalMapSize, withAlphaChannel: false),
        ["assets/" + SpecularMap + "_texture.jp2"] = TextureKit.Solid(specularMapSize, specularMapSize, withAlphaChannel: false),
        ["assets/" + MaterialA + "_material.xml"] = MaterialXml(NormalMap, TransformA, SpecularMap, TransformA),
        ["assets/" + MaterialB + "_material.xml"] = MaterialXml(NormalMap, TransformB, Guid.Empty, FaceTransform.Identity),
        ["assets/" + MaterialA2 + "_material.xml"] = MaterialXml(NormalMap, TransformA2, SpecularMap, TransformA2),
        ["assets/" + MaterialM + "_material.xml"] = MaterialXml(NormalMap, NormalM, SpecularMap, FaceM)
    };

    private static Primitive.TextureEntryFace Face(Primitive.TextureEntry te, uint index, MappingType mapping, Guid material, FaceTransform t)
    {
        Primitive.TextureEntryFace f = te.CreateFace(index);
        f.TextureID = new UUID(Diffuse);
        f.MaterialID = new UUID(material);
        f.TexMapType = mapping;
        f.RepeatU = t.RepeatU; f.RepeatV = t.RepeatV; f.OffsetU = t.OffsetU; f.OffsetV = t.OffsetV; f.Rotation = t.Rotation;
        return f;
    }

    /// <summary>The texture entry of one part: the four faces described above, the others plain.</summary>
    public static string TextureEntry()
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(Diffuse));
        Face(te, 0, MappingType.Default, MaterialA, TransformA);
        Face(te, 1, MappingType.Planar, MaterialB, TransformB);
        Face(te, 2, MappingType.Planar, MaterialA2, TransformA2);
        Face(te, 3, MappingType.Default, MaterialM, FaceM);
        return Convert.ToBase64String(te.GetBytes());
    }

    public static List<List<KitPart>> Parts(string textureEntry = null)
    {
        List<List<KitPart>> sets = new List<List<KitPart>>();
        for (int s = 0; s < ObjectCount; s++)
        {
            List<KitPart> parts = new List<KitPart>();
            for (int i = 0; i < PartsPerObject; i++)
            {
                KitPart p = new KitPart("Maps " + s + "." + (i + 1), i + 1)
                {
                    Offset = i == 0 ? "0,0,0" : (i * 1.5 + 0.5).ToString("0.###", CultureInfo.InvariantCulture) + "," + ((i % 3) - 1) + "," + (0.25 * i).ToString("0.###", CultureInfo.InvariantCulture),
                    Scale = (0.8 + 0.3 * i).ToString("0.###", CultureInfo.InvariantCulture) + "," + (1.2 - 0.1 * i).ToString("0.###", CultureInfo.InvariantCulture) + "," + (0.6 + 0.2 * (i % 4)).ToString("0.###", CultureInfo.InvariantCulture),
                    Rotation = i % 2 == 0 ? "0,0,0,1" : "0,0,0.3826834,0.9238795",
                    TextureEntry = textureEntry ?? TextureEntry()
                };
                if (i % 3 == 2)
                    p.Shape = FaceMatrix.Shape("cylinder", "plain");
                parts.Add(p);
            }
            sets.Add(parts);
        }
        return sets;
    }

    public static string Write() => EligibilityKit.WriteArchive(Parts(), Assets(), PlanarFixture.FlatTerrain());
}
