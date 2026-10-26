/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Security;
using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Framework;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Archives whose faces show media. Two objects of eight parts (boxes and cylinders, turned and sized differently); in every part
///   face 0 shows the first media entry and has a texture transform;
///   face 1 shows an entry equal to the first (made separately) with another transform, so it shares a merged face with face 0;
///   face 2 is planar and shows the second media entry;
///   face 3 is an ordinary face with a transform, the others are plain.
/// The media list of the part has an entry (or an empty place) for each face, in the part's own face order.
/// </summary>
internal static class MediaFixture
{
    public static readonly Guid Diffuse = Guid.Parse("51111111-2222-4333-8444-555555555555");
    public const int ObjectCount = 2, PartsPerObject = 8;

    public static MediaEntry Entry(string url, int width) => new MediaEntry
    {
        CurrentURL = url, HomeURL = url, Width = width, Height = width / 2, AutoPlay = true, AutoLoop = true, Controls = MediaControls.Standard, InteractOnFirstClick = true
    };

    /// <summary>The Media element's text for a part: the entries by face number (null for a face with none).</summary>
    public static string MediaXml(params MediaEntry[] entries) => new PrimitiveBaseShape.MediaList(entries).ToXml();

    public static string ShapeXml(string mediaXml) => "<Media>" + SecurityElement.Escape(mediaXml) + "</Media>";

    public static Dictionary<string, byte[]> Assets() => new Dictionary<string, byte[]>
    {
        ["assets/" + Diffuse + "_texture.jp2"] = TextureKit.Solid(512, 512, withAlphaChannel: false)
    };

    public static string TextureEntry(params int[] mediaFaces)
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(Diffuse));
        void Face(uint i, MappingType mapping, float repeat, float offset, float rotation)
        {
            Primitive.TextureEntryFace f = te.CreateFace(i);
            f.TextureID = new UUID(Diffuse);
            f.TexMapType = mapping;
            f.RepeatU = repeat; f.RepeatV = repeat + 1; f.OffsetU = offset; f.OffsetV = -offset; f.Rotation = rotation;
            f.MediaFlags = mediaFaces.Contains((int)i);
        }
        Face(0, MappingType.Default, 3f, 0.25f, 0.5f);
        Face(1, MappingType.Default, 2f, 0.125f, 0.25f);
        Face(2, MappingType.Planar, 2f, 0.5f, 0.75f);
        Face(3, MappingType.Default, 2f, 0.25f, 0.3f);
        return Convert.ToBase64String(te.GetBytes());
    }

    public static KitPart Part(string name, int linkNum, string offset, string scale, string rotation, string textureEntry, string shapeXml) =>
        new KitPart(name, linkNum) { Offset = offset, Scale = scale, Rotation = rotation, TextureEntry = textureEntry, ShapeXml = shapeXml };

    public static List<List<KitPart>> Parts()
    {
        string media = ShapeXml(MediaXml(Entry("https://media.example.org/one", 512), Entry("https://media.example.org/one", 512), Entry("https://media.example.org/two", 256)));
        string te = TextureEntry(0, 1, 2);
        List<List<KitPart>> sets = new List<List<KitPart>>();
        for (int s = 0; s < ObjectCount; s++)
        {
            List<KitPart> parts = new List<KitPart>();
            for (int i = 0; i < PartsPerObject; i++)
            {
                KitPart p = Part("Media " + s + "." + (i + 1), i + 1,
                    i == 0 ? "0,0,0" : (i * 1.5 + 0.5).ToString("0.###", CultureInfo.InvariantCulture) + "," + ((i % 3) - 1) + "," + (0.25 * i).ToString("0.###", CultureInfo.InvariantCulture),
                    (0.8 + 0.3 * i).ToString("0.###", CultureInfo.InvariantCulture) + "," + (1.2 - 0.1 * i).ToString("0.###", CultureInfo.InvariantCulture) + "," + (0.6 + 0.2 * (i % 4)).ToString("0.###", CultureInfo.InvariantCulture),
                    i % 2 == 0 ? "0,0,0,1" : "0,0,0.3826834,0.9238795", te, media);
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
