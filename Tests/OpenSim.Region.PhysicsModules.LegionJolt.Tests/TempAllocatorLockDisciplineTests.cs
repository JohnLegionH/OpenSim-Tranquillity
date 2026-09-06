using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenSim.Region.PhysicsModules.LegionJolt.Tests;

/// <summary>
/// PHYS-1. The patched joltc gives every <c>JPH_PhysicsSystem</c> its own
/// <c>TempAllocatorImplWithMallocFallback</c> (<c>joltc.cpp:956</c>) and passes it to exactly seven native
/// entry points: <c>PhysicsSystem::Update</c> (<c>:1050</c>) and six <c>CharacterVirtual</c> scratch users
/// (<c>:8107, :8135, :8151, :8182, :8198, :8223</c>). Two of those seven are reachable from this tree's managed
/// code — <c>ExtendedUpdate</c> and <c>SetShape</c> — plus <c>Update</c> itself.
///
/// <para>
/// Jolt's contract is unforgiving and is not a locking one: <i>"This allocator works as a stack: The blocks must
/// always be freed in the reverse order as they are allocated. Note that allocations and frees can take place
/// from different threads, but the order is guaranteed though job dependencies, so it is not needed to use any
/// form of locking."</i> (<c>Jolt/Core/TempAllocator.h:11-13</c>). Two callers that are not part of the same job
/// graph have no dependency to order them, <c>mTop</c> is a plain non-atomic field, and
/// <c>TempAllocatorImpl::Free</c> answers an out-of-order free with <c>std::abort()</c>
/// (<c>TempAllocator.h:83-84</c>).
/// </para>
///
/// <para>
/// <b>Why this is a source test.</b> The failure is <c>std::abort()</c> inside native code: it does not throw,
/// it terminates the process. A runtime reproduction cannot be a failing assertion — it takes the test host
/// down with it — so the red state was verified by reverting the fix and running the region, and is recorded in
/// the ledger rather than here. What can be asserted deterministically, and is exactly the thing that was wrong,
/// is the lexical rule the backend file already states in its own header: every native call that touches a
/// PhysicsSystem happens under <c>_simLock</c>, and character ops take <c>_characterGate</c> INSIDE it.
/// </para>
/// </summary>
public class TempAllocatorLockDisciplineTests
{
    private static string BackendSource([CallerFilePath] string here = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
        var path = Path.Combine(repo, "Addons", "LegionPhysics", "Legion.Physics", "JoltPhysicsBackend.cs");
        Assert.True(File.Exists(path), $"backend source not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The body of the method named, brace-matched from its signature. Good enough for this file, which is
    /// ordinary C# with balanced braces and no braces inside string literals in the regions we inspect.
    /// </summary>
    private static string MethodBody(string source, string signatureFragment)
    {
        var at = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signatureFragment}' not found; the method was renamed and this test is stale");
        var open = source.IndexOf('{', at);
        Assert.True(open > 0);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..i];
        }
        Assert.Fail($"unbalanced braces after '{signatureFragment}'");
        return "";
    }

    /// <summary>
    /// Where a method takes a given lock, or -1. A position within the body, so two can be compared for order.
    /// This reads the whole body rather than only its opening lines: in this file the locks are always taken at
    /// the top, and demanding that as well would fail on the guard clauses that legitimately precede them.
    /// </summary>
    private static int LockAt(string source, string signatureFragment, string lockName)
        => MethodBody(source, signatureFragment).IndexOf($"lock ({lockName})", StringComparison.Ordinal);

    // ------------------------------------------------------------------ the three allocator-touching sites

    /// <summary>
    /// <c>CharacterVirtual::SetShape</c> — <c>joltc.cpp:8223</c>. This is the PHYS-1 defect: it held
    /// <c>_characterGate</c> only, and <c>Step</c> releases that gate before <c>_system.Update</c>, so the two
    /// ran concurrently on one allocator.
    /// </summary>
    [Fact]
    public void SetCharacterShape_takes_simLock_before_characterGate()
    {
        var source = BackendSource();
        var sim = LockAt(source, "public void SetCharacterShape(", "_simLock");
        var chr = LockAt(source, "public void SetCharacterShape(", "_characterGate");

        Assert.True(sim >= 0,
            "SetCharacterShape calls CharacterVirtual.SetShape, which allocates on the PhysicsSystem's "
            + "TempAllocator (joltc.cpp:8223). Without _simLock it races _system.Update on that same allocator "
            + "and Jolt aborts the process (TempAllocator.h:83-84). This is PHYS-1.");
        Assert.True(chr >= 0, "the character record still needs _characterGate");
        Assert.True(sim < chr, "lock order is _simLock then _characterGate, as the file's own header states");
    }

    /// <summary>
    /// <c>CharacterVirtual::ExtendedUpdate</c> — <c>joltc.cpp:8135</c>. Reached only from <c>StepCharacter</c>,
    /// which is called only from <c>Step</c>, inside both locks. If a second caller ever appears this test does
    /// not catch it, so the call-site count is asserted too.
    /// </summary>
    [Fact]
    public void ExtendedUpdate_is_only_reachable_from_the_step()
    {
        var source = BackendSource();

        Assert.Equal(1, Regex.Matches(source, @"\.ExtendedUpdate\(").Count);
        Assert.Equal(1, Regex.Matches(source, @"\bStepCharacter\(\s*\w+\s*,").Count);   // the one call, in Step

        var step = MethodBody(source, "StepResult Step(");
        Assert.Contains("StepCharacter(", step);
        Assert.Contains("lock (_simLock)", step);
        Assert.Contains("lock (_characterGate)", step);
    }

    /// <summary>
    /// <c>PhysicsSystem::Update</c> — <c>joltc.cpp:1050</c>. One call site, inside <c>_simLock</c>.
    /// </summary>
    [Fact]
    public void PhysicsSystem_Update_is_called_once_and_under_simLock()
    {
        var source = BackendSource();
        Assert.Equal(1, Regex.Matches(source, @"_system\.Update\(").Count);

        var step = MethodBody(source, "StepResult Step(");
        Assert.Contains("_system.Update(", step);

        var lockAt = step.IndexOf("lock (_simLock)", StringComparison.Ordinal);
        var updateAt = step.IndexOf("_system.Update(", StringComparison.Ordinal);
        Assert.True(lockAt >= 0 && lockAt < updateAt, "_system.Update must be inside the step's _simLock");
    }

    // ------------------------------------------------------------------ the rule that was already written down

    /// <summary>
    /// Every remaining public character op takes <c>_characterGate</c>. Those that do not touch the allocator
    /// may hold it alone; this pins which ones those are, so that adding an allocator-touching native call to
    /// one of them is a decision someone has to make deliberately rather than by accident — which is precisely
    /// how PHYS-1 happened.
    /// </summary>
    [Theory]
    [InlineData("public void SetCharacterTransform(", false)]   // Position/Rotation setters, no scratch
    [InlineData("public void ReGroundCharacter(", false)]       // Position/LinearVelocity setters, no scratch
    [InlineData("public void SetCharacterMovement(", false)]    // managed fields only
    [InlineData("public bool TryGetCharacterState(", false)]    // reads
    [InlineData("public void SetCharacterShape(", true)]        // CharacterVirtual::SetShape -> the allocator
    [InlineData("public CharacterId CreateCharacter(", true)]
    [InlineData("public void RemoveCharacter(", true)]
    public void Character_ops_hold_the_locks_their_native_calls_require(string signature, bool needsSimLock)
    {
        var source = BackendSource();
        var chr = LockAt(source, signature, "_characterGate");
        var sim = LockAt(source, signature, "_simLock");

        Assert.True(chr >= 0, $"{signature} must take _characterGate");
        Assert.Equal(needsSimLock, sim >= 0);
        if (needsSimLock) Assert.True(sim < chr, "lock order is _simLock then _characterGate");
    }
}
