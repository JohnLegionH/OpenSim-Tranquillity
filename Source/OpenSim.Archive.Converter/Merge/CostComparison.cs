/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>
/// The viewer's streaming cost of an archive before and after merging, the plain prims priced by the viewer's own estimate and the mesh parts by the
/// byte sizes of their levels (see <see cref="LandImpact"/>). A merge replaces cheap prims with meshes that hold real triangles, so the cost can rise
/// even while the part count falls; the report says so rather than leaving it to be found in a viewer.
/// </summary>
public sealed class CostComparison
{
    public CostComparison(double before, double after)
    {
        Before = before;
        After = after;
    }

    public double Before { get; }
    public double After { get; }
    public bool Heavier => After > Before;

    public static CostComparison Measure(string sourcePath, string mergedPath) =>
        new CostComparison(LandImpact.Measure(sourcePath).ViewerStreamingCostTotal, LandImpact.Measure(mergedPath).ViewerStreamingCostTotal);

    public string ToText()
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        string text = "viewer streaming cost: " + Before.ToString("0.00", inv) + " before merging, " + After.ToString("0.00", inv) + " after";
        if (!Heavier)
            return text + " (not heavier for viewers)";
        return text + " (heavier for viewers" + (Before > 0 ? ", " + (After / Before).ToString("0.0", inv) + " times" : "") + "; a larger --lod-pixel-limit lowers it at the price of a visible difference at the distance the viewer switches levels)";
    }
}
