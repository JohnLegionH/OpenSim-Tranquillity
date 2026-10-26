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
/// Fields a part keeps for itself (what it does when sat on, touched, paid or bought) are not carried by a merged part, so a part whose
/// value differs from the default stays as it is. Each test writes its own archive and shares nothing, so the class runs in parallel.
/// </summary>
public class MergePlanPerPartFieldTests
{
    private static List<KitPart> Linkset(string extraXmlOfThirdPart)
    {
        List<KitPart> parts = Enumerable.Range(1, 5).Select(i => new KitPart("Box " + i, i) { Offset = (i * 2) + ",0,0", Scale = "1,1,1" }).ToList();
        parts[2].ExtraXml = extraXmlOfThirdPart;
        return parts;
    }

    private static MergePlan Plan(List<KitPart> parts) =>
        ArchiveMergePlanner.Plan(EligibilityKit.WriteArchive(new List<List<KitPart>> { parts }), new MergeOptions());

    public static IEnumerable<object[]> Differing() => new[]
    {
        new object[] { "sit target", "<SitTargetPosition><X>0</X><Y>0</Y><Z>0.5</Z></SitTargetPosition>", "SIT_TARGET" },
        new object[] { "sit text", "<SitName>Sit here</SitName>", "SIT_TEXT" },
        new object[] { "click action", "<ClickAction>2</ClickAction>", "CLICK_ACTION" },
        new object[] { "sale type", "<ObjectSaleType>1</ObjectSaleType>", "SALE_TYPE" },
        new object[] { "sale price", "<ObjectSaleType>1</ObjectSaleType><SalePrice>50</SalePrice>", "SALE_PRICE" },
        new object[] { "hover text", "<Text>Open</Text>", "HOVER_TEXT" },
        new object[] { "hover text colour", "<Text>Open</Text><Color><R>255</R><G>0</G><B>0</B><A>255</A></Color>", "HOVER_TEXT_COLOR" },
        new object[] { "hover text transparency", "<Text>Open</Text><Color><R>0</R><G>0</G><B>0</B><A>0</A></Color>", "HOVER_TEXT_COLOR" },
        new object[] { "touch text", "<TouchName>Open the door</TouchName>", "TOUCH_TEXT" },
        new object[] { "pay price", "<PayPrice0>10</PayPrice0>", "PAY_PRICE" },
    };

    [Theory]
    [MemberData(nameof(Differing))]
    public void APartWhoseFieldDiffersFromTheDefaultStaysSeparate(string field, string xml, string code)
    {
        List<KitPart> parts = Linkset(xml);
        MergePlan plan = Plan(parts);
        KeptPartPlan kept = Assert.Single(plan.Linksets.SelectMany(l => l.Kept), k => k.PartId == parts[2].Id);
        Assert.True(kept.Rules.Any(r => r.Code == code), field + " should keep the part with " + code + ", it has " + string.Join(",", kept.Rules.Select(r => r.Code)));
        Assert.Equal(4, Assert.Single(plan.Linksets[0].Merged).Members.Count);
    }

    [Fact]
    public void TheDefaultsTheRegionWritesForAPartDoNotKeepIt()
    {
        string defaults = "<ClickAction>0</ClickAction><ObjectSaleType>0</ObjectSaleType><SalePrice>0</SalePrice><SitName></SitName><TouchName></TouchName>"
            + "<Text></Text><Color><R>0</R><G>0</G><B>0</B><A>255</A></Color>"
            + "<PayPrice0>-2</PayPrice0><PayPrice1>-2</PayPrice1><PayPrice2>-2</PayPrice2><PayPrice3>-2</PayPrice3><PayPrice4>-2</PayPrice4>";
        MergePlan plan = Plan(Linkset(defaults));
        Assert.Empty(plan.Linksets[0].Kept);
        Assert.Equal(5, Assert.Single(plan.Linksets[0].Merged).Members.Count);
    }

    [Fact]
    public void ASalePriceOnAPartThatIsNotForSaleIsNeverSeenSoItDoesNotKeepThePart()
    {
        MergePlan plan = Plan(Linkset("<ObjectSaleType>0</ObjectSaleType><SalePrice>10</SalePrice>"));
        Assert.Empty(plan.Linksets[0].Kept);
        Assert.Equal(5, Assert.Single(plan.Linksets[0].Merged).Members.Count);
    }

    [Fact]
    public void AHoverTextColourOnAPartWithNoHoverTextIsNeverSeenSoItDoesNotKeepThePart()
    {
        MergePlan plan = Plan(Linkset("<Text></Text><Color><R>0</R><G>255</G><B>0</B><A>255</A></Color>"));
        Assert.Empty(plan.Linksets[0].Kept);
        Assert.Equal(5, Assert.Single(plan.Linksets[0].Merged).Members.Count);
    }
}
