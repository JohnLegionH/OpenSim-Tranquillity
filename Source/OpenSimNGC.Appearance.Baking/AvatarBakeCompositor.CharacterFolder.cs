/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSimNGC.Appearance.Baking;

/// <summary>A character folder could not be used for baking; the message says which part is wrong.</summary>
public sealed class CharacterFolderException : Exception
{
    public CharacterFolderException(string message, IReadOnlyList<string>? problemFiles = null, Exception? inner = null) : base(message, inner)
    {
        ProblemFiles = problemFiles ?? Array.Empty<string>();
    }

    /// <summary>The names of the files that are missing or cannot be decoded (empty when the folder itself is the problem).</summary>
    public IReadOnlyList<string> ProblemFiles { get; }
}

public sealed partial class AvatarBakeCompositor
{
    /// <summary>Name of the avatar definition file inside a character folder.</summary>
    public const string CharacterFolderDefinitionFile = "avatar_lad.xml";

    /// <summary>
    /// A compositor that reads avatar_lad.xml and the mask and base images from <paramref name="folder"/> instead of
    /// the copies embedded in this assembly. The folder has the layout of the official viewer's <c>character/</c>
    /// folder: <c>avatar_lad.xml</c> and the <c>.tga</c> files it names, side by side; other files in it are ignored.
    /// </summary>
    /// <remarks>
    /// The folder is checked completely before the compositor is returned: every image that a baked layer, a layer
    /// set or a parameter mask names must be present and decodable. A folder that fails the check is an error, never a
    /// silent fall back to the embedded data, so a caller that draws the avatar from the same folder is sure to bake
    /// from the same definition. Use <c>new AvatarBakeCompositor()</c> for the embedded data.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is null, empty or white space.</exception>
    /// <exception cref="CharacterFolderException">The folder, its avatar_lad.xml or an image it needs is missing or unusable.</exception>
    public static AvatarBakeCompositor FromCharacterFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("a character folder is required", nameof(folder));
        if (!Directory.Exists(folder)) throw new CharacterFolderException($"character folder '{folder}' does not exist");

        var ladPath = Path.Combine(folder, CharacterFolderDefinitionFile);
        if (!File.Exists(ladPath)) throw new CharacterFolderException($"character folder '{folder}' has no {CharacterFolderDefinitionFile}");

        AvatarLad lad;
        try { lad = AvatarLad.Load(ladPath); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new CharacterFolderException($"{CharacterFolderDefinitionFile} in '{folder}' cannot be read: {ex.Message}", inner: ex);
        }

        var compositor = new AvatarBakeCompositor(lad, BakeResourceFiles.FromDirectory(folder));
        var needed = Enum.GetValues<BakeChannel>()
            .SelectMany(compositor.ResourceFilesOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var missing = needed.Where(f => !File.Exists(Path.Combine(folder, f))).ToList();
        var undecodable = needed.Except(missing).Where(f => !compositor.Files.Exists(f)).ToList();
        if (missing.Count > 0 || undecodable.Count > 0)
        {
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add($"missing: {string.Join(", ", missing)}");
            if (undecodable.Count > 0) parts.Add($"cannot be decoded as TGA: {string.Join(", ", undecodable)}");
            throw new CharacterFolderException($"character folder '{folder}' is incomplete ({string.Join("; ", parts)})", missing.Concat(undecodable).ToList());
        }
        return compositor;
    }
}
