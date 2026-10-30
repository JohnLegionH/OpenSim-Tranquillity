/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.Base.Hosting;
using Serilog.Configuration;
using Serilog.Formatting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// The log file can be given a size limit (the file rolls over to a new one when it is reached) and a number of files to
/// keep, with the <c>Startup</c> settings <c>LogFileSizeLimitMB</c> and <c>LogFileRetainedFileCount</c>. With neither set
/// the file sink gets the arguments it had before the settings existed: a daily file, and the file sink library's own
/// limit and retention. The logging setup touches process-wide state (console sink, log paths), so these run in the
/// serial collection.
/// </summary>
[Collection("MainConsole")]
public sealed class LogFileLimitsTests
{
    private const string SizeKey = "Startup:LogFileSizeLimitMB";
    private const string RetainedKey = "Startup:LogFileRetainedFileCount";

    private static IConfiguration Settings(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => KeyValuePair.Create(p.Key, (string?)p.Value)))
            .Build();

    /// <summary>The file sink overload the servers call, so that its declared defaults can be read.</summary>
    private static MethodInfo FileSinkOverload() =>
        typeof(Serilog.FileLoggerConfigurationExtensions).GetMethods()
            .Where(m => m.Name == "File"
                && m.GetParameters()[0].ParameterType == typeof(LoggerSinkConfiguration)
                && m.GetParameters().Any(p => p.Name == "rollOnFileSizeLimit")
                && m.GetParameters()[1].ParameterType == typeof(ITextFormatter))
            .OrderByDescending(m => m.GetParameters().Length)
            .First();

    /// <summary>A default as the library declares it (a constant of the literal's own type, so converted by the caller).</summary>
    private static object? DeclaredDefault(string parameter) =>
        FileSinkOverload().GetParameters().Single(p => p.Name == parameter).DefaultValue;

    /// <summary>
    /// Builds the logging the way the servers do, with the given settings, runs <paramref name="act"/> and returns the
    /// folder the log files were written to (the pipeline is disposed first, so everything is flushed).
    /// </summary>
    private static string WriteWith(Action<ILoggerFactory> act, string? statsFile = null, params (string Key, string Value)[] settings)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "log-limits-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "Example.log");
        string? stats = statsFile is null ? null : Path.Combine(dir, statsFile);

        ConsoleBase savedConsole = OpenSimConsoleLogSink.Console;
        LogLevel savedMinimum = OpenSimConsoleLogSink.MinimumLevel;
        CategoryLogLevels savedLevels = OpenSimConsoleLogSink.CategoryLevels;

        try
        {
            IConfiguration configuration = Settings(settings);
            ServiceCollection services = new();
            services.AddLogging(b => b.ClearProviders().AddOpenSimLogging("Example", dir, file, stats, configuration));

            using (ServiceProvider provider = services.BuildServiceProvider())
                act(provider.GetRequiredService<ILoggerFactory>());

            return dir;
        }
        finally
        {
            OpenSimConsoleLogSink.Console = savedConsole;
            OpenSimConsoleLogSink.MinimumLevel = savedMinimum;
            OpenSimConsoleLogSink.CategoryLevels = savedLevels;
        }
    }

    private static void WriteLines(ILoggerFactory factory, string category, int count, int width)
    {
        ILogger log = factory.CreateLogger(category);
        string padding = new('x', width);

        for (int i = 0; i < count; i++)
            log.LogWarning($"[LINE {i:D5}] {padding}");
    }

    private static string[] LogFiles(string dir, string prefix) =>
        Directory.GetFiles(dir, prefix + "*.log").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    [Fact]
    public void WithNothingSetTheSinkArgumentsAreTheFileSinkLibrarysOwnDefaults()
    {
        LogFileSinkArguments args = LogFileLimits.FromConfiguration(Settings()).ToSinkArguments();

        Assert.Equal(Convert.ToInt64(DeclaredDefault("fileSizeLimitBytes")), args.FileSizeLimitBytes);
        Assert.Equal(Convert.ToBoolean(DeclaredDefault("rollOnFileSizeLimit")), args.RollOnFileSizeLimit);
        Assert.Equal(Convert.ToInt32(DeclaredDefault("retainedFileCountLimit")), args.RetainedFileCountLimit);
    }

    [Fact]
    public void TheTwoSettingsAreReadFromTheStartupSection()
    {
        LogFileLimits limits = LogFileLimits.FromConfiguration(Settings((SizeKey, "5"), (RetainedKey, "7")));
        LogFileSinkArguments args = limits.ToSinkArguments();

        Assert.Equal(5L * 1024 * 1024, args.FileSizeLimitBytes);
        Assert.True(args.RollOnFileSizeLimit);
        Assert.Equal(7, args.RetainedFileCountLimit);
    }

    [Fact]
    public void ASizeInMegabytesMayBeAFraction()
    {
        LogFileSinkArguments args = LogFileLimits.FromConfiguration(Settings((SizeKey, "0.5"))).ToSinkArguments();

        Assert.Equal(512L * 1024, args.FileSizeLimitBytes);
        Assert.True(args.RollOnFileSizeLimit);
    }

    [Fact]
    public void ARetainedCountAloneLeavesTheSizeAtTheDefaultWithNoRollOver()
    {
        LogFileSinkArguments args = LogFileLimits.FromConfiguration(Settings((RetainedKey, "4"))).ToSinkArguments();

        Assert.Equal(4, args.RetainedFileCountLimit);
        Assert.Equal(Convert.ToInt64(DeclaredDefault("fileSizeLimitBytes")), args.FileSizeLimitBytes);
        Assert.False(args.RollOnFileSizeLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    [InlineData("")]
    public void ASettingThatIsNotAPositiveNumberLeavesTheDefaults(string value)
    {
        LogFileSinkArguments args = LogFileLimits.FromConfiguration(Settings((SizeKey, value), (RetainedKey, value))).ToSinkArguments();

        Assert.Equal(Convert.ToInt64(DeclaredDefault("fileSizeLimitBytes")), args.FileSizeLimitBytes);
        Assert.Equal(Convert.ToBoolean(DeclaredDefault("rollOnFileSizeLimit")), args.RollOnFileSizeLimit);
        Assert.Equal(Convert.ToInt32(DeclaredDefault("retainedFileCountLimit")), args.RetainedFileCountLimit);
    }

    [Fact]
    public void WithNothingSetTheLogStaysOneDailyFileHoweverLongItGrows()
    {
        string dir = WriteWith(f => WriteLines(f, "Example.Area", 300, 700));

        string[] files = LogFiles(dir, "Example");
        Assert.Single(files);
        Assert.True(new FileInfo(files[0]).Length > 200_000, "the test should write well past the small size used below");
    }

    [Fact]
    public void AFileThatReachesTheSizeLimitContinuesInANewFileAndLosesNoLine()
    {
        string dir = WriteWith(f => WriteLines(f, "Example.Area", 300, 700), null, (SizeKey, "0.05"));

        string[] files = LogFiles(dir, "Example");
        Assert.True(files.Length > 1, $"expected the log to roll over, found {files.Length} file(s)");
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 0.05 * 1024 * 1024 + 2000, $"{Path.GetFileName(f)} is over the limit"));

        string all = string.Concat(files.Select(File.ReadAllText));
        for (int i = 0; i < 300; i += 37)
            Assert.Contains($"[LINE {i:D5}]", all);
        Assert.Contains("[LINE 00299]", all);
    }

    [Fact]
    public void OnlyTheRetainedNumberOfFilesIsKeptAndTheNewestLinesSurvive()
    {
        string dir = WriteWith(f => WriteLines(f, "Example.Area", 300, 700), null, (SizeKey, "0.05"), (RetainedKey, "3"));

        string[] files = LogFiles(dir, "Example");
        Assert.Equal(3, files.Length);
        Assert.Contains("[LINE 00299]", string.Concat(files.Select(File.ReadAllText)));
        Assert.DoesNotContain("[LINE 00000]", string.Concat(files.Select(File.ReadAllText)));
    }

    [Fact]
    public void TheStatsLogFileGetsTheSameLimits()
    {
        string dir = WriteWith(f => WriteLines(f, "special.StatsLogger", 300, 700), "ExampleStats.log", (SizeKey, "0.05"));

        Assert.True(LogFiles(dir, "ExampleStats").Length > 1, "the stats log should roll over too");
    }
}
