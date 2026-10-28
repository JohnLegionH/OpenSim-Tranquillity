/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.ArchiveInspector;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>The oar-export command line: cleaning is on unless --no-clean is given, and --version names the version.</summary>
public class CleanExportCommandTests
{
    private static (int exit, string output) Run(params string[] args)
    {
        TextWriter saved = Console.Out;
        StringWriter capture = new StringWriter();
        Console.SetOut(capture);
        try { return (ExportCommand.Run(args), capture.ToString()); }
        finally { Console.SetOut(saved); }
    }

    private static Glb ExportWith(params string[] extra)
    {
        string dir = NewFolder();
        string archive = Path.Combine(dir, "sphere.oar");
        File.WriteAllBytes(archive, Oar(("sphere", ObjectXml(new PartSpec { Name = "Sphere", Shape = Box(profileCurve: 5, pathCurve: 32) }))));
        string output = Path.Combine(dir, "out");
        int exit = ExportCommand.Run(new[] { archive, "--out", output }.Concat(extra).ToArray());
        Assert.Equal(0, exit);
        return ReadGlb(File.ReadAllBytes(Directory.GetFiles(output, "*.glb", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public void WithoutAnyOptionTheFileIsCleanedAndStamped()
    {
        Assert.True(ExportWith().Root.GetProperty("asset").GetProperty("extras").GetProperty("oarExport").GetProperty("clean").GetBoolean());
    }

    [Fact]
    public void NoCleanWritesTheFileWithoutCleaningAndWithoutAStamp()
    {
        Glb glb = ExportWith("--no-clean");
        Assert.False(glb.Root.GetProperty("asset").TryGetProperty("extras", out _));
        Assert.False(glb.Root.GetProperty("meshes")[0].GetProperty("primitives")[0].TryGetProperty("indices", out _));
    }

    [Fact]
    public void VersionPrintsTheProgramAndItsVersionAndExitsWithoutExporting()
    {
        (int exit, string output) = Run("--version");
        Assert.Equal(0, exit);
        Assert.Contains("oar-export " + ToolRelease.Version, output);
    }
}
