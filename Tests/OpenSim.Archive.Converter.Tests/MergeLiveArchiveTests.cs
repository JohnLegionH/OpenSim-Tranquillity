/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// An archive for loading into a region: scripted linksets whose scripts write what they see (link count, their own link number, names and
/// descriptions of the parts they address, the root) into their own hover text, which a save of the region writes back into the archive. Loaded
/// from the original and from the merged archive, the hover texts show side by side what the scripts saw in each. The test writes the archive
/// where OARX_LIVE_ARCHIVE_OUT names, and does nothing when the variable is not set.
/// </summary>
public class MergeLiveArchiveTests
{
    public const string OutVariable = "OARX_LIVE_ARCHIVE_OUT";

    /// <summary>The script variants: a tag, and the body that builds the string r the script shows.</summary>
    internal static readonly (string Tag, string Body)[] Variants =
    {
        ("count", "string r = \"np=\" + (string)llGetNumberOfPrims();"),
        ("self", "string r = \"me=\" + (string)llGetLinkNumber() + \" np=\" + (string)llGetNumberOfPrims();"),
        ("literal", "list d = llGetLinkPrimitiveParams(4, [PRIM_NAME, PRIM_DESC]); string r = \"4=\" + llList2String(d, 0) + \"|\" + llList2String(d, 1) + \" k=\" + llGetLinkName(4);"),
        ("target", "list d = llGetLinkPrimitiveParams(LINK_THIS, [PRIM_LINK_TARGET, 4, PRIM_NAME, PRIM_DESC]); string r = \"t4=\" + llList2String(d, 0) + \"|\" + llList2String(d, 1);"),
        ("root", "string r = \"root=\" + llGetLinkName(LINK_ROOT) + \"|\" + llGetLinkName(1);"),
        ("sit", "llLinkSitTarget(4, <0.0, 0.0, 0.5>, ZERO_ROTATION); string r = \"sit target set on 4\";"),
        ("free", "string r = \"own \" + llGetObjectName() + \"|\" + llGetObjectDesc();")
    };

    internal static string ScriptText(string body) =>
        "default\n{\n    state_entry()\n    {\n        llSleep(2.0);\n        " + body + "\n        llSetText(r, <1.0, 1.0, 1.0>, 1.0);\n    }\n}\n";

    internal static string Write(string path)
    {
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        List<List<KitPart>> builds = new List<List<KitPart>>();
        foreach ((string tag, string body) in Variants)
        {
            List<KitPart> parts = Enumerable.Range(1, 8).Select(j => new KitPart("V " + tag + " part " + j, j)
            {
                Description = "V " + tag + " desc " + j, Offset = (j * 2) + ",0,0", Scale = "1,1,1"
            }).ToList();
            parts[7].ExtraXml = "<SitTargetPosition><X>0</X><Y>0</Y><Z>0.5</Z></SitTargetPosition>";
            var (item, entry, data) = EligibilityKit.Script("report " + tag, ScriptText(body));
            parts[1].Items.Add(item);
            assets[entry] = data;
            builds.Add(parts);
        }
        string source = EligibilityKit.WriteArchive(builds, assets, PlanarFixture.FlatTerrain());
        File.Copy(source, path, true);
        return path;
    }

    [Fact]
    public void WritesTheArchiveForTheRegionWhenAsked()
    {
        string path = Environment.GetEnvironmentVariable(OutVariable);
        if (string.IsNullOrEmpty(path))
            return;
        Write(path);
        MergePlan plan = ArchiveMergePlanner.Plan(path, new MergeOptions());
        // the archive is worth loading only if it merges something and leaves the linksets that count their parts alone
        Assert.True(plan.MergedParts > 0);
        Assert.Contains(plan.Linksets, l => l.LockedBy.Length > 0);
    }
}
