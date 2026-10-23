/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>One linkset to one glTF binary and one manifest, built from object XML written in the tests.</summary>
public class ExporterTests
{
    private static PartSpec Part(string name, int link, Vector3? offset = null, Quaternion? rot = null, Vector3? scale = null, string shape = null)
    {
        PartSpec p = new PartSpec { Name = name, Link = link, Description = "description of " + name };
        if (offset.HasValue) p.Offset = offset.Value;
        if (rot.HasValue) p.Rotation = rot.Value;
        if (scale.HasValue) p.Scale = scale.Value;
        if (shape != null) p.Shape = shape;
        return p;
    }

    private static BuiltBuild Build(ParsedGroup g) => LinksetExporter.Build(g, "test.oar", "objects/test.xml", "test.glb");

    private static string[] NamesInOrder(Glb g) => g.PartNodes.Select(n => n.GetProperty("name").GetString()).ToArray();

    [Fact]
    public void PartsFollowLinkNumberNotFileOrder()
    {
        // file order 1, 4, 2, 5, 3
        ParsedGroup g = Group(Part("one", 1), Part("four", 4), Part("two", 2), Part("five", 5), Part("three", 3));
        Glb glb = ReadGlb(Build(g).Glb);
        Assert.Equal(new[] { "one", "two", "three", "four", "five" }, NamesInOrder(glb));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, glb.PartNodes.Select(n => n.GetProperty("extras").GetProperty("linkNumber").GetInt32()).ToArray());
    }

    [Fact]
    public void DuplicateNamesAreKeptExactlyAndTheLinkNumberTellsThemApart()
    {
        ParsedGroup g = Group(Part("Object", 1), Part("Object", 3), Part("Object", 2));
        BuiltBuild b = Build(g);
        Glb glb = ReadGlb(b.Glb);
        Assert.Equal(new[] { "Object", "Object", "Object" }, NamesInOrder(glb));
        Assert.Equal(new[] { 1, 2, 3 }, b.Manifest.Parts.Select(p => p.LinkNumber).ToArray());
    }

    [Fact]
    public void ANodeCarriesLinkNumberUuidDescriptionCreatorAndMasks()
    {
        PartSpec root = Part("Root", 1);
        root.Creator = Bob;
        root.Owner = Bob;
        root.BaseMask = 0x7fffffff; root.OwnerMask = 0x7ffff000; root.GroupMask = 0x8000; root.EveryoneMask = 0x4000; root.NextOwnerMask = 0x82000;
        PartSpec child = Part("Child", 2);
        Glb glb = ReadGlb(Build(Group(root, child)).Glb);
        JsonElement e = glb.PartNodes.First().GetProperty("extras");
        Assert.Equal(1, e.GetProperty("linkNumber").GetInt32());
        Assert.Equal(root.Id.ToString(), e.GetProperty("partUuid").GetString());
        Assert.Equal("description of Root", e.GetProperty("description").GetString());
        Assert.Equal(Bob.ToString(), e.GetProperty("creatorId").GetString());
        Assert.Equal("Root", e.GetProperty("partName").GetString());
        JsonElement perms = e.GetProperty("permissions");
        Assert.Equal(0x7fffffffL, perms.GetProperty("base").GetInt64());
        Assert.Equal(0x7ffff000L, perms.GetProperty("owner").GetInt64());
        Assert.Equal(0x8000L, perms.GetProperty("group").GetInt64());
        Assert.Equal(0x4000L, perms.GetProperty("everyone").GetInt64());
        Assert.Equal(0x82000L, perms.GetProperty("nextOwner").GetInt64());
    }

    [Fact]
    public void TextureCoordinatesPutTheTopOfTheImageAtTheTopOfASide()
    {
        // glTF puts v = 0 at the top row of an image: the top edge of a box side is at v = 0, the bottom edge at v = 1
        Glb glb = ReadGlb(Build(Group(Part("Box", 0))).Glb);
        JsonElement mesh = glb.Root.GetProperty("meshes")[0];
        int sides = 0;
        foreach (JsonElement prim in mesh.GetProperty("primitives").EnumerateArray())
        {
            int face = prim.GetProperty("extras").GetProperty("primFace").GetInt32();
            if (face < 1 || face > 4)
                continue;
            float[] pos = glb.Floats(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
            float[] uv = glb.Floats(prim.GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32());
            for (int i = 0; i < pos.Length / 3; i++)
            {
                float height = pos[i * 3 + 1]; // glTF y is the prim's z
                Assert.Equal(height > 0 ? 0f : 1f, uv[i * 2 + 1], 3);
            }
            sides++;
        }
        Assert.Equal(4, sides);
    }

    [Fact]
    public void TheBuildNodeStaysInTheRootPartFrameUnlessWorldIsAsked()
    {
        // the test objects sit at (10, 20, 30) in the region
        PartSpec root = Part("Root", 1, rot: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
        ParsedGroup g = Group(root, Part("Child", 2, offset: new Vector3(1, 0, 0)));

        JsonElement flat = ReadGlb(LinksetExporter.Build(g, "test.oar", "objects/test.xml", "test.glb").Glb).ObjectNode;
        Assert.False(flat.TryGetProperty("translation", out _));
        Assert.False(flat.TryGetProperty("rotation", out _));
        Assert.Equal("root-part", flat.GetProperty("extras").GetProperty("placement").GetString());
        Assert.Equal(10f, flat.GetProperty("extras").GetProperty("regionPosition")[0].GetSingle());

        JsonElement world = ReadGlb(LinksetExporter.Build(g, "test.oar", "objects/test.xml", "test.glb", world: true).Glb).ObjectNode;
        float[] t = world.GetProperty("translation").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(new[] { 10f, 30f, -20f }, t); // viewer (x, y, z) is glTF (x, z, -y)
        float[] r = world.GetProperty("rotation").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(0f, r[0], 4);
        Assert.Equal(MathF.Sqrt(0.5f), r[1], 4); // a quarter turn about the viewer's z is a quarter turn about glTF's y
        Assert.Equal(0f, r[2], 4);
        Assert.Equal(MathF.Sqrt(0.5f), r[3], 4);
        Assert.Equal("world", world.GetProperty("extras").GetProperty("placement").GetString());
        // the extras keep the region position either way
        Assert.Equal(30f, world.GetProperty("extras").GetProperty("regionPosition")[2].GetSingle());
    }

    [Fact]
    public void ASinglePartKeepsLinkNumberZero()
    {
        BuiltBuild b = Build(Group(Part("Lonely", 0)));
        Assert.Equal(0, b.Manifest.Parts.Single().LinkNumber);
        Assert.Equal(0, ReadGlb(b.Glb).PartNodes.Single().GetProperty("extras").GetProperty("linkNumber").GetInt32());
    }

    [Fact]
    public void ChildrenAreRelativeToTheRootAndTheAxesAreTurnedToYUp()
    {
        float s = MathF.Sqrt(0.5f);
        PartSpec root = Part("Root", 1);
        PartSpec child = Part("Child", 2, offset: new Vector3(1, 2, 3), rot: new Quaternion(0, 0, s, s));
        Glb glb = ReadGlb(Build(Group(root, child)).Glb);
        JsonElement[] nodes = glb.PartNodes.ToArray();
        Assert.False(nodes[0].TryGetProperty("translation", out _), "the root part sits at the object origin");
        float[] t = nodes[1].GetProperty("translation").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        Assert.Equal(new[] { 1f, 3f, -2f }, t);
        float[] r = nodes[1].GetProperty("rotation").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        Assert.Equal(0f, r[0], 5);
        Assert.Equal(s, r[1], 5);
        Assert.Equal(0f, r[2], 5);
        Assert.Equal(s, r[3], 5);
    }

    [Fact]
    public void ThePartSizeIsBakedIntoTheVertices()
    {
        PartSpec root = Part("Root", 1, scale: new Vector3(2, 4, 6));
        Glb glb = ReadGlb(Build(Group(root, Part("Other", 2))).Glb);
        // the bounds of all position accessors together (each face has its own)
        JsonElement[] accs = glb.Root.GetProperty("accessors").EnumerateArray().Where(a => a.TryGetProperty("min", out _)).ToArray();
        float[] max = Enumerable.Range(0, 3).Select(c => accs.Max(a => a.GetProperty("max")[c].GetSingle())).ToArray();
        float[] min = Enumerable.Range(0, 3).Select(c => accs.Min(a => a.GetProperty("min")[c].GetSingle())).ToArray();
        // viewer (x, y, z) = (2, 4, 6) wide becomes glTF (x, z, -y)
        Assert.Equal(new[] { 1f, 3f, 2f }, max.Select(v => MathF.Round(v, 3)).ToArray());
        Assert.Equal(new[] { -1f, -3f, -2f }, min.Select(v => MathF.Round(v, 3)).ToArray());
    }

    [Fact]
    public void EveryPrimFaceIsItsOwnPrimitiveWithItsFaceNumber()
    {
        Glb glb = ReadGlb(Build(Group(Part("Box", 0))).Glb);
        JsonElement mesh = glb.Root.GetProperty("meshes")[0];
        JsonElement[] prims = mesh.GetProperty("primitives").EnumerateArray().ToArray();
        Assert.Equal(6, prims.Length);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, prims.Select(p => p.GetProperty("extras").GetProperty("primFace").GetInt32()).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, mesh.GetProperty("extras").GetProperty("primFaces").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        foreach (JsonElement p in prims)
        {
            JsonElement a = p.GetProperty("attributes");
            Assert.True(a.TryGetProperty("POSITION", out _) && a.TryGetProperty("NORMAL", out _) && a.TryGetProperty("TEXCOORD_0", out _));
            Assert.Equal(4, p.GetProperty("mode").GetInt32());
        }
    }

    [Fact]
    public void EveryVertexCarriesItsFaceNumberSoAToolThatMergesPrimitivesKeepsThem()
    {
        Glb glb = ReadGlb(Build(Group(Part("Box", 0))).Glb);
        foreach (JsonElement prim in glb.Root.GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray())
        {
            int face = prim.GetProperty("extras").GetProperty("primFace").GetInt32();
            float[] values = glb.Floats(prim.GetProperty("attributes").GetProperty("_PRIM_FACE").GetInt32());
            float[] positions = glb.Floats(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
            Assert.Equal(positions.Length / 3, values.Length);
            Assert.All(values, v => Assert.Equal((float)face, v));
        }
    }

    [Fact]
    public void TheObjectNodeIsNamedApartFromTheRootPart()
    {
        Glb glb = ReadGlb(Build(Group(Part("Root", 1), Part("Child", 2))).Glb);
        Assert.Equal("Root [build]", glb.ObjectNode.GetProperty("name").GetString());
        Assert.Equal("Root", glb.PartNodes.First().GetProperty("name").GetString());
        JsonElement o = glb.ObjectNode.GetProperty("extras");
        Assert.Equal("object", o.GetProperty("kind").GetString());
        Assert.Equal("Root", o.GetProperty("name").GetString());
        Assert.Equal(2, o.GetProperty("partCount").GetInt32());
        Assert.Equal(new[] { 10f, 20f, 30f }, o.GetProperty("regionPosition").EnumerateArray().Select(x => x.GetSingle()).ToArray());
    }

    [Fact]
    public void MaterialsAreNamedByTheFacesTextureAndKeepTheColour()
    {
        UUID wood = UUID.Random(), stone = UUID.Random();
        PartSpec p = Part("Box", 0);
        p.TextureEntryBase64 = TextureEntryBase64(wood, (2, stone, new Color4(1f, 0.5f, 0.25f, 0.5f)));
        Glb glb = ReadGlb(Build(Group(p)).Glb);
        JsonElement[] prims = glb.Root.GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray().ToArray();
        JsonElement[] mats = glb.Root.GetProperty("materials").EnumerateArray().ToArray();
        string MaterialOf(int face) => mats[prims[face].GetProperty("material").GetInt32()].GetProperty("name").GetString();
        Assert.Equal(wood.ToString(), MaterialOf(0));
        Assert.Equal(wood.ToString(), MaterialOf(1));
        Assert.Equal(stone.ToString(), MaterialOf(2));
        JsonElement m2 = mats[prims[2].GetProperty("material").GetInt32()];
        Assert.Equal("BLEND", m2.GetProperty("alphaMode").GetString());
        // a texture entry stores colour in one byte per channel
        Assert.Equal(0.5f, m2.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")[3].GetSingle(), 2);
        Assert.Equal(stone.ToString(), m2.GetProperty("extras").GetProperty("textureId").GetString());
        // faces that share a texture and colour share a material
        Assert.Equal(prims[0].GetProperty("material").GetInt32(), prims[1].GetProperty("material").GetInt32());
    }

    [Theory]
    [InlineData(9, 5, "7e0f0000-0000-4000-8000-00000000aaaa", "mesh", true)]
    [InlineData(9, 2, "7e0f0000-0000-4000-8000-00000000bbbb", "sculpt", true)]
    [InlineData(255, 0, null, "tree", false)]
    [InlineData(111, 0, null, "tree", false)]
    [InlineData(95, 0, null, "grass", false)]
    public void MeshSculptTreeAndGrassBecomeNamedEmptyNodesInTheRightPlace(int pcode, int sculptType, string sculptTexture, string kind, bool sculptEntry)
    {
        PartSpec root = Part("Root", 1);
        PartSpec special = Part("Special " + kind, 2, offset: new Vector3(4, 5, 6), shape: Box(pcode: pcode, sculptType: sculptType, sculptTexture: sculptTexture, sculptEntry: sculptEntry, state: 3));
        BuiltBuild b = Build(Group(root, special));
        Glb glb = ReadGlb(b.Glb);
        JsonElement node = glb.PartNodes.ElementAt(1);
        Assert.Equal("Special " + kind, node.GetProperty("name").GetString());
        Assert.False(node.TryGetProperty("mesh", out _), "no geometry yet");
        Assert.Equal(new[] { 4f, 6f, -5f }, node.GetProperty("translation").EnumerateArray().Select(x => x.GetSingle()).ToArray());
        JsonElement e = node.GetProperty("extras");
        Assert.Equal(kind, e.GetProperty("kind").GetString());
        Assert.Equal("pending", e.GetProperty("geometry").GetString());
        Assert.False(string.IsNullOrEmpty(e.GetProperty("geometryPending").GetString()));
        Assert.Equal(2, e.GetProperty("linkNumber").GetInt32());

        ManifestPart mp = b.Manifest.Parts[1];
        Assert.Equal(ExportStatus.Placeheld, mp.Status);
        Assert.Equal(kind, mp.Kind);
        Assert.Equal(1, b.Manifest.Placeheld);
        Assert.Equal(1, b.Manifest.Exported);
        if (sculptTexture != null)
            Assert.Equal(sculptTexture, e.GetProperty("sculptTexture").GetString());
        if (kind == "tree")
            Assert.Equal(3, e.GetProperty("species").GetInt32());
    }

    [Fact]
    public void TheManifestListsEveryPartWithContents()
    {
        PartSpec root = Part("Door", 1);
        root.Inventory = "<TaskInventory>" + TaskItem("open script", 10, 10) + TaskItem("how to use", 7, 7) + "</TaskInventory>";
        PartSpec child = Part("Handle", 2);
        child.Creator = Alice; child.Owner = Alice;
        BuiltBuild b = Build(Group(root, child));
        BuildManifest m = b.Manifest;
        Assert.Equal("test.oar", m.Archive);
        Assert.Equal(2, m.PartCount);
        Assert.Equal(2, m.Exported);
        ManifestPart door = m.Parts[0];
        Assert.Equal(new[] { "open script", "how to use" }, door.Contents.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "LSLText", "Notecard" }, door.Contents.Select(c => c.Type).ToArray());
        Assert.Equal(new[] { "LSL", "Notecard" }, door.Contents.Select(c => c.InventoryType).ToArray());
        Assert.Equal("description of Door", door.Description);
        Assert.Equal(Alice.ToString(), door.CreatorId);
        Assert.Equal(root.Id.ToString(), door.PartUuid);
        Assert.Empty(m.Parts[1].Contents);
    }

    [Fact]
    public void NamesWithControlCharactersAndBrokenSurrogatesStillMakeValidFiles()
    {
        // a right-to-left mark is kept; the control character is not
        PartSpec p = Part("bad\u0001name‎tail", 0);
        p.Description = "line\u0007bell";
        BuiltBuild b = Build(Group(p));
        Glb glb = ReadGlb(b.Glb);
        Assert.Equal("badname‎tail", glb.PartNodes.Single().GetProperty("name").GetString());
        Assert.Equal("badname‎tail", b.Manifest.Parts[0].Name);
        Assert.Equal("linebell", b.Manifest.Parts[0].Description);
        JsonSerializerOptionsRoundTrip(b.Manifest);
    }

    [Fact]
    public void AnUnpairedSurrogateIsDroppedAndAPairIsKept()
    {
        Assert.Equal("ab", Text.Clean("a\ud800b"));
        Assert.Equal("ab", Text.Clean("a\udc00b"));
        Assert.Equal("a😀b", Text.Clean("a😀b"));
        Assert.Equal("a😀", Text.Clean("a😀\ud83d"));
        Assert.Equal("", Text.Clean(null));
    }

    private static void JsonSerializerOptionsRoundTrip(BuildManifest m)
    {
        string json = JsonSerializer.Serialize(m, ArchiveExporter.JsonOptions);
        using JsonDocument d = JsonDocument.Parse(json);
        Assert.Equal(m.PartCount, d.RootElement.GetProperty("partCount").GetInt32());
    }

    [Fact]
    public void ARootThatMakesNoGeometryStillLeavesTheOtherPartsInPlace()
    {
        PartSpec root = Part("Root", 1, shape: Box(pcode: 255));
        PartSpec child = Part("Child", 2, offset: new Vector3(1, 0, 0));
        BuiltBuild b = Build(Group(root, child));
        Glb glb = ReadGlb(b.Glb);
        Assert.Equal(new[] { "Root", "Child" }, NamesInOrder(glb));
        Assert.Equal(new[] { ExportStatus.Placeheld, ExportStatus.Exported }, b.Manifest.Parts.Select(p => p.Status).ToArray());
    }

    // ------------------------------------------------------------ the export-permission rule

    private const uint Export = (uint)PermissionMask.Export;

    private static PartSpec Held(uint baseMask, uint ownerMask, uint everyoneMask)
    {
        PartSpec p = Part("Held", 0);
        p.Creator = Alice; p.Owner = Bob;
        p.BaseMask = baseMask; p.OwnerMask = ownerMask; p.EveryoneMask = everyoneMask;
        return p;
    }

    [Theory]
    [InlineData(Export | 0x7fff, 0x7fff, 0u, false)]         // base has the right, nobody grants it: refused
    [InlineData(Export | 0x7fff, 0x7fff | Export, 0u, true)] // owner mask grants it
    [InlineData(Export | 0x7fff, 0x7fff, Export, true)]      // everyone mask keeps the creator's choice
    [InlineData(0x7fffu, 0x7fffu, 0u, true)]                 // no export bit in base: an archive from before the bit; not counted as refused
    public void OnlyAnExplicitRefusalOfAHeldPartBlocksIt(uint baseMask, uint ownerMask, uint everyoneMask, bool allowed)
    {
        BuiltBuild b = Build(Group(Held(baseMask, ownerMask, everyoneMask)));
        ManifestPart mp = b.Manifest.Parts.Single();
        Assert.Equal(allowed ? ExportStatus.Exported : ExportStatus.Skipped, mp.Status);
        if (allowed)
            Assert.NotNull(b.Glb);
        else
        {
            Assert.Contains("creator did not allow export", mp.Reason);
            Assert.Null(b.Glb);
            Assert.Null(b.Manifest.File);
            Assert.Null(mp.Contents);
            Assert.Equal(1, b.Manifest.Skipped);
        }
    }

    [Fact]
    public void ThePartHeldByItsCreatorIsAlwaysAllowed()
    {
        PartSpec p = Held(Export | 0x7fff, 0x7fff, 0u);
        p.Owner = p.Creator;
        Assert.Equal(ExportStatus.Exported, Build(Group(p)).Manifest.Parts.Single().Status);
    }

    [Fact]
    public void ASkippedPartIsListedWithItsLinkNumberAndLeavesTheRestInTheFile()
    {
        PartSpec root = Part("Root", 1);
        PartSpec refused = Held(Export | 0x7fff, 0x7fff, 0u);
        refused.Name = "Refused"; refused.Link = 2;
        PartSpec ok = Part("Third", 3);
        BuiltBuild b = Build(Group(root, ok, refused));
        Assert.Equal(new[] { "Root", "Third" }, NamesInOrder(ReadGlb(b.Glb)));
        Assert.Equal(new[] { 1, 2, 3 }, b.Manifest.Parts.Select(p => p.LinkNumber).ToArray());
        Assert.Equal(new[] { ExportStatus.Exported, ExportStatus.Skipped, ExportStatus.Exported }, b.Manifest.Parts.Select(p => p.Status).ToArray());
        Assert.Equal(3, b.Manifest.PartCount);
        Assert.Equal(2, b.Manifest.Exported);
        Assert.Equal(1, b.Manifest.Skipped);
    }
}
