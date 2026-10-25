/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.GltfExport;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;

namespace OpenSim.Archive.Restore.Tests;

/// <summary>One linkset of four parts (a root and three children with distinct offsets), made with or without a script, exported in either placement.</summary>
internal sealed class Lset
{
    public const string Creator = "c0ffee00-0000-4000-8000-000000000001", Owner = "0a11ce00-0000-4000-8000-000000000002";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public readonly string Dir = Path.Combine(Path.GetTempPath(), "archive-restore-tests", Guid.NewGuid().ToString("N"));
    public readonly KitPart Root, A, B, C;
    public readonly string Archive, GlbDir;
    public readonly bool World;

    /// <param name="scriptIn">Name of the part ("Root", "A", "B" or "C") that holds a script, or null for a linkset with no script.</param>
    public Lset(bool world = false, string scriptIn = null, string rootRotation = "0,0,0,1", int childPhysics = -1)
    {
        World = world;
        Directory.CreateDirectory(Dir);
        string te = RestoreTests.Fixture.ValidTextureEntry();
        Root = new KitPart("Root", 1) { Scale = "1,2,3", Creator = Creator, Owner = Owner, Rotation = rootRotation, TextureEntry = te };
        A = new KitPart("A", 2) { Scale = "1,1,1", Offset = "2,0,0", PhysicsShapeType = childPhysics, TextureEntry = te, Creator = Creator, Owner = Owner };
        B = new KitPart("B", 3) { Scale = "1,1,1", Offset = "0,3,0", PhysicsShapeType = childPhysics, TextureEntry = te, Creator = Creator, Owner = Owner };
        C = new KitPart("C", 4) { Scale = "1,1,1", Offset = "0,0,4", PhysicsShapeType = childPhysics, TextureEntry = te, Creator = Creator, Owner = Owner };
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        if (scriptIn != null)
        {
            (KitItem item, string entry, byte[] data) s = EligibilityKit.Script("a script", RestoreTests.Benign);
            Named(scriptIn).Items.Add(s.item);
            assets[s.entry] = s.data;
        }
        Archive = EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { Root, A, B, C } }, assets);
        GlbDir = Path.Combine(Dir, "glb");
        ExportSummary export = ArchiveExporter.Export(Archive, new ExportOptions { OutputDirectory = GlbDir, ImageCheck = () => null, World = world });
        Assert.Equal(0, export.ExitCode);
    }

    public KitPart Named(string name) => name switch { "Root" => Root, "A" => A, "B" => B, _ => C };
    public KitPart[] Parts => new[] { Root, A, B, C };

    public string GlbPath => Directory.GetFiles(GlbDir, "*.glb", SearchOption.AllDirectories).Single();

    public void Edit(Action<GlbEdit> edit) => File.WriteAllBytes(GlbPath, GlbEdit.Apply(File.ReadAllBytes(GlbPath), edit));

    public string Out(string name = "out.oar") => Path.Combine(Dir, name);

    public RestoreResult Restore(RestoreOptions options = null, string outName = "out.oar")
    {
        options ??= new RestoreOptions();
        options.WorkFolder ??= Path.Combine(Dir, "work");
        return new ArchiveRestorer(options).Restore(Archive, GlbDir, Out(outName));
    }

    public Dictionary<string, byte[]> Before => RestoreTests.Entries(Archive);
    public Dictionary<string, byte[]> After => RestoreTests.Entries(Out());
    public static string ObjectKey(Dictionary<string, byte[]> entries) => entries.Keys.Single(k => k.StartsWith("objects/", StringComparison.Ordinal));
    public XElement PartIn(Dictionary<string, byte[]> entries, Guid id) => RestoreTests.Part(entries[ObjectKey(entries)], id);
    public XElement PartIn(Dictionary<string, byte[]> entries, KitPart p) => PartIn(entries, p.Id);

    public static float F(XElement e, params string[] path)
    {
        foreach (string name in path)
            e = e.Element(name);
        return float.Parse(e.Value, Inv);
    }

    public static float[] Vec(XElement part, string element) => new[] { F(part, element, "X"), F(part, element, "Y"), F(part, element, "Z") };

    public static byte[] Png(byte r, byte g, byte b)
    {
        using SKBitmap bitmap = new SKBitmap(4, 4);
        bitmap.Erase(new SKColor(r, g, b, 255));
        Assert.True(SkiaImageUtils.TryEncodeToPng(bitmap, out byte[] png));
        return png;
    }
}

/// <summary>Credit for added parts and new pictures, and the warning for a texture transform.</summary>
public class RestoreCreatorTests
{
    private static string CreatorOf(XElement part) => part.Element("CreatorID").Element("UUID").Value;

    [Fact]
    public void AnAddedPartIsCreditedToTheRootsOwnerNotItsCreator()
    {
        Lset f = new Lset();
        f.Edit(g => g.Cube("New cube", 0.5f, translation: new[] { 3f, 0f, 0f }));
        RestoreResult r = f.Restore();

        Assert.Equal(0, r.ExitCode);
        PartReport add = r.Parts.Single(p => p.Outcome == PartOutcome.Added);
        Dictionary<string, byte[]> after = f.After;
        XElement part = f.PartIn(after, add.PartId), root = f.PartIn(after, f.Root);
        Assert.Equal(Lset.Owner, CreatorOf(part));
        Assert.NotEqual(CreatorOf(root), CreatorOf(part));
        // owner, group and permissions stay the root's
        Assert.Equal(root.Element("OwnerID").Element("UUID").Value, part.Element("OwnerID").Element("UUID").Value);
        Assert.Equal(root.Element("GroupID").Element("UUID").Value, part.Element("GroupID").Element("UUID").Value);
        foreach (string e in new[] { "BaseMask", "OwnerMask", "GroupMask", "EveryoneMask", "NextOwnerMask" })
            Assert.Equal(root.Element(e).Value, part.Element(e).Value);
        // the root's creator data names the root's creator, so it is not copied to a part credited to someone else
        Assert.Null(part.Element("CreatorData"));
    }

    [Fact]
    public void TheCreatorOptionNamesTheCreatorOfAddedPartsAndNewTextures()
    {
        Lset f = new Lset();
        Guid creator = Guid.Parse("5eed0000-0000-4000-8000-0000000000aa");
        f.Edit(g =>
        {
            int fresh = g.Material(new[] { 1f, 1f, 1f, 1f }, "OPAQUE", Lset.Png(10, 200, 30));
            g.Cube("With a picture", 0.4f, fresh, new[] { 3f, 0f, 0f });
        });
        RestoreResult r = f.Restore(new RestoreOptions { Creator = creator });

        Assert.Equal(0, r.ExitCode);
        PartReport add = r.Parts.Single(p => p.Outcome == PartOutcome.Added);
        Dictionary<string, byte[]> after = f.After;
        XElement part = f.PartIn(after, add.PartId);
        Assert.Equal(creator.ToString(), CreatorOf(part));
        Assert.Equal(Lset.Owner, part.Element("OwnerID").Element("UUID").Value);
        Assert.Equal(1, r.TexturesAdded);
        string text = r.ToText();
        Guid texture = Guid.Parse(Path.GetFileName(after.Keys.Single(k => k.EndsWith("_texture.jp2", StringComparison.Ordinal))).Replace("_texture.jp2", string.Empty));
        Assert.Contains("new texture " + texture.ToString("D") + "  creator " + creator.ToString("D"), text);
        Assert.Contains("added part link 5 'With a picture' " + add.PartId.ToString("D") + "  creator " + creator.ToString("D"), text);
    }

    [Fact]
    public void TheReportListsEveryAddedPartAndNewTextureWithTheCreatorUsed()
    {
        Lset f = new Lset();
        f.Edit(g =>
        {
            int one = g.Material(new[] { 1f, 1f, 1f, 1f }, "OPAQUE", Lset.Png(10, 200, 30));
            int two = g.Material(new[] { 1f, 1f, 1f, 1f }, "OPAQUE", Lset.Png(200, 30, 10));
            g.Cube("First", 0.4f, one, new[] { 3f, 0f, 0f });
            g.Cube("Second", 0.4f, two, new[] { 4f, 0f, 0f });
            g.Cube("Third", 0.4f, -1, new[] { 5f, 0f, 0f });
        });
        RestoreResult r = f.Restore();   // no --creator: the root's owner is credited

        string[] lines = r.ToText().Split('\n');
        Assert.Equal(3, lines.Count(l => l.StartsWith("  added part link ", StringComparison.Ordinal) && l.EndsWith("  creator " + Lset.Owner, StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(l => l.StartsWith("  new texture ", StringComparison.Ordinal) && l.EndsWith("  creator " + Lset.Owner, StringComparison.Ordinal)));
    }

    [Fact]
    public void ATextureTransformOnAnAddedPartsPictureIsWarnedAboutAndNotDropped()
    {
        Lset f = new Lset();
        f.Edit(g =>
        {
            int moved = g.Material(new[] { 1f, 1f, 1f, 1f }, "OPAQUE", Lset.Png(10, 200, 30), textureTransform: true);
            g.Cube("Shifted picture", 0.4f, moved, new[] { 3f, 0f, 0f });
        });
        RestoreResult r = f.Restore();

        PartReport add = r.Parts.Single(p => p.Outcome == PartOutcome.Added);
        Assert.Contains(add.Notes, n => n.Contains("texture transform") && n.Contains("not applied"));
        Assert.Contains("texture transform", r.ToText());
    }

    [Fact]
    public void ATextureTransformOnAKeptPartsMaterialIsWarnedAbout()
    {
        Lset f = new Lset();
        f.Edit(g =>
        {
            int moved = g.Material(new[] { 1f, 1f, 1f, 1f }, "OPAQUE", Lset.Png(10, 200, 30), textureTransform: true);
            g.UseMaterial(f.A.Id.ToString(), moved);
        });
        RestoreResult r = f.Restore();

        PartReport a = RestoreTests.Of(r, f.A);
        Assert.Contains(a.Notes, n => n.Contains("texture transform"));
        Assert.DoesNotContain(RestoreTests.Of(r, f.B).Notes, n => n.Contains("texture transform"));
    }
}

/// <summary>What you see in Blender is what you get: every part lands at its place in the .glb, in both placements the exporter writes.</summary>
public class RestorePlacementTests
{
    private const string QuarterTurnZ = "0,0,0.70710678,0.70710678";
    private static readonly float H = MathF.Sqrt(0.5f);

    private static void SetTranslation(GlbEdit g, KitPart part, float x, float y, float z) =>
        g.Part(part.Id.ToString())["translation"] = new JsonArray(x, y, z);

    private static float[] Translation(GlbEdit g, KitPart part) =>
        g.Part(part.Id.ToString())["translation"] is JsonArray a ? new[] { (float)a[0], (float)a[1], (float)a[2] } : new[] { 0f, 0f, 0f };

    private static void AssertNear(float[] expected, float[] actual, string what)
    {
        for (int i = 0; i < 3; i++)
            Assert.True(MathF.Abs(expected[i] - actual[i]) < 1e-3f, what + ": expected <" + string.Join(", ", expected) + "> but found <" + string.Join(", ", actual) + ">");
    }

    private static void AssertSameExcept(XElement before, XElement after, params string[] except)
    {
        foreach (XElement e in before.Elements().Where(e => !except.Contains(e.Name.LocalName)))
            Assert.Equal(e.ToString(), after.Element(e.Name).ToString());
    }

    private static string Rotation(bool rotated) => rotated ? QuarterTurnZ : "0,0,0,1";

    // the root moved one metre along the viewer's X axis: the group's place is (1,1,1) plus that metre seen through the root's own turn
    private static float[] MovedGroup(bool rotated) => rotated ? new[] { 1f, 2f, 1f } : new[] { 2f, 1f, 1f };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheRootMovedAloneLeavesTheChildrenWhereTheyAre(bool world, bool rotated)
    {
        Lset f = new Lset(world, rootRotation: Rotation(rotated));
        f.Edit(g => SetTranslation(g, f.Root, 1f, 0f, 0f));
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> before = f.Before, after = f.After;
        foreach (KitPart p in f.Parts)
            AssertNear(MovedGroup(rotated), Lset.Vec(f.PartIn(after, p), "GroupPosition"), p.Name + " group position");
        // the children keep their place in the world, so their offsets from the root lose the metre the root moved
        AssertNear(new[] { 1f, 0f, 0f }, Lset.Vec(f.PartIn(after, f.A), "OffsetPosition"), "A");
        AssertNear(new[] { -1f, 3f, 0f }, Lset.Vec(f.PartIn(after, f.B), "OffsetPosition"), "B");
        AssertNear(new[] { -1f, 0f, 4f }, Lset.Vec(f.PartIn(after, f.C), "OffsetPosition"), "C");
        // the root keeps its turn, and nothing else about any part changed
        AssertSameExcept(f.PartIn(before, f.Root), f.PartIn(after, f.Root), "GroupPosition");
        foreach (KitPart p in new[] { f.A, f.B, f.C })
            AssertSameExcept(f.PartIn(before, p), f.PartIn(after, p), "GroupPosition", "OffsetPosition");
        foreach (KitPart p in f.Parts)
            Assert.Equal(PartOutcome.Moved, RestoreTests.Of(r, p).Outcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheRootMovedWithItsChildrenMovesTheWholeLinkset(bool world, bool rotated)
    {
        Lset f = new Lset(world, rootRotation: Rotation(rotated));
        f.Edit(g =>
        {
            foreach (KitPart p in f.Parts)
            {
                float[] t = Translation(g, p);
                SetTranslation(g, p, t[0] + 1f, t[1], t[2]);
            }
        });
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> before = f.Before, after = f.After;
        foreach (KitPart p in f.Parts)
            AssertNear(MovedGroup(rotated), Lset.Vec(f.PartIn(after, p), "GroupPosition"), p.Name + " group position");
        foreach (KitPart p in new[] { f.A, f.B, f.C })
        {
            AssertSameExcept(f.PartIn(before, p), f.PartIn(after, p), "GroupPosition");
            Assert.Equal(PartOutcome.Unchanged, RestoreTests.Of(r, p).Outcome);
        }
        AssertSameExcept(f.PartIn(before, f.Root), f.PartIn(after, f.Root), "GroupPosition");
        Assert.Equal(PartOutcome.Moved, RestoreTests.Of(r, f.Root).Outcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheRootMovedWithItsChildrenParentedInBlenderMovesTheWholeLinkset(bool world, bool rotated)
    {
        Lset f = new Lset(world, rootRotation: Rotation(rotated));
        f.Edit(g =>
        {
            foreach (KitPart p in new[] { f.A, f.B, f.C })
                g.Reparent(p.Id.ToString(), f.Root.Id.ToString());
            SetTranslation(g, f.Root, 1f, 0f, 0f);
        });
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> before = f.Before, after = f.After;
        foreach (KitPart p in f.Parts)
            AssertNear(MovedGroup(rotated), Lset.Vec(f.PartIn(after, p), "GroupPosition"), p.Name + " group position");
        foreach (KitPart p in new[] { f.A, f.B, f.C })
        {
            AssertSameExcept(f.PartIn(before, p), f.PartIn(after, p), "GroupPosition");
            Assert.Equal(PartOutcome.Unchanged, RestoreTests.Of(r, p).Outcome);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneMovedChildChangesOnlyThatChild(bool world)
    {
        Lset f = new Lset(world);
        f.Edit(g => SetTranslation(g, f.B, 1f, 0f, -3f));   // viewer <1, 3, 0>
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> before = f.Before, after = f.After;
        AssertNear(new[] { 1f, 3f, 0f }, Lset.Vec(f.PartIn(after, f.B), "OffsetPosition"), "B");
        Assert.Equal(PartOutcome.Moved, RestoreTests.Of(r, f.B).Outcome);
        foreach (KitPart p in new[] { f.Root, f.A, f.C })
        {
            Assert.Equal(f.PartIn(before, p).ToString(), f.PartIn(after, p).ToString());
            Assert.Equal(PartOutcome.Unchanged, RestoreTests.Of(r, p).Outcome);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRootTurnedAloneLeavesTheChildrenWhereTheyAreInTheWorld(bool world)
    {
        Lset f = new Lset(world);
        f.Edit(g => g.Part(f.Root.Id.ToString())["rotation"] = new JsonArray(0f, H, 0f, H));   // a quarter turn about glTF's up axis
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        Dictionary<string, byte[]> after = f.After;
        XElement root = f.PartIn(after, f.Root);
        Assert.Equal(H, Lset.F(root, "RotationOffset", "Z"), 3);
        Assert.Equal(H, Lset.F(root, "RotationOffset", "W"), 3);
        foreach (KitPart p in f.Parts)
            AssertNear(new[] { 1f, 1f, 1f }, Lset.Vec(f.PartIn(after, p), "GroupPosition"), p.Name + " group position");
        // each child keeps its place in the world: its offset and turn are the old ones seen from the turned root
        AssertNear(new[] { 0f, -2f, 0f }, Lset.Vec(f.PartIn(after, f.A), "OffsetPosition"), "A");
        AssertNear(new[] { 3f, 0f, 0f }, Lset.Vec(f.PartIn(after, f.B), "OffsetPosition"), "B");
        AssertNear(new[] { 0f, 0f, 4f }, Lset.Vec(f.PartIn(after, f.C), "OffsetPosition"), "C");
        foreach (KitPart p in new[] { f.A, f.B, f.C })
        {
            XElement part = f.PartIn(after, p);
            Assert.Equal(-H, Lset.F(part, "RotationOffset", "Z"), 3);
            Assert.Equal(H, Lset.F(part, "RotationOffset", "W"), 3);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnAddedPartIsPlacedInTheFrameOfTheMovedRoot(bool world, bool rotated)
    {
        Lset f = new Lset(world, rootRotation: Rotation(rotated));
        f.Edit(g =>
        {
            SetTranslation(g, f.Root, 1f, 0f, 0f);
            g.Cube("New cube", 0.4f, -1, new[] { 3f, 0f, 0f });
        });
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.True(r.Parity.Pass, r.Parity.ToText());
        PartReport add = r.Parts.Single(p => p.Outcome == PartOutcome.Added);
        Assert.Equal(5, add.LinkNum);
        XElement part = f.PartIn(f.After, add.PartId);
        AssertNear(MovedGroup(rotated), Lset.Vec(part, "GroupPosition"), "the added part's group position");
        AssertNear(new[] { 2f, 0f, 0f }, Lset.Vec(part, "OffsetPosition"), "the added part's offset from the root");
    }
}

/// <summary>A deleted part kept as a stub: no Phantom flag on a child, physics type None, and the root part is never deleted.</summary>
public class RestoreStubTests
{
    // a script in the last part keeps the linkset on the refuse-or-stub path, whichever other part is deleted
    private static Lset Scripted(int childPhysics = -1) => new Lset(scriptIn: "C", childPhysics: childPhysics);

    [Fact]
    public void AStubNeverGetsThePhantomFlag()
    {
        Lset f = Scripted();
        f.Edit(g => g.RemovePart(f.A.Id.ToString()));
        RestoreResult r = f.Restore(new RestoreOptions { StubDeleted = true });

        Assert.Equal(PartOutcome.Stubbed, RestoreTests.Of(r, f.A).Outcome);
        Assert.Equal(0, r.ExitCode);
        XElement was = f.PartIn(f.Before, f.A), now = f.PartIn(f.After, f.A);
        Assert.Equal(was.Element("Flags").Value, now.Element("Flags").Value);
        Assert.DoesNotContain("Phantom", now.Element("Flags").Value);
        // phantom is a setting of the whole linkset, so no other part has it either
        foreach (KitPart p in f.Parts)
            Assert.DoesNotContain("Phantom", f.PartIn(f.After, p).Element("Flags").Value);
    }

    [Fact]
    public void AStubGetsPhysicsShapeTypeNoneWhereThePartHasTheElement()
    {
        Lset f = Scripted(childPhysics: 0);
        f.Edit(g => g.RemovePart(f.A.Id.ToString()));
        RestoreResult r = f.Restore(new RestoreOptions { StubDeleted = true });

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.Equal("1", f.PartIn(f.After, f.A).Element("PhysicsShapeType").Value);
        Assert.Equal("0", f.PartIn(f.After, f.B).Element("PhysicsShapeType").Value);
    }

    [Fact]
    public void AStubGetsPhysicsShapeTypeNoneAddedWhereThePartLacksTheElement()
    {
        Lset f = Scripted();
        Assert.Null(f.PartIn(f.Before, f.A).Element("PhysicsShapeType"));
        f.Edit(g => g.RemovePart(f.A.Id.ToString()));
        RestoreResult r = f.Restore(new RestoreOptions { StubDeleted = true });

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.Equal("1", f.PartIn(f.After, f.A).Element("PhysicsShapeType").Value);
        Assert.Null(f.PartIn(f.After, f.B).Element("PhysicsShapeType"));
        // the stub keeps its link number, name and contents
        XElement was = f.PartIn(f.Before, f.A), now = f.PartIn(f.After, f.A);
        Assert.Equal(was.Element("LinkNum").Value, now.Element("LinkNum").Value);
        Assert.Equal(was.Element("Name").Value, now.Element("Name").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletingTheRootPartIsRefusedWithAndWithoutStubDeleted(bool stub)
    {
        Lset f = new Lset();   // no script: a child would really be deleted, the root never is
        f.Edit(g => g.RemovePart(f.Root.Id.ToString()));
        RestoreResult r = f.Restore(new RestoreOptions { StubDeleted = stub });

        PartReport root = RestoreTests.Of(r, f.Root);
        Assert.Equal(PartOutcome.Refused, root.Outcome);
        Assert.Equal("ROOT_DELETED", root.Code);
        Assert.Equal(3, r.ExitCode);
        Assert.True(r.Written);
        // the archive is written as it was
        Dictionary<string, byte[]> before = f.Before, after = f.After;
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (string k in before.Keys)
            Assert.True(before[k].SequenceEqual(after[k]), "entry differs: " + k);
        Assert.True(r.ToText().IndexOf("REFUSED PARTS (1)", StringComparison.Ordinal) >= 0);
    }
}

/// <summary>In a linkset where no part holds a script, a part deleted in Blender is deleted for real and the link numbers are rewritten contiguous.</summary>
public class RestoreDeleteTests
{
    private static string LinkOf(Lset f, Dictionary<string, byte[]> entries, KitPart p) => f.PartIn(entries, p).Element("LinkNum").Value;

    private static void AssertSameBytesExceptObjects(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (string k in before.Keys.Where(k => !k.StartsWith("objects/", StringComparison.Ordinal)))
            Assert.True(before[k].SequenceEqual(after[k]), "entry differs: " + k);
    }

    private static void AssertSameExcept(XElement before, XElement after, params string[] except)
    {
        foreach (XElement e in before.Elements().Where(e => !except.Contains(e.Name.LocalName)))
            Assert.Equal(e.ToString(), after.Element(e.Name).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeletedPartOfAScriptFreeLinksetIsRemovedAndTheLinkNumbersStayContiguous(bool stubDeleted)
    {
        Lset f = new Lset();
        f.Edit(g => g.RemovePart(f.B.Id.ToString()));
        RestoreResult r = f.Restore(new RestoreOptions { StubDeleted = stubDeleted });

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.Equal(PartOutcome.Deleted, RestoreTests.Of(r, f.B).Outcome);
        Assert.Equal(1, r.Count(PartOutcome.Deleted));
        Assert.Contains("DELETED  link 3  'B'", r.ToText());
        Dictionary<string, byte[]> before = f.Before, after = f.After;
        string key = Lset.ObjectKey(before);
        XDocument doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(after[key]));
        Assert.Equal(3, doc.Descendants("SceneObjectPart").Count());
        Assert.DoesNotContain(doc.Descendants("SceneObjectPart"), p => p.Element("Name").Value == "B");
        Assert.Equal(new[] { "1", "2", "3" }, new[] { f.Root, f.A, f.C }.Select(p => LinkOf(f, after, p)));
        // nothing else in the archive changed: every other entry is the same bytes, and the parts left have the same text apart from C's number
        AssertSameBytesExceptObjects(before, after);
        AssertSameExcept(f.PartIn(before, f.Root), f.PartIn(after, f.Root));
        AssertSameExcept(f.PartIn(before, f.A), f.PartIn(after, f.A));
        AssertSameExcept(f.PartIn(before, f.C), f.PartIn(after, f.C), "LinkNum");
        Assert.Equal("4", LinkOf(f, before, f.C));
    }

    [Fact]
    public void TwoDeletedPartsLeaveContiguousNumbers()
    {
        Lset f = new Lset();
        f.Edit(g =>
        {
            g.RemovePart(f.A.Id.ToString());
            g.RemovePart(f.C.Id.ToString());
        });
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Assert.Equal(2, r.Count(PartOutcome.Deleted));
        Dictionary<string, byte[]> after = f.After;
        Assert.Equal(new[] { "1", "2" }, new[] { f.Root, f.B }.Select(p => LinkOf(f, after, p)));
        AssertSameBytesExceptObjects(f.Before, after);
    }

    [Fact]
    public void DeletingTheLastPartLeavesTheOtherNumbersAlone()
    {
        Lset f = new Lset();
        f.Edit(g => g.RemovePart(f.C.Id.ToString()));
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Dictionary<string, byte[]> before = f.Before, after = f.After;
        foreach (KitPart p in new[] { f.Root, f.A, f.B })
            Assert.Equal(f.PartIn(before, p).ToString(), f.PartIn(after, p).ToString());
    }

    [Fact]
    public void DeletingEveryChildLeavesTheRootAsASinglePartWithLinkNumberZero()
    {
        Lset f = new Lset();
        f.Edit(g =>
        {
            foreach (KitPart p in new[] { f.A, f.B, f.C })
                g.RemovePart(p.Id.ToString());
        });
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        Dictionary<string, byte[]> after = f.After;
        Assert.Equal("0", LinkOf(f, after, f.Root));
        AssertSameExcept(f.PartIn(f.Before, f.Root), f.PartIn(after, f.Root), "LinkNum");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("C")]
    [InlineData("Root")]
    public void ALinksetWithAScriptAnywhereKeepsRefuseOrStub(string scriptIn)
    {
        Lset f = new Lset(scriptIn: scriptIn);
        f.Edit(g => g.RemovePart(f.B.Id.ToString()));

        RestoreResult refused = f.Restore();
        Assert.Equal(PartOutcome.Refused, RestoreTests.Of(refused, f.B).Outcome);
        Assert.Equal("PART_DELETED", RestoreTests.Of(refused, f.B).Code);
        Assert.Equal(3, refused.ExitCode);
        Dictionary<string, byte[]> before = f.Before, same = f.After;
        foreach (string k in before.Keys)
            Assert.True(before[k].SequenceEqual(same[k]), "entry differs: " + k);

        RestoreResult stubbed = f.Restore(new RestoreOptions { StubDeleted = true }, "stub.oar");
        Assert.Equal(PartOutcome.Stubbed, RestoreTests.Of(stubbed, f.B).Outcome);
        Dictionary<string, byte[]> after = RestoreTests.Entries(f.Out("stub.oar"));
        Assert.Equal(new[] { "1", "2", "3", "4" }, f.Parts.Select(p => LinkOf(f, after, p)));
    }

    [Fact]
    public void AnAddedPartFollowsTheRenumberedParts()
    {
        Lset f = new Lset();
        f.Edit(g =>
        {
            g.RemovePart(f.B.Id.ToString());
            g.Cube("New cube", 0.4f, -1, new[] { 3f, 0f, 0f });
        });
        RestoreResult r = f.Restore();

        Assert.True(r.ExitCode == 0, r.ToText() + r.Parity?.ToText());
        PartReport add = r.Parts.Single(p => p.Outcome == PartOutcome.Added);
        Assert.Equal(4, add.LinkNum);
        Dictionary<string, byte[]> after = f.After;
        Assert.Equal(new[] { "1", "2", "3", "4" }, new[] { f.Root, f.A, f.C }.Select(p => LinkOf(f, after, p)).Append(f.PartIn(after, add.PartId).Element("LinkNum").Value));
    }
}
