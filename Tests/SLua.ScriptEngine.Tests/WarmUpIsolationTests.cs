/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The script API warm-up (<c>ApiWarmUp</c>) is process-wide: <c>WarmUpTests</c> replace its work and count its runs.
/// Adding an enabled engine to a region starts it, so a test class that does so while <c>WarmUpTests</c> run would
/// start the warm-up with the wrong work, or count as one of its runs. Every test class that adds an engine to a
/// region therefore either belongs to the "native-library" collection (its classes run one at a time) or turns the
/// warm-up off in its configuration. This test reads the test sources and names each class that does neither.
/// A plain file read: it touches no process-wide state, so it runs in parallel.
/// </summary>
public class WarmUpIsolationTests
{
    private static readonly Regex ClassDeclaration = new(@"^(?:public\s+)?(?:sealed\s+)?(?:static\s+)?class\s+(\w+)", RegexOptions.Multiline);

    private static string TestsFolder([CallerFilePath] string here = "") => Path.GetDirectoryName(here);

    /// <summary>The top-level test classes of a source file that add an engine to a region, with how each is guarded.</summary>
    private static IEnumerable<(string Class, bool Serial, bool WarmUpOff)> EngineAddingClasses(string text)
    {
        // A file whose tests never add a region to an engine starts no warm-up.
        if (!Regex.IsMatch(text, @"\.AddRegion\(") || !Regex.IsMatch(text, @"SLuaEngine|EngineRig"))
            yield break;
        if (!Regex.IsMatch(text, @"\[(Fact|Theory|RequiresNativeFact|RequiresNativeTheory)"))
            yield break;
        MatchCollection classes = ClassDeclaration.Matches(text);
        for (int i = 0; i < classes.Count; i++)
        {
            int start = classes[i].Index;
            int end = i + 1 < classes.Count ? classes[i + 1].Index : text.Length;
            string body = text[start..end];
            if (!body.Contains(".AddRegion("))
                continue;
            // The attribute lines directly above the declaration.
            string above = text[Math.Max(0, start - 200)..start];
            bool serial = above.Contains("[Collection(\"native-library\")]");
            bool off = Regex.IsMatch(body, @"""WarmUp""\s*,\s*""false""");
            yield return (classes[i].Groups[1].Value, serial, off);
        }
    }

    [Fact]
    public void EveryTestClassThatAddsAnEngineToARegionRunsSeriallyOrTurnsTheWarmUpOff()
    {
        var unguarded = new List<string>();
        int seen = 0;
        foreach (string file in Directory.GetFiles(TestsFolder(), "*.cs"))
        {
            foreach (var (name, serial, off) in EngineAddingClasses(File.ReadAllText(file)))
            {
                seen++;
                if (!serial && !off)
                    unguarded.Add(name);
            }
        }
        Assert.True(seen > 10, "the scan found the classes that add an engine to a region (" + seen + ")");
        Assert.True(unguarded.Count == 0, "run beside WarmUpTests, these start the warm-up with its work replaced: " + string.Join(", ", unguarded.OrderBy(n => n)));
    }

    [Theory]
    [InlineData("[Collection(\"native-library\")]\npublic class Guarded { void T() { engine.AddRegion(scene); new SLuaEngine(); } [Fact] void F() {} }", true, false)]
    [InlineData("public class Off { void T() { new SLuaEngine(); source.Set(\"WarmUp\", \"false\"); engine.AddRegion(scene); } [Fact] void F() {} }", false, true)]
    [InlineData("public class Neither { void T() { new SLuaEngine(); engine.AddRegion(scene); } [Fact] void F() {} }", false, false)]
    public void TheScanTellsGuardedFromUnguardedClasses(string source, bool serial, bool warmUpOff)
    {
        var found = EngineAddingClasses(source).ToList();
        Assert.Single(found);
        Assert.Equal(serial, found[0].Serial);
        Assert.Equal(warmUpOff, found[0].WarmUpOff);
    }

    [Fact]
    public void AFileWithNoRegionAddedToAnEngineIsNotCounted()
    {
        Assert.Empty(EngineAddingClasses("public class Plain { [Fact] void F() { new SLuaEngine(); } }"));
    }
}
