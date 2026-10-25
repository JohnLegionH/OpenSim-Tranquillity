/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Restore.Tests;

public class RestoreCommandVersionTests
{
    [Fact]
    public void VersionPrintsTheProgramAndItsVersion()
    {
        TextWriter saved = Console.Out;
        StringWriter capture = new StringWriter();
        Console.SetOut(capture);
        try { Assert.Equal(0, RestoreCommand.Run(new[] { "--version" })); }
        finally { Console.SetOut(saved); }
        Assert.Contains("oar-restore 0.2", capture.ToString());
    }
}
