/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.GltfExport;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Restore.Tests;

/// <summary>Edits a binary glTF file the way a modelling tool would, without a modelling tool.</summary>
internal static class GlbPatch
{
    public static byte[] Edit(byte[] glb, Action<JsonNode, byte[]> edit)
    {
        int jsonLength = BitConverter.ToInt32(glb, 12);
        JsonNode json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
        int binAt = 20 + ((jsonLength + 3) & ~3) + 8;
        int binLength = BitConverter.ToInt32(glb, binAt - 8);
        byte[] bin = new byte[binLength];
        Array.Copy(glb, binAt, bin, 0, binLength);
        edit(json, bin);
        byte[] j = Encoding.UTF8.GetBytes(json.ToJsonString());
        int jp = (j.Length + 3) & ~3, bp = (bin.Length + 3) & ~3;
        using MemoryStream ms = new MemoryStream();
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(0x46546C67); w.Write(2); w.Write(12 + 8 + jp + 8 + bp);
            w.Write(jp); w.Write(0x4E4F534A); w.Write(j); for (int i = j.Length; i < jp; i++) w.Write((byte)0x20);
            w.Write(bp); w.Write(0x004E4942); w.Write(bin); for (int i = bin.Length; i < bp; i++) w.Write((byte)0);
        }
        return ms.ToArray();
    }

    public static JsonNode PartNode(JsonNode json, string partUuid) =>
        json["nodes"].AsArray().First(n => (string)n["extras"]?["partUuid"] == partUuid);

    private static (int offset, int stride, int count) Accessor(JsonNode json, int accessor)
    {
        JsonNode a = json["accessors"][accessor], v = json["bufferViews"][(int)a["bufferView"]];
        int comps = (string)a["type"] == "VEC3" ? 12 : 4;
        return (((int)(v["byteOffset"] ?? 0)) + ((int)(a["byteOffset"] ?? 0)), (int)(v["byteStride"] ?? comps), (int)a["count"]);
    }

    /// <summary>Moves every vertex of one prim face of one part up (glTF Y) by <paramref name="dy"/> metres.</summary>
    public static void MoveFace(JsonNode json, byte[] bin, string partUuid, int face, float dy)
    {
        JsonNode mesh = json["meshes"][(int)PartNode(json, partUuid)["mesh"]];
        foreach (JsonNode p in mesh["primitives"].AsArray())
        {
            var pos = Accessor(json, (int)p["attributes"]["POSITION"]);
            var fn = Accessor(json, (int)p["attributes"]["_PRIM_FACE"]);
            for (int i = 0; i < pos.count; i++)
            {
                if ((int)Math.Round(BitConverter.ToSingle(bin, fn.offset + i * fn.stride)) != face)
                    continue;
                int at = pos.offset + i * pos.stride + 4;
                BitConverter.TryWriteBytes(bin.AsSpan(at), BitConverter.ToSingle(bin, at) + dy);
            }
        }
    }
}

public class RestoreTests
{
    internal const string Benign = "default { state_entry() { llSay(0, \"hi\"); } }";
    internal const string ChangesShape = "default { state_entry() { llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_BOX, 0, <0,1,0>, 0.0, <0,0,0>, <1,1,0>, <0,0,0>]); } }";

    /// <summary>One run's folders and the parts of a two-build fixture: a linkset of two parts with a script in the child, and a single part.</summary>
    internal sealed class Fixture
    {
        public string Dir = Path.Combine(Path.GetTempPath(), "archive-restore-tests", Guid.NewGuid().ToString("N"));
        public KitPart Root = new KitPart("Root", 0) { Scale = "1,2,3" };
        public KitPart Child = new KitPart("Child", 2) { Scale = "2,2,2" };
        public KitPart Solo = new KitPart("Solo", 0) { Scale = "1,1,1" };
        public string Archive, GlbDir;

        /// <summary>Two faces with their own pictures and colours over a default one, as a texture entry a region could have written.</summary>
        public static readonly Guid FacePicture0 = Guid.Parse("11111111-1111-4111-8111-111111111111"), FacePicture1 = Guid.Parse("22222222-2222-4222-8222-222222222222");

        public static string ValidTextureEntry()
        {
            OpenMetaverse.Primitive.TextureEntry te = new OpenMetaverse.Primitive.TextureEntry(new OpenMetaverse.UUID("89556747-24cb-43ed-920b-47caed15465f"));
            OpenMetaverse.Primitive.TextureEntryFace a = te.CreateFace(0), b = te.CreateFace(1);
            a.TextureID = new OpenMetaverse.UUID(FacePicture0);
            a.RGBA = new OpenMetaverse.Color4(1f, 0.5f, 0.25f, 1f);
            b.TextureID = new OpenMetaverse.UUID(FacePicture1);
            b.RGBA = new OpenMetaverse.Color4(0.1f, 0.2f, 0.3f, 1f);
            return Convert.ToBase64String(te.GetBytes());
        }

        public Fixture(string childScript = Benign, string childFlags = "None")
        {
            Directory.CreateDirectory(Dir);
            Root.TextureEntry = Child.TextureEntry = Solo.TextureEntry = ValidTextureEntry();
            Child.Flags = childFlags;
            (KitItem item, string entry, byte[] data) s = EligibilityKit.Script("child script", childScript);
            Child.Items.Add(s.item);
            Root.LinkNum = 1;
            Archive = EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { Root, Child }, new List<KitPart> { Solo } },
                new Dictionary<string, byte[]> { [s.entry] = s.data });
            GlbDir = Path.Combine(Dir, "glb");
            ExportSummary export = ArchiveExporter.Export(Archive, new ExportOptions { OutputDirectory = GlbDir, ImageCheck = () => null });
            Assert.Equal(0, export.ExitCode);
        }

        public string Out(string name = "out.oar") => Path.Combine(Dir, name);

        public string GlbOf(KitPart root) => Directory.GetFiles(GlbDir, "*" + root.Id.ToString("N").Substring(0, 12) + ".glb", SearchOption.AllDirectories).Single();

        public void Edit(KitPart root, Action<JsonNode, byte[]> edit)
        {
            string path = GlbOf(root);
            File.WriteAllBytes(path, GlbPatch.Edit(File.ReadAllBytes(path), edit));
        }

        public RestoreResult Restore(RestoreOptions options = null, string output = null)
        {
            options ??= new RestoreOptions();
            options.WorkFolder ??= Path.Combine(Dir, "work");
            return new ArchiveRestorer(options).Restore(Archive, GlbDir, output ?? Out());
        }
    }

    internal static Dictionary<string, byte[]> Entries(string oar)
    {
        Dictionary<string, byte[]> result = new Dictionary<string, byte[]>();
        using FileStream fs = File.OpenRead(oar);
        using GZipStream gz = new GZipStream(fs, CompressionMode.Decompress);
        using TarReader tar = new TarReader(gz);
        TarEntry e;
        while ((e = tar.GetNextEntry()) != null)
            if (e.EntryType == TarEntryType.RegularFile)
            {
                using MemoryStream ms = new MemoryStream();
                e.DataStream.CopyTo(ms);
                result[e.Name] = ms.ToArray();
            }
        return result;
    }

    internal static string PartUuid(KitPart p) => p.Id.ToString();

    internal static PartReport Of(RestoreResult r, KitPart p) => r.Parts.Single(x => x.PartId == p.Id);

    [Fact]
    public void NoEditsLeavesEveryPartAndEntryUntouched()
    {
        Fixture f = new Fixture();
        RestoreResult r = f.Restore();
        Assert.Equal(3, r.Count(PartOutcome.Unchanged));
        Assert.Equal(0, r.Count(PartOutcome.Rebuilt) + r.Count(PartOutcome.Refused));
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass);
        Dictionary<string, byte[]> a = Entries(f.Archive), b = Entries(f.Out());
        Assert.Equal(a.Keys.OrderBy(k => k), b.Keys.OrderBy(k => k));
        foreach (string k in a.Keys)
            Assert.True(a[k].SequenceEqual(b[k]), "entry differs: " + k);
    }

    [Fact]
    public void AnEditedFaceRebuildsOnlyThatPartAndKeepsEverythingElseAboutIt()
    {
        Fixture f = new Fixture();
        f.Edit(f.Root, (j, bin) => GlbPatch.MoveFace(j, bin, PartUuid(f.Child), 0, 0.5f));
        RestoreResult r = f.Restore();

        Assert.Equal(PartOutcome.Rebuilt, Of(r, f.Child).Outcome);
        Assert.Equal(PartOutcome.Unchanged, Of(r, f.Root).Outcome);
        Assert.Equal(PartOutcome.Unchanged, Of(r, f.Solo).Outcome);
        Assert.Equal(6, Of(r, f.Child).Faces);
        Assert.True(r.Parity.Pass);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void ARebuiltPartKeepsItsFieldsAndItsMeshHoldsTheEdit()
    {
        Fixture f = new Fixture();
        f.Edit(f.Root, (j, bin) => GlbPatch.MoveFace(j, bin, PartUuid(f.Child), 0, 0.5f));
        RestoreResult r = f.Restore();
        Assert.Equal(0, r.ExitCode);

        Dictionary<string, byte[]> before = Entries(f.Archive), after = Entries(f.Out());
        string objectKey = before.Keys.Single(k => k.StartsWith("objects/Root_", StringComparison.Ordinal));
        XElement oldChild = Part(before[objectKey], f.Child.Id), newChild = Part(after[objectKey], f.Child.Id);
        // everything outside the shape is the same text; inside the shape only the geometry fields and the mesh id change
        foreach (XElement e in oldChild.Elements().Where(e => e.Name != "Shape"))
            Assert.Equal(e.ToString(), newChild.Element(e.Name).ToString());
        Assert.Equal(oldChild.Element("Shape").Element("TextureEntry").Value, newChild.Element("Shape").Element("TextureEntry").Value);
        Assert.Equal("true", newChild.Element("Shape").Element("SculptEntry").Value);
        Assert.Equal(Part(before[objectKey], f.Root.Id).ToString(), Part(after[objectKey], f.Root.Id).ToString());
        Assert.Equal(1, after.Keys.Count(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal)));

        // the mesh has the part's six faces; the top face was 1 m above the centre of the 2 m part and moved up 0.5 m: 0.75 of the part's size
        MeshAssetModel mesh = MeshCodec.Decode(after[after.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal))]);
        Assert.Equal(6, mesh.High.Faces.Count);
        float top = mesh.High.Faces.Where(x => !x.NoGeometry).SelectMany(x => x.Positions).Max(p => p.Z);
        Assert.InRange(top, 0.7f, 0.8f);
    }

    internal static XElement Part(byte[] objectXml, Guid id) =>
        XDocument.Parse(Encoding.UTF8.GetString(objectXml)).Descendants("SceneObjectPart").Single(p => p.Element("UUID").Element("UUID").Value == id.ToString());

    [Fact]
    public void ATouchedScriptThatChangesTheShapeIsRefusedUntilAllowed()
    {
        Fixture f = new Fixture(ChangesShape);
        f.Edit(f.Root, (j, bin) => GlbPatch.MoveFace(j, bin, PartUuid(f.Child), 0, 0.5f));

        RestoreResult refused = f.Restore();
        PartReport p = Of(refused, f.Child);
        Assert.Equal(PartOutcome.Refused, p.Outcome);
        Assert.Equal("SCRIPT_UNSAFE", p.Code);
        Assert.Contains("child script", p.Detail);
        Assert.Equal(0, refused.MeshesAdded);
        Assert.Equal(3, refused.ExitCode);
        Dictionary<string, byte[]> same = Entries(f.Out());
        foreach (KeyValuePair<string, byte[]> e in Entries(f.Archive))
            Assert.True(e.Value.SequenceEqual(same[e.Key]), "entry differs: " + e.Key);

        RestoreResult allowed = f.Restore(new RestoreOptions { AllowUnsafe = true }, f.Out("allowed.oar"));
        PartReport q = Of(allowed, f.Child);
        Assert.Equal(PartOutcome.Rebuilt, q.Outcome);
        Assert.Contains(q.Notes, n => n.Contains("--allow-unsafe"));
    }

    [Fact]
    public void AChangedPhysicalPartIsRefused()
    {
        Fixture f = new Fixture(Benign, "Physics");
        f.Edit(f.Root, (j, bin) => GlbPatch.MoveFace(j, bin, PartUuid(f.Child), 0, 0.5f));
        RestoreResult r = f.Restore();
        Assert.Equal(PartOutcome.Refused, Of(r, f.Child).Outcome);
        Assert.Equal("PHYSICAL", Of(r, f.Child).Code);
    }

    [Fact]
    public void ATriangleThatLostItsFaceNumberIsRefusedWhenItsPartChanged()
    {
        Fixture f = new Fixture();
        f.Edit(f.Root, (j, bin) =>
        {
            GlbPatch.MoveFace(j, bin, PartUuid(f.Child), 0, 0.5f);
            JsonNode mesh = j["meshes"][(int)GlbPatch.PartNode(j, PartUuid(f.Child))["mesh"]];
            foreach (JsonNode p in mesh["primitives"].AsArray())
            {
                p["attributes"].AsObject().Remove("_PRIM_FACE");
                p.AsObject().Remove("extras");
            }
        });
        RestoreResult r = f.Restore();
        Assert.Equal("FACE_NUMBERS_LOST", Of(r, f.Child).Code);
    }

    [Fact]
    public void AGlbFromAnotherArchiveIsRefused()
    {
        Fixture f = new Fixture(), other = new Fixture();
        File.Copy(other.GlbOf(other.Solo), Path.Combine(f.GlbDir, "stranger.glb"));
        RestoreException e = Assert.Throws<RestoreException>(() => f.Restore());
        Assert.Contains("belongs to no build", e.Message);
    }

    [Fact]
    public void ABuildWithNoGlbIsKeptAndTheOutputIsNeverOverwritten()
    {
        Fixture f = new Fixture();
        File.Move(f.GlbOf(f.Solo), Path.Combine(f.Dir, "solo-moved-aside.glb.keep"));
        RestoreResult r = f.Restore();
        Assert.Single(r.BuildsWithoutGlb);
        Assert.Equal(2, r.Parts.Count);
        Assert.Throws<RestoreException>(() => f.Restore());
    }

    // ------------------------------------------------------------------ the geometry comparison

    private static float[] Tri(float x, float y, float z, float nx = 0, float ny = 1, float nz = 0, float u = 0, float v = 0) =>
        new[]
        {
            x, y, z, nx, ny, nz, u, v,
            x + 1, y, z, nx, ny, nz, u + 1, v,
            x, y, z + 1, nx, ny, nz, u, v + 1
        };

    private static PartGeometry Geometry(params float[][] triangles)
    {
        PartGeometry g = new PartGeometry();
        g.Faces[0] = triangles.ToList();
        g.Materials[0] = triangles.Select(_ => -1).ToList();
        return g;
    }

    [Fact]
    public void TriangleOrderAndStartingCornerDoNotMatter()
    {
        float[] a = Tri(0, 0, 0), b = Tri(5, 0, 0);
        float[] rotated = new float[24];
        for (int c = 0; c < 3; c++)
            Array.Copy(a, ((c + 1) % 3) * 8, rotated, c * 8, 8);
        GeometryComparison cmp = GeometryComparer.Compare(Geometry(a, b), Geometry(b, rotated), new GeometryTolerance());
        Assert.True(cmp.Equal, cmp.Difference);
    }

    [Fact]
    public void AMovedVertexBeyondTheToleranceIsADifference()
    {
        float[] moved = Tri(0, 0, 0);
        moved[0] += 2 * GeometryTolerance.DefaultPosition;
        Assert.False(GeometryComparer.Compare(Geometry(Tri(0, 0, 0)), Geometry(moved), new GeometryTolerance()).Equal);
        moved[0] = Tri(0, 0, 0)[0] + GeometryTolerance.DefaultPosition / 2;
        Assert.True(GeometryComparer.Compare(Geometry(Tri(0, 0, 0)), Geometry(moved), new GeometryTolerance()).Equal);
    }

    [Fact]
    public void AFlippedTriangleANormalTurnedAndAMovedTextureCoordinateAreDifferences()
    {
        float[] flipped = new float[24];
        Array.Copy(Tri(0, 0, 0), 0, flipped, 0, 8);
        Array.Copy(Tri(0, 0, 0), 16, flipped, 8, 8);
        Array.Copy(Tri(0, 0, 0), 8, flipped, 16, 8);
        Assert.False(GeometryComparer.Compare(Geometry(Tri(0, 0, 0)), Geometry(flipped), new GeometryTolerance()).Equal);
        Assert.False(GeometryComparer.Compare(Geometry(Tri(0, 0, 0)), Geometry(Tri(0, 0, 0, 1, 0, 0)), new GeometryTolerance()).Equal);
        Assert.False(GeometryComparer.Compare(Geometry(Tri(0, 0, 0)), Geometry(Tri(0, 0, 0, u: 0.01f)), new GeometryTolerance()).Equal);
    }

    [Fact]
    public void ATriangleWithNoAreaIsComparedByPositionAndTextureOnly()
    {
        float[] flat = Tri(0, 0, 0);
        for (int c = 1; c < 3; c++)
            Array.Copy(flat, 0, flat, c * 8, 3);   // all three corners at one point
        float[] other = (float[])flat.Clone();
        for (int c = 0; c < 3; c++)
            other[c * 8 + 4] = -1f;   // a different normal
        Assert.True(GeometryComparer.Compare(Geometry(flat), Geometry(other), new GeometryTolerance()).Equal);
    }

    [Fact]
    public void ADifferentTriangleCountOrFaceSetIsADifference()
    {
        Assert.False(GeometryComparer.Compare(Geometry(Tri(0, 0, 0)), Geometry(Tri(0, 0, 0), Tri(5, 0, 0)), new GeometryTolerance()).Equal);
        PartGeometry other = Geometry(Tri(0, 0, 0));
        other.Faces[3] = other.Faces[0];
        other.Faces.Remove(0);
        other.Materials[3] = other.Materials[0];
        Assert.False(GeometryComparer.Compare(Geometry(Tri(0, 0, 0)), other, new GeometryTolerance()).Equal);
    }
}
