/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace OpenSimNGC.Appearance.Baking.Tests;

/// <summary>
/// The compositor reading avatar_lad.xml and the mask files from a caller-supplied character folder (the layout of
/// the official viewer's character/ folder) instead of the embedded copies. The folders here are written from the
/// embedded resources, so "the same files" is literally true.
/// </summary>
public class CharacterFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "character-folder-tests-" + Guid.NewGuid().ToString("N"));

    public CharacterFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Writes avatar_lad.xml and every embedded character image into a new folder and returns it.</summary>
    private string WriteFolder(string name = "character")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var asm = typeof(AvatarLad).Assembly;
        foreach (var res in asm.GetManifestResourceNames())
        {
            string? file = null;
            if (res == AvatarLad.EmbeddedResourceName) file = "avatar_lad.xml";
            else if (res.StartsWith(ResourceImages.EmbeddedPrefix, StringComparison.Ordinal)) file = res[ResourceImages.EmbeddedPrefix.Length..];
            if (file is null) continue;
            using var s = asm.GetManifestResourceStream(res)!;
            using var o = File.Create(Path.Combine(dir, file));
            s.CopyTo(o);
        }
        return dir;
    }

    private static List<string> NamedFiles()
    {
        var c = new AvatarBakeCompositor();
        return Enum.GetValues<BakeChannel>().SelectMany(c.ResourceFilesOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    public static IEnumerable<object[]> BakeVectors() => BakeVectorTests.BakeVectors();

    [Theory]
    [MemberData(nameof(BakeVectors))]
    public void FolderOfTheSameFilesMatchesTheReferenceVectors(string id)
    {
        var c = AvatarBakeCompositor.FromCharacterFolder(WriteFolder());
        Assert.NotNull(c);
        BakeVectorTests.AssertVectorMatches(c, id);
    }

    [Fact]
    public void FolderCompositorHasTheSameDefinitionAsTheEmbeddedOne()
    {
        var c = AvatarBakeCompositor.FromCharacterFolder(WriteFolder());
        Assert.NotNull(c);
        Assert.Equal(AvatarLad.Embedded.Params.Count, c.Lad.Params.Count);
        Assert.Equal(AvatarLad.Embedded.LayerSets.Keys.OrderBy(k => k), c.Lad.LayerSets.Keys.OrderBy(k => k));
        Assert.Equal(AvatarLad.Embedded.MorphMaskLayers.Count, c.Lad.MorphMaskLayers.Count);
    }

    [Fact]
    public void FolderCompositorReadsEveryFileTheBakeLayersName()
    {
        var c = AvatarBakeCompositor.FromCharacterFolder(WriteFolder());
        Assert.NotNull(c);
        var files = NamedFiles();
        Assert.True(files.Count > 40, $"expected the layer sets to name dozens of files, got {files.Count}");
        Assert.Empty(files.Where(f => !c.Files.Exists(f)));
    }

    [Fact]
    public void FilesOfTheFolderAreUsedNotTheEmbeddedOnes()
    {
        var dir = WriteFolder();
        var files = NamedFiles();
        var a = files.First();
        var b = files.Skip(1).First(f => !File.ReadAllBytes(Path.Combine(dir, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, a))));
        File.Copy(Path.Combine(dir, b), Path.Combine(dir, a), overwrite: true);

        var c = AvatarBakeCompositor.FromCharacterFolder(dir);
        Assert.NotNull(c);
        var fromFolder = c.Files.Colour(a);
        var other = BakeResourceFiles.Embedded.Colour(b);
        var own = BakeResourceFiles.Embedded.Colour(a);
        Assert.NotNull(fromFolder); Assert.NotNull(other); Assert.NotNull(own);
        Assert.Equal(other.Width, fromFolder.Width);
        Assert.Equal(other.Height, fromFolder.Height);
        Assert.Equal(other.R, fromFolder.R);
        Assert.False(own.Width == fromFolder.Width && own.Height == fromFolder.Height && own.R.AsSpan().SequenceEqual(fromFolder.R), "the folder's file was ignored");
    }

    [Fact]
    public void ExtraFilesInTheFolderAreIgnored()
    {
        var dir = WriteFolder();
        File.WriteAllText(Path.Combine(dir, "avatar_skeleton.xml"), "<linden_skeleton/>");
        File.WriteAllBytes(Path.Combine(dir, "head.llm"), new byte[] { 1, 2, 3 });
        var c = AvatarBakeCompositor.FromCharacterFolder(dir);
        Assert.NotNull(c);
    }

    [Fact]
    public void MissingFolderIsAnErrorNamingTheFolder()
    {
        var dir = Path.Combine(_root, "no-such-folder");
        var ex = Assert.Throws<CharacterFolderException>(() => AvatarBakeCompositor.FromCharacterFolder(dir));
        Assert.Contains(dir, ex.Message);
    }

    [Fact]
    public void FolderWithoutTheDefinitionFileIsAnErrorNamingIt()
    {
        var dir = WriteFolder();
        File.Delete(Path.Combine(dir, "avatar_lad.xml"));
        var ex = Assert.Throws<CharacterFolderException>(() => AvatarBakeCompositor.FromCharacterFolder(dir));
        Assert.Contains("avatar_lad.xml", ex.Message);
    }

    [Fact]
    public void EmptyFolderIsAnErrorNotAFallbackToTheEmbeddedData()
    {
        var dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);
        Assert.Throws<CharacterFolderException>(() => AvatarBakeCompositor.FromCharacterFolder(dir));
    }

    [Fact]
    public void DefinitionFileThatIsNotXmlIsAnErrorNamingIt()
    {
        var dir = WriteFolder();
        File.WriteAllText(Path.Combine(dir, "avatar_lad.xml"), "this is not xml");
        var ex = Assert.Throws<CharacterFolderException>(() => AvatarBakeCompositor.FromCharacterFolder(dir));
        Assert.Contains("avatar_lad.xml", ex.Message);
    }

    [Fact]
    public void MissingImageIsAnErrorListingTheFile()
    {
        var dir = WriteFolder();
        var gone = NamedFiles().First();
        File.Delete(Path.Combine(dir, gone));
        var ex = Assert.Throws<CharacterFolderException>(() => AvatarBakeCompositor.FromCharacterFolder(dir));
        Assert.Contains(gone, ex.ProblemFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(gone, ex.Message);
    }

    [Fact]
    public void EveryMissingImageIsListed()
    {
        var dir = WriteFolder();
        var gone = NamedFiles().Take(3).ToList();
        foreach (var f in gone) File.Delete(Path.Combine(dir, f));
        var ex = Assert.Throws<CharacterFolderException>(() => AvatarBakeCompositor.FromCharacterFolder(dir));
        foreach (var f in gone) Assert.Contains(f, ex.ProblemFiles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageThatCannotBeDecodedIsAnErrorListingTheFile()
    {
        var dir = WriteFolder();
        var bad = NamedFiles().First();
        File.WriteAllBytes(Path.Combine(dir, bad), new byte[] { 0, 1, 2 });
        var ex = Assert.Throws<CharacterFolderException>(() => AvatarBakeCompositor.FromCharacterFolder(dir));
        Assert.Contains(bad, ex.ProblemFiles, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoFolderNameIsAnArgumentError(string? folder)
        => Assert.ThrowsAny<ArgumentException>(() => AvatarBakeCompositor.FromCharacterFolder(folder!));
}
