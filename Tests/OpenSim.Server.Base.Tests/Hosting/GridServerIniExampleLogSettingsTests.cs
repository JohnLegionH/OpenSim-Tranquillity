/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Configuration;
using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// The grid server (Robust) shares the logging setup with the region server, so it takes the <c>Startup</c> settings
/// <c>LogFileSizeLimitMB</c> and <c>LogFileRetainedFileCount</c>. Its shipped ini example documents them in the
/// <c>[Startup]</c> section, commented out so that a copy of the example behaves as before, and the example values it
/// shows are values the server accepts. The tests read the file in the source tree; nothing here is process-wide, so
/// they run in parallel.
/// </summary>
public sealed class GridServerIniExampleLogSettingsTests
{
    private static string ExamplePath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Source", "OpenSim.Server.GridServer", "AppData", "GridServer.ini.example");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("GridServer.ini.example was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The lines of the <c>[Startup]</c> section, up to the next section header.</summary>
    private static List<string> StartupSection()
    {
        var lines = new List<string>();
        bool inside = false;
        foreach (string raw in File.ReadAllLines(ExamplePath()))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inside = line.Equals("[Startup]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inside)
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>The value of a commented-out example line "; Key = value", or null when there is none.</summary>
    private static string? ExampleValue(string key)
    {
        foreach (string line in StartupSection())
        {
            string rest = line.TrimStart(';').Trim();
            if (line.StartsWith(';') && rest.StartsWith(key, StringComparison.Ordinal))
            {
                int eq = rest.IndexOf('=');
                if (eq > 0 && rest[..eq].Trim() == key)
                    return rest[(eq + 1)..].Trim();
            }
        }

        return null;
    }

    [Theory]
    [InlineData("LogFileSizeLimitMB")]
    [InlineData("LogFileRetainedFileCount")]
    public void TheStartupSectionShowsTheSettingAsACommentedOutExample(string key)
    {
        Assert.NotNull(ExampleValue(key));
    }

    [Theory]
    [InlineData("LogFileSizeLimitMB")]
    [InlineData("LogFileRetainedFileCount")]
    public void TheSettingIsNotSetInTheExample(string key)
    {
        Assert.Contains(StartupSection(), l => l.StartsWith(';') && l.Contains(key, StringComparison.Ordinal));
        Assert.DoesNotContain(StartupSection(), l => !l.StartsWith(';')
            && l.StartsWith(key, StringComparison.Ordinal));
    }

    [Fact]
    public void ACopyOfTheExampleLeavesTheLogLimitsAtTheirDefaults()
    {
        IConfiguration copyOfExample = new ConfigurationBuilder().AddInMemoryCollection(
            StartupSection()
                .Where(l => !l.StartsWith(';') && l.Contains('='))
                .Select(l => KeyValuePair.Create("Startup:" + l[..l.IndexOf('=')].Trim(), (string?)l[(l.IndexOf('=') + 1)..].Trim().Trim('"'))))
            .Build();

        LogFileLimits limits = LogFileLimits.FromConfiguration(copyOfExample);

        Assert.Null(limits.SizeLimitBytes);
        Assert.Null(limits.RetainedFileCount);
        Assert.Equal(
            new LogFileSinkArguments(LogFileLimits.DefaultFileSizeLimitBytes, false, LogFileLimits.DefaultRetainedFileCount),
            limits.ToSinkArguments());
    }

    [Fact]
    public void TheExampleValuesAreAcceptedByTheServer()
    {
        string? size = ExampleValue("LogFileSizeLimitMB");
        string? retained = ExampleValue("LogFileRetainedFileCount");
        Assert.NotNull(size);
        Assert.NotNull(retained);

        IConfiguration settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Startup:LogFileSizeLimitMB"] = size,
            ["Startup:LogFileRetainedFileCount"] = retained,
        }).Build();

        LogFileLimits limits = LogFileLimits.FromConfiguration(settings);

        Assert.NotNull(limits.SizeLimitBytes);
        Assert.NotNull(limits.RetainedFileCount);
    }
}
