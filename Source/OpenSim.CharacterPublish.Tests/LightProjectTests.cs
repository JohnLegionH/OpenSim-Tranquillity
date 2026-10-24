/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Reflection;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish.Tests;

/// <summary>
/// OpenSim.CharacterPublish is what the grid's inventory server loads, so it must not pull in the region code; and the
/// safe-description rule holds in the wearable writer as it does in the object writer.
/// </summary>
public class LightProjectTests
{
    static string FindProject(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Source", name, name + ".csproj");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("cannot find " + name + ".csproj above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheLightProjectAndEveryProjectItReferencesHaveNoRegionProject()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(FindProject("OpenSim.CharacterPublish"));
        while (pending.Count > 0)
        {
            var proj = pending.Pop();
            if (!seen.Add(proj)) continue;
            foreach (var r in XDocument.Load(proj).Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include")))
            {
                Assert.NotNull(r);
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(proj)!, r!.Replace('\\', '/')));
                Assert.DoesNotContain("OpenSim.Region", Path.GetFileNameWithoutExtension(target));
                pending.Push(target);
            }
        }
        // the light project, the baker's library and the mesh library: nothing else
        Assert.True(seen.Count >= 3, "expected the light project and its references, saw " + string.Join(", ", seen));
    }

    [Fact]
    public void TheBuiltAssemblyAndItsOpenSimDependenciesReferenceNoRegionAssembly()
    {
        var seen = new HashSet<string>();
        void Walk(Assembly a)
        {
            foreach (var r in a.GetReferencedAssemblies())
            {
                if (r.Name == null || !r.Name.StartsWith("OpenSim", StringComparison.Ordinal) || !seen.Add(r.Name)) continue;
                Assert.False(r.Name.StartsWith("OpenSim.Region", StringComparison.Ordinal), $"{a.GetName().Name} references {r.Name}");
                Walk(Assembly.Load(r));
            }
        }
        Walk(typeof(BundleValidator).Assembly);
    }

    [Theory]
    [InlineData("type 3", false)]
    [InlineData("  parameters are listed below", false)]
    [InlineData("textures", false)]
    [InlineData("typed by hand", true)]
    [InlineData("A skin of type 3", true)]
    [InlineData("", true)]
    public void TheSafeDescriptionRuleHolds(string description, bool safe) =>
        Assert.Equal(safe, SafeDescription.IsSafe(description));

    [Theory]
    [InlineData("type 1")]
    [InlineData("parameters 5")]
    [InlineData("textures")]
    public void TheWearableWriterRefusesAnUnsafeDescription(string description)
    {
        var doc = WearableFactory.Shape("Example Shape", description, Fixtures.Perms);
        var e = Assert.Throws<ArgumentException>(() => WearableWriter.Write(doc));
        Assert.Contains("description", e.Message);
    }

    [Fact]
    public void ADescriptionThatOpensWithAKeywordReallyBreaksTheBakersParser_SoTheRuleIsNeeded()
    {
        // Hand-written text, past the writer: the baker's parser takes the description line for a section header and
        // reads the next lines as parameters.
        var text = "LLWearable version 22\nName\nparameters 3\n\tpermissions 0\n\t{\n\t}\ntype 1\nparameters 0\ntextures 0\n";
        Assert.Throws<FormatException>(() => WearableParser.Parse(text));
    }

    [Fact]
    public void ASafeDescriptionWritesAndParses()
    {
        var doc = WearableFactory.Skin("Example Skin", "Mesh body: Example by Test User, CC BY 4.0, modified.", Fixtures.Perms, Fixtures.TexA, Fixtures.TexB, Fixtures.TexC);
        var text = WearableWriter.Write(doc);
        Assert.Equal(WearableKind.Skin, WearableParser.Parse(text).Kind);
    }

    [Fact]
    public void ContentIdsAreStableAndDependOnTheBytes()
    {
        var a = ContentId.OfBytes(new byte[] { 1, 2, 3 });
        Assert.Equal(a, ContentId.OfBytes(new byte[] { 1, 2, 3 }));
        Assert.NotEqual(a, ContentId.OfBytes(new byte[] { 1, 2, 4 }));
        Assert.NotEqual(UUID.Zero, a);
        Assert.Equal(ContentId.Derive("item", "x", "1"), ContentId.Derive("item", "x", "1"));
        Assert.NotEqual(ContentId.Derive("item", "x", "1"), ContentId.Derive("item", "x1"));
    }
}
