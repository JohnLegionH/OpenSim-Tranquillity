/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Restore.Import;

/// <summary>One level (or the physics shape, or a picture) compared with its input.</summary>
public sealed class CheckLine
{
    public string What { get; set; }
    public int InputTriangles { get; set; }
    public int OutputTriangles { get; set; }
    /// <summary>Largest position difference in metres and texture-coordinate difference found.</summary>
    public float MaxPosition { get; set; }
    public float MaxUv { get; set; }
    public bool Ok { get; set; }
    public string Detail { get; set; }
}

/// <summary>The result of writing an imported archive back out to glTF and comparing it with the input.</summary>
public sealed class ImportCheckResult
{
    public bool Passed { get; set; }
    public List<CheckLine> Lines { get; } = new List<CheckLine>();
    public string ToText() => string.Empty;
}

public static class ImportCheck
{
    /// <summary>Position difference, in metres, the round trip may show (half a millimetre).</summary>
    public const float PositionTolerance = 0.0005f;

    public static ImportCheckResult Run(string archivePath, string folder, ImportOptions options, string workFolder) => new ImportCheckResult();
}
