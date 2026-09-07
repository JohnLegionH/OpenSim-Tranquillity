using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-2f. A fresh script instance runs its <c>state_entry</c> but never registers its event
/// mask, so the region does not know the prim is touchable: no touch cursor, and
/// <c>touch_start</c> never fires. Observed live on 1.1.277 at 15:48-15:53 — llSetColor, llSetText,
/// llSay and llOwnerSay all ran, the Running box was ticked, and the prim could not be clicked.
/// </summary>
public class EventMaskRegistrationTests
{
    private readonly ITestOutputHelper _out;
    public EventMaskRegistrationTests(ITestOutputHelper o) => _out = o;

    private const string Touchable = @"
default
{
    state_entry()
    {
        llSay(0, ""ready"");
    }

    touch_start(integer n)
    {
        llSay(0, ""Touched."");
    }
}
";

    [Fact]
    public void AFreshCompileRegistersTouchOnThePart()
    {
        using var h = new SchedulerHarness();
        h.RezScript(Touchable);
        h.Pump();

        _out.WriteLine($"ScriptEvents={h.Prim.ScriptEvents} Aggregated={h.Prim.AggregatedScriptEvents} aggregated={h.Prim.AggregatedScriptEvents}");

        Assert.True(h.Prim.ScriptEvents.HasFlag(scriptEvents.touch_start),
            $"the part was never told the script handles touch; mask={h.Prim.ScriptEvents}");
        // PrimFlags.Touch is DERIVED from this: aggregateScriptEvents sets it in m_localFlags when
        // anytouch is present (SceneObjectPart.cs:5254-5256, :5269) and the Flags setter strips it
        // (:1524), so the aggregate is the observable invariant - and it is what the region's own
        // touch dispatch tests (Scene.PacketHandlers.cs:334).
        Assert.True((h.Prim.AggregatedScriptEvents & scriptEvents.anytouch) != 0,
            $"no touch bit reached the part, so the viewer shows no touch cursor; aggregated={h.Prim.AggregatedScriptEvents}");
    }

    [Fact]
    public void ASharedScriptStartAlsoRegistersTouch()
    {
        using var h = new SchedulerHarness();
        var asset = OpenMetaverse.UUID.Random();

        h.RezScript(Touchable, asset);
        h.Pump();
        h.RezScript(Touchable, asset);   // second instance of the same asset: the shared path
        h.Pump();

        Assert.True(h.Prim.ScriptEvents.HasFlag(scriptEvents.touch_start),
            $"mask after a shared start={h.Prim.ScriptEvents}");
        Assert.True((h.Prim.AggregatedScriptEvents & scriptEvents.anytouch) != 0);
    }

    [Fact]
    public void ATouchThroughTheScenesOwnPathReachesTouchStart()
    {
        // Not a directly posted event: this is the region's real route, the one that was silent in
        // world - EventManager.TriggerObjectGrab into the engine's OnObjectGrab handler.
        using var h = new SchedulerHarness();
        var item = h.RezScript(Touchable);
        h.Pump();
        Assert.True(h.SaidAnything(item), "state_entry must have run before touch means anything");

        h.ClearSaid(item);
        h.TouchViaScene();
        h.Pump();

        Assert.Contains(h.Said, m => m.Contains("Touched"));
    }
}
