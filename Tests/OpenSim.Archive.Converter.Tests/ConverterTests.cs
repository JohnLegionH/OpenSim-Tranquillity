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
using OpenSim.Archive.Converter;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Framework.Serialization;
using OpenSim.Region.Framework.Scenes;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>A sculpt map made in code, so a sculpt part can be converted without a JPEG 2000 file.</summary>
internal sealed class FakeSculptMaps : ISculptMapSource
{
    public SculptMap Get(Guid textureId, out string problem)
    {
        problem = null;
        const int n = 32;
        byte[] rgb = new byte[n * n * 3];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double th = Math.PI * y / (n - 1), ph = 2 * Math.PI * x / n;
                int i = (y * n + x) * 3;
                rgb[i] = (byte)(127.5 + 127.5 * Math.Sin(th) * Math.Cos(ph));
                rgb[i + 1] = (byte)(127.5 + 127.5 * Math.Sin(th) * Math.Sin(ph));
                rgb[i + 2] = (byte)(127.5 + 127.5 * Math.Cos(th));
            }
        return new SculptMap { Width = n, Height = n, Rgb = rgb };
    }
}

public class ConverterTests
{
    private static string Out(string source) => Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + "-out.oar");

    private static ConvertResult Convert(string source, out string output, ConvertOptions options = null)
    {
        output = Out(source);
        options ??= new ConvertOptions();
        options.SculptMaps ??= new FakeSculptMaps();
        return new ArchiveConverter(options).Convert(source, output);
    }

    private static Dictionary<Guid, SceneObjectPart> LoadParts(string archive)
    {
        Dictionary<Guid, SceneObjectPart> parts = new Dictionary<Guid, SceneObjectPart>();
        using FileStream fs = File.OpenRead(archive);
        using GZipStream gz = new GZipStream(fs, CompressionMode.Decompress);
        TarArchiveReader tar = new TarArchiveReader(gz);
        byte[] data;
        while ((data = tar.ReadEntry(out string name, out TarArchiveReader.TarEntryType type)) != null)
        {
            if (type != TarArchiveReader.TarEntryType.TYPE_NORMAL_FILE || !name.StartsWith("objects/", StringComparison.Ordinal))
                continue;
            foreach (XElement part in XDocument.Parse(Encoding.UTF8.GetString(data)).Descendants("SceneObjectPart"))
            {
                using XmlReader r = XmlReader.Create(new StringReader(part.ToString(SaveOptions.DisableFormatting)));
                r.MoveToContent();
                SceneObjectPart sop = SceneObjectPart.FromXml(r);
                parts[sop.UUID.Guid] = sop;
            }
        }
        return parts;
    }

    private static HashSet<string> Entries(string archive)
    {
        HashSet<string> names = new HashSet<string>();
        using FileStream fs = File.OpenRead(archive);
        using GZipStream gz = new GZipStream(fs, CompressionMode.Decompress);
        TarArchiveReader tar = new TarArchiveReader(gz);
        while (tar.ReadEntry(out string name, out TarArchiveReader.TarEntryType type) != null)
            names.Add(name);
        return names;
    }

    private static KitPart Fixture(string shape, string modifier, string name, int link = 0) =>
        new KitPart(name, link) { Shape = FaceMatrix.Shape(shape, modifier), Scale = "2,3,4" };

    public static IEnumerable<object[]> Fixtures()
    {
        yield return new object[] { "box", "plain" };
        yield return new object[] { "box", "hollow" };
        yield return new object[] { "box", "hollow+profile-cut" };
        yield return new object[] { "box", "path-cut" };
        yield return new object[] { "cylinder", "plain" };
        yield return new object[] { "cylinder", "hollow" };
        yield return new object[] { "cylinder", "profile-cut-quarter" };
        yield return new object[] { "prism", "plain" };
        yield return new object[] { "prism", "hollow+profile-cut" };
        yield return new object[] { "sphere", "plain" };
        yield return new object[] { "sphere", "hollow" };
        yield return new object[] { "sphere", "taper" };
        yield return new object[] { "torus", "plain" };
        yield return new object[] { "torus", "hollow" };
        yield return new object[] { "torus", "revolutions" };
        yield return new object[] { "tube", "plain" };
        yield return new object[] { "tube", "hollow+path-cut" };
        yield return new object[] { "ring", "plain" };
        yield return new object[] { "ring", "hollow" };
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AConvertedPartKeepsEverythingButItsShapeAndTheParityCheckPasses(string shape, string modifier)
    {
        KitPart part = Fixture(shape, modifier, "Fixture");
        string source = EligibilityKit.WriteArchive(part);
        int viewerBefore = FaceAgreement.Measure(part.Shape).Viewer;
        SceneObjectPart before = LoadParts(source)[part.Id];

        ConvertResult r = Convert(source, out string output);

        Assert.True(r.Pass, r.ToText());
        Assert.Single(r.Converted);
        Assert.Empty(r.Kept);
        SceneObjectPart after = LoadParts(output)[part.Id];
        Assert.Equal(viewerBefore, after.GetNumberOfSides());
        Assert.Equal(before.Scale, after.Scale);
        Assert.Equal(before.Shape.TextureEntry, after.Shape.TextureEntry);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.LinkNum, after.LinkNum);
        Assert.True(after.Shape.SculptEntry);
        Assert.Equal((byte)SculptType.Mesh, after.Shape.SculptType);
        Assert.Equal(r.Converted[0].MeshId, after.Shape.SculptTexture.Guid);
        Assert.Contains("assets/" + r.Converted[0].MeshId.ToString("D") + "_mesh.llmesh", Entries(output));
    }

    [Fact]
    public void ASculptBecomesAOneFaceMeshWithItsSizeAndTextureEntryKept()
    {
        Guid map = Guid.NewGuid();
        KitPart sculpt = new KitPart("Sculpt") { Shape = FaceMatrix.Shape("sphere", "plain"), Scale = "2,3,4", SculptTexture = map };
        sculpt.Shape.SculptEntry = true;
        sculpt.Shape.SculptType = (int)SculptType.Sphere;
        string source = EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { sculpt } }, new Dictionary<string, byte[]> { ["assets/" + map + "_texture.jp2"] = new byte[] { 1, 2, 3 } });
        SceneObjectPart before = LoadParts(source)[sculpt.Id];

        ConvertResult r = Convert(source, out string output);

        Assert.True(r.Pass, r.ToText());
        Assert.Equal("sculpt", r.Converted.Single().Kind);
        SceneObjectPart after = LoadParts(output)[sculpt.Id];
        Assert.Equal(1, after.GetNumberOfSides());
        Assert.Equal(before.Scale, after.Scale);
        Assert.Equal(before.Shape.TextureEntry, after.Shape.TextureEntry);
        Assert.Equal((byte)SculptType.Mesh, after.Shape.SculptType);
        Assert.True(r.Converted[0].Triangles > 100);
    }

    [Fact]
    public void ALinksetKeepsLinkNumbersNamesAndContentsAndOnlyConvertibleChildrenChange()
    {
        var script = EligibilityKit.Script("keeper", "default { state_entry() { llSay(0, \"hi\"); } }");
        KitPart root = Fixture("box", "plain", "Root");
        KitPart a = Fixture("cylinder", "hollow", "Child A", 2);
        a.Items.Add(script.item);
        KitPart flexi = Fixture("box", "plain", "Physical Child", 3);
        flexi.Flags = "Physics";
        KitPart d = Fixture("torus", "plain", "Child D", 4);
        string source = EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { root, a, flexi, d } }, new Dictionary<string, byte[]> { [script.entry] = script.data });

        ConvertResult r = Convert(source, out string output);

        Assert.True(r.Pass, r.ToText());
        Assert.Equal(3, r.Converted.Count);
        KeptPart kept = Assert.Single(r.Kept);
        Assert.Equal(ReasonCodes.Physical, kept.Code);
        Assert.Equal(3, kept.LinkNum);
        Dictionary<Guid, SceneObjectPart> after = LoadParts(output);
        Assert.False(after[flexi.Id].Shape.SculptEntry);
        Dictionary<Guid, SceneObjectPart> was = LoadParts(source);
        Assert.Equal(was.ToDictionary(k => k.Key, k => k.Value.LinkNum), after.ToDictionary(k => k.Key, k => k.Value.LinkNum));
        Assert.Equal("Child A", after[a.Id].Name);
        Assert.Single(after[a.Id].Inventory.GetInventoryList());
    }

    [Fact]
    public void ASecondRunGivesTheSameArchiveAndTwoIdenticalPartsShareOneMesh()
    {
        KitPart one = Fixture("box", "hollow", "One");
        KitPart two = Fixture("box", "hollow", "Two");
        two.Scale = "9,9,9";   // the size is not in the mesh, so the meshes are identical
        string source = EligibilityKit.WriteArchive(one, two);

        ConvertResult first = Convert(source, out string out1);
        ConvertResult second = Convert(source, out string out2);

        Assert.True(first.Pass, first.ToText());
        Assert.Equal(1, first.MeshesAdded);
        Assert.Equal(first.Converted[0].MeshId, first.Converted[1].MeshId);
        Assert.Equal(first.Converted.Select(c => c.MeshId), second.Converted.Select(c => c.MeshId));
        Assert.Equal(File.ReadAllBytes(out1).Length, File.ReadAllBytes(out2).Length);
        Assert.Equal(ArchiveSnapshot.Read(out1).Entries, ArchiveSnapshot.Read(out2).Entries);
    }

    [Fact]
    public void TheMeshIdComesFromTheEncodedBytesAndNoStandInIdIsLeft()
    {
        KitPart part = Fixture("torus", "hollow", "Ring");
        string source = EligibilityKit.WriteArchive(part);
        ConvertResult r = Convert(source, out string output);

        ConvertedPart c = r.Converted.Single();
        Assert.False(StandInIds.Is(c.MeshId));
        List<EncoderFace> faces = FaceBuilder.FromPrim(part.Shape, out _);
        Assert.Equal(c.MeshId, MeshId.FromBytes(new MeshAssetEncoder().Encode(faces, new EncodeOptions())));
        Assert.DoesNotContain(StandInIds.Prefix, r.PlanJson);
        Assert.All(Entries(output), e => Assert.DoesNotContain(StandInIds.Prefix, e));
    }

    [Fact]
    public void ADifferentShapeGivesADifferentMeshId()
    {
        string source = EligibilityKit.WriteArchive(Fixture("box", "plain", "A"), Fixture("box", "hollow", "B"));
        ConvertResult r = Convert(source, out _);
        Assert.Equal(2, r.MeshesAdded);
        Assert.NotEqual(r.Converted[0].MeshId, r.Converted[1].MeshId);
    }

    private sealed class EmptyEncoder : IMeshEncoder
    {
        public string Name => "empty";
        public bool ProducesValidMesh => false;
        public byte[] Encode(IReadOnlyList<EncoderFace> faces, EncodeOptions options) => Array.Empty<byte>();
    }

    [Fact]
    public void AnEncoderThatReturnsNothingStopsTheRunBeforeAnythingIsWritten()
    {
        string source = EligibilityKit.WriteArchive(Fixture("box", "plain", "A"));
        string output = Out(source);
        Assert.Throws<ConvertException>(() => new ArchiveConverter(new ConvertOptions { Encoder = new EmptyEncoder() }).Convert(source, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TheConverterNeverOverwritesAnExistingFileOrTheSource()
    {
        string source = EligibilityKit.WriteArchive(Fixture("box", "plain", "A"));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source));
        string existing = Out(source);
        File.WriteAllText(existing, "x");
        Assert.ThrowsAny<IOException>(() => new ArchiveConverter().Convert(source, existing));
        Assert.Throws<ArgumentException>(() => new ArchiveConverter().Convert(source, source));
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void TheParityCheckerFailsOnAStandInIdInThePlanOrTheArchive()
    {
        KitPart part = Fixture("box", "plain", "A");
        string source = EligibilityKit.WriteArchive(part);
        ConvertResult r = Convert(source, out string output);
        Assert.True(r.Pass);

        // the same rewrite checked against a plan that names a stand-in id: unplanned difference and failure
        string stand = StandInIds.Make(1).ToString("D");
        string plan = "{\"version\":1,\"partChanges\":[{\"part\":\"" + part.Id + "\",\"field\":\"shape\",\"to\":\"mesh\",\"faces\":6}],\"addedEntries\":[\"assets/" + stand + "_mesh.llmesh\",\"" + r.Converted[0].MeshId + "\"]}";
        CompareResult bad = new ArchiveComparer().Compare(source, output, ComparePlan.Parse(plan));
        Assert.False(bad.Pass);
        Assert.Contains(bad.Unplanned, d => d.Field == "standInId");
    }

    [Fact]
    public void ADraftPlanWithStandInIdsIsNotAcceptedByTheChecker()
    {
        KitPart part = Fixture("box", "plain", "A");
        string source = EligibilityKit.WriteArchive(part);
        EligibilityResult elig = new ArchiveEligibility().Evaluate(source);
        string draft = EligibilityReport.ToDraftPlan(elig);
        ConvertResult r = Convert(source, out string output);
        CompareResult c = new ArchiveComparer().Compare(source, output, ComparePlan.Parse(draft));
        Assert.False(c.Pass);
        Assert.Contains(c.Unplanned, d => d.Field == "standInId");
    }

    // ------------------------------------------------------------ the eligibility rules

    private static EligibilityRow Row(string source, KitPart p) => new ArchiveEligibility().Evaluate(source).Parts.Single(x => x.PartId == p.Id);

    private static string WithScript(KitPart part, string src)
    {
        var s = EligibilityKit.Script("script", src);
        part.Items.Add(s.item);
        return EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { part } }, new Dictionary<string, byte[]> { [s.entry] = s.data });
    }

    [Theory]
    [InlineData("default { state_entry() { llSetStatus(STATUS_PHYSICS, TRUE); } }")]
    [InlineData("default { state_entry() { llSetLinkPrimitiveParamsFast(LINK_THIS, [PRIM_PHYSICS, TRUE]); } }")]
    [InlineData("default { state_entry() { llSetVehicleType(VEHICLE_TYPE_CAR); } }")]
    public void APartWhoseScriptTurnsPhysicsOnAtRunTimeIsKept(string source)
    {
        KitPart part = Fixture("box", "plain", "Mover");
        EligibilityRow row = Row(WithScript(part, source), part);
        Assert.Equal(PartAction.KeepPrim, row.Action);
        Assert.Equal(ReasonCodes.PhysicalScript, row.Code);
    }

    [Fact]
    public void ASetOfThePhysicsShapeTypeAloneDoesNotTurnPhysicsOn()
    {
        KitPart part = Fixture("box", "plain", "Shaped");
        EligibilityRow row = Row(WithScript(part, "default { state_entry() { llSetLinkPrimitiveParamsFast(LINK_THIS, [PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_CONVEX]); } }"), part);
        Assert.NotEqual(ReasonCodes.PhysicalScript, row.Code);
    }

    [Fact]
    public void AnUnexplainedFaceCountDifferenceIsKeptAsUnverified()
    {
        KitPart part = Fixture("sphere", "taper", "Odd");
        part.Shape.PathRevolutions = 20;   // two causes at once: no settled case covers it
        EligibilityRow row = Row(EligibilityKit.WriteArchive(part), part);
        Assert.Equal(PartAction.KeepPrim, row.Action);
        Assert.Equal(ReasonCodes.FaceRuleUnverified, row.Code);
    }

    [Theory]
    [InlineData("torus")]
    [InlineData("tube")]
    [InlineData("ring")]
    public void AClosedHoleRadiusOffsetIsKeptBecauseTheMesherDrawsItOpen(string shape)
    {
        KitPart part = Fixture(shape, "hole-size-one+radius-offset", "Offset");
        EligibilityRow row = Row(EligibilityKit.WriteArchive(part), part);
        Assert.Equal(PartAction.KeepPrim, row.Action);
        Assert.Equal(ReasonCodes.FaceRuleUnverified, row.Code);
    }

    [Fact]
    public void ASettledDisagreementConvertsWithTheViewersCountAndAWarning()
    {
        KitPart part = Fixture("sphere", "taper", "Tapered");
        EligibilityRow row = Row(EligibilityKit.WriteArchive(part), part);
        Assert.Equal(PartAction.Convert, row.Action);
        Assert.Equal(3, row.TargetFaces);
        Assert.Contains(row.Warnings, w => w.Code == ReasonCodes.SideCountChanges);
    }
}
