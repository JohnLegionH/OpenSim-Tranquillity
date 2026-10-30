/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace OpenSim.Server.Base.Hosting;

/// <summary>The size, roll-over and retention arguments the log file sink is built with.</summary>
public readonly record struct LogFileSinkArguments(long FileSizeLimitBytes, bool RollOnFileSizeLimit, int RetainedFileCountLimit);

/// <summary>
/// The optional limits on the log files, from the <c>Startup</c> settings <c>LogFileSizeLimitMB</c> (a number of megabytes,
/// fractions allowed: the file rolls over to a new one when it reaches that size) and <c>LogFileRetainedFileCount</c>
/// (how many files to keep). A limit that is not set, or is not a positive number, leaves the file sink as it was before
/// the settings existed: a daily file, and the file sink library's own size limit and retention.
/// </summary>
public sealed record LogFileLimits(long? SizeLimitBytes, int? RetainedFileCount)
{
    /// <summary>
    /// The file sink library's own default size limit (1 GiB; with no roll-over a file stops growing there) and number of
    /// files kept (31), which the log has always used. A test compares them with the library's declared defaults.
    /// </summary>
    public const long DefaultFileSizeLimitBytes = 1073741824L;

    public const int DefaultRetainedFileCount = 31;

    public static LogFileLimits FromConfiguration(IConfiguration configuration)
    {
        long? size = null;
        int? retained = null;

        if (double.TryParse(configuration?["Startup:LogFileSizeLimitMB"], NumberStyles.Float, CultureInfo.InvariantCulture, out double megabytes)
            && double.IsFinite(megabytes) && megabytes > 0)
            size = (long)Math.Clamp(megabytes * 1024 * 1024, 1, long.MaxValue / 2);

        if (int.TryParse(configuration?["Startup:LogFileRetainedFileCount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            && count > 0)
            retained = count;

        return new LogFileLimits(size, retained);
    }

    public LogFileSinkArguments ToSinkArguments() => new(
        SizeLimitBytes ?? DefaultFileSizeLimitBytes,
        SizeLimitBytes.HasValue,
        RetainedFileCount ?? DefaultRetainedFileCount);
}
