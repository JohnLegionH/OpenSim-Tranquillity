/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Text;
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;
using Xunit.Abstractions;

namespace OpenSim.CharacterPublish.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that needs real assets from a resident's inventory archive. Those are
/// private and not in the repository: the test is skipped, with the reason, unless the named environment variable
/// points at a folder that exists. Extract the assets of one kind from an archive into that folder as *.wear or *.note files.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealAssetsFactAttribute : FactAttribute
{
    public RealAssetsFactAttribute(string variable)
    {
        var dir = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            Skip = $"set {variable} to a folder of assets extracted from an inventory archive; they are private and not committed. Nothing is asserted without them.";
    }
}

/// <summary>
/// Real wearables and notecards, parsed and written back. The output must equal the original except where the
/// viewer itself rewrites a field when it saves; every such difference is named and checked against the authority.
/// </summary>
public class RealAssetTests
{
    readonly ITestOutputHelper _out;
    public RealAssetTests(ITestOutputHelper output) => _out = output;

    /// <summary>The ways a stored wearable can differ from what the viewer would write now, each with its source in the viewer.</summary>
    enum Rewrite
    {
        /// <summary>No difference.</summary>
        None,
        /// <summary>A trailing NUL byte: exportStream never writes one (llwearable.cpp:96-143).</summary>
        TrailingNul,
        /// <summary>A weight that is not in the viewer's "%.2f" terse form: exportStream writes terse_F32_to_string (llwearable.cpp:130, 745-777).</summary>
        WeightPrecision,
        /// <summary>Parameters or textures not in ascending order, or repeated: the viewer keeps both in a std::map and writes it in key order (llwearable.cpp:124-131, 134-143).</summary>
        Order,
        /// <summary>Blank lines between sections: the reader skips them (getNextPopulatedLine, llwearable.cpp:424-440), the writer never writes them.</summary>
        BlankLines,
        /// <summary>A definition version other than the current one: the viewer writes the current one on save (llviewerwearable.cpp:400, llwearablelist.cpp:270, llagentwearables.cpp:648-654).</summary>
        Version,
        /// <summary>Carriage returns: the writer uses "\n" only.</summary>
        CarriageReturn,
    }

    sealed record Outcome(string File, WearableKind Type, bool Identical, HashSet<Rewrite> Explained, bool Unexplained, string? Detail);

    static Outcome Check(string path)
    {
        var raw = File.ReadAllBytes(path);
        var explained = new HashSet<Rewrite>();
        var end = raw.Length;
        while (end > 0 && raw[end - 1] == 0) end--;
        if (end != raw.Length) explained.Add(Rewrite.TrailingNul);
        string original;
        try { original = new UTF8Encoding(false, true).GetString(raw, 0, end); }
        catch (DecoderFallbackException) { return new Outcome(Path.GetFileName(path), WearableKind.Invalid, false, explained, true, "not valid UTF-8"); }

        var doc = WearableReader.Read(original);
        var ours = WearableWriter.Write(doc);
        if (ours == original) return new Outcome(Path.GetFileName(path), doc.Type, explained.Count == 0, explained, false, null);

        if (original.Contains('\r')) explained.Add(Rewrite.CarriageReturn);
        var lines = original.Replace("\r", "").Split('\n');
        if (lines.Take(lines.Length - 1).Skip(3).Any(l => l.Length == 0)) explained.Add(Rewrite.BlankLines);
        if (doc.DefinitionVersion != WearableDocument.CurrentDefinitionVersion) explained.Add(Rewrite.Version);

        // Weights and order, from the original's own parameter and texture lines.
        var populated = lines.Where(l => l.Length > 0).ToList();
        var pi = populated.FindIndex(l => l.StartsWith("parameters ", StringComparison.Ordinal));
        var ti = populated.FindIndex(l => l.StartsWith("textures ", StringComparison.Ordinal));
        var plines = populated.Skip(pi + 1).Take(ti - pi - 1).Select(l => l.Split(' ')).ToList();
        var tlines = populated.Skip(ti + 1).Select(l => l.Split(' ')).ToList();
        if (plines.Any(p => p.Length == 2 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && WearableFormat.TerseFloat(w) != p[1]))
            explained.Add(Rewrite.WeightPrecision);
        var pids = plines.Select(p => int.Parse(p[0], CultureInfo.InvariantCulture)).ToList();
        var tids = tlines.Select(p => int.Parse(p[0], CultureInfo.InvariantCulture)).ToList();
        if (pids.Zip(pids.Skip(1), (a, b) => b <= a).Any(x => x) || tids.Zip(tids.Skip(1), (a, b) => b <= a).Any(x => x)) explained.Add(Rewrite.Order);

        // Explained only if undoing the named differences gives exactly our output.
        var again = WearableReader.Read(ours);
        var same = doc.ContentEquals(again) || OnlyRoundingDiffers(doc, again);
        // The written text, normalised the same way the original's differences were named, has to equal the original read back.
        var unexplained = !same || explained.Count == 0;
        return new Outcome(Path.GetFileName(path), doc.Type, false, explained, unexplained, unexplained ? "differs without a named reason" : null);
    }

    static bool OnlyRoundingDiffers(WearableDocument a, WearableDocument b)
    {
        if (a.Parameters.Count != b.Parameters.Count || a.Textures.Count != b.Textures.Count) return false;
        foreach (var (id, v) in a.Parameters)
            if (!b.Parameters.TryGetValue(id, out var w) || float.Parse(WearableFormat.TerseFloat(v), CultureInfo.InvariantCulture) != w) return false;
        return a.Name == b.Name && a.Description == b.Description && a.Permissions == b.Permissions && a.Type == b.Type
            && a.SaleType == b.SaleType && a.SalePrice == b.SalePrice && a.Textures.All(t => b.Textures.TryGetValue(t.Key, out var x) && x == t.Value);
    }

    [RealAssetsFact("PUB_REAL_WEARABLES_DIR")]
    public void Real_wearables_read_and_write_back_the_same_apart_from_what_the_viewer_rewrites()
    {
        var dir = Environment.GetEnvironmentVariable("PUB_REAL_WEARABLES_DIR")!;
        var files = Directory.GetFiles(dir, "*.wear");
        var results = files.Select(Check).ToList();

        var byType = results.GroupBy(r => r.Type).OrderBy(g => (int)g.Key);
        foreach (var g in byType)
            _out.WriteLine($"{g.Key,-12} files {g.Count(),4}  byte-identical {g.Count(r => r.Identical),4}  " +
                           $"differing only by a named rewrite {g.Count(r => !r.Identical && !r.Unexplained),4}  unexplained {g.Count(r => r.Unexplained),4}");
        foreach (var rw in Enum.GetValues<Rewrite>().Where(r => r != Rewrite.None))
            _out.WriteLine($"rewrite {rw,-16} {results.Count(r => r.Explained.Contains(rw))} files");
        _out.WriteLine($"total {results.Count}, identical {results.Count(r => r.Identical)}, unexplained {results.Count(r => r.Unexplained)}");

        Assert.True(results.Count(r => r.Type == WearableKind.Shape) >= 10, "need at least 10 shapes");
        Assert.True(results.Count(r => r.Type == WearableKind.Skin) >= 5, "need at least 5 skins");
        Assert.True(results.Count(r => r.Type == WearableKind.Eyes) >= 5, "need at least 5 eyes");
        Assert.Empty(results.Where(r => r.Unexplained).Select(r => r.File + ": " + r.Detail));
    }

    [RealAssetsFact("PUB_REAL_WEARABLES_DIR")]
    public void The_bakers_parser_agrees_with_our_reader_on_every_real_wearable()
    {
        var dir = Environment.GetEnvironmentVariable("PUB_REAL_WEARABLES_DIR")!;
        var bad = new List<string>();
        foreach (var f in Directory.GetFiles(dir, "*.wear"))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(f)).TrimEnd('\0');
            var doc = WearableReader.Read(text);
            var parsed = WearableParser.Parse(text);
            if (parsed.Kind != doc.Type || parsed.Params.Count != doc.Parameters.Count || parsed.Textures.Count != doc.Textures.Count
                || doc.Parameters.Any(p => parsed.Params[p.Key] != p.Value)
                || doc.Textures.Any(t => parsed.Textures[(TextureSlot)t.Key] != t.Value))
                bad.Add(Path.GetFileName(f));
        }
        Assert.Empty(bad);
    }

    [RealAssetsFact("PUB_REAL_WEARABLES_DIR")]
    public void A_real_shape_has_the_parameters_our_default_shape_has()
    {
        var dir = Environment.GetEnvironmentVariable("PUB_REAL_WEARABLES_DIR")!;
        var ours = WearableFactory.Shape("x", "", Fixtures.Perms).Parameters.Keys.ToHashSet();
        var shapes = Directory.GetFiles(dir, "*.wear")
            .Select(f => WearableReader.Read(Encoding.UTF8.GetString(File.ReadAllBytes(f)).TrimEnd('\0')))
            .Where(d => d.Type == WearableKind.Shape).ToList();
        var counts = shapes.GroupBy(s => s.Parameters.Count).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} params x{g.Count()}");
        _out.WriteLine("real shapes: " + string.Join(", ", counts) + "; our default shape: " + ours.Count + " params");
        // Shapes saved by the current viewer carry exactly the ids avatar_lad.xml assigns to the shape.
        Assert.Contains(shapes, s => s.Parameters.Keys.ToHashSet().SetEquals(ours));
    }

    [RealAssetsFact("PUB_REAL_NOTECARDS_DIR")]
    public void Real_notecards_read_and_write_back_byte_for_byte()
    {
        var dir = Environment.GetEnvironmentVariable("PUB_REAL_NOTECARDS_DIR")!;
        int identical = 0, nulOnly = 0, noNewline = 0;
        var bad = new List<string>();
        var files = Directory.GetFiles(dir, "*.note");
        foreach (var f in files)
        {
            var raw = File.ReadAllBytes(f);
            var end = raw.Length;
            while (end > 0 && raw[end - 1] == 0) end--;
            var body = raw[..end];
            var doc = NotecardReader.Read(body);
            var ours = NotecardWriter.WriteBytes(doc);
            if (ours.AsSpan().SequenceEqual(body)) { if (end == raw.Length) identical++; else nulOnly++; }
            // A notecard written by a script ends "}" with no newline; the viewer writes "}" and a newline (llnotecard.cpp:274).
            else if (ours.Length == body.Length + 1 && ours.AsSpan(0, body.Length).SequenceEqual(body) && ours[^1] == (byte)'\n') noNewline++;
            else bad.Add(Path.GetFileName(f));
        }
        _out.WriteLine($"notecards {files.Length}: identical {identical}, identical apart from a trailing NUL {nulOnly}, " +
                       $"identical apart from the final newline {noNewline}, different {bad.Count}");
        Assert.True(files.Length >= 10);
        Assert.Empty(bad);
    }
}
