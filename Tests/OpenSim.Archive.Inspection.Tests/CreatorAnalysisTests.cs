/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;
using static OpenSim.Archive.Inspection.Tests.HandMade;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>The creator analysis, on archives made by hand so every number is known.</summary>
public class CreatorAnalysisTests
{
    private static readonly UUID Library = UUID.Parse(Constants.m_MrOpenSimIDString);

    private static ArchiveReport Parts(int count, Func<int, UUID> creator, Func<int, UUID> owner = null, Func<int, string> data = null, Func<int, int> date = null, AnalysisSettings settings = null)
    {
        byte[] oar = OarOfParts(count, i =>
        {
            UUID c = creator(i);
            UUID o = owner != null ? owner(i) : c;
            return (c, o, o, data?.Invoke(i), date != null ? date(i) : 1700000000 + i * 90000);
        });
        return Inspect(oar, settings == null ? null : new InspectorOptions { Analysis = settings });
    }

    private static string Codes(ArchiveReport r) => string.Join(",", r.CreatorAnalysis.Flags.Select(f => f.Code + "/" + f.Grouping));

    // ------------------------------------------------------------------ the edges

    [Theory]
    [InlineData(19, 0, false, null)]
    [InlineData(20, 0, true, "SINGLE_CREATOR")]
    [InlineData(100, 0, true, "SINGLE_CREATOR")]
    public void SingleCreator_NeedsEveryRecordAndAtLeastTwenty(int alice, int bob, bool fires, string code)
    {
        ArchiveReport r = Parts(alice + bob, i => i < alice ? Alice : Bob);
        Assert.Equal(fires, r.CreatorAnalysis.Fired);
        if (fires)
            Assert.Contains(r.CreatorAnalysis.Flags, f => f.Code == code && f.Grouping == "uuid" && f.CreatorId == Alice);
        Assert.Equal(fires ? 3 : 0, r.ExitCode);
    }

    [Theory]
    [InlineData(18, 2, true)]     // 90.0% of 20
    [InlineData(17, 3, false)]    // 85%
    [InlineData(19, 1, true)]     // 95%
    [InlineData(90, 10, true)]    // exactly 90% of 100
    [InlineData(89, 11, false)]   // 89%
    [InlineData(9, 1, false)]     // 90% but only ten records
    public void DominantCreator_NeedsNinetyPercentAndTwentyRecords(int alice, int bob, bool fires)
    {
        ArchiveReport r = Parts(alice + bob, i => i < alice ? Alice : Bob);
        Assert.Equal(fires, r.CreatorAnalysis.Fired);
        if (fires)
        {
            Assert.Contains(r.CreatorAnalysis.Flags, f => f.Code == "DOMINANT_CREATOR" && f.Count == alice);
            Assert.DoesNotContain(r.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR");
        }
    }

    [Fact]
    public void LibraryContentAndBlankCreators_AreCountedApartAndLeftOutOfTheShares()
    {
        // 20 by Alice among 100 library items: the library items are not counted, so Alice is the single creator
        ArchiveReport lib = Parts(120, i => i < 20 ? Alice : Library);
        Assert.Equal(100, lib.CreatorAnalysis.LibraryRecords);
        Assert.Equal(20, lib.CreatorAnalysis.CountedRecords);
        Assert.Contains(lib.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR" && f.CreatorId == Alice);

        // 19 by Alice and 60 blank: only 19 counted, so no flag
        ArchiveReport blank = Parts(79, i => i < 19 ? Alice : UUID.Zero);
        Assert.Equal(60, blank.CreatorAnalysis.BlankRecords);
        Assert.Equal(19, blank.CreatorAnalysis.CountedRecords);
        Assert.False(blank.CreatorAnalysis.Fired);

        ArchiveReport blank20 = Parts(80, i => i < 20 ? Alice : UUID.Zero);
        Assert.Contains(blank20.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR");

        // the library owner is reported as library content in the table too
        Assert.True(lib.Creators.Single(c => c.Id == Library).IsLibrary);
    }

    [Fact]
    public void GroupingByName_FiresWhenTheIdsDiffer()
    {
        // twenty different creator ids, all saying they are the same person on the same grid
        ArchiveReport r = Parts(20, i => new UUID("00000000-0000-4000-8000-" + (0x7000 + i).ToString("x12")),
            data: i => "http://grid.example.org:8002/;Dave Builder");
        Assert.Contains(r.CreatorAnalysis.Flags, f => f.Grouping == "name" && f.Code == "SINGLE_CREATOR" && f.Creator.Contains("Dave Builder"));
        Assert.DoesNotContain(r.CreatorAnalysis.Flags, f => f.Grouping == "uuid");
        Assert.Contains("name", Codes(r));
    }

    [Fact]
    public void ThresholdsAreSettings()
    {
        AnalysisSettings s = new AnalysisSettings { MinRecords = 5 };
        Assert.True(Parts(5, i => Alice, settings: s).CreatorAnalysis.Fired);
        Assert.False(Parts(4, i => Alice, settings: s).CreatorAnalysis.Fired);

        AnalysisSettings strict = new AnalysisSettings { DominantShare = 0.99 };
        Assert.False(Parts(100, i => i < 95 ? Alice : Bob, settings: strict).CreatorAnalysis.Fired);
    }

    // ------------------------------------------------------------------ the three matches

    [Fact]
    public void SameAccountMatch_GivesBothReadings()
    {
        // creator, owner and last owner are one account, no creator data, dates spread out
        ArchiveReport r = Parts(30, i => Dave, date: i => 1500000000 + i * 5_000_000);
        Assert.Contains(r.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR");
        AnalysisMatch m = Assert.Single(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_SAME_ACCOUNT");
        Assert.Contains("30 of 30", m.Statement);
        Assert.Contains("estate owner", m.Statement);
        Assert.Contains("--no-defaultuser", m.Statement);
        Assert.Contains("--lookup-aliases", m.Statement);
        Assert.DoesNotContain(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_BULK_DATES");
        Assert.DoesNotContain(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_ONE_BUILDER");
        Assert.Equal(LoaderBehaviour.Advisory, r.CreatorAnalysis.Advisory);
    }

    [Fact]
    public void BulkDatesMatch_SaysLoadsKeepTheDate()
    {
        // one creator with creator data, but 28 of 30 dated within three hours
        ArchiveReport r = Parts(30, i => Alice, owner: i => i % 2 == 0 ? Bob : Carol, data: i => "http://grid.example.org:8002/;Ann Builder",
            date: i => i < 28 ? 1650000000 + i * 300 : 1400000000 + i);
        AnalysisMatch m = Assert.Single(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_BULK_DATES");
        Assert.Contains("28 of 30", m.Statement);
        Assert.Contains("24-hour", m.Statement);
        Assert.Contains("Neither loader changes CreationDate", m.Statement);
        Assert.DoesNotContain(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_SAME_ACCOUNT");
        Assert.DoesNotContain(r.CreatorAnalysis.Matches, x => x.Id == "MATCH_ONE_BUILDER");
    }

    [Fact]
    public void BulkDatesMatch_EdgeIsEightyPercent()
    {
        // 24 of 30 inside the window is exactly 80%
        ArchiveReport edge = Parts(30, i => Alice, date: i => i < 24 ? 1650000000 + i * 60 : 1300000000 + i * 3_000_000);
        Assert.Contains(edge.CreatorAnalysis.Matches, x => x.Id == "MATCH_BULK_DATES");
        ArchiveReport under = Parts(30, i => Alice, date: i => i < 23 ? 1650000000 + i * 60 : 1300000000 + i * 3_000_000);
        Assert.DoesNotContain(under.CreatorAnalysis.Matches, x => x.Id == "MATCH_BULK_DATES");
    }

    [Fact]
    public void OneBuilderMatch_NeedsOneHomeAndNameSpreadDatesAndVariedOwners()
    {
        UUID[] owners = { Alice, Bob, Carol, Dave, UUID.Parse("0e0e0e00-0000-4000-8000-000000000099") };
        ArchiveReport r = Parts(30, i => Alice, owner: i => owners[i % owners.Length],
            data: i => "http://grid.example.org:8002/;Ann Builder", date: i => 1400000000 + i * 4_000_000);
        Assert.True(r.CreatorAnalysis.Fired);
        AnalysisMatch m = Assert.Single(r.CreatorAnalysis.Matches);
        Assert.Equal("MATCH_ONE_BUILDER", m.Id);
        Assert.Contains("5 different owners", m.Statement);

        // two different names on the grid is not one builder
        ArchiveReport two = Parts(30, i => Alice, owner: i => owners[i % owners.Length],
            data: i => "http://grid.example.org:8002/;" + (i % 2 == 0 ? "Ann Builder" : "Anna Builder"), date: i => 1400000000 + i * 4_000_000);
        Assert.DoesNotContain(two.CreatorAnalysis.Matches, x => x.Id == "MATCH_ONE_BUILDER");
        Assert.Contains(two.CreatorAnalysis.Matches, x => x.Id == "MATCH_NONE");
    }

    [Fact]
    public void NoFlag_NoMatchesAndNoAdvisory()
    {
        ArchiveReport r = Parts(30, i => i % 3 == 0 ? Alice : i % 3 == 1 ? Bob : Carol);
        Assert.False(r.CreatorAnalysis.Fired);
        Assert.Empty(r.CreatorAnalysis.Matches);
        Assert.Null(r.CreatorAnalysis.Advisory);
        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("CREATOR ANALYSIS", TextReport.Build(r, false));
    }

    [Fact]
    public void TextReportPutsTheAnalysisFirstAndAlwaysPrintsTheAdvisory()
    {
        ArchiveReport r = Parts(25, i => Dave);
        string text = TextReport.Build(r, false);
        Assert.StartsWith("CREATOR ANALYSIS", text);
        Assert.Contains(LoaderBehaviour.Advisory, text);
        Assert.Contains(LoaderBehaviour.EditNote, text);
        Assert.Contains("SINGLE_CREATOR", text);
    }

    // ------------------------------------------------------------------ IAR forms

    [Fact]
    public void Iar12_CreatorIsOwner_MatchesSameAccountEvenThoughTheSaverStampedCreatorData()
    {
        UUID owner = Dave;
        byte[] iar = IarOfItems(25, i => (owner, owner, "http://grid.example.org:8002/" + owner + ";Dave Builder", "ospa:n=Dave Builder", owner.ToString(), 1700000000 + i * 90000), "1.2");
        ArchiveReport r = Inspect(iar);
        Assert.Contains(r.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR" && f.Grouping == "uuid");
        Assert.Contains(r.CreatorAnalysis.Matches, m => m.Id == "MATCH_SAME_ACCOUNT" && m.Statement.Contains("loading the archive"));
    }

    [Fact]
    public void Iar03_OspaNamesAlone_FireByNameButCannotBeTiedToTheOwner()
    {
        byte[] iar = IarOfItems(25, i => (UUID.Zero, Dave, null, "ospa:n=Dave Builder", null, 1700000000 + i * 90000), "0.3");
        ArchiveReport r = Inspect(iar);
        Assert.Contains(r.CreatorAnalysis.Flags, f => f.Code == "SINGLE_CREATOR" && f.Grouping == "name");
        Assert.DoesNotContain(r.CreatorAnalysis.Matches, m => m.Id == "MATCH_SAME_ACCOUNT");   // a 0.x item names no creator id
        Assert.Equal(3, r.ExitCode);
    }
}
