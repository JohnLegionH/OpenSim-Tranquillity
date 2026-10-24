/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMetaverse;

namespace OpenSim.CharacterPublish;

/// <summary>
/// A bundle on disk: one folder holding the assets, each in the file its <see cref="BundleAsset.File"/> names, and
/// <c>bundle.json</c> describing them. The same bytes are produced for the same bundle (fixed property order, "\n" line ends,
/// no byte order mark), so assembling twice gives byte-identical folders.
/// </summary>
public static class BundleFiles
{
    public const string ManifestName = "bundle.json";

    /// <summary>Writes a UUID as its string form in JSON.</summary>
    public sealed class UuidJsonConverter : JsonConverter<UUID>
    {
        public override UUID Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            UUID.TryParse(reader.GetString(), out var u) ? u : throw new JsonException("not a UUID");
        public override void Write(Utf8JsonWriter writer, UUID value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    /// <summary>The options bundle.json is written and read with.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new UuidJsonConverter() },
    };

    public static byte[] ToJson(Bundle b) => new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(b, Options) + "\n");

    public static Bundle FromJson(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<Bundle>(json, Options) ?? throw new JsonException("empty bundle.json");

    /// <summary>Writes every asset file and then bundle.json into <paramref name="folder"/>, which is created if needed.</summary>
    public static void Write(Bundle b, IReadOnlyDictionary<UUID, byte[]> content, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var a in b.Assets)
            File.WriteAllBytes(PlainPath(folder, a.File), content[a.Id]);
        File.WriteAllBytes(Path.Combine(folder, ManifestName), ToJson(b));
    }

    /// <summary>What <see cref="Read"/> found.</summary>
    public sealed record Loaded(Bundle Bundle, IReadOnlyDictionary<UUID, byte[]> Content, IReadOnlyList<string> UnlistedFiles);

    /// <summary>Reads bundle.json and the files it lists. A listed file that is missing is simply absent from the content (validation reports it).</summary>
    public static Loaded Read(string folder)
    {
        var b = FromJson(File.ReadAllBytes(Path.Combine(folder, ManifestName)));
        var content = new Dictionary<UUID, byte[]>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
        foreach (var a in b.Assets)
        {
            var path = PlainPath(folder, a.File);
            listed.Add(a.File);
            if (File.Exists(path)) content[a.Id] = File.ReadAllBytes(path);
        }
        var unlisted = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Where(n => n != null && !listed.Contains(n!)).Select(n => n!).Order(StringComparer.Ordinal).ToList();
        return new Loaded(b, content, unlisted);
    }

    /// <summary>Reads a bundle folder and validates it; a file in the folder that bundle.json does not list is a problem too.</summary>
    public static ValidationResult ValidateFolder(string folder, BundleLimits? limits = null)
    {
        var loaded = Read(folder);
        var r = BundleValidator.Validate(loaded.Bundle, loaded.Content, limits);
        if (loaded.UnlistedFiles.Count == 0) return r;
        var problems = r.Problems.ToList();
        foreach (var n in loaded.UnlistedFiles) problems.Add(new BundleProblem("unlisted-file", n, "is in the bundle folder but bundle.json does not list it"));
        return new ValidationResult(problems);
    }

    /// <summary>The path of a listed file, refusing anything that is not a plain file name in the folder.</summary>
    static string PlainPath(string folder, string file)
    {
        if (file.Length == 0 || file != Path.GetFileName(file) || file.Contains("..") || file.Contains(':'))
            throw new InvalidDataException($"'{file}' is not a plain file name");
        return Path.Combine(folder, file);
    }
}
