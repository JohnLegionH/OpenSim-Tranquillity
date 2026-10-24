/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenSim.Archive.Rewrite;
using Xunit;
using static OpenSim.Archive.Compare.Tests.CompareKit;

namespace OpenSim.Archive.Compare.Tests;

// No process-wide state: every test builds its own archives in its own files, so the class runs in parallel with others.
public class CompareTests
{
    private static CompareResult Run(string a, string b, string planJson = null) =>
        new ArchiveComparer().Compare(a, b, planJson == null ? null : ComparePlan.Parse(planJson));

    private static FixturePart Second(List<List<FixturePart>> builds) => builds[0][1];

    [Fact]
    public void IdentityCopy_Passes()
    {
        (string a, string b) = Pair((builds, assets) => { });
        CompareResult r = Run(a, b);
        Assert.True(r.Pass, r.ToText());
        Assert.Empty(r.Unplanned);
        Assert.Equal(4, r.PartsMatched);
        Assert.StartsWith("PASS", r.ToText());
    }

    [Fact]
    public void IdentityCopy_ThroughTheRewriter_Passes()
    {
        string a = Sample();
        string b = TempFile(".oar");
        new ArchiveRewriter().Rewrite(a, b);
        Assert.True(Run(a, b).Pass);
    }

    [Fact]
    public void Rename_WithPlan_Passes_WithoutPlan_Fails()
    {
        List<List<FixturePart>> builds = SampleBuilds();
        FixturePart target = Second(builds);
        string a = WriteArchive(builds, SampleAssets());
        target.Name = "Renamed";
        string b = WriteArchive(builds, SampleAssets());

        CompareResult without = Run(a, b);
        Assert.False(without.Pass);
        Assert.Single(without.Unplanned);
        Assert.Contains("name", without.Unplanned[0].Text);
        Assert.StartsWith("FAIL", without.ToText());

        string plan = "{\"version\":1,\"partChanges\":[{\"part\":\"" + target.Id + "\",\"field\":\"name\",\"to\":\"Renamed\"}]}";
        CompareResult with = Run(a, b, plan);
        Assert.True(with.Pass, with.ToText());
        Assert.Single(with.Planned);

        // The plan states a different name than the one found: still a failure.
        string wrong = plan.Replace("\"Renamed\"", "\"Something else\"");
        Assert.False(Run(a, b, wrong).Pass);
    }

    [Fact]
    public void PlannedChange_ThatDidNotHappen_Fails()
    {
        List<List<FixturePart>> builds = SampleBuilds();
        string a = WriteArchive(builds, SampleAssets());
        string b = WriteArchive(builds, SampleAssets());
        CompareResult r = Run(a, b, "{\"version\":1,\"partChanges\":[{\"part\":\"" + Second(builds).Id + "\",\"field\":\"name\"}]}");
        Assert.False(r.Pass);
        Assert.Single(r.UnmetPlan);
    }

    [Fact]
    public void SwappedLinkNumbers_Fail()
    {
        (string a, string b) = Pair((builds, assets) =>
        {
            (builds[0][1].LinkNum, builds[0][2].LinkNum) = (builds[0][2].LinkNum, builds[0][1].LinkNum);
        });
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Equal(2, r.Unplanned.Count(d => d.Field == "linkNum"));
    }

    [Fact]
    public void DroppedContentsItem_Fails()
    {
        (string a, string b) = Pair((builds, assets) => Second(builds).Items.RemoveAt(0));
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Difference d = Assert.Single(r.Unplanned);
        Assert.Equal("contents", d.Field);
        Assert.Equal("removed", d.Kind);
        Assert.Contains("Read me", d.Text);
    }

    [Fact]
    public void ChangedScriptAssetBytes_Fail()
    {
        (string a, string b) = Pair((builds, assets) =>
        {
            string key = assets.Keys.Single(k => k.EndsWith("_script.lsl"));
            assets[key] = Encoding.ASCII.GetBytes("default { state_entry() { llSay(0, \"changed\"); } }");
        });
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Difference d = Assert.Single(r.Unplanned);
        Assert.Equal("entry", d.Field);
        Assert.Contains("_script.lsl", d.Text);
        Assert.Contains("bytes differ", d.Text);
    }

    [Fact]
    public void ChangedCreator_Fails()
    {
        (string a, string b) = Pair((builds, assets) => builds[1][0].Creator = "b0b00000-0000-4000-8000-000000000002");
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Contains(r.Unplanned, d => d.Field == "creator" && d.PartName == "Lamp");
    }

    [Fact]
    public void ChangedPhysicsShapeType_NeedsThePlan()
    {
        List<List<FixturePart>> builds = SampleBuilds();
        FixturePart root = builds[0][0];
        root.PhysicsShapeType = "1";
        string a = WriteArchive(builds, SampleAssets());
        root.PhysicsShapeType = "0";
        string b = WriteArchive(builds, SampleAssets());

        CompareResult without = Run(a, b);
        Assert.False(without.Pass);
        Assert.Contains(without.Unplanned, d => d.Field == "physics");

        string plan = "{\"version\":1,\"partChanges\":[{\"part\":\"" + root.Id + "\",\"field\":\"physics\"}]}";
        Assert.True(Run(a, b, plan).Pass);
        // an unchanged type needs no plan
        Assert.True(Run(b, WriteArchive(builds, SampleAssets())).Pass);
    }

    [Fact]
    public void ExtraAsset_NotInThePlan_Fails_AndPassesWhenPlanned()
    {
        Guid extra = Guid.NewGuid();
        (string a, string b) = Pair((builds, assets) => assets["assets/" + extra + "_mesh.llmesh"] = new byte[] { 1, 2, 3 });

        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Difference d = Assert.Single(r.Unplanned);
        Assert.Equal("added", d.Kind);

        Assert.True(Run(a, b, "{\"version\":1,\"addedEntries\":[\"assets/" + extra + "_mesh.llmesh\"]}").Pass);
        // A bare asset id names the entry too.
        Assert.True(Run(a, b, "{\"version\":1,\"addedEntries\":[\"" + extra + "\"]}").Pass);
        // A planned asset that is not there is a failure.
        CompareResult missing = Run(a, WriteArchive(SampleBuilds(), SampleAssets()), "{\"version\":1,\"addedEntries\":[\"" + extra + "\"]}");
        Assert.False(missing.Pass);
    }

    [Fact]
    public void ShapeChangedToMesh_PassesWithThePlannedFaceCount_AndFailsWithAnother()
    {
        // A box with a hollow has 7 faces; as a mesh built the same way, 7.
        List<List<FixturePart>> builds = SampleBuilds();
        FixturePart target = builds[0][2];
        target.ProfileCurve = "1";
        target.ProfileHollow = "1";
        string a = WriteArchive(builds, SampleAssets());
        target.Mesh = true;
        string b = WriteArchive(builds, SampleAssets());

        string Plan(int faces) => "{\"version\":1,\"partChanges\":[{\"part\":\"" + target.Id + "\",\"field\":\"shape\",\"to\":\"mesh\",\"faces\":" + faces + "}]}";

        CompareResult unplanned = Run(a, b);
        Assert.False(unplanned.Pass);
        Assert.Contains(unplanned.Unplanned, d => d.Field == "shape");

        CompareResult ok = Run(a, b, Plan(7));
        Assert.True(ok.Pass, ok.ToText());

        CompareResult bad = Run(a, b, Plan(6));
        Assert.False(bad.Pass);
        Assert.True(bad.Unplanned.Any(d => d.Field == "faces" && d.Text.Contains("planned 6, found 7")), bad.ToText());
    }

    [Fact]
    public void ShapeChangedToMesh_WithAChangedFaceCount_PassesOnlyWhenStated()
    {
        List<List<FixturePart>> builds = SampleBuilds();
        FixturePart target = builds[0][2];           // a cylinder-like profile, 3 faces
        string a = WriteArchive(builds, SampleAssets());
        target.Mesh = true;                          // as a mesh the same fields give a box's 6 faces
        string b = WriteArchive(builds, SampleAssets());

        string Plan(int faces) => "{\"version\":1,\"partChanges\":[{\"part\":\"" + target.Id + "\",\"field\":\"shape\",\"to\":\"mesh\",\"faces\":" + faces + "}]}";
        Assert.True(Run(a, b, Plan(6)).Pass);
        Assert.False(Run(a, b, Plan(3)).Pass);
    }

    [Fact]
    public void PlannedShapeChange_WhereThePartIsNotAMesh_Fails()
    {
        List<List<FixturePart>> builds = SampleBuilds();
        FixturePart target = builds[0][2];
        string a = WriteArchive(builds, SampleAssets());
        target.ProfileHollow = "1";                  // a different shape, but not a mesh
        string b = WriteArchive(builds, SampleAssets());
        CompareResult r = Run(a, b, "{\"version\":1,\"partChanges\":[{\"part\":\"" + target.Id + "\",\"field\":\"shape\",\"to\":\"mesh\",\"faces\":4}]}");
        Assert.False(r.Pass);
    }

    [Fact]
    public void TextureEntryReplaced_NeedsThePlan()
    {
        List<List<FixturePart>> builds = SampleBuilds();
        string a = WriteArchive(builds, SampleAssets());
        builds[1][0].Texture = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";
        string b = WriteArchive(builds, SampleAssets());
        Assert.False(Run(a, b).Pass);
        Assert.True(Run(a, b, "{\"version\":1,\"partChanges\":[{\"part\":\"" + builds[1][0].Id + "\",\"field\":\"textureEntry\"}]}").Pass);
    }

    [Fact]
    public void PartMissingOrNew_Fails()
    {
        List<List<FixturePart>> builds = SampleBuilds();
        string a = WriteArchive(builds, SampleAssets());
        FixturePart gone = builds[0][2];
        builds[0].RemoveAt(2);
        string b = WriteArchive(builds, SampleAssets());

        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Contains(r.Unplanned, d => d.Field == "part" && d.Kind == "removed");
        Assert.True(Run(a, b, "{\"version\":1,\"removedParts\":[\"" + gone.Id + "\"]}").Pass);
        Assert.False(Run(b, a).Pass);
    }

    [Fact]
    public void ScriptState_Change_Fails()
    {
        // Script state lives beside the parts in the object file, not in an asset.
        List<List<FixturePart>> builds = SampleBuilds();
        builds[0][0].ScriptState = "state one";
        string a = WriteArchive(builds, SampleAssets());
        builds[0][0].ScriptState = "state two";
        string b = WriteArchive(builds, SampleAssets());
        CompareResult r = Run(a, b);
        Assert.False(r.Pass);
        Assert.Single(r.Unplanned, d => d.Field == "scriptStates");
    }

    [Fact]
    public void ComparingAnArchiveWithItself_IsRefused()
    {
        string x = Sample();
        Assert.Throws<CompareException>(() => Run(x, x));
    }

    [Fact]
    public void PlanWithAnUnknownMember_IsRefused()
    {
        Assert.Throws<CompareException>(() => ComparePlan.Parse("{\"version\":1,\"partchanges\":[],\"surprise\":1}"));
        Assert.Throws<CompareException>(() => ComparePlan.Parse("{\"version\":2}"));
        Assert.Throws<CompareException>(() => ComparePlan.Parse("{\"version\":1,\"partChanges\":[{\"part\":\"nope\",\"field\":\"name\"}]}"));
        Assert.Throws<CompareException>(() => ComparePlan.Parse("{\"version\":1,\"partChanges\":[{\"part\":\"" + Guid.NewGuid() + "\",\"field\":\"shape\",\"to\":\"mesh\"}]}"));
        Assert.Throws<CompareException>(() => ComparePlan.Parse("{\"version\":1,\"partChanges\":[{\"part\":\"" + Guid.NewGuid() + "\",\"field\":\"colour\"}]}"));
    }

    [Fact]
    public void UnreadableArchive_IsAToolFailure()
    {
        string junk = TempFile(".oar");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });
        Assert.Throws<CompareException>(() => Run(junk, Sample()));
        Assert.Throws<CompareException>(() => Run(Path.Combine(Path.GetTempPath(), "does-not-exist.oar"), Sample()));
    }

    [Fact]
    public void Report_IsShortAndGroupedByBuild()
    {
        (string a, string b) = Pair((builds, assets) =>
        {
            builds[0][1].Name = "Changed";
            builds[1][0].Description = "now described";
        });
        CompareResult r = Run(a, b);
        string[] lines = r.ToText().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("FAIL", lines[0].TrimEnd());
        Assert.Contains(lines, l => l.StartsWith("Tower @"));
        Assert.Contains(lines, l => l.StartsWith("Lamp @"));
        Assert.Contains("\"result\": \"FAIL\"", r.ToJson());
    }
}
