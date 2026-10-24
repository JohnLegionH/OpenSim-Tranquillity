/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// The memory cap an export uses when none is named: 2048 MB, or half of the machine's memory if that is smaller. The
/// machine's size comes in through <see cref="ExportOptions.PhysicalMemoryBytes"/>, so these tests do not depend on the
/// machine they run on and touch no process-wide state (they run in parallel with the other classes).
/// </summary>
public class DefaultMemoryCapTests
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    [Theory]
    [InlineData(512 * Mb, 256)]
    [InlineData(1 * Gb, 512)]
    [InlineData(3 * Gb, 1536)]
    [InlineData(4 * Gb, 2048)]
    [InlineData(4 * Gb + 2 * Mb, 2048)]
    [InlineData(16 * Gb, 2048)]
    [InlineData(1024 * Gb, 2048)]
    public void TheDefaultIs2048OrHalfTheMachinesMemoryWhicheverIsSmaller(long physical, int expected)
    {
        Assert.Equal(expected, ArchiveExporter.DefaultMaxMemoryMegabytes(physical));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AnUnknownMachineSizeKeepsTheEarlierDefaultOf1024(long physical)
    {
        Assert.Equal(1024, ArchiveExporter.DefaultMaxMemoryMegabytes(physical));
    }

    [Fact]
    public void ATinyMachineStillGetsACapOfAtLeastOneMegabyte()
    {
        Assert.Equal(1, ArchiveExporter.DefaultMaxMemoryMegabytes(1));
    }

    [Fact]
    public void TheCapComesFromTheOptionsMachineSizeWhenNoCapIsNamed()
    {
        Assert.Equal(1536, ArchiveExporter.MaxMemoryMegabytesFor(new ExportOptions { PhysicalMemoryBytes = 3 * Gb }));
        Assert.Equal(2048, ArchiveExporter.MaxMemoryMegabytesFor(new ExportOptions { PhysicalMemoryBytes = 32 * Gb }));
    }

    [Fact]
    public void ACapNamedInTheOptionsWinsOverTheMachinesSize()
    {
        Assert.Equal(300, ArchiveExporter.MaxMemoryMegabytesFor(new ExportOptions { MaxMemoryMegabytes = 300, PhysicalMemoryBytes = 32 * Gb }));
        Assert.Equal(8192, ArchiveExporter.MaxMemoryMegabytesFor(new ExportOptions { MaxMemoryMegabytes = 8192, PhysicalMemoryBytes = 2 * Gb }));
    }

    [Fact]
    public void WithNoMachineSizeGivenTheRuntimesAvailableMemoryDecides()
    {
        // the real machine: the answer is the default for whatever memory the runtime reports, and never above the limit
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int cap = ArchiveExporter.MaxMemoryMegabytesFor(new ExportOptions());
        Assert.Equal(ArchiveExporter.DefaultMaxMemoryMegabytes(available), cap);
        Assert.InRange(cap, 1, ArchiveExporter.DefaultMaxMemoryLimitMegabytes);
    }
}
