/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Xml.Linq;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Restore.Import;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The "specular" entry of a face in the manifest: a legacy specular map (colour in red, green and blue, glossiness in alpha) next to the normal map in the
/// face's legacy material, with the specular colour, the glossiness and the environment intensity. The slab folder has face 0 = "top" (no normal map)
/// and face 1 = "side" (a normal map when the test asks for one). Each test writes its own folder and archive, so the class runs in parallel.
/// </summary>
public class SpecularImportTests
{
    private static readonly byte[] SpecPng = ImportKit.Png(77, 66, 55, 16);
    private const string SideSpec = "\"faces\": [ { \"material\": \"side\", \"specular\": { \"map\": \"spec.png\", \"color\": [1, 0.5, 0.25], \"glossiness\": 120, \"environment\": 40 } } ]";

    private static Dictionary<string, byte[]> Spec() => new Dictionary<string, byte[]> { ["spec.png"] = SpecPng };

    private static OSDMap Material(Dictionary<string, byte[]> oar, XElement part, int face)
    {
        Primitive.TextureEntryFace f = MaterialKit.Face(part, face);
        Assert.NotEqual(UUID.Zero, f.MaterialID);
        OSD osd = OSDParser.Deserialize(MaterialKit.Asset(oar, f.MaterialID.Guid, "_material.xml"));
        return Assert.IsType<OSDMap>(osd);
    }

    [Fact]
    public void TheManifestReadsTheSpecularMapColourGlossinessAndEnvironment()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: SideSpec, files: Spec());
        LinksetFace face = Assert.Single(LinksetManifest.Load(b.ManifestPath).Parts[0].Faces);
        Assert.NotNull(face.Specular);
        Assert.Equal(Path.GetFullPath(Path.Combine(b.Folder, "spec.png")), Path.GetFullPath(face.Specular.File));
        Assert.Equal(new[] { 1f, 0.5f, 0.25f }, face.Specular.Color);
        Assert.Equal(120, face.Specular.Glossiness);
        Assert.Equal(40, face.Specular.Environment);
    }

    [Fact]
    public void TheLegacyMaterialOfAFaceWithANormalMapAlsoNamesTheSpecularMapAndItsSettings()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: SideSpec, files: Spec(), configure: (top, side) => side.Normal = ImportKit.Png(128, 128, 255, 16));
        (ImportResult result, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement part = MaterialKit.Parts(oar)[0];
        OSDMap material = Material(oar, part, 1);
        UUID normal = material["NormMap"].AsUUID(), spec = material["SpecMap"].AsUUID();
        Assert.NotEqual(UUID.Zero, normal);
        Assert.NotEqual(UUID.Zero, spec);
        Assert.NotEqual(normal, spec);
        OSDArray color = Assert.IsType<OSDArray>(material["SpecColor"]);
        Assert.Equal(new[] { 255, 128, 64 }, new[] { color[0].AsInteger(), color[1].AsInteger(), color[2].AsInteger() });
        Assert.Equal(120, material["SpecExp"].AsInteger());
        Assert.Equal(40, material["EnvIntensity"].AsInteger());
        Assert.Contains(result.Textures, t => t.Id == spec.Guid && t.Role == "specular map");
    }

    [Fact]
    public void TheSpecularMapIsABareJpeg2000CodestreamThatDecodesToThePicture()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: SideSpec, files: Spec());
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        UUID spec = Material(oar, MaterialKit.Parts(oar)[0], 1)["SpecMap"].AsUUID();
        Assert.NotEqual(UUID.Zero, spec);
        byte[] asset = MaterialKit.Asset(oar, spec.Guid, "_texture.jp2");
        Assert.True(asset.AsSpan(0, 4).SequenceEqual(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }), "not a bare codestream");
        Assert.Equal(MaterialKit.PngPixels(SpecPng), MaterialKit.Pixels(asset));
    }

    [Fact]
    public void AFaceWithASpecularMapAndNoNormalMapGetsALegacyMaterialWithNoNormalMap()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"top\", \"specular\": { \"map\": \"spec.png\" } } ]", files: Spec());
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        OSDMap material = Material(oar, MaterialKit.Parts(oar)[0], 0);
        Assert.Equal(UUID.Zero, material["NormMap"].AsUUID());
        Assert.NotEqual(UUID.Zero, material["SpecMap"].AsUUID());
    }

    [Fact]
    public void ASpecularEntryWithOnlyAMapTakesTheDefaultColourGlossinessAndEnvironment()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"top\", \"specular\": { \"map\": \"spec.png\" } } ]", files: Spec());
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        OSDMap material = Material(oar, MaterialKit.Parts(oar)[0], 0);
        OSDArray color = Assert.IsType<OSDArray>(material["SpecColor"]);
        Assert.Equal(new[] { 255, 255, 255, 255 }, new[] { color[0].AsInteger(), color[1].AsInteger(), color[2].AsInteger(), color[3].AsInteger() });
        Assert.Equal(51, material["SpecExp"].AsInteger());
        Assert.Equal(0, material["EnvIntensity"].AsInteger());
    }

    [Fact]
    public void TheSameSpecularPictureOnTwoFacesIsOneTextureAsset()
    {
        string both = "\"faces\": [ { \"material\": \"top\", \"specular\": { \"map\": \"spec.png\" } }, { \"material\": \"side\", \"specular\": { \"map\": \"spec.png\" } } ]";
        MaterialKit.Built b = MaterialKit.Write(rootKeys: both, files: Spec());
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement part = MaterialKit.Parts(oar)[0];
        Assert.NotEqual(UUID.Zero, Material(oar, part, 0)["SpecMap"].AsUUID());
        Assert.Equal(Material(oar, part, 0)["SpecMap"].AsUUID(), Material(oar, part, 1)["SpecMap"].AsUUID());
    }

    [Fact]
    public void APartWithNoSpecularEntryKeepsTheLegacyMaterialItHadWithNoSpecularMap()
    {
        MaterialKit.Built b = MaterialKit.Write(configure: (top, side) => side.Normal = ImportKit.Png(128, 128, 255, 16));
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        OSDMap material = Material(oar, MaterialKit.Parts(oar)[0], 1);
        Assert.NotEqual(UUID.Zero, material["NormMap"].AsUUID());
        Assert.Equal(UUID.Zero, material["SpecMap"].AsUUID());
        Assert.Equal(UUID.Zero, MaterialKit.Face(MaterialKit.Parts(oar)[0], 0).MaterialID);
    }

    [Fact]
    public void AMissingSpecularFileIsRefusedByName()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: SideSpec);
        ImportException e = Assert.Throws<ImportException>(() => LinksetManifest.Load(b.ManifestPath));
        Assert.Contains("spec.png", e.Message);
    }

    [Fact]
    public void ASpecularFileThatIsNotAPictureIsRefusedByName()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: SideSpec, files: new Dictionary<string, byte[]> { ["spec.png"] = Encoding.ASCII.GetBytes("not a picture") });
        LinksetManifest manifest = LinksetManifest.Load(b.ManifestPath);
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.ImportLinkset(manifest, ImportKit.OutputPath(), MaterialKit.Options()));
        Assert.Contains("spec.png", e.Message);
    }

    [Theory]
    [InlineData("{ \"color\": [1, 1, 1] }", "map")]
    [InlineData("{ \"map\": \"spec.png\", \"color\": [1, 1] }", "color")]
    [InlineData("{ \"map\": \"spec.png\", \"color\": [1, 2, 1] }", "color")]
    [InlineData("{ \"map\": \"spec.png\", \"glossiness\": 256 }", "glossiness")]
    [InlineData("{ \"map\": \"spec.png\", \"glossiness\": 1.5 }", "glossiness")]
    [InlineData("{ \"map\": \"spec.png\", \"environment\": -1 }", "environment")]
    [InlineData("{ \"map\": \"spec.png\", \"shine\": 3 }", "shine")]
    public void ASpecularEntryThatIsIncompleteOutOfRangeOrUnknownIsRefusedByName(string entry, string named)
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"side\", \"specular\": " + entry + " } ]", files: Spec());
        ImportException e = Assert.Throws<ImportException>(() => LinksetManifest.Load(b.ManifestPath));
        Assert.Contains(named, e.Message);
    }
}
