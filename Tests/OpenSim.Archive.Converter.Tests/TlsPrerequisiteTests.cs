/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.TextureFetch;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>An https login on a Linux without OpenSSL is refused with a message; an http login, and any login elsewhere, is not.</summary>
public class TlsPrerequisiteTests
{
    [Theory]
    [InlineData("https://grid.example.org:8002/")]
    [InlineData("HTTPS://grid.example.org/login")]
    public void AnHttpsLoginOnALinuxWithoutOpenSslIsRefusedWithAMessageThatSaysWhatToDo(string uri)
    {
        string message = TlsPrerequisite.Check(uri, true, () => false);
        Assert.NotNull(message);
        Assert.Contains("https", message);
        Assert.Contains("libssl", message);
        Assert.Contains("http login", message);
    }

    [Fact]
    public void AnHttpsLoginOnALinuxWithOpenSslGoesAhead()
    {
        Assert.NotNull(TlsPrerequisite.Check("https://grid.example.org/", true, () => false));
        Assert.Null(TlsPrerequisite.Check("https://grid.example.org/", true, () => true));
    }

    [Fact]
    public void AnHttpLoginGoesAheadWithoutOpenSslAndTheLibraryIsNotProbed()
    {
        bool probed = false;
        Assert.NotNull(TlsPrerequisite.Check("https://grid.example.org/", true, () => false));
        Assert.Null(TlsPrerequisite.Check("http://grid.example.org:9000/", true, () => { probed = true; return false; }));
        Assert.False(probed);
    }

    [Fact]
    public void AnHttpsLoginOnAnotherSystemGoesAhead()
    {
        Assert.NotNull(TlsPrerequisite.Check("https://grid.example.org/", true, () => false));
        Assert.Null(TlsPrerequisite.Check("https://grid.example.org/", false, () => false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a uri")]
    public void AnAddressThatIsNotAUriIsLeftToTheLogin(string uri)
    {
        Assert.NotNull(TlsPrerequisite.Check("https://grid.example.org/", true, () => false));
        Assert.Null(TlsPrerequisite.Check(uri, true, () => false));
    }
}
