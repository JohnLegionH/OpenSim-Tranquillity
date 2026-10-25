/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Archive.Converter;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.GltfExport;
using OpenSim.Framework;
using OpenSim.MeshAsset;
using SkiaSharp;
using Xunit;

namespace OpenSim.Archive.Restore.Tests;

/// <summary>Deleted, added, moved, turned and resized parts, and a face over the vertex limit.</summary>
public class RestoreEditTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static void Edit(RestoreTests.Fixture f, KitPart root, Action<GlbEdit> edit)
    {
        string path = f.GlbOf(root);
        File.WriteAllBytes(path, GlbEdit.Apply(File.ReadAllBytes(path), edit));
    }

    private static string ObjectKey(Dictionary<string, byte[]> entries, KitPart root) => entries.Keys.Single(k => k.StartsWith("objects/" + root.Name + "_", StringComparison.Ordinal));

    private static float F(XElement e, params string[] path)
    {
        foreach (string name in path)
            e = e.Element(name);
        return float.Parse(e.Value, Inv);
    }

    private static PartReport Added(RestoreResult r) => r.Parts.Single(p => p.Outcome == PartOutcome.Added);

    private static void AssertSameBytes(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b, params string[] except)
    {
        // every entry the original had is in the new archive, byte for byte, apart from those named
        foreach (string k in a.Keys.Where(k => !except.Contains(k)))
        {
            Assert.True(b.ContainsKey(k), "entry missing: " + k);
            Assert.True(a[k].SequenceEqual(b[k]), "entry differs: " + k);
        }
    }

    // ------------------------------------------------------------------ deleted parts

    [Fact]
    public void ADeletedPartIsRefusedByDefaultAndTheArchiveIsWrittenAsItWas()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        Edit(f, f.Root, g => g.RemovePart(RestoreTests.PartUuid(f.Child)));
        RestoreResult r = f.Restore();

        PartReport p = RestoreTests.Of(r, f.Child);
        Assert.Equal(PartOutcome.Refused, p.Outcome);
        Assert.Equal("PART_DELETED", p.Code);
        Assert.Equal(3, r.ExitCode);
        Assert.True(r.Written);
        Dictionary<string, byte[]> kept = RestoreTests.Entries(f.Out());
        AssertSameBytes(RestoreTests.Entries(f.Archive), kept);
        Assert.Equal(RestoreTests.Entries(f.Archive).Count, kept.Count);
        string text = r.ToText();
        Assert.True(text.IndexOf("REFUSED PARTS (1)", StringComparison.Ordinal) >= 0, text);
        Assert.True(text.IndexOf("REFUSED PARTS (1)", StringComparison.Ordinal) < text.IndexOf("UNCHANGED", StringComparison.Ordinal), "the refused parts come first");
    }

    [Fact]
    public void ADeletedPartBecomesAnInvisiblePhantomStubThatKeepsItsLinkNumberAndScripts()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        Edit(f, f.Root, g => g.RemovePart(RestoreTests.PartUuid(f.Child)));
        RestoreResult r = f.Restore(new RestoreOptions { StubDeleted = true });

        Assert.Equal(PartOutcome.Stubbed, RestoreTests.Of(r, f.Child).Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());

        Dictionary<string, byte[]> before = RestoreTests.Entries(f.Archive), after = RestoreTests.Entries(f.Out());
        string key = ObjectKey(before, f.Root);
        XElement oldChild = RestoreTests.Part(before[key], f.Child.Id), newChild = RestoreTests.Part(after[key], f.Child.Id);
        Assert.Equal(oldChild.Element("Name").Value, newChild.Element("Name").Value);
        Assert.Equal("2", newChild.Element("LinkNum").Value);
        Assert.Equal(oldChild.Element("TaskInventory").ToString(), newChild.Element("TaskInventory").ToString());
        Assert.Contains("Phantom", newChild.Element("Flags").Value);
        byte[] te = Convert.FromBase64String(newChild.Element("Shape").Element("TextureEntry").Value);
        Primitive.TextureEntry entry = new Primitive.TextureEntry(te, 0, te.Length);
        Assert.Equal(0f, entry.DefaultTexture.RGBA.A);
        Assert.All(entry.FaceTextures.Where(x => x != null), x => Assert.Equal(0f, x.RGBA.A));

        // link numbers have no gap, and nothing else changed: the root part is the same text, the other build the same bytes
        Assert.Equal(new[] { 1, 2 }, new[] { f.Root.Id, f.Child.Id }.Select(id => int.Parse(RestoreTests.Part(after[key], id).Element("LinkNum").Value)));
        Assert.Equal(RestoreTests.Part(before[key], f.Root.Id).ToString(), RestoreTests.Part(after[key], f.Root.Id).ToString());
        AssertSameBytes(before, after, key);
        Assert.Equal(0, r.MeshesAdded);
    }

    // ------------------------------------------------------------------ added parts

    [Fact]
    public void AnAddedPartGetsTheNextLinkNumberAndIsBuiltAsAMeshPart()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        Edit(f, f.Root, g => g.Cube("New cube", 0.5f, translation: new[] { 1f, 0f, 0f }));
        RestoreResult r = f.Restore();

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        PartReport add = Added(r);
        Assert.Equal(3, add.LinkNum);
        Assert.Equal("New cube", add.Name);
        Assert.Equal(1, r.MeshesAdded);

        Dictionary<string, byte[]> before = RestoreTests.Entries(f.Archive), after = RestoreTests.Entries(f.Out());
        string key = ObjectKey(before, f.Root);
        XElement part = RestoreTests.Part(after[key], add.PartId);
        Assert.Equal("3", part.Element("LinkNum").Value);
        Assert.Equal("New cube", part.Element("Name").Value);
        Assert.Empty(part.Element("TaskInventory")?.Elements() ?? Enumerable.Empty<XElement>());
        Assert.Equal("true", part.Element("Shape").Element("SculptEntry").Value);
        Assert.Equal("5", part.Element("Shape").Element("SculptType").Value);
        Assert.Equal(0.5f, F(part, "Scale", "X"), 3);
        Assert.Equal(1f, F(part, "OffsetPosition", "X"), 3);
        Assert.Equal(0f, F(part, "OffsetPosition", "Z"), 3);
        // the owner and the permissions are the root part's
        XElement root = RestoreTests.Part(after[key], f.Root.Id);
        foreach (string e in new[] { "BaseMask", "OwnerMask", "EveryoneMask" })
            Assert.Equal(root.Element(e).Value, part.Element(e).Value);
        Assert.Equal(root.Element("OwnerID").Value, part.Element("OwnerID").Value);
        // the parts that were there are the same text; the other build is the same bytes
        Assert.Equal(RestoreTests.Part(before[key], f.Root.Id).ToString(), root.ToString());
        Assert.Equal(RestoreTests.Part(before[key], f.Child.Id).ToString(), RestoreTests.Part(after[key], f.Child.Id).ToString());
        AssertSameBytes(before, after, key, after.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal)));
        MeshAssetModel mesh = MeshCodec.Decode(after[after.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal))]);
        Assert.Single(mesh.High.Faces);
        Assert.Equal(12, mesh.High.Faces[0].Indices.Length / 3);
    }

    [Fact]
    public void APartAddedToASinglePartObjectIsNumberedTwoAndTwoAddedPartsFollowInOrder()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        Edit(f, f.Solo, g => g.Cube("Second", 0.3f, translation: new[] { 0f, 1f, 0f }));
        Edit(f, f.Root, g =>
        {
            g.Cube("Fourth", 0.2f, translation: new[] { 2f, 0f, 0f });
            g.Cube("Fifth", 0.2f, translation: new[] { 3f, 0f, 0f });
        });
        RestoreResult r = f.Restore();

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Assert.Equal(new[] { 2, 3, 4 }, r.Parts.Where(p => p.Outcome == PartOutcome.Added).Select(p => p.LinkNum).OrderBy(x => x));
        Assert.Equal(new[] { "Fourth", "Fifth" }, r.Parts.Where(p => p.Outcome == PartOutcome.Added && p.LinkNum > 2).OrderBy(p => p.LinkNum).Select(p => p.Name));
        Dictionary<string, byte[]> after = RestoreTests.Entries(f.Out());
        XElement solo = RestoreTests.Part(after[ObjectKey(after, f.Solo)], f.Solo.Id);
        Assert.Equal("0", solo.Element("LinkNum").Value);
    }

    private static byte[] Png(byte r, byte g, byte b)
    {
        using SKBitmap bitmap = new SKBitmap(4, 4);
        bitmap.Erase(new SKColor(r, g, b, 255));
        Assert.True(SkiaImageUtils.TryEncodeToPng(bitmap, out byte[] png));
        return png;
    }

    [Fact]
    public void AnAddedPartsNewPictureBecomesATextureAssetAndAnIdenticalOneReusesTheExistingAsset()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        // a picture the archive already has: the comparison export holds a PNG for every texture asset, named by its id
        Guid existing = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(f.Dir, "work", "textures"));
        File.WriteAllBytes(Path.Combine(f.Dir, "work", "textures", existing + ".png"), Png(200, 10, 10));
        Edit(f, f.Root, g =>
        {
            int fresh = g.Material(new[] { 1f, 1f, 1f, 1f }, "OPAQUE", Png(10, 200, 30));
            int same = g.Material(new[] { 0.5f, 0.25f, 1f, 1f }, "OPAQUE", Png(200, 10, 10));
            int plain = g.Material(new[] { 0.2f, 0.4f, 0.6f, 0.5f }, "BLEND");
            g.Cube("With a new picture", 0.4f, fresh, new[] { 1f, 0f, 0f });
            g.Cube("With the old picture", 0.4f, same, new[] { 2f, 0f, 0f });
            g.Cube("Only a colour", 0.4f, plain, new[] { 3f, 0f, 0f });
        });
        RestoreResult r = f.Restore();

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Assert.Equal(1, r.TexturesAdded);
        Dictionary<string, byte[]> after = RestoreTests.Entries(f.Out());
        string textureKey = after.Keys.Single(k => k.EndsWith("_texture.jp2", StringComparison.Ordinal));
        Guid created = Guid.Parse(Path.GetFileName(textureKey).Replace("_texture.jp2", string.Empty));
        Assert.True(SkiaImageUtils.TryDecodeFromJ2K(after[textureKey], out SKBitmap decoded));
        Assert.Equal(4, decoded.Width);

        string key = ObjectKey(after, f.Root);
        Primitive.TextureEntryFace Face(string name)
        {
            PartReport p = r.Parts.Single(x => x.Name == name);
            byte[] te = Convert.FromBase64String(RestoreTests.Part(after[key], p.PartId).Element("Shape").Element("TextureEntry").Value);
            return new Primitive.TextureEntry(te, 0, te.Length).FaceTextures[0];
        }
        Assert.Equal(created, Face("With a new picture").TextureID.Guid);
        Primitive.TextureEntryFace old = Face("With the old picture");
        Assert.Equal(existing, old.TextureID.Guid);
        Assert.Equal(0.5f, old.RGBA.R, 2);
        Primitive.TextureEntryFace plainFace = Face("Only a colour");
        Assert.Equal(0.6f, plainFace.RGBA.B, 2);
        Assert.Equal(0.5f, plainFace.RGBA.A, 2);
    }

    [Fact]
    public void ACopiedPartThatKeptItsPartIdIsANewPartAndTheOriginalStaysAsItWas()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        Edit(f, f.Root, g =>
        {
            JsonNode child = g.Part(RestoreTests.PartUuid(f.Child));
            JsonNode copy = JsonNode.Parse(child.ToJsonString());
            copy["name"] = "Child.001";
            copy["translation"] = new JsonArray(2f, 0f, 0f);
            g.Array("nodes").Add(copy);
            g.Json["nodes"][g.ObjectNode]["children"].AsArray().Add(g.Json["nodes"].AsArray().Count - 1);
        });
        RestoreResult r = f.Restore();

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(PartOutcome.Unchanged, RestoreTests.Of(r, f.Child).Outcome);
        PartReport add = Added(r);
        Assert.Equal(3, add.LinkNum);
        Assert.Equal("Child.001", add.Name);
        Assert.Equal(6, add.Faces);
        Dictionary<string, byte[]> after = RestoreTests.Entries(f.Out());
        XElement part = RestoreTests.Part(after[ObjectKey(after, f.Root)], add.PartId);
        Assert.Empty(part.Element("TaskInventory")?.Elements() ?? Enumerable.Empty<XElement>());
        Assert.Equal(2f, F(part, "Scale", "X"), 3);
    }

    // ------------------------------------------------------------------ moved, turned and resized parts

    private static void Set(RestoreTests.Fixture f, KitPart root, KitPart part, string property, params float[] values) =>
        Edit(f, root, g => g.Part(RestoreTests.PartUuid(part))[property] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()));

    private static void AssertKeptShape(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after, KitPart root, KitPart part)
    {
        string key = ObjectKey(before, root);
        Assert.Equal(RestoreTests.Part(before[key], part.Id).Element("Shape").ToString(), RestoreTests.Part(after[key], part.Id).Element("Shape").ToString());
        Assert.DoesNotContain(after.Keys, k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal));
    }

    [Fact]
    public void AMovedChildStaysAPrimAndGetsItsNewPosition()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        Set(f, f.Root, f.Child, "translation", 1f, 0f, 0f);
        RestoreResult r = f.Restore();

        Assert.Equal(PartOutcome.Moved, RestoreTests.Of(r, f.Child).Outcome);
        Assert.Equal(PartOutcome.Unchanged, RestoreTests.Of(r, f.Root).Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> before = RestoreTests.Entries(f.Archive), after = RestoreTests.Entries(f.Out());
        XElement child = RestoreTests.Part(after[ObjectKey(after, f.Root)], f.Child.Id);
        Assert.Equal(1f, F(child, "OffsetPosition", "X"), 4);
        Assert.Equal(0f, F(child, "OffsetPosition", "Y"), 4);
        AssertKeptShape(before, after, f.Root, f.Child);
        Assert.Equal(RestoreTests.Part(before[ObjectKey(before, f.Root)], f.Child.Id).Element("Scale").ToString(), child.Element("Scale").ToString());
        AssertSameBytes(before, after, ObjectKey(before, f.Root));
    }

    [Fact]
    public void ATurnedChildStaysAPrimAndGetsItsNewRotation()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        float h = MathF.Sqrt(0.5f);
        Set(f, f.Root, f.Child, "rotation", 0f, h, 0f, h);   // a quarter turn about glTF's up axis, which is the viewer's Z
        RestoreResult r = f.Restore();

        Assert.Equal(PartOutcome.Moved, RestoreTests.Of(r, f.Child).Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> before = RestoreTests.Entries(f.Archive), after = RestoreTests.Entries(f.Out());
        XElement child = RestoreTests.Part(after[ObjectKey(after, f.Root)], f.Child.Id);
        Assert.Equal(0f, F(child, "RotationOffset", "X"), 4);
        Assert.Equal(0f, F(child, "RotationOffset", "Y"), 4);
        Assert.Equal(h, F(child, "RotationOffset", "Z"), 4);
        Assert.Equal(h, F(child, "RotationOffset", "W"), 4);
        AssertKeptShape(before, after, f.Root, f.Child);
    }

    [Fact]
    public void AResizedChildKeepsItsShapeAndGetsItsNewSize()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        Set(f, f.Root, f.Child, "scale", 2f, 1f, 3f);   // glTF x, y, z are the viewer's x, z, y: sizes 2 x 2 x 2 become 4 x 6 x 2
        RestoreResult r = f.Restore();

        Assert.Equal(PartOutcome.Moved, RestoreTests.Of(r, f.Child).Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> before = RestoreTests.Entries(f.Archive), after = RestoreTests.Entries(f.Out());
        XElement child = RestoreTests.Part(after[ObjectKey(after, f.Root)], f.Child.Id);
        Assert.Equal(4f, F(child, "Scale", "X"), 4);
        Assert.Equal(6f, F(child, "Scale", "Y"), 4);
        Assert.Equal(2f, F(child, "Scale", "Z"), 4);
        AssertKeptShape(before, after, f.Root, f.Child);
    }

    [Fact]
    public void MovingTheRootPartWithItsChildrenFollowingMovesTheWholeLinkset()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        float h = MathF.Sqrt(0.5f);
        // Blender parenting: the child follows the root, so the whole linkset moves and turns
        Edit(f, f.Root, g =>
        {
            g.Reparent(RestoreTests.PartUuid(f.Child), RestoreTests.PartUuid(f.Root));
            g.Part(RestoreTests.PartUuid(f.Root))["translation"] = new JsonArray(0f, 1f, 0f);   // one metre up
            g.Part(RestoreTests.PartUuid(f.Root))["rotation"] = new JsonArray(0f, h, 0f, h);
        });
        RestoreResult r = f.Restore();

        Assert.Equal(PartOutcome.Moved, RestoreTests.Of(r, f.Root).Outcome);
        Assert.Equal(PartOutcome.Unchanged, RestoreTests.Of(r, f.Child).Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> before = RestoreTests.Entries(f.Archive), after = RestoreTests.Entries(f.Out());
        string key = ObjectKey(before, f.Root);
        foreach (KitPart p in new[] { f.Root, f.Child })
        {
            XElement part = RestoreTests.Part(after[key], p.Id);
            Assert.Equal(1f, F(part, "GroupPosition", "X"), 4);
            Assert.Equal(1f, F(part, "GroupPosition", "Y"), 4);
            Assert.Equal(2f, F(part, "GroupPosition", "Z"), 4);
        }
        XElement root = RestoreTests.Part(after[key], f.Root.Id);
        Assert.Equal(h, F(root, "RotationOffset", "Z"), 4);
        Assert.Equal(h, F(root, "RotationOffset", "W"), 4);
        // the child keeps its place inside the linkset
        Assert.Equal(RestoreTests.Part(before[key], f.Child.Id).Element("OffsetPosition").ToString(), RestoreTests.Part(after[key], f.Child.Id).Element("OffsetPosition").ToString());
        AssertSameBytes(before, after, key);
    }

    [Fact]
    public void APartMovedAndReshapedIsRebuiltInItsNewPlace()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        Edit(f, f.Root, g => g.Part(RestoreTests.PartUuid(f.Child))["translation"] = new JsonArray(1f, 0f, 0f));
        string path = f.GlbOf(f.Root);
        File.WriteAllBytes(path, GlbPatch.Edit(File.ReadAllBytes(path), (j, bin) => GlbPatch.MoveFace(j, bin, RestoreTests.PartUuid(f.Child), 0, 0.5f)));
        RestoreResult r = f.Restore();

        PartReport p = RestoreTests.Of(r, f.Child);
        Assert.Equal(PartOutcome.Rebuilt, p.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> after = RestoreTests.Entries(f.Out());
        XElement child = RestoreTests.Part(after[ObjectKey(after, f.Root)], f.Child.Id);
        Assert.Equal(1f, F(child, "OffsetPosition", "X"), 4);
        Assert.Equal("true", child.Element("Shape").Element("SculptEntry").Value);
    }

    [Fact]
    public void AMovedPhysicalPartStaysAPrim()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture(RestoreTests.Benign, "Physics");
        Set(f, f.Root, f.Child, "translation", 1f, 0f, 0f);
        RestoreResult r = f.Restore();
        Assert.Equal(PartOutcome.Moved, RestoreTests.Of(r, f.Child).Outcome);
        Assert.Equal(0, r.ExitCode);
    }

    // ------------------------------------------------------------------ the work folder

    [Fact]
    public void TheWorkFolderIsRemovedWhenTheRunMadeItUnlessKept()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        RestoreResult made = new ArchiveRestorer(new RestoreOptions()).Restore(f.Archive, f.GlbDir, f.Out("a.oar"));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), made.WorkFolder, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(made.WorkFolder));
        Assert.True(made.WorkFolderRemoved);

        RestoreResult kept = new ArchiveRestorer(new RestoreOptions { KeepWork = true }).Restore(f.Archive, f.GlbDir, f.Out("b.oar"));
        Assert.True(Directory.Exists(kept.WorkFolder));
        Assert.False(kept.WorkFolderRemoved);

        // a folder that held other files is never emptied, kept or not
        string mine = Path.Combine(f.Dir, "mine");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "notes.txt"), "mine");
        RestoreResult shared = new ArchiveRestorer(new RestoreOptions { WorkFolder = mine }).Restore(f.Archive, f.GlbDir, f.Out("c.oar"));
        Assert.True(File.Exists(Path.Combine(mine, "notes.txt")));
        Assert.False(shared.WorkFolderRemoved);
    }

    // ------------------------------------------------------------------ a face over the vertex limit

    [Fact]
    public void AFaceIsSplitWhereTheViewersUploaderSplitsItAndNoPartIsLost()
    {
        // 30 000 triangles that share no vertex: 90 000 vertices; the uploader closes a face when it holds 65 532 at the end of a triangle
        int triangles = 30000;
        PrimFace face = new PrimFace { Number = 0, Kind = PrimFaceKind.Outer, MesherNumber = 0, Positions = new float[triangles * 9], Normals = new float[triangles * 9], Uvs = new float[triangles * 6] };
        for (int i = 0; i < triangles * 3; i++)
        {
            face.Positions[i * 3] = i * 0.001f; face.Positions[i * 3 + 1] = (i % 7) * 0.01f; face.Positions[i * 3 + 2] = (i % 13) * 0.01f;
            face.Normals[i * 3 + 1] = 1f;
            face.Uvs[i * 2] = (i % 100) / 100f; face.Uvs[i * 2 + 1] = (i / 100 % 100) / 100f;
        }
        List<EncoderFace> faces = FaceBuilder.SlotsSplit(new[] { face }, 3, out List<int> source);

        Assert.Equal(4, faces.Count);   // slot 0 in two parts, slots 1 and 2 empty
        Assert.Equal(new[] { 0, 0, 1, 2 }, source);
        Assert.Equal(FaceBuilder.SplitAtVertices, faces[0].VertexCount);
        Assert.Equal(90000 - FaceBuilder.SplitAtVertices, faces[1].VertexCount);
        Assert.Equal(triangles, faces[0].TriangleCount + faces[1].TriangleCount);
        Assert.All(faces, x => Assert.True(x.VertexCount < 65535));
        Assert.True(faces[2].IsEmpty && faces[3].IsEmpty);

        // below the limit nothing is split
        List<EncoderFace> small = FaceBuilder.SlotsSplit(new[] { new PrimFace { Number = 0, Kind = PrimFaceKind.Outer, MesherNumber = 0, Positions = new float[9], Normals = new float[9], Uvs = new float[6] } }, 1, out List<int> one);
        Assert.Single(small);
        Assert.Equal(new[] { 0 }, one);
    }

    [Fact]
    public void AnEditedFaceOverTheVertexLimitIsSplitInsteadOfRefusedAndItsTextureEntryFollows()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        var grid = GlbEdit.Grid(300);   // 90 601 vertices, 180 000 triangles, all in face 0 of the child
        Edit(f, f.Root, g =>
        {
            int mesh = g.AddMesh("dense", grid.p, grid.n, grid.t, grid.i, -1, 0);
            g.Part(RestoreTests.PartUuid(f.Child))["mesh"] = mesh;
        });
        Stopwatch clock = Stopwatch.StartNew();
        RestoreResult r = f.Restore();
        clock.Stop();

        PartReport p = RestoreTests.Of(r, f.Child);
        Assert.Equal(PartOutcome.Rebuilt, p.Outcome);
        Assert.Equal(7, p.Faces);   // the child had six faces; face 0 is now two
        Assert.Equal(180000, p.Triangles);
        Assert.Contains(p.Notes, n => n.Contains("split"));
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "took " + clock.Elapsed);

        Dictionary<string, byte[]> after = RestoreTests.Entries(f.Out());
        MeshAssetModel mesh2 = MeshCodec.Decode(after[after.Keys.Single(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal))]);
        Assert.Equal(7, mesh2.High.Faces.Count);
        Assert.All(mesh2.High.Faces, x => Assert.True(x.NoGeometry || x.Positions.Length < 65535));
        XElement child = RestoreTests.Part(after[ObjectKey(after, f.Root)], f.Child.Id);
        byte[] te = Convert.FromBase64String(child.Element("Shape").Element("TextureEntry").Value);
        Primitive.TextureEntry entry = new Primitive.TextureEntry(te, 0, te.Length);
        // faces 0 and 1 are the two halves of the old face 0; the old face 1 is now face 2
        Assert.Equal(RestoreTests.Fixture.FacePicture0, entry.FaceTextures[0].TextureID.Guid);
        Assert.Equal(RestoreTests.Fixture.FacePicture0, entry.FaceTextures[1].TextureID.Guid);
        Assert.Equal(RestoreTests.Fixture.FacePicture1, entry.FaceTextures[2].TextureID.Guid);
        Assert.Equal(0.5f, entry.FaceTextures[1].RGBA.G, 2);
    }
}
