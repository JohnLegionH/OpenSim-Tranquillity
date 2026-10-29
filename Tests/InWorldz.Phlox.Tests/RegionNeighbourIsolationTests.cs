/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Test regions share one grid store for the whole test process, and a region stays in it after its test has ended.
/// Every harness region sits at 1000,1000, so a region a test puts beside that one (up to 16 tiles away, the reach of
/// the largest variable-size region) would be found by any test that asks the grid what lies next to the harness
/// region at the same time: llEdgeOfWorld answers "not the edge" while the neighbour is registered. A test that needs
/// a second region therefore places it far from 1000,1000 (the existing tests use 7300 and up). This test reads the
/// test sources for region locations given as literals and names each one that is too near. A plain file read: it
/// touches no process-wide state, so it runs in parallel.
/// </summary>
public class RegionNeighbourIsolationTests
{
    private const int DefaultX = 1000, DefaultY = 1000, Reach = 16;

    private static readonly Regex HarnessRegion = new(@"regionX:\s*(?<x>\d+)", RegexOptions.Compiled);
    private static readonly Regex SetupSceneLocation = new(@"SetupScene\(\s*""[^""]*""\s*,\s*[^,]+,\s*(?<x>\d+)u?\s*,\s*(?<y>\d+)u?\s*[,)]", RegexOptions.Compiled);

    private static string TestsFolder([CallerFilePath] string here = "") => Path.GetDirectoryName(here);

    /// <summary>The region locations the text gives as literals (a harness region has y 1000), as "x,y".</summary>
    private static IEnumerable<(int X, int Y)> Locations(string text)
    {
        foreach (Match m in HarnessRegion.Matches(text))
            yield return (int.Parse(m.Groups["x"].Value), DefaultY);
        foreach (Match m in SetupSceneLocation.Matches(text))
            yield return (int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value));
    }

    private static bool TooNear((int X, int Y) at)
        => !(at.X == DefaultX && at.Y == DefaultY) && Math.Abs(at.X - DefaultX) <= Reach && Math.Abs(at.Y - DefaultY) <= Reach;

    [Fact]
    public void NoTestPlacesARegionBesideTheHarnessRegion()
    {
        var near = new List<string>();
        int seen = 0;
        foreach (string file in Directory.GetFiles(TestsFolder(), "*.cs").Where(f => Path.GetFileName(f) != nameof(RegionNeighbourIsolationTests) + ".cs"))
        {
            foreach (var at in Locations(File.ReadAllText(file)))
            {
                seen++;
                if (TooNear(at))
                    near.Add(Path.GetFileName(file) + " " + at.X + "," + at.Y);
            }
        }
        Assert.True(seen >= 6, "the scan found the regions the tests place (" + seen + ")");
        Assert.True(near.Count == 0, "a region this close to the harness region at 1000,1000 changes llEdgeOfWorld for tests running beside it: " + string.Join("; ", near.Distinct().OrderBy(n => n)));
    }

    [Theory]
    [InlineData("new SchedulerHarness(Configure, regionSize: 512, regionX: 1001)", 1001, 1000, true)]
    [InlineData("new SchedulerHarness(regionX: 7390)", 7390, 1000, false)]
    [InlineData("sh.SetupScene(\"Example Region B\", UUID.Random(), 1000, 1001)", 1000, 1001, true)]
    [InlineData("sh.SetupScene(\"Example Region B\", UUID.Random(), 7300, 7299)", 7300, 7299, false)]
    [InlineData("sh.SetupScene(\"Example Region\", UUID.Random(), 1016u, 984u, cfg)", 1016, 984, true)]
    [InlineData("sh.SetupScene(\"Example Region\", UUID.Random(), 1017, 1000)", 1017, 1000, false)]
    public void TheScanReadsAPlacementAndJudgesItsDistance(string source, int x, int y, bool tooNear)
    {
        var found = Locations(source).ToList();
        Assert.Single(found);
        Assert.Equal((x, y), found[0]);
        Assert.Equal(tooNear, TooNear(found[0]));
    }
}
