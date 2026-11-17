/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Diagnostics;
using System.IO;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

// Physics steps while the process cannot commit more memory (TempStarve). Each test runs the harness in a child
// process, because the failure being guarded against is an access violation in joltc that ends the process. The child
// caps only its own commit (a job object); nothing else on the machine is starved. Runs in parallel: the child owns
// all the process-wide state it touches.
public class TempStarveTests
{
    private static (int ExitCode, string Output) RunChild(params string[] extra)
    {
        string harness = typeof(TempStarve).Assembly.Location;
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(harness);
        psi.ArgumentList.Add("--temp-starve");
        foreach (string a in extra)
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(180_000))
        {
            p.Kill(true);
            throw new TimeoutException("the --temp-starve child did not finish in 180 s:\n" + output);
        }
        return (p.ExitCode, output + err.Result);
    }

    [Fact]
    public void With_the_step_kept_in_the_temp_stack_physics_steps_on_when_the_process_cannot_commit_more()
    {
        if (!OperatingSystem.IsWindows())
            return;   // job objects are Windows only; the arithmetic is covered by TempStackFitTests everywhere
        (int exit, string output) = RunChild("--jolt", "FitStepInTempStack=true");
        Assert.Contains("CAPPED", output);
        Assert.True(exit == 0, $"child exit 0x{exit:X8} (0xC0000005 is the access violation in joltc):\n{output}");
        Assert.Contains("SURVIVED steps=100", output);
    }

    // The control: the same run with the key off (the contact buffer comes from malloc every step) must end in the
    // access violation, or the cap above is not starving joltc and the test proves nothing.
    [Fact]
    public void Without_it_the_capped_process_ends_in_an_access_violation_in_joltc()
    {
        if (!OperatingSystem.IsWindows())
            return;
        (int exit, string output) = RunChild();
        Assert.Contains("CAPPED", output);
        Assert.DoesNotContain("SURVIVED", output);
        Assert.Equal(unchecked((int)0xC0000005), exit);
    }
}
