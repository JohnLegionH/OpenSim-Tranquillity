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
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// An archive of two objects of eight parts each (boxes and cylinders, rotated and sized differently) whose faces use planar texture
/// mapping with their own repeats, offsets and rotations, and whose fourth face of every part is default-mapped, so a part mixes both.
/// A region can load it: the terrain is a full flat 256 x 256 block. The same figures give the same archive.
/// </summary>
internal static class PlanarFixture
{
    public static readonly Guid Texture = Guid.Parse("11111111-2222-4333-8444-555555555555");
    public const int ObjectCount = 2, PartsPerObject = 8, TexturedFaces = 4, DefaultMappedFace = 3;

    /// <summary>A 256 x 256 block of 32-bit floats, all at one height.</summary>
    public static byte[] FlatTerrain(float height = 25f)
    {
        byte[] data = new byte[256 * 256 * 4];
        byte[] one = BitConverter.GetBytes(height);
        for (int i = 0; i < data.Length; i += 4)
            Buffer.BlockCopy(one, 0, data, i, 4);
        return data;
    }

    public static List<List<KitPart>> Parts()
    {
        List<List<KitPart>> sets = new List<List<KitPart>>();
        for (int s = 0; s < ObjectCount; s++)
        {
            List<KitPart> parts = new List<KitPart>();
            for (int i = 0; i < PartsPerObject; i++)
            {
                KitPart p = new KitPart("Planar " + s + "." + (i + 1), i + 1)
                {
                    Offset = i == 0 ? "0,0,0" : (i * 1.5 + 0.5).ToString("0.###", CultureInfo.InvariantCulture) + "," + ((i % 3) - 1) + "," + (0.25 * i).ToString("0.###", CultureInfo.InvariantCulture),
                    Scale = (0.8 + 0.3 * i).ToString("0.###", CultureInfo.InvariantCulture) + "," + (1.2 - 0.1 * i).ToString("0.###", CultureInfo.InvariantCulture) + "," + (0.6 + 0.2 * (i % 4)).ToString("0.###", CultureInfo.InvariantCulture),
                    Rotation = i % 2 == 0 ? "0,0,0,1" : "0,0,0.3826834,0.9238795"
                };
                if (i % 3 == 2)
                    p.Shape = FaceMatrix.Shape("cylinder", "plain");
                Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(Texture));
                te.DefaultTexture.TexMapType = MappingType.Planar;
                te.DefaultTexture.RepeatU = 1f + i * 0.5f;
                te.DefaultTexture.RepeatV = 2f - i * 0.125f;
                te.DefaultTexture.OffsetU = 0.05f * i - 0.1f;
                te.DefaultTexture.OffsetV = 0.2f - 0.03f * i;
                te.DefaultTexture.Rotation = 0.15f * i;
                for (uint f = 0; f < TexturedFaces; f++)
                {
                    Primitive.TextureEntryFace face = te.CreateFace(f);
                    face.TextureID = new UUID(Texture);
                    face.TexMapType = f == DefaultMappedFace ? MappingType.Default : MappingType.Planar;
                    face.RepeatU = 0.5f + f * 1.5f + i * 0.25f;
                    face.RepeatV = 3f - f * 0.5f;
                    face.OffsetU = -0.3f + 0.1f * f;
                    face.OffsetV = 0.4f - 0.12f * f + i * 0.01f;
                    face.Rotation = (f * 0.4f) - 0.5f + (s == 1 ? 0.7f : 0f);
                }
                // the library's texture entry writer leaves a face's own default mapping out when the default face is planar, so the face is added to the bytes
                p.TextureEntry = Convert.ToBase64String(WithFaceMapping(te.GetBytes(), DefaultMappedFace, MappingType.Default));
                parts.Add(p);
            }
            sets.Add(parts);
        }
        return sets;
    }

    /// <summary>
    /// Sets the mapping type of one face (below 7) in the media section of a texture entry. The sections run texture (16 bytes), colour (4), repeat u (4),
    /// repeat v (4), offset u (2), offset v (2), rotation (2), bump and shiny (1), media and mapping (1); each holds the default value, then
    /// a face mask and value for each face that differs, then a zero mask. The mapping type is bits 1 and 2 of the media byte.
    /// </summary>
    internal static byte[] WithFaceMapping(byte[] te, int face, MappingType type)
    {
        int pos = 0;
        foreach (int size in new[] { 16, 4, 4, 4, 2, 2, 2, 1 })
        {
            pos += size;
            while (te[pos] != 0)
                pos += 1 + size;
            pos++;
        }
        byte defaultMedia = te[pos];
        int end = pos + 1;
        while (te[end] != 0)
            end += 2;
        List<byte> result = new List<byte>(te.Take(end));
        result.Add((byte)(1 << face));
        result.Add((byte)(((int)type & 0x06) | (defaultMedia & ~0x06)));
        result.AddRange(te.Skip(end));
        return result.ToArray();
    }

    /// <summary>Writes the archive to a new file and returns its path. The texture is a 1024 pixel square, so the plan reads its size.</summary>
    public static string Write() => EligibilityKit.WriteArchive(Parts(), new Dictionary<string, byte[]> { ["assets/" + Texture + "_texture.jp2"] = TextureKit.Solid(1024, 1024, withAlphaChannel: false) }, FlatTerrain());

    /// <summary>
    /// What an archive's parts say about the mapping of their first faces: one line for each part and face with the mapping type, then the
    /// raw texture entry of the part in hex. For comparing a generated archive with the one a region saved from it.
    /// </summary>
    public static string Census(string archivePath, int faces = TexturedFaces)
    {
        ArchiveMergePlanner.Plan(archivePath, new MergeOptions(), null, out Dictionary<Guid, ParsedPart> parsed);
        StringBuilder sb = new StringBuilder();
        foreach (ParsedPart pp in parsed.Values.OrderBy(x => x.Part.Name, StringComparer.Ordinal))
        {
            Primitive.TextureEntry te = ArchiveMergePlanner.ReadTextureEntry(pp);
            sb.Append(pp.Part.Name).Append(" default=").Append(te?.DefaultTexture?.TexMapType.ToString() ?? "-");
            for (int f = 0; f < faces; f++)
            {
                Primitive.TextureEntryFace tef = te == null ? null : (te.FaceTextures != null && f < te.FaceTextures.Length ? te.FaceTextures[f] : null) ?? te.DefaultTexture;
                sb.Append(" f").Append(f).Append('=').Append(tef?.TexMapType.ToString() ?? "-");
            }
            sb.AppendLine();
            sb.Append("  te ").AppendLine(Convert.ToHexString(pp.TextureEntry));
        }
        return sb.ToString();
    }
}
