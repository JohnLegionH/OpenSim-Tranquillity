/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Xml.Linq;
using OpenSim.Archive.Restore.Import;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A box that hands out clothing, written into its own folder for the wearable and object contents tests: the box is a mesh tool's output folder (the root
/// part); its contents are two rigged garments as objects (one with its model named by "base", one found in its folder), an unrigged slab with pictures as an
/// object, a system shirt (wearable type 4) with its picture for the upper shirt texture entry (1), and a system alpha (wearable type 13) with pictures for
/// the lower alpha (21) and the upper alpha (22) entries. The viewer's numbers are those of ETextureIndex in llavatarappearancedefines.h of the viewer source.
/// </summary>
internal static class ClothingBoxKit
{
    public const int UpperShirt = 1, LowerAlpha = 21, UpperAlpha = 22;
    public static readonly Guid ShirtCreator = Guid.Parse("6f1c0a2e-93d4-4b7a-8e15-2c9d7b40a3f1");
    public static readonly Guid ShirtOwner = Guid.Parse("b84e2d71-5c3a-4f09-a6d2-91e8c7f05b34");
    public static readonly Guid ShirtOldTexture = Guid.Parse("3d9a6c15-7e2b-4a80-b4f1-5c8e2d09a7b6");

    /// <summary>A shirt as a viewer saves it: its own creator and owner, two parameters and the picture of the upper shirt entry.</summary>
    public static readonly string ShirtText =
        "LLWearable version 22\nTest Shirt\nA shirt for the import tests\n\n\tpermissions 0\n\t{\n\t\tbase_mask\t7fffffff\n\t\towner_mask\t7fffffff\n\t\tgroup_mask\t00000000\n" +
        "\t\teveryone_mask\t00000000\n\t\tnext_owner_mask\t7fffffff\n\t\tcreator_id\t" + ShirtCreator.ToString("D") + "\n\t\towner_id\t" + ShirtOwner.ToString("D") +
        "\n\t\tlast_owner_id\t" + ShirtOwner.ToString("D") + "\n\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n\t}\n\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\n" +
        "type 4\nparameters 2\n781 .5\n800 .25\ntextures 1\n" + UpperShirt + " " + ShirtOldTexture.ToString("D") + "\n";

    /// <summary>An alpha with the creator and the owner left as @CREATOR@ and @OWNER@, no parameters and no pictures; written with carriage returns.</summary>
    public static readonly string AlphaText =
        "LLWearable version 22\nTest Alpha\n\n\tpermissions 0\n\t{\n\t\tbase_mask\t7fffffff\n\t\towner_mask\t7fffffff\n\t\tgroup_mask\t00000000\n" +
        "\t\teveryone_mask\t00000000\n\t\tnext_owner_mask\t7fffffff\n\t\tcreator_id\t@CREATOR@\n\t\towner_id\t@OWNER@\n\t\tlast_owner_id\t@OWNER@\n" +
        "\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n\t}\n\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\ntype 13\nparameters 0\ntextures 0\n";

    /// <summary>A shape (a body part, wearable type 0) with no pictures.</summary>
    public static readonly string ShapeText =
        "LLWearable version 22\nTest Shape\n\n\tpermissions 0\n\t{\n\t\tbase_mask\t7fffffff\n\t\towner_mask\t7fffffff\n\t\tgroup_mask\t00000000\n" +
        "\t\teveryone_mask\t00000000\n\t\tnext_owner_mask\t7fffffff\n\t\tcreator_id\t@CREATOR@\n\t\towner_id\t@OWNER@\n\t\tlast_owner_id\t@OWNER@\n" +
        "\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n\t}\n\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\ntype 0\nparameters 1\n33 .4\ntextures 0\n";

    public const string DefaultContents = @"
      { ""type"": ""object"", ""folder"": ""shirt-mesh"", ""base"": ""shirt"", ""name"": ""Test Shirt Mesh"", ""description"": ""a rigged shirt"", ""jointPositions"": false },
      { ""type"": ""object"", ""folder"": ""skirt-mesh"", ""name"": ""Test Skirt Mesh"" },
      { ""type"": ""object"", ""folder"": ""slab"", ""name"": ""Test Slab"" },
      { ""type"": ""wearable"", ""file"": ""shirt.wearable.txt"", ""name"": ""Test Shirt"", ""description"": ""a system shirt"", ""textures"": { ""1"": ""shirt.png"" } },
      { ""type"": ""wearable"", ""file"": ""alpha.wearable.txt"", ""name"": ""Test Alpha"", ""textures"": { ""22"": ""upper-alpha.png"", ""21"": ""lower-alpha.png"" } }";

    public sealed class Built
    {
        public string Folder, ManifestPath;
        public string ShirtMesh, SkirtMesh, Slab;
        public string Shirt, Alpha, Shape, ShirtPicture, UpperAlphaPicture, LowerAlphaPicture;
        public byte[] ShirtPng, UpperAlphaPng, LowerAlphaPng;
    }

    public static Built Write(string contents = DefaultContents)
    {
        string root = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ImportKit.Write(into: Path.Combine(root, "box"), name: "box");
        Built b = new Built
        {
            Folder = root,
            ManifestPath = Path.Combine(root, "linkset.json"),
            ShirtMesh = RigKit.Write(name: "shirt", into: Path.Combine(root, "shirt-mesh")),
            SkirtMesh = RigKit.Write(new RigKit.Options { Fixed = new[] { 0.7f, 0.3f } }, into: Path.Combine(root, "skirt-mesh")),
            Slab = ImportKit.Write(into: Path.Combine(root, "slab"), name: "slab").Path,
            Shirt = Path.Combine(root, "shirt.wearable.txt"),
            Alpha = Path.Combine(root, "alpha.wearable.txt"),
            Shape = Path.Combine(root, "shape.wearable.txt"),
            ShirtPicture = Path.Combine(root, "shirt.png"),
            UpperAlphaPicture = Path.Combine(root, "upper-alpha.png"),
            LowerAlphaPicture = Path.Combine(root, "lower-alpha.png"),
            ShirtPng = ImportKit.Png(200, 30, 60, 16),
            UpperAlphaPng = ImportKit.Png(10, 250, 120, 8),
            LowerAlphaPng = ImportKit.Png(90, 90, 240, 8)
        };
        UTF8Encoding utf8 = new UTF8Encoding(false);
        File.WriteAllBytes(b.Shirt, utf8.GetBytes(ShirtText));
        File.WriteAllBytes(b.Alpha, utf8.GetBytes(AlphaText.Replace("\n", "\r\n")));
        File.WriteAllBytes(b.Shape, utf8.GetBytes(ShapeText));
        File.WriteAllBytes(b.ShirtPicture, b.ShirtPng);
        File.WriteAllBytes(b.UpperAlphaPicture, b.UpperAlphaPng);
        File.WriteAllBytes(b.LowerAlphaPicture, b.LowerAlphaPng);
        File.WriteAllText(b.ManifestPath, @"{
  ""name"": ""Test Clothing Box"", ""description"": ""garments for the import tests"",
  ""parts"": [ { ""name"": ""Box"", ""description"": ""holds the garments"", ""folder"": ""box"", ""contents"": [" + contents + @" ] } ]
}");
        return b;
    }

    public static ImportOptions Options() => new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(40f, 50f, 60f), Created = 1767225600 };

    /// <summary>Imports the box; a refusal fails the test with the importer's message.</summary>
    public static Dictionary<string, byte[]> Import(Built b, out string archive)
    {
        archive = ImportKit.OutputPath();
        try { ObjectImporter.ImportLinkset(LinksetManifest.Load(b.ManifestPath), archive, Options()); }
        catch (ImportException e) { Assert.Fail("the importer refused the box: " + e.Message); }
        return ImportKit.ReadOar(archive);
    }

    public static Dictionary<string, byte[]> Import(Built b) => Import(b, out _);

    /// <summary>The message of the refusal of a manifest, or null when it was read.</summary>
    public static string Refusal(Built b)
    {
        try
        {
            ObjectImporter.ImportLinkset(LinksetManifest.Load(b.ManifestPath), ImportKit.OutputPath(), Options());
            return null;
        }
        catch (ImportException e) { return e.Message; }
    }

    /// <summary>The item of the box with this name; the item is asserted to be there first.</summary>
    public static XElement Item(Dictionary<string, byte[]> oar, string name)
    {
        List<XElement> items = LinksetKit.Items(LinksetKit.Parts(oar)[0]);
        Assert.True(items.Any(i => i.Element("Name")?.Value == name), "the box holds no item '" + name + "'; it holds " + string.Join(", ", items.Select(i => i.Element("Name")?.Value)));
        return items.Single(i => i.Element("Name").Value == name);
    }

    public static string AssetId(XElement item) => item.Element("AssetID").Element("UUID").Value;

    /// <summary>The asset of an item, by the asset id and the file name ending its asset type has in an archive; asserted to be there first.</summary>
    public static byte[] Asset(Dictionary<string, byte[]> oar, XElement item, string ending)
    {
        string key = "assets/" + AssetId(item) + ending;
        Assert.True(oar.ContainsKey(key), "the archive has no " + key + "; its assets: " + string.Join(", ", oar.Keys.Where(k => k.StartsWith("assets/", StringComparison.Ordinal))));
        return oar[key];
    }

    /// <summary>The lines of a wearable's textures block as texture entry number and asset id.</summary>
    public static Dictionary<int, string> TextureLines(string wearable)
    {
        string[] lines = wearable.Split('\n');
        int at = Array.FindIndex(lines, l => l.StartsWith("textures ", StringComparison.Ordinal));
        Assert.True(at >= 0, "the wearable has no textures line:\n" + wearable);
        int count = int.Parse(lines[at].Substring("textures ".Length));
        Dictionary<int, string> result = new Dictionary<int, string>();
        for (int i = 1; i <= count; i++)
        {
            string[] f = lines[at + i].Split(' ');
            result[int.Parse(f[0])] = f[1];
        }
        return result;
    }

    /// <summary>The pixels of a picture as RGBA bytes; a JPEG 2000 codestream when <paramref name="jpeg2000"/>.</summary>
    public static byte[] Pixels(byte[] encoded, bool jpeg2000)
    {
        SkiaSharp.SKBitmap bitmap;
        bool ok = jpeg2000 ? OpenSim.Framework.SkiaImageUtils.TryDecodeFromJ2K(encoded, out bitmap) : OpenSim.Framework.SkiaImageUtils.TryDecodeFromBytes(encoded, out bitmap);
        Assert.True(ok && bitmap != null, "a picture cannot be decoded");
        using (bitmap)
        using (SkiaSharp.SKBitmap rgba = bitmap.Copy(SkiaSharp.SKColorType.Rgba8888))
            return rgba.Bytes;
    }
}
