/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.ArchiveInspector;
using Xunit;

namespace OpenSim.Archive.Restore.Tests;

/// <summary>The files oar-restore --report writes (the text, the JSON and the plan) use LF on every platform.</summary>
[Collection("Console output")]
public class RestoreReportLineEndingTests
{
    [Fact]
    public void TheRestoreReportFilesHaveNoCarriageReturns()
    {
        RestoreTests.Fixture f = new RestoreTests.Fixture();
        string report = Path.Combine(f.Dir, "report.txt");
        TextWriter saved = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            Assert.Equal(0, RestoreCommand.Run(new[] { f.Archive, f.GlbDir, "--out", f.Out(), "--report", report, "--work", Path.Combine(f.Dir, "work") }));
        }
        finally { Console.SetOut(saved); }
        foreach (string path in new[] { report, report + ".json", report + ".plan.json" })
        {
            Assert.True(File.Exists(path), path + " is written");
            string text = File.ReadAllText(path);
            Assert.True(text.Split('\n').Length > 3, path + " holds more than a few lines");
            Assert.DoesNotContain('\r', text);
        }
    }
}
