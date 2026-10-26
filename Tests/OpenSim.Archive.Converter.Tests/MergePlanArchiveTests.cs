/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The merge plan read from real fixture archives, and the command line that writes it. Each test writes its own archive and
/// its own output folder, and nothing is shared between tests, so the class runs in parallel with others.
/// </summary>
public class MergePlanArchiveTests
{
    private static KitPart Box(int link, string name = null) =>
        new KitPart(name ?? "Box " + link, link) { Offset = (link * 2) + ",0,0", Scale = "1,1,1" };

    private static List<KitPart> Linkset(int count, Action<List<KitPart>> change = null)
    {
        List<KitPart> parts = Enumerable.Range(1, count).Select(i => Box(i)).ToList();
        change?.Invoke(parts);
        return parts;
    }

    private static MergePlan PlanOf(params List<KitPart>[] builds) =>
        ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(builds.ToList()), new MergeOptions());

    private static MergePlan PlanOf(List<List<KitPart>> builds, Dictionary<string, byte[]> assets) =>
        ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(builds, assets), new MergeOptions());

    private static KeptPartPlan Kept(MergePlan plan, KitPart p) => Assert.Single(plan.Linksets.SelectMany(l => l.Kept), k => k.PartId == p.Id);

    private static string Folder() => Path.Combine(Path.GetTempPath(), "merge-plan-tests", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- the plan from an archive

    [Fact]
    public void PlainPartsOfOneObjectArePlannedAsOneMergedPart()
    {
        MergePlan plan = PlanOf(Linkset(3));
        Assert.Equal(3, plan.PartsBefore);
        Assert.Equal(1, plan.PartsAfter);
        Assert.Equal(1, plan.MergedParts);
        Assert.Equal(6 + 6 + 6, plan.Linksets[0].Merged[0].Slots.Sum(s => s.Vertices) / 4);
    }

    [Fact]
    public void ObjectsOfOnePartAreKeptAndNothingMergesAcrossObjects()
    {
        KitPart a = Box(0), b = Box(0);
        MergePlan plan = PlanOf(new List<KitPart> { a }, new List<KitPart> { b });
        Assert.Equal(0, plan.MergedParts);
        Assert.Contains(Kept(plan, a).Rules, r => r.Rule == "7");
        Assert.Contains(Kept(plan, b).Rules, r => r.Rule == "7");
    }

    [Fact]
    public void ThePlanIsTheSameOnEveryRun()
    {
        string path = EligibilityKit.WriteArchive(new List<List<KitPart>> { Linkset(5), Linkset(4) });
        string a = MergePlanFiles.ArchiveJson(ArchiveMergePlanner.Plan(path, new MergeOptions()));
        string b = MergePlanFiles.ArchiveJson(ArchiveMergePlanner.Plan(path, new MergeOptions()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void APartWithASitTargetAHoverTextOrADifferentSurfaceMaterialIsHandledByItsOwnFields()
    {
        List<KitPart> parts = Linkset(6, p =>
        {
            p[2].ExtraXml = "<SitTargetPosition><X>0</X><Y>0</Y><Z>0.5</Z></SitTargetPosition>";
            p[3].ExtraXml = "<Text>Open</Text>";
            p[4].ExtraXml = "<Material>3</Material>";
        });
        MergePlan plan = PlanOf(parts);
        Assert.Contains(Kept(plan, parts[2]).Rules, r => r.Code == "SIT_TARGET");
        Assert.Contains(Kept(plan, parts[3]).Rules, r => r.Code == "HOVER_TEXT");
        Assert.Contains(Kept(plan, parts[4]).Rules, r => r.Code == "SOLE_IN_CLASS");
        Assert.Equal(new[] { 1, 2, 6 }, plan.Linksets[0].Merged.Single().Members.Select(m => m.LinkNum));
    }

    [Fact]
    public void ALightAndAPhysicalPartStayAndAPhantomPartMergesOnlyWithPhantomParts()
    {
        List<KitPart> parts = Linkset(7, p =>
        {
            p[2].Light = true;
            p[3].Flags = "Physics";
            p[4].Flags = "Phantom";
            p[5].Flags = "Phantom";
        });
        MergePlan plan = PlanOf(parts);
        Assert.Contains(Kept(plan, parts[2]).Rules, r => r.Code == "LIGHT");
        Assert.Contains(Kept(plan, parts[3]).Rules, r => r.Code == "PHYSICAL");
        MergedPartPlan phantom = plan.Linksets[0].Merged.Single(m => m.Members.Any(x => x.LinkNum == 5));
        Assert.Equal(new[] { 5, 6 }, phantom.Members.Select(x => x.LinkNum));
    }

    [Fact]
    public void ADifferentCreatorKeepsAPartOut()
    {
        List<KitPart> parts = Linkset(4, p => p[3].Creator = "a11ce000-0000-4000-8000-000000000002");
        MergePlan plan = PlanOf(parts);
        Assert.Contains(Kept(plan, parts[3]).Rules, r => r.Rule == "10");
    }

    // ---------------------------------------------------------------- what the scripts say

    private static (List<List<KitPart>>, Dictionary<string, byte[]>, List<KitPart>) WithScript(string source, int host = 1)
    {
        List<KitPart> parts = Linkset(6);
        var (item, entry, data) = EligibilityKit.Script("s", source);
        parts[host].Items.Add(item);
        return (new List<List<KitPart>> { parts }, new Dictionary<string, byte[]> { [entry] = data }, parts);
    }

    [Fact]
    public void AScriptedPartAndThePartsItAddressesByNumberStayAndNumbersUpToTheHighestStayToo()
    {
        var (builds, assets, parts) = WithScript("default { touch_start(integer n) { llSetLinkColor(4, <1,0,0>, ALL_SIDES); } }");
        MergePlan plan = PlanOf(builds, assets);
        Assert.Contains(Kept(plan, parts[1]).Rules, r => r.Code == "SCRIPTED");
        Assert.Contains(Kept(plan, parts[3]).Rules, r => r.Code == "ADDRESSED");
        Assert.Contains(Kept(plan, parts[2]).Rules, r => r.Code == "PROTECTED_LINK_NUMBER");
        Assert.Equal(new[] { 5, 6 }, plan.Linksets[0].Merged.Single().Members.Select(m => m.LinkNum));
    }

    [Theory]
    [InlineData("llSetLinkColor(LINK_SET, <1,0,0>, ALL_SIDES);")]
    [InlineData("integer i; llSetLinkAlpha(i, 1.0, ALL_SIDES);")]
    [InlineData("integer c = llGetNumberOfPrims();")]
    public void AScriptThatAddressesEveryPartOrCountsThemLocksTheLinkset(string body)
    {
        var (builds, assets, parts) = WithScript("default { touch_start(integer n) { " + body + " } }");
        MergePlan plan = PlanOf(builds, assets);
        Assert.Equal(0, plan.MergedParts);
        Assert.All(parts, p => Assert.Contains(Kept(plan, p).Rules, r => r.Code == "LINKSET_LOCKED"));
        Assert.NotEmpty(plan.Linksets[0].LockedBy);
    }

    [Fact]
    public void AScriptThatOnlyMessagesTheWholeLinksetLeavesTheOtherPartsFree()
    {
        var (builds, assets, parts) = WithScript("default { touch_start(integer n) { llMessageLinked(LINK_SET, 0, \"go\", NULL_KEY); } }");
        MergePlan plan = PlanOf(builds, assets);
        Assert.Equal(1, plan.MergedParts);
        Assert.Empty(plan.Linksets[0].LockedBy);
    }

    [Fact]
    public void AScriptWhoseSourceIsMissingLocksTheLinkset()
    {
        List<KitPart> parts = Linkset(4);
        parts[1].Items.Add(new KitItem { Name = "lost", Asset = Guid.NewGuid(), Type = 10 });
        MergePlan plan = PlanOf(parts);
        Assert.Equal(0, plan.MergedParts);
        Assert.Contains("lost", plan.Linksets[0].LockedBy);
    }

    // ---------------------------------------------------------------- faces and legacy materials

    private static string TextureEntryWith(UUID materialId)
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID("89556747-24cb-43ed-920b-47caed15465f"));
        te.DefaultTexture.RepeatU = 1;
        Primitive.TextureEntryFace f = te.CreateFace(0);
        f.TextureID = new UUID("89556747-24cb-43ed-920b-47caed15465f");
        f.MaterialID = materialId;
        return Convert.ToBase64String(te.GetBytes());
    }

    private static byte[] MaterialXml(bool normalMap, int repeatX)
    {
        string norm = normalMap ? "<uuid>11111111-2222-3333-4444-555555555555</uuid>" : "<uuid>00000000-0000-0000-0000-000000000000</uuid>";
        return Encoding.UTF8.GetBytes("<llsd><map><key>NormMap</key>" + norm + "<key>NormRepeatX</key><integer>" + repeatX + "</integer><key>NormRepeatY</key><integer>10000</integer>"
            + "<key>SpecMap</key><uuid>00000000-0000-0000-0000-000000000000</uuid><key>DiffuseAlphaMode</key><integer>0</integer></map></llsd>");
    }

    private static bool FirstSlotBaked(bool withMaterialAsset, bool normalMap, int repeatX)
    {
        UUID materialId = UUID.Random();
        List<KitPart> parts = Linkset(3);
        parts[0].TextureEntry = TextureEntryWith(materialId);
        Dictionary<string, byte[]> assets = new Dictionary<string, byte[]>();
        if (withMaterialAsset)
            assets["assets/" + materialId + "_material.xml"] = MaterialXml(normalMap, repeatX);
        MergePlan plan = PlanOf(new List<List<KitPart>> { parts }, assets);
        SlotPlan slot = plan.Linksets[0].Merged.Single().Slots.Single(s => s.MaterialKey.Contains(materialId.ToString()));
        return slot.Baked;
    }

    [Fact]
    public void ALegacyMaterialWithNoMapsBakesAndOneWhoseNormalMapRepeatsLikeTheDiffuseBakes()
    {
        Assert.True(FirstSlotBaked(true, false, 10000));
        Assert.True(FirstSlotBaked(true, true, 10000));
    }

    [Fact]
    public void ALegacyMaterialWhoseNormalMapRepeatsDifferentlyStillBakesBecauseTheMergedMaterialStatesTheDifference()
    {
        // the face is not scaled (1 by 1) and the map repeats four times: the merged face's material draws the map four times over the baked coordinates
        Assert.True(FirstSlotBaked(true, true, 40000));
    }

    [Fact]
    public void ALegacyMaterialThatIsMissingFromTheArchiveStaysUnbaked()
    {
        Assert.False(FirstSlotBaked(false, false, 0));
    }

    [Fact]
    public void AColourAlphaBelowOneMarksTheFaceAsAlpha()
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID("89556747-24cb-43ed-920b-47caed15465f"));
        te.CreateFace(2).RGBA = new Color4(1, 1, 1, 0.5f);
        List<KitPart> parts = Linkset(3);
        parts[0].TextureEntry = Convert.ToBase64String(te.GetBytes());
        parts[1].TextureEntry = parts[0].TextureEntry;
        MergePlan plan = PlanOf(parts);
        Assert.Equal(2, plan.AlphaFaces);
    }

    [Fact]
    public void ThePlanCountsEveryRuleThatKeepsAPartNotOnlyTheFirst()
    {
        List<KitPart> parts = Linkset(5, p =>
        {
            p[1].Light = true;
            p[1].Flags = "Physics";
        });
        MergePlan plan = PlanOf(parts);
        SortedDictionary<string, int> first = MergePlanFiles.KeptByFirstRule(plan), any = MergePlanFiles.KeptByAnyRule(plan);
        Assert.Equal(1, first.Values.Sum());
        Assert.Equal(1, any["part LIGHT"]);
        Assert.Equal(1, any["part PHYSICAL"]);
        string text = MergePlanFiles.ReportText(plan);
        Assert.Contains("every rule that applies", text);
        Assert.Contains("part PHYSICAL", text);
        Assert.Contains("keptByAnyRule", MergePlanFiles.ArchiveJson(plan));
    }

    // ---------------------------------------------------------------- the files and the command line

    [Fact]
    public void TheFilesHoldOneJsonForTheArchiveAndOneForEachLinksetAndAReport()
    {
        string folder = Folder();
        MergePlan plan = ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { Linkset(3), Linkset(2) }), new MergeOptions());
        MergePlanFiles.Write(plan, folder);
        Assert.True(File.Exists(Path.Combine(folder, "merge-plan.json")));
        Assert.True(File.Exists(Path.Combine(folder, "merge-plan.txt")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, "linksets"), "*.json").Length);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "merge-plan.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("totals").GetProperty("partsBefore").GetInt32());
    }

    [Fact]
    public void TheCommandWithPlanOnlyWritesThePlanAndNoArchive()
    {
        string source = EligibilityKit.WriteArchive(new List<List<KitPart>> { Linkset(3) });
        string folder = Folder();
        string notWritten = Path.Combine(folder, "out.oar");
        Assert.Equal(0, ConvertCommand.Run(new[] { source, "--merge", "--plan-only", "--merge-plan-dir", folder, "--out", notWritten }));
        Assert.True(File.Exists(Path.Combine(folder, "merge-plan.json")));
        Assert.False(File.Exists(notWritten));
        Assert.Empty(Directory.GetFiles(folder, "*.oar", SearchOption.AllDirectories));
    }

    [Fact]
    public void TheCommandNeedsAFolderForThePlanAndAnOutputUnlessItOnlyPlans()
    {
        string source = EligibilityKit.WriteArchive(new List<List<KitPart>> { Linkset(3) });
        string folder = Folder();
        Assert.Equal(2, ConvertCommand.Run(new[] { source, "--merge", "--plan-only" }));
        Assert.Equal(2, ConvertCommand.Run(new[] { source, "--merge", "--merge-plan-dir", folder }));
        Assert.Equal(2, ConvertCommand.Run(new[] { source, "--plan-only", "--merge-plan-dir", folder }));
        Assert.False(Directory.Exists(folder));
    }
}
