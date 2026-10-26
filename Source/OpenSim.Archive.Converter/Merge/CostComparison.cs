/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Converter.Merge;

/// <summary>The viewer's streaming cost of an archive before and after merging (not yet written).</summary>
public sealed class CostComparison
{
    public CostComparison(double before, double after) => throw new NotImplementedException();
    public double Before => throw new NotImplementedException();
    public double After => throw new NotImplementedException();
    public bool Heavier => throw new NotImplementedException();
    public static CostComparison Measure(string sourcePath, string mergedPath) => throw new NotImplementedException();
    public string ToText() => throw new NotImplementedException();
}
