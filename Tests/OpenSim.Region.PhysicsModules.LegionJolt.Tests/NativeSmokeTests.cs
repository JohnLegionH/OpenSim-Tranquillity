using Legion.Physics;
using Legion.Physics.Jolt;
using Xunit;

namespace OpenSim.Region.PhysicsModules.LegionJolt.Tests;

/// <summary>PHYS-2 step 0: can a test host load the patched joltc and stand a PhysicsSystem up at all?</summary>
public class NativeSmokeTests
{
    [Fact]
    public void A_backend_initialises_and_steps()
    {
        var b = new JoltPhysicsBackend();
        b.Initialize(new PhysicsBackendSettings());
        b.Dispose();
    }
}
