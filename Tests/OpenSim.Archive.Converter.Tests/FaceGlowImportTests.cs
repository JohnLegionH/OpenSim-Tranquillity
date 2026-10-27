/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.Restore.Import;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A part's "faces" key in the manifest: glow and fullbright for a face named by its material (or by its number), written into the part's texture entry.
/// The slab folder has face 0 = material "top" and face 1 = material "side". Each test writes its own folder and archive, so the class runs in parallel.
/// </summary>
public class FaceGlowImportTests
{
    private const float Step = 1f / 255f;

    [Fact]
    public void TheManifestReadsGlowAndFullbrightForAFaceByMaterialOrByNumber()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"side\", \"glow\": 0.12, \"fullbright\": true }, { \"face\": 0, \"glow\": 0.5 } ]");
        LinksetPart part = LinksetManifest.Load(b.ManifestPath).Parts[0];
        Assert.Equal(2, part.Faces.Count);
        Assert.Equal("side", part.Faces[0].Material);
        Assert.Equal(0.12f, part.Faces[0].Glow.Value, 5);
        Assert.True(part.Faces[0].Fullbright.Value);
        Assert.Equal(0, part.Faces[1].Face);
        Assert.Null(part.Faces[1].Material);
        Assert.Null(part.Faces[1].Fullbright);
    }

    [Fact]
    public void ImportWritesGlowAndFullbrightOnTheNamedFaceAndLeavesTheOtherFaceAlone()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"side\", \"glow\": 0.12, \"fullbright\": true } ]");
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement part = MaterialKit.Parts(oar)[0];
        Primitive.TextureEntryFace side = MaterialKit.Face(part, 1), top = MaterialKit.Face(part, 0);
        Assert.InRange(side.Glow, 0.12f - Step, 0.12f + Step);
        Assert.True(side.Glow > 0f);
        Assert.True(side.Fullbright);
        Assert.Equal(0f, top.Glow);
        Assert.False(top.Fullbright);
    }

    [Fact]
    public void AFaceNumberNamesTheFaceTheSameWayAMaterialDoes()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"face\": 0, \"glow\": 0.5, \"fullbright\": true } ]");
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement part = MaterialKit.Parts(oar)[0];
        Assert.InRange(MaterialKit.Face(part, 0).Glow, 0.5f - Step, 0.5f + Step);
        Assert.True(MaterialKit.Face(part, 0).Fullbright);
        Assert.Equal(0f, MaterialKit.Face(part, 1).Glow);
        Assert.False(MaterialKit.Face(part, 1).Fullbright);
    }

    [Fact]
    public void GlowAloneLeavesFullbrightOffAndFullbrightAloneLeavesGlowZero()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"top\", \"glow\": 1 }, { \"material\": \"side\", \"fullbright\": true } ]");
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement part = MaterialKit.Parts(oar)[0];
        Assert.Equal(1f, MaterialKit.Face(part, 0).Glow, 3);
        Assert.False(MaterialKit.Face(part, 0).Fullbright);
        Assert.Equal(0f, MaterialKit.Face(part, 1).Glow);
        Assert.True(MaterialKit.Face(part, 1).Fullbright);
    }

    [Fact]
    public void EachPartTakesItsOwnFaceSettings()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"side\", \"glow\": 0.25 } ]", childKeys: "\"faces\": [ { \"material\": \"top\", \"fullbright\": true } ]");
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        List<XElement> parts = MaterialKit.Parts(oar);
        Assert.InRange(MaterialKit.Face(parts[0], 1).Glow, 0.25f - Step, 0.25f + Step);
        Assert.False(MaterialKit.Face(parts[0], 0).Fullbright);
        Assert.True(MaterialKit.Face(parts[1], 0).Fullbright);
        Assert.Equal(0f, MaterialKit.Face(parts[1], 1).Glow);
    }

    [Fact]
    public void APartWithNoFacesKeyHasNoGlowAndNoFullbrightOnAnyFace()
    {
        MaterialKit.Built b = MaterialKit.Write();
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement part = MaterialKit.Parts(oar)[0];
        for (int face = 0; face < 2; face++)
        {
            Assert.Equal(0f, MaterialKit.Face(part, face).Glow);
            Assert.False(MaterialKit.Face(part, face).Fullbright);
        }
    }

    [Fact]
    public void ASettingForAMaterialThePartDoesNotHaveIsRefusedByName()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"glass\", \"glow\": 0.1 } ]");
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.ImportLinkset(LinksetManifest.Load(b.ManifestPath), ImportKit.OutputPath(), MaterialKit.Options()));
        Assert.Contains("glass", e.Message);
        Assert.Contains("Seat", e.Message);
    }

    [Fact]
    public void ASettingForAFaceNumberThePartDoesNotHaveIsRefused()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"face\": 5, \"glow\": 0.1 } ]");
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.ImportLinkset(LinksetManifest.Load(b.ManifestPath), ImportKit.OutputPath(), MaterialKit.Options()));
        Assert.Contains("5", e.Message);
    }

    [Theory]
    [InlineData("{ \"material\": \"side\", \"glow\": 1.5 }", "glow")]
    [InlineData("{ \"material\": \"side\", \"glow\": -0.1 }", "glow")]
    [InlineData("{ \"material\": \"side\", \"fullbright\": \"yes\" }", "fullbright")]
    [InlineData("{ \"glow\": 0.1 }", "material")]
    [InlineData("{ \"material\": \"side\", \"face\": 1, \"glow\": 0.1 }", "face")]
    [InlineData("{ \"material\": \"side\", \"shine\": 1 }", "shine")]
    public void AFaceEntryThatIsOutOfRangeAmbiguousOrUnknownIsRefusedByName(string entry, string named)
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ " + entry + " ]");
        ImportException e = Assert.Throws<ImportException>(() => LinksetManifest.Load(b.ManifestPath));
        Assert.Contains(named, e.Message);
    }

    [Fact]
    public void TheSameFaceNamedTwiceIsRefused()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"faces\": [ { \"material\": \"side\", \"glow\": 0.1 }, { \"face\": 1, \"fullbright\": true } ]");
        LinksetManifest manifest = LinksetManifest.Load(b.ManifestPath);
        ImportException e = Assert.Throws<ImportException>(() => ObjectImporter.ImportLinkset(manifest, ImportKit.OutputPath(), MaterialKit.Options()));
        Assert.Contains("twice", e.Message);
    }
}
