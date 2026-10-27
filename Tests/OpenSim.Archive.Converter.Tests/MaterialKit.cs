/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.Restore.Import;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A linked object of one or two parts, each from a slab folder (ImportKit: face 0 is the material "top", face 1 the material "side"), with the manifest
/// text of each part's extra keys given by the test. Each call writes its own folder, so the classes that use it run in parallel.
/// </summary>
internal static class MaterialKit
{
    public sealed class Built
    {
        public string Folder, ManifestPath;
        public ImportKit.Folder Shell;
    }

    /// <param name="rootKeys">extra manifest keys of the first part, such as <c>"sit": {...}</c> (no leading comma)</param>
    /// <param name="childKeys">extra keys of a second part named Frame; null writes no second part</param>
    /// <param name="topKeys">extra keys of the manifest itself, such as <c>"pbr": true</c></param>
    /// <param name="files">extra files written next to the manifest, by relative name</param>
    public static Built Write(string rootKeys = null, string childKeys = null, string topKeys = null, Action<ImportMaterial, ImportMaterial> configure = null, Dictionary<string, byte[]> files = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ImportKit.Folder shell = ImportKit.Write(normalMap: false, into: Path.Combine(root, "shell"), name: "shell", configure: configure);
        if (childKeys != null)
            ImportKit.Write(normalMap: false, into: Path.Combine(root, "frame"), name: "frame", configure: configure);
        if (files != null)
            foreach (KeyValuePair<string, byte[]> f in files)
                File.WriteAllBytes(Path.Combine(root, f.Key), f.Value);
        string parts = "{ \"name\": \"Seat\", \"folder\": \"shell\"" + (rootKeys == null ? "" : ", " + rootKeys) + " }";
        if (childKeys != null)
            parts += ", { \"name\": \"Frame\", \"folder\": \"frame\", \"position\": [0, 1, 0]" + (childKeys.Length == 0 ? "" : ", " + childKeys) + " }";
        Built b = new Built { Folder = root, ManifestPath = Path.Combine(root, "linkset.json"), Shell = shell };
        File.WriteAllText(b.ManifestPath, "{ \"name\": \"Bench Test\", " + (topKeys == null ? "" : topKeys + ", ") + "\"parts\": [ " + parts + " ] }");
        return b;
    }

    public static ImportOptions Options(bool pbr = false) => new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600, Pbr = pbr };

    public static (ImportResult Result, Dictionary<string, byte[]> Oar) Import(Built built, bool pbr = false)
    {
        string output = ImportKit.OutputPath();
        ImportResult result = ObjectImporter.ImportLinkset(LinksetManifest.Load(built.ManifestPath), output, Options(pbr));
        Assert.True(File.Exists(output), "the archive was not written");
        return (result, ImportKit.ReadOar(output));
    }

    public static List<XElement> Parts(Dictionary<string, byte[]> oar) => LinksetKit.Parts(oar);

    public static Primitive.TextureEntry TextureEntry(XElement part)
    {
        byte[] bytes = Convert.FromBase64String(ImportKit.Text(part, "Shape/TextureEntry"));
        return new Primitive.TextureEntry(bytes, 0, bytes.Length);
    }

    public static Primitive.TextureEntryFace Face(XElement part, int face)
    {
        Primitive.TextureEntry te = TextureEntry(part);
        return (face < te.FaceTextures.Length ? te.FaceTextures[face] : null) ?? te.DefaultTexture;
    }

    public static float F(XElement part, string path) => float.Parse(ImportKit.Text(part, path), CultureInfo.InvariantCulture);

    /// <summary>The PBR material assigned to each face number by the part's ExtraParams.</summary>
    public static Dictionary<int, Guid> PbrFaces(XElement part)
    {
        PrimitiveBaseShape shape = new PrimitiveBaseShape();
        shape.ReadInExtraParamsBytes(Convert.FromBase64String(ImportKit.Text(part, "Shape/ExtraParams")));
        Dictionary<int, Guid> result = new Dictionary<int, Guid>();
        if (shape.RenderMaterials?.entries != null)
            foreach (var e in shape.RenderMaterials.entries)
                result[e.te_index] = e.id.Guid;
        return result;
    }

    public static byte[] Pixels(byte[] encoded)
    {
        Assert.True(SkiaImageUtils.TryDecodeFromJ2K(encoded, out SKBitmap bitmap) && bitmap != null, "a texture asset does not decode as JPEG 2000");
        using (bitmap)
        using (SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888))
            return rgba.Bytes;
    }

    public static byte[] PngPixels(byte[] png)
    {
        Assert.True(SkiaImageUtils.TryDecodeFromBytes(png, out SKBitmap bitmap) && bitmap != null);
        using (bitmap)
        using (SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888))
            return rgba.Bytes;
    }

    public static byte[] Asset(Dictionary<string, byte[]> oar, Guid id, string suffix)
    {
        Assert.True(oar.TryGetValue("assets/" + id.ToString("D") + suffix, out byte[] data), "the archive has no assets/" + id.ToString("D") + suffix);
        return data;
    }
}
