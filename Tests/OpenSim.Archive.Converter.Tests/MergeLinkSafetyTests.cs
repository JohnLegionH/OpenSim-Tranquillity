/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Archive.Inspection;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A merge must not change anything a script in the archive can observe about its own linkset: the link numbers it can reach, the names
/// and descriptions of the parts, the number of parts, the root, the sit targets and which parts carry scripts. Each test writes its own
/// archive and shares no process state, so the class runs in parallel with the others.
/// </summary>
public class MergeLinkSafetyTests
{
    private const int PartsPerLinkset = 6;

    /// <summary>One script variant: its body, the link numbers it addresses by number, and whether it ties the whole linkset down.</summary>
    public sealed record Variant(string Body, int[] Numbers, bool Locks);

    // the numbers refer to a six-part linkset whose script sits in link 2
    public static IEnumerable<object[]> Addressing()
    {
        // calls whose link argument is not the first argument, or that the link-number scan did not know
        yield return new object[] { "llAvatarOnLinkSitTarget(4);", 4 };
        yield return new object[] { "llSitOnLink(llGetOwner(), 4);", 4 };
        yield return new object[] { "key k = llGetObjectLinkKey(llGetKey(), 4);", 4 };
        yield return new object[] { "integer s = llIsLinkGLTFMaterial(4, 0);", 4 };
        yield return new object[] { "float r = osGetLinkSitActiveRange(4);", 4 };
        yield return new object[] { "vector v = osGetLinkStandTarget(4);", 4 };
        yield return new object[] { "osSetLinkSitActiveRange(4, 2.0);", 4 };
        // a link number inside a parameter list: PRIM_LINK_TARGET switches the part the rules after it apply to
        yield return new object[] { "llSetPrimitiveParams([PRIM_LINK_TARGET, 4, PRIM_COLOR, ALL_SIDES, <1,0,0>, 1.0]);", 4 };
        yield return new object[] { "llSetLinkPrimitiveParams(LINK_THIS, [PRIM_LINK_TARGET, 4, PRIM_NAME, \"x\"]);", 4 };
        yield return new object[] { "list l = llGetLinkPrimitiveParams(LINK_THIS, [PRIM_LINK_TARGET, 4, PRIM_DESC]);", 4 };
        yield return new object[] { "llSetPrimitiveParams([PRIM_NAME, \"a\", PRIM_LINK_TARGET, 4, PRIM_NAME, \"b\"]);", 4 };
        // already known: a name, a description, a sit target and a link number named directly
        yield return new object[] { "string n = llGetLinkName(4);", 4 };
        yield return new object[] { "list l = llGetLinkPrimitiveParams(4, [PRIM_NAME, PRIM_DESC]);", 4 };
        yield return new object[] { "llLinkSitTarget(4, <0,0,1>, ZERO_ROTATION);", 4 };
        yield return new object[] { "key k = llGetLinkKey(4);", 4 };
        yield return new object[] { "llMessageLinked(4, 0, \"go\", NULL_KEY);", 4 };
    }

    public static IEnumerable<object[]> Locking()
    {
        yield return new object[] { "integer i; key k = llAvatarOnLinkSitTarget(i);" };
        yield return new object[] { "integer i; llSitOnLink(llGetOwner(), i);" };
        yield return new object[] { "integer i; key k = llGetObjectLinkKey(llGetKey(), i);" };
        yield return new object[] { "integer i; float r = osGetLinkSitActiveRange(i);" };
        yield return new object[] { "llSetPrimitiveParams([PRIM_LINK_TARGET, LINK_SET, PRIM_COLOR, ALL_SIDES, <1,0,0>, 1.0]);" };
        yield return new object[] { "llSetPrimitiveParams([PRIM_LINK_TARGET, LINK_ALL_OTHERS, PRIM_NAME, \"x\"]);" };
        yield return new object[] { "integer n = 4; llSetPrimitiveParams([PRIM_LINK_TARGET, n, PRIM_NAME, \"x\"]);" };
        yield return new object[] { "integer i; llSetLinkPrimitiveParams(LINK_THIS, [PRIM_LINK_TARGET, i, PRIM_NAME, \"x\"]);" };
        // already known
        yield return new object[] { "integer c = llGetNumberOfPrims();" };
        yield return new object[] { "integer c = llGetObjectPrimCount(llGetKey());" };
        yield return new object[] { "integer me = llGetLinkNumber();" };
        yield return new object[] { "string n; integer i; n = llGetLinkName(i);" };
    }

    private static List<KitPart> Linkset(string tag, int count = PartsPerLinkset, Action<List<KitPart>> change = null)
    {
        List<KitPart> parts = Enumerable.Range(1, count).Select(i => new KitPart(tag + " part " + i, i)
        {
            Description = tag + " desc " + i, Offset = (i * 2) + ",0,0", Scale = "1,1,1"
        }).ToList();
        change?.Invoke(parts);
        return parts;
    }

    private static (List<List<KitPart>> builds, Dictionary<string, byte[]> assets, List<KitPart> parts) WithScript(string body, int host = 1)
    {
        List<KitPart> parts = Linkset("L");
        var (item, entry, data) = EligibilityKit.Script("s", "default { touch_start(integer n) { " + body + " } }");
        parts[host].Items.Add(item);
        return (new List<List<KitPart>> { parts }, new Dictionary<string, byte[]> { [entry] = data }, parts);
    }

    private static MergePlan Plan(List<List<KitPart>> builds, Dictionary<string, byte[]> assets) =>
        ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(builds, assets), new MergeOptions());

    private static KeptPartPlan Kept(MergePlan plan, KitPart p) => Assert.Single(plan.Linksets.SelectMany(l => l.Kept), k => k.PartId == p.Id);

    private static string Output() => Path.Combine(Path.GetTempPath(), "merge-link-safety-tests", Guid.NewGuid().ToString("N") + ".oar");

    // ---------------------------------------------------------------- the plan

    [Theory]
    [MemberData(nameof(Addressing))]
    public void APartAScriptAddressesByNumberStaysAndTheNumbersBeforeItStayToo(string body, int number)
    {
        var (builds, assets, parts) = WithScript(body);
        MergePlan plan = Plan(builds, assets);
        Assert.Empty(plan.Linksets[0].LockedBy);
        Assert.Contains(Kept(plan, parts[number - 1]).Rules, r => r.Code == "ADDRESSED");
        Assert.Contains(Kept(plan, parts[number - 2]).Rules, r => r.Code == "PROTECTED_LINK_NUMBER");
        Assert.Contains(Kept(plan, parts[1]).Rules, r => r.Code == "SCRIPTED");
        // the parts after the addressed one are free to merge and are the only ones that do
        Assert.Equal(new[] { 5, 6 }, plan.Linksets[0].Merged.Single().Members.Select(m => m.LinkNum));
    }

    [Theory]
    [MemberData(nameof(Locking))]
    public void ALinkArgumentTheScanCannotWorkOutOrAWholeLinksetCallLocksTheLinkset(string body)
    {
        var (builds, assets, parts) = WithScript(body);
        MergePlan plan = Plan(builds, assets);
        Assert.Equal(0, plan.MergedParts);
        Assert.NotEmpty(plan.Linksets[0].LockedBy);
        Assert.All(parts, p => Assert.Contains(Kept(plan, p).Rules, r => r.Code == "LINKSET_LOCKED"));
    }

    [Fact]
    public void EveryKeptPartOfAScriptedLinksetCarriesAReasonWithACode()
    {
        var (builds, assets, parts) = WithScript("llSetLinkColor(4, <1,0,0>, ALL_SIDES);");
        MergePlan plan = Plan(builds, assets);
        Assert.All(plan.Linksets.SelectMany(l => l.Kept), k =>
        {
            Assert.NotEmpty(k.Rules);
            Assert.All(k.Rules, r => Assert.False(string.IsNullOrEmpty(r.Code) || string.IsNullOrEmpty(r.Detail)));
        });
    }

    // ---------------------------------------------------------------- the written archive

    private sealed record Seen(Guid Id, string Name, string Description, int Number, bool Root);

    private static Dictionary<Guid, List<Seen>> Read(string archive)
    {
        Dictionary<Guid, ParsedPart> parsed = new Dictionary<Guid, ParsedPart>();
        EligibilityResult elig = new ArchiveEligibility(new EligibilityOptions { PartObserver = pp => parsed.TryAdd(pp.Part.PartId.Guid, pp) }).Evaluate(archive);
        Dictionary<Guid, List<Seen>> byLinkset = new Dictionary<Guid, List<Seen>>();
        foreach (var group in elig.Parts.GroupBy(r => (r.Entry, r.LinksetId)))
        {
            List<Seen> parts = group.Select(r => new Seen(r.PartId, r.Name ?? "", parsed[r.PartId].Description ?? "", group.Count() <= 1 ? 0 : r.IsRoot ? 1 : r.LinkNum, r.IsRoot))
                .OrderBy(s => s.Number).ToList();
            byLinkset[group.First(r => r.IsRoot).PartId] = parts;
        }
        return byLinkset;
    }

    /// <summary>The scripted linksets: each is eight parts with one script in link 2, a sit target on link 8 and the numbers its script reaches.</summary>
    private static List<(Variant v, List<KitPart> parts)> ScriptedArchive(out Dictionary<string, byte[]> assets)
    {
        assets = new Dictionary<string, byte[]>();
        List<(Variant, List<KitPart>)> result = new List<(Variant, List<KitPart>)>();
        int n = 0;
        foreach (object[] row in Addressing().Select(r => r).Concat(Locking().Select(r => new object[] { r[0], 0 })))
        {
            string body = (string)row[0];
            int number = (int)row[1];
            bool locks = number == 0;
            List<KitPart> parts = Linkset("S" + n++, 8);
            parts[7].ExtraXml = "<SitTargetPosition><X>0</X><Y>0</Y><Z>0.5</Z></SitTargetPosition>";
            var (item, entry, data) = EligibilityKit.Script("s" + n, "default { touch_start(integer n) { " + body + " } }");
            parts[1].Items.Add(item);
            assets[entry] = data;
            result.Add((new Variant(body, locks ? Array.Empty<int>() : new[] { number }, locks), parts));
        }
        return result;
    }

    [Fact]
    public void WhatTheScriptsOfAnArchiveCanObserveIsTheSameInTheMergedArchive()
    {
        var linksets = ScriptedArchive(out Dictionary<string, byte[]> assets);
        string source = EligibilityKit.WriteArchive(linksets.Select(l => l.parts).ToList(), assets), output = Output();
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        MergeArchiveWriter.Write(source, output);
        Dictionary<Guid, List<Seen>> before = Read(source), after = Read(output);

        int merged = 0;
        foreach (var (variant, parts) in linksets)
        {
            List<Seen> b = before[parts[0].Id], a = after[parts[0].Id];
            string where = variant.Body;
            Assert.True(a.Count <= b.Count, where);
            if (a.Count < b.Count)
                merged++;
            // the root, and the part that carries the script: same part, name, description and number
            Assert.Equal(b[0], a[0]);
            Assert.Equal(b.Single(s => s.Id == parts[1].Id), a.Single(s => s.Id == parts[1].Id));
            // the sit target stays on its own part, with its name and description
            Assert.Equal(b.Single(s => s.Id == parts[7].Id).Name, a.Single(s => s.Id == parts[7].Id).Name);
            Assert.Equal(b.Single(s => s.Id == parts[7].Id).Description, a.Single(s => s.Id == parts[7].Id).Description);
            // every link number the script addresses leads to the same part as before
            foreach (int number in variant.Numbers)
                Assert.Equal(b.Single(s => s.Number == number), a.Single(s => s.Number == number));
            // a script that counts the parts or reaches all of them sees every part as before
            if (variant.Locks)
                Assert.Equal(b, a);
        }
        Assert.True(merged > 0, "the archive must merge something for the comparison to mean anything");
    }
}
