/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;
using static OpenSim.Archive.Inspection.Tests.HandMade;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>The command-line tool: arguments, exit codes, output files. Console output is captured per call.</summary>
[Collection(ConsoleCollection.Name)]
public class ToolTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "archive-inspector-tool-tests");

    private static string Write(string name, byte[] data)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static (int code, string output, string error) Run(params string[] args)
    {
        TextWriter oldOut = Console.Out, oldErr = Console.Error;
        StringWriter o = new StringWriter(), e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        try
        {
            int code = OpenSim.ArchiveInspector.Program.Main(args);
            return (code, o.ToString(), e.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static byte[] SomeParts(Func<int, UUID> creator) => OarOfParts(25, i => (creator(i), creator(i), creator(i), null, 1700000000 + i * 100000));

    [Fact]
    public void ExitCodes_ReadFully_Flag_Partial_NotAnArchive()
    {
        string clean = Write("clean.oar", SomeParts(i => i % 3 == 0 ? Alice : i % 3 == 1 ? Bob : Carol));
        Assert.Equal(0, Run(clean).code);

        string flagged = Write("flagged.oar", SomeParts(i => Dave));
        (int code, string output, _) = Run(flagged);
        Assert.Equal(3, code);
        Assert.StartsWith("CREATOR ANALYSIS", output);

        byte[] whole = SomeParts(i => Alice);
        string cut = Write("cut.oar", whole.Take(whole.Length / 2).ToArray());
        Assert.Equal(1, Run(cut).code);

        string junk = Write("junk.oar", new byte[3000]);
        (int jc, string jo, _) = Run(junk);
        Assert.Equal(2, jc);
        Assert.Contains("Not an OAR or IAR", jo);

        Assert.Equal(2, Run(Path.Combine(Folder, "does-not-exist.oar")).code);
    }

    [Fact]
    public void KindComesFromTheControlFile_NotTheExtension()
    {
        string asIar = Write("really-an-oar.iar", SomeParts(i => Alice));
        Assert.Contains("kind: Oar", Run(asIar).output);
    }

    [Fact]
    public void Json_WritesTheFullReportWithASchemaVersion_AndStatsPrint()
    {
        string archive = Write("flagged.oar", SomeParts(i => Dave));
        string json = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".json");
        (int code, string output, _) = Run(archive, "--json", json, "--stats", "--all");

        Assert.Equal(3, code);
        Assert.Contains("wall time:", output);
        Assert.Contains("peak working set:", output);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
        JsonElement root = doc.RootElement;
        Assert.Equal("1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("Complete", root.GetProperty("status").GetString());
        Assert.Equal(25, root.GetProperty("partCount").GetInt64());
        Assert.Equal(25, root.GetProperty("parts").GetArrayLength());
        Assert.True(root.GetProperty("creatorAnalysis").GetProperty("fired").GetBoolean());
        Assert.Equal("SINGLE_CREATOR", root.GetProperty("creatorAnalysis").GetProperty("flags")[0].GetProperty("code").GetString());
        Assert.Equal(Dave.ToString(), root.GetProperty("creators")[0].GetProperty("id").GetString());
        Assert.True(root.GetProperty("stats").GetProperty("wallSeconds").GetDouble() >= 0);
    }

    [Fact]
    public void TopFifty_UnlessAll()
    {
        string archive = Write("many.oar", OarOfParts(80, i => (Alice, Alice, Alice, null, 1700000000 + i * 100000)));
        string brief = Run(archive).output;
        string full = Run(archive, "--all").output;
        Assert.Contains("more (use --all)", brief);
        Assert.DoesNotContain("more (use --all)", full);
        Assert.True(full.Length > brief.Length);
    }

    [Fact]
    public void BadArguments_ExitSixtyFour_AndNeverTouchTheArchive()
    {
        Assert.Equal(64, Run().code);
        Assert.Equal(64, Run("a.oar", "b.oar").code);
        Assert.Equal(64, Run("a.oar", "--json").code);
        Assert.Equal(64, Run("a.oar", "--extract").code);

        string archive = Write("same.oar", SomeParts(i => Alice));
        byte[] before = File.ReadAllBytes(archive);
        Assert.Equal(64, Run(archive, "--json", archive).code);   // refuses to overwrite the archive itself
        Assert.Equal(before, File.ReadAllBytes(archive));
    }

    [Fact]
    public void ReadsTheArchiveWithoutChangingItOrWritingBesideIt()
    {
        string archive = Write("readonly.oar", SomeParts(i => Alice));
        byte[] before = File.ReadAllBytes(archive);
        DateTime stamp = File.GetLastWriteTimeUtc(archive);
        string[] filesBefore = Directory.GetFiles(Folder).OrderBy(x => x).ToArray();
        Run(archive, "--all");
        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(archive));
        Assert.Equal(filesBefore, Directory.GetFiles(Folder).OrderBy(x => x).ToArray());
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleCollection
{
    /// <summary>The tool tests redirect Console.Out and Console.Error, which belong to the whole process.</summary>
    public const string Name = "console";
}

public class PublicSurfaceTests
{
    [Fact]
    public void TheBuiltLibraryExportsEveryNewPublicType()
    {
        string[] expected =
        {
            "ArchiveInspector", "ArchiveReport", "ArchiveInfo", "RegionInfo", "ArchiveKind", "InspectionStatus", "RecordContext", "ItemSource", "ReferenceKind", "Certainty",
            "ArchiveRecord", "CreatorRef", "PermissionMasks", "LinksetRecord", "PartRecord", "ItemRecord", "FolderRecord", "ParcelRecord", "RegionSettingsRecord", "AssetRecord",
            "ReferrerInfo", "ReferenceRecord", "BoxedObjectRecord", "LegacyPayloadRecord", "ProblemRecord", "TextAssetBody", "IArchiveCheck", "ArchiveCheckBase", "Finding", "FindingSeverity",
            "AnalysisSettings", "InspectorOptions", "CreatorAnalysisCheck", "LoaderBehaviour", "PartyTotals", "AssetTypeSummary", "AssetUse", "AssetUser", "LegacySummary", "CreatorFlag",
            "AnalysisMatch", "CreatorAnalysisResult", "RetentionInfo", "RunStats", "TarStreamReader", "TarEntryHeader", "TarFormatException", "ArchiveLimitException", "CountingStream",
            "WellKnownAssets", "Sanitize", "ReportJson", "TextReport"
        };
        HashSet<string> exported = new HashSet<string>(typeof(ArchiveInspector).Assembly.GetExportedTypes().Select(t => t.Name));
        foreach (string name in expected)
            Assert.True(exported.Contains(name), "public type missing from the built library: " + name);
    }
}
