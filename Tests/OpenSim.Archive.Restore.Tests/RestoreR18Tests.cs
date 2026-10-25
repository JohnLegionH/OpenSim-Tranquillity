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

    public RestoreResult Restore(RestoreOptions options = null)
    {
        options ??= new RestoreOptions();
        options.WorkFolder ??= Path.Combine(Dir, "work");
        return new ArchiveRestorer(options).Restore(Archive, GlbDir, Out());
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
