/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>Prints the ten-box tower's per-box sleep state, speed and height at every heartbeat.</summary>
[Collection(JoltNativeSerial.Name)]
public class TowerSleepDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public TowerSleepDiagnosticTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void Print_the_tower(double physicsRate)
    {
        Scenario sc = Harness.Harness.Find("tower-10");
        Action<Run> original = sc.Input;
        var sb = new StringBuilder();
        sc.Input = r =>
        {
            original(r);
            sb.Append(CultureInfo.InvariantCulture, $"t={r.Now:0.000}");
            foreach (HarnessPart p in r.Parts)
            {
                if (p.Actor is not JoltPrim jp)
                    continue;
                sb.Append(CultureInfo.InvariantCulture, $" | {p.Name} {(jp.BodyAwake ? 'A' : 'z')} v={p.Actor.Velocity.Length():0.0000} z={p.Actor.Position.Z:0.0000}");
            }
            sb.AppendLine();
        };
        try
        {
            RunResult r = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate });
            _out.WriteLine($"physicsRate {physicsRate}");
            _out.WriteLine(sb.ToString());
            foreach (string line in r.PartLines())
                _out.WriteLine(line);
        }
        finally { sc.Input = original; }
    }
}
