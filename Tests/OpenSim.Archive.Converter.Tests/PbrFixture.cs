/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Framework;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Archives whose faces carry PBR (glTF) materials. Two objects of eight parts (boxes and cylinders, turned and sized differently); in every part
///   face 0 shows material P1 with no override and has a texture transform of its own (which the viewer does not apply to a PBR face);
///   face 1 shows material P1 with override O1 (a different face from face 0 for that reason);
///   face 2 is planar and shows material P2 with the same override O1;
///   face 3 is an ordinary face with a transform, the others are plain.
/// </summary>
internal static class PbrFixture
{
    public static readonly Guid Diffuse = Guid.Parse("41111111-2222-4333-8444-555555555555");
    public static readonly Guid P1 = Guid.Parse("42111111-2222-4333-8444-555555555555");
    public static readonly Guid P2 = Guid.Parse("43111111-2222-4333-8444-555555555555");
    public static readonly Guid P3Blend = Guid.Parse("44111111-2222-4333-8444-555555555555");
    public const string O1 = "{\"bc\":[0.25,0.5,1,1],\"mf\":0.5}";
    public const int ObjectCount = 2, PartsPerObject = 8;

    public static string MaterialJson(string alphaMode) =>
        "{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"alphaMode\":\"" + alphaMode + "\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,1,1,1]}}]}";

    public static Dictionary<string, byte[]> Assets() => new Dictionary<string, byte[]>
    {
        ["assets/" + Diffuse + "_texture.jp2"] = TextureKit.Solid(512, 512, withAlphaChannel: false),
        ["assets/" + P1 + "_prmat.dat"] = Encoding.UTF8.GetBytes(MaterialJson("OPAQUE")),
        ["assets/" + P2 + "_prmat.dat"] = Encoding.UTF8.GetBytes(MaterialJson("OPAQUE")),
        ["assets/" + P3Blend + "_prmat.dat"] = Encoding.UTF8.GetBytes(MaterialJson("BLEND"))
    };

    /// <summary>The base64 of a part's ExtraParams (the PBR assignment block) and of its material overrides, for the given faces.</summary>
    public static (string ExtraParams, string Overrides) Blocks((int Face, Guid Material)[] materials, (int Face, string Data)[] overrides)
    {
        PrimitiveBaseShape shape = new PrimitiveBaseShape
        {
            RenderMaterials = new Primitive.RenderMaterials
            {
                entries = materials.Select(m => new Primitive.RenderMaterials.RenderMaterialEntry { te_index = (byte)m.Face, id = new UUID(m.Material) }).ToArray(),
                overrides = overrides.Select(o => new Primitive.RenderMaterials.RenderMaterialOverrideEntry { te_index = (byte)o.Face, data = o.Data }).ToArray()
            }
        };
        return (Convert.ToBase64String(shape.ExtraParamsToBytes()), Convert.ToBase64String(shape.RenderMaterialsOvrToRawBin()));
    }

    public static string TextureEntry()
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(Diffuse));
        void Face(uint i, MappingType mapping, float repeat, float offset, float rotation)
        {
            Primitive.TextureEntryFace f = te.CreateFace(i);
            f.TextureID = new UUID(Diffuse);
            f.TexMapType = mapping;
            f.RepeatU = repeat; f.RepeatV = repeat + 1; f.OffsetU = offset; f.OffsetV = -offset; f.Rotation = rotation;
        }
        Face(0, MappingType.Default, 3f, 0.25f, 0.5f);
        Face(1, MappingType.Default, 2f, 0.125f, 0.25f);
        Face(2, MappingType.Planar, 2f, 0.5f, 0.75f);
        Face(3, MappingType.Default, 2f, 0.25f, 0.3f);
        return Convert.ToBase64String(te.GetBytes());
    }

    public static KitPart Part(string name, int linkNum, string offset, string scale, string rotation, string extra, string overrides, string textureEntry = null)
    {
        KitPart p = new KitPart(name, linkNum) { Offset = offset, Scale = scale, Rotation = rotation, TextureEntry = textureEntry ?? TextureEntry(), ExtraParams = extra };
        if (overrides != null)
            p.ShapeXml = "<MatOvrd>" + overrides + "</MatOvrd>";
        return p;
    }

    public static List<List<KitPart>> Parts()
    {
        (string extra, string overrides) = Blocks(new[] { (0, P1), (1, P1), (2, P2) }, new[] { (1, O1), (2, O1) });
        List<List<KitPart>> sets = new List<List<KitPart>>();
        for (int s = 0; s < ObjectCount; s++)
        {
            List<KitPart> parts = new List<KitPart>();
            for (int i = 0; i < PartsPerObject; i++)
            {
                KitPart p = Part("Pbr " + s + "." + (i + 1), i + 1,
                    i == 0 ? "0,0,0" : (i * 1.5 + 0.5).ToString("0.###", CultureInfo.InvariantCulture) + "," + ((i % 3) - 1) + "," + (0.25 * i).ToString("0.###", CultureInfo.InvariantCulture),
                    (0.8 + 0.3 * i).ToString("0.###", CultureInfo.InvariantCulture) + "," + (1.2 - 0.1 * i).ToString("0.###", CultureInfo.InvariantCulture) + "," + (0.6 + 0.2 * (i % 4)).ToString("0.###", CultureInfo.InvariantCulture),
                    i % 2 == 0 ? "0,0,0,1" : "0,0,0.3826834,0.9238795", extra, overrides);
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
