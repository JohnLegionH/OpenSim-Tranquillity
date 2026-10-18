/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
public class TowerSleepDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public TowerSleepDiagnosticTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Tower_threshold_sweep()
    {
        foreach (string t in new[] { "0.04", "0.05", "0.06", "0.07", "0.08", "0.1" })
        {
            var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 0.0 };
            o.Jolt["SleepVelocityThreshold"] = t;
            RunResult r = Harness.Harness.Run(Harness.Harness.Find("tower-10"), o);
            float moved = r.Parts.Max(p => System.Numerics.Vector3.Distance(p.Position, p.EndPosition));
            _out.WriteLine($"SWEEP threshold {t}: box10 asleep {r.Parts[9].SleptAt:0.00} box1 asleep {r.Parts[0].SleptAt:0.00} asleep at end {r.Parts.Count(p => p.AsleepAtEnd)} max moved {moved:0.0000}");
        }
    }
}
