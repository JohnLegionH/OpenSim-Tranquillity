/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace OpenSim.Archive.ScriptScan.Tests;

// No process-wide state: every test reads its own script text, so the class runs in parallel with others.
public class LinkUseTests
{
    private static List<LinkUse> Uses(string body) => LslLinkUses.Find("default { touch_start(integer n) { " + body + " } }");

    [Fact]
    public void ALiteralLinkNumberIsReadWithItsFunctionAndLine()
    {
        LinkUse u = Assert.Single(Uses("llSetLinkColor(3, <1,0,0>, ALL_SIDES);"));
        Assert.Equal(LinkUseKind.Literal, u.Kind);
        Assert.Equal(3, u.Number);
        Assert.Equal("llSetLinkColor", u.Function);
        Assert.Equal(1, u.Line);
        Assert.False(u.MessageOnly);
    }

    [Fact]
    public void LinkRootAndLinkThisAreReadAsRootAndSelf()
    {
        List<LinkUse> u = Uses("llSetLinkAlpha(LINK_ROOT, 0.5, ALL_SIDES); llSetLinkAlpha(LINK_THIS, 0.5, ALL_SIDES);");
        Assert.Equal(new[] { LinkUseKind.Root, LinkUseKind.Self }, u.Select(x => x.Kind));
    }

    [Fact]
    public void LinkSetAndItsSiblingsAreReadByName()
    {
        List<LinkUse> u = Uses("llSetLinkPrimitiveParamsFast(LINK_SET, []); llSetLinkAlpha(LINK_ALL_OTHERS, 1.0, ALL_SIDES); llSetLinkAlpha(LINK_ALL_CHILDREN, 1.0, ALL_SIDES);");
        Assert.Equal(new[] { LinkUseKind.Set, LinkUseKind.AllOthers, LinkUseKind.AllChildren }, u.Select(x => x.Kind));
    }

    [Fact]
    public void ALinkArgumentTheScanCannotWorkOutIsUnknown()
    {
        LinkUse u = Assert.Single(LslLinkUses.Find("integer i; default { touch_start(integer n) { llSetLinkAlpha(i + 1, 1.0, ALL_SIDES); } }"));
        Assert.Equal(LinkUseKind.Unknown, u.Kind);
    }

    [Theory]
    [InlineData("integer c = llGetNumberOfPrims();")]
    [InlineData("integer c = llGetObjectPrimCount(llGetKey());")]
    [InlineData("integer c = llGetLinkNumber();")]
    [InlineData("integer c = llDetectedLinkNumber(0);")]
    public void CountingOrNumberingCallsAreEnumerations(string body)
    {
        LinkUse u = Assert.Single(Uses(body));
        Assert.Equal(LinkUseKind.Enumeration, u.Kind);
    }

    [Fact]
    public void ReadingANameOrKeyByLiteralNumberAddressesThatPart()
    {
        List<LinkUse> u = Uses("string a = llGetLinkName(2); key b = llGetLinkKey(4);");
        Assert.Equal(new[] { 2, 4 }, u.Select(x => x.Number));
        Assert.All(u, x => Assert.Equal(LinkUseKind.Literal, x.Kind));
    }

    [Fact]
    public void AMessageToTheWholeLinksetIsMarkedMessageOnly()
    {
        List<LinkUse> u = Uses("llMessageLinked(LINK_SET, 0, \"go\", NULL_KEY); llMessageLinked(2, 0, \"go\", NULL_KEY);");
        Assert.Equal(new[] { LinkUseKind.Set, LinkUseKind.Literal }, u.Select(x => x.Kind));
        Assert.All(u, x => Assert.True(x.MessageOnly));
    }

    [Fact]
    public void ALinkMessageHandlerThatReadsTheSenderNumberIsReported()
    {
        LinkUse u = Assert.Single(LslLinkUses.Find("default { link_message(integer sender, integer num, string str, key id) { llSay(0, (string)sender); } }"));
        Assert.Equal(LinkUseKind.LinkMessageSender, u.Kind);
        Assert.Equal("link_message", u.Function);
    }

    [Fact]
    public void ALinkMessageHandlerThatIgnoresTheSenderNumberIsNotReported()
    {
        Assert.Empty(LslLinkUses.Find("default { link_message(integer sender, integer num, string str, key id) { llSay(0, str); } }"));
    }

    [Fact]
    public void AScriptWithoutLinkCallsHasNoUses()
    {
        Assert.Empty(Uses("llSay(0, \"hello\"); llSetColor(<1,1,1>, ALL_SIDES);"));
    }

    [Fact]
    public void AnalyzeCarriesTheUsesOfTheScript()
    {
        ScriptAnalysis a = LslScriptAnalyzer.Analyze("default { touch_start(integer n) { llSetLinkColor(5, <1,0,0>, ALL_SIDES); } }");
        Assert.Equal(5, Assert.Single(a.LinkUses).Number);
    }
}
