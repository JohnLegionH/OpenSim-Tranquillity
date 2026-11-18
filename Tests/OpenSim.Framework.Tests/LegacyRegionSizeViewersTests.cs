/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Framework.Tests;

/// <summary>
/// Which login channels are viewers that only take 256 m regions. The list is a set of channel prefixes.
/// </summary>
public class LegacyRegionSizeViewersTests
{
    [Fact]
    public void DefaultListIsLindenLabsChannel()
    {
        string[] list = LegacyRegionSizeViewers.ParseChannels(LegacyRegionSizeViewers.DefaultChannels);

        Assert.Equal(new[] { "Second Life" }, list);
    }

    [Theory]
    [InlineData("Second Life Release")]
    [InlineData("Second Life Beta")]
    [InlineData("Second Life Project Example")]
    [InlineData("Second Life Test")]
    [InlineData("second life release")]
    public void LindenLabsChannelsMatchTheDefault(string channel)
    {
        string[] list = LegacyRegionSizeViewers.ParseChannels(LegacyRegionSizeViewers.DefaultChannels);

        Assert.True(LegacyRegionSizeViewers.IsLegacyChannel(list, channel));
    }

    [Theory]
    [InlineData("Firestorm-Releasex64")]
    [InlineData("Example Viewer")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherChannelsDoNotMatchTheDefault(string channel)
    {
        string[] list = LegacyRegionSizeViewers.ParseChannels(LegacyRegionSizeViewers.DefaultChannels);

        Assert.False(LegacyRegionSizeViewers.IsLegacyChannel(list, channel));
    }

    [Fact]
    public void ListAcceptsCommasSemicolonsAndSpaces()
    {
        string[] list = LegacyRegionSizeViewers.ParseChannels(" Second Life ;Old Viewer,  Another One ,,");

        Assert.Equal(new[] { "Second Life", "Old Viewer", "Another One" }, list);
        Assert.True(LegacyRegionSizeViewers.IsLegacyChannel(list, "Old Viewer 1.2"));
        Assert.True(LegacyRegionSizeViewers.IsLegacyChannel(list, "Another One"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    [InlineData(null)]
    public void EmptyListMatchesNothing(string text)
    {
        string[] list = LegacyRegionSizeViewers.ParseChannels(text);

        Assert.Empty(list);
        Assert.False(LegacyRegionSizeViewers.IsLegacyChannel(list, "Second Life Release"));
    }

    [Fact]
    public void AMissingListMatchesNothing()
    {
        Assert.False(LegacyRegionSizeViewers.IsLegacyChannel(null, "Second Life Release"));
    }
}
