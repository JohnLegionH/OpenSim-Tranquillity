/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.Compare;
using OpenSim.Archive.Rewrite;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Region.Framework.Scenes;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>The element edits that turn a part into a mesh part with N faces (MeshShape).</summary>
public class MeshShapeTests
{
    private static readonly Guid MeshAsset = Guid.Parse("5c21a000-0000-4000-8000-0000000000b7");

    /// <summary>ExtraParams holding a light and a projection block, made by the server's own encoder.</summary>
    private static byte[] LightAndProjection()
    {
        PrimitiveBaseShape s = new PrimitiveBaseShape
        {
            LightEntry = true, LightColorR = 1f, LightColorG = 0.5f, LightColorB = 0.25f, LightIntensity = 0.8f, LightRadius = 7f, LightCutoff = 0.5f, LightFalloff = 1.5f,
            ProjectionEntry = true, ProjectionTextureUUID = UUID.Random(), ProjectionFOV = 1.2f, ProjectionFocus = 2f, ProjectionAmbiance = 0.1f
        };
        return s.ExtraParamsToBytes();
    }

    private static KitPart SourcePart(byte[] extra)
    {
        // a hollow, twisted box with a size that is not a unit box, carrying a light and a projection
        return new KitPart("Lamp")
        {
            Scale = "1.5,2.5,3.5",
            Light = extra != null,
            Shape = FaceMatrix.Shape("box", "hollow+twist"),
            ExtraParams = extra == null ? "" : Convert.ToBase64String(extra)
        };
    }

    private static byte[] ReadEntry(string archive, string wanted)
    {
        using FileStream fs = File.OpenRead(archive);
        using GZipStream gz = new GZipStream(fs, CompressionMode.Decompress);
        TarArchiveReader tar = new TarArchiveReader(gz);
        byte[] data;
        while ((data = tar.ReadEntry(out string name, out TarArchiveReader.TarEntryType type)) != null)
            if (type == TarArchiveReader.TarEntryType.TYPE_NORMAL_FILE && name.StartsWith(wanted, StringComparison.Ordinal))
                return data;
        throw new InvalidOperationException("no entry " + wanted);
    }

    /// <summary>The part, read back the way the region server reads it.</summary>
    private static SceneObjectPart Load(string archive, Guid partId)
    {
        byte[] data = ReadEntry(archive, "objects/");
        XDocument doc = XDocument.Parse(Encoding.UTF8.GetString(data));
        XElement part = doc.Descendants("SceneObjectPart").Single(p => (string)p.Element("UUID")?.Element("UUID") == partId.ToString());
        using XmlReader r = XmlReader.Create(new StringReader(part.ToString(SaveOptions.DisableFormatting)));
        r.MoveToContent();
        return SceneObjectPart.FromXml(r);
    }

    private static string Rewrite(string source, KitPart part, int faces)
    {
        ArchiveRewriter w = new ArchiveRewriter();
        w.AddPatch(new PartPatch(part.Id, MeshShape.Edits(faces, MeshAsset, string.IsNullOrEmpty(part.ExtraParams) ? null : Convert.FromBase64String(part.ExtraParams)).ToArray()));
        string dest = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + ".oar");
        w.Rewrite(source, dest);
        return dest;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void APatchedPartReloadsAsAMeshWithNFacesAndTheSameSize(int faces)
    {
        KitPart part = SourcePart(LightAndProjection());
        string source = EligibilityKit.WriteArchive(part);
        SceneObjectPart before = Load(source, part.Id);

        string output = Rewrite(source, part, faces);
        SceneObjectPart after = Load(output, part.Id);

        Assert.Equal(faces, after.GetNumberOfSides());
        Assert.Equal(before.Scale, after.Scale);
        Assert.True(after.Shape.SculptEntry);
        Assert.Equal((byte)SculptType.Mesh, after.Shape.SculptType);
        Assert.Equal(new UUID(MeshAsset), after.Shape.SculptTexture);
        // the texture entry and the other blocks of the extra parameters are as they were
        Assert.Equal(before.Shape.TextureEntry, after.Shape.TextureEntry);
        Assert.True(after.Shape.LightEntry);
        Assert.Equal(before.Shape.LightRadius, after.Shape.LightRadius);
        Assert.Equal(before.Shape.LightColorG, after.Shape.LightColorG);
        Assert.True(after.Shape.ProjectionEntry);
        Assert.Equal(before.Shape.ProjectionTextureUUID, after.Shape.ProjectionTextureUUID);
        Assert.Equal(before.Shape.ProjectionFOV, after.Shape.ProjectionFOV);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void TheCheckerPassesTheRewriteWithAPlanToMeshWithNFaces(int faces)
    {
        KitPart part = SourcePart(LightAndProjection());
        string source = EligibilityKit.WriteArchive(part);
        string output = Rewrite(source, part, faces);

        string plan = "{\"version\":1,\"partChanges\":[{\"part\":\"" + part.Id + "\",\"field\":\"shape\",\"to\":\"mesh\",\"faces\":" + faces + "}]}";
        CompareResult result = new ArchiveComparer().Compare(source, output, ComparePlan.Parse(plan));
        Assert.True(result.Pass, result.ToText());

        // a plan that names another count does not pass
        string wrong = "{\"version\":1,\"partChanges\":[{\"part\":\"" + part.Id + "\",\"field\":\"shape\",\"to\":\"mesh\",\"faces\":" + (faces % 8 + 1) + "}]}";
        Assert.False(new ArchiveComparer().Compare(source, output, ComparePlan.Parse(wrong)).Pass);
    }

    [Fact]
    public void TheShapeEditsLeaveEveryOtherFieldOfThePartAsItWas()
    {
        KitPart part = SourcePart(LightAndProjection());
        string source = EligibilityKit.WriteArchive(part);
        string output = Rewrite(source, part, 6);
        XElement a = XDocument.Parse(Encoding.UTF8.GetString(ReadEntry(source, "objects/"))).Descendants("SceneObjectPart").Single();
        XElement b = XDocument.Parse(Encoding.UTF8.GetString(ReadEntry(output, "objects/"))).Descendants("SceneObjectPart").Single();

        string[] shapeFieldsThatChange =
        {
            "ProfileCurve", "PathCurve", "PathBegin", "PathEnd", "PathRadiusOffset", "PathRevolutions", "PathScaleX", "PathScaleY", "PathShearX", "PathShearY",
            "PathSkew", "PathTaperX", "PathTaperY", "PathTwist", "PathTwistBegin", "ProfileBegin", "ProfileEnd", "ProfileHollow", "ProfileShape", "HollowShape",
            "SculptTexture", "SculptType", "SculptEntry", "ExtraParams"
        };
        foreach (XElement child in a.Elements())
            if (child.Name != "Shape")
                Assert.Equal(child.ToString(SaveOptions.DisableFormatting), b.Element(child.Name).ToString(SaveOptions.DisableFormatting));
        foreach (XElement child in a.Element("Shape").Elements())
            if (!shapeFieldsThatChange.Contains(child.Name.LocalName))
                Assert.Equal(child.ToString(SaveOptions.DisableFormatting), b.Element("Shape").Element(child.Name).ToString(SaveOptions.DisableFormatting));
        Assert.Equal(a.Element("Shape").Elements().Select(e => e.Name.LocalName), b.Element("Shape").Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void TheEditsGiveTheShapeValuesTheServerGivesAMeshUpload()
    {
        for (int n = 1; n <= 8; n++)
        {
            PrimitiveBaseShape want = PrimitiveBaseShape.CreateMesh(n, new UUID(MeshAsset));
            Dictionary<string, string> edits = MeshShape.Edits(n, MeshAsset, null).ToDictionary(e => e.Path, e => e.Value);
            Assert.Equal(want.ProfileCurve.ToString(), edits["Shape/ProfileCurve"]);
            Assert.Equal(want.PathCurve.ToString(), edits["Shape/PathCurve"]);
            Assert.Equal(want.ProfileHollow.ToString(), edits["Shape/ProfileHollow"]);
            Assert.Equal(want.ProfileBegin.ToString(), edits["Shape/ProfileBegin"]);
            Assert.Equal(want.PathScaleY.ToString(), edits["Shape/PathScaleY"]);
            Assert.Equal("5", edits["Shape/SculptType"]);
            Assert.Equal("true", edits["Shape/SculptEntry"]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void ANumberOfFacesOutsideOneToEightIsRefused(int faces)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshShape.Edits(faces, MeshAsset, null));
    }

    // ------------------------------------------------------------ the sculpt block of ExtraParams

    private static PrimitiveBaseShape Decode(byte[] extra)
    {
        PrimitiveBaseShape s = new PrimitiveBaseShape();
        s.ReadInExtraParamsBytes(extra);
        return s;
    }

    [Fact]
    public void ASculptBlockIsAddedWhereThePartHasNoExtraParameters()
    {
        foreach (byte[] none in new[] { null, Array.Empty<byte>(), new byte[] { 0 } })
        {
            PrimitiveBaseShape s = Decode(MeshShape.WithSculptBlock(none, MeshAsset, 5));
            Assert.True(s.SculptEntry);
            Assert.Equal((byte)5, s.SculptType);
            Assert.Equal(new UUID(MeshAsset), s.SculptTexture);
        }
    }

    [Fact]
    public void ASculptBlockIsAppendedAndTheOtherBlocksAreKeptByteForByte()
    {
        byte[] before = LightAndProjection();
        byte[] after = MeshShape.WithSculptBlock(before, MeshAsset, 5);
        Assert.Equal(before[0] + 1, after[0]);
        Assert.Equal(before.Skip(1), after.Skip(1).Take(before.Length - 1));
        PrimitiveBaseShape s = Decode(after);
        Assert.True(s.SculptEntry && s.LightEntry && s.ProjectionEntry);
    }

    [Fact]
    public void AnExistingSculptBlockIsReplacedInPlaceAndItsNeighboursAreKept()
    {
        PrimitiveBaseShape original = new PrimitiveBaseShape
        {
            LightEntry = true, LightRadius = 4f, SculptEntry = true, SculptType = 1, SculptTexture = UUID.Random(),
            ProjectionEntry = true, ProjectionTextureUUID = UUID.Random()
        };
        byte[] before = original.ExtraParamsToBytes();
        byte[] after = MeshShape.WithSculptBlock(before, MeshAsset, 5);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before[0], after[0]);
        PrimitiveBaseShape s = Decode(after);
        Assert.Equal((byte)5, s.SculptType);
        Assert.Equal(new UUID(MeshAsset), s.SculptTexture);
        Assert.Equal(4f, s.LightRadius);
        Assert.Equal(original.ProjectionTextureUUID, s.ProjectionTextureUUID);
    }

    [Fact]
    public void ACutShortBlockIsRefused()
    {
        byte[] before = LightAndProjection();
        Assert.Throws<InvalidDataException>(() => MeshShape.WithSculptBlock(before.Take(before.Length - 3).ToArray(), MeshAsset, 5));
    }

    [Fact]
    public void AnOptionalElementThePartLacksIsNotEdited()
    {
        HashSet<string> present = new HashSet<string>(StringComparer.Ordinal) { "Shape/ProfileCurve", "Shape/SculptTexture/Guid" };
        IReadOnlyList<ElementEdit> edits = MeshShape.Edits(3, MeshAsset, null, present);
        Assert.DoesNotContain(edits, e => e.Path == "Shape/ProfileShape" || e.Path == "Shape/HollowShape");
        Assert.Contains(edits, e => e.Path == "Shape/SculptTexture/Guid");
        Assert.DoesNotContain(edits, e => e.Path == "Shape/SculptTexture/UUID");
    }
}
