using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-2f. <c>llSetTimerEvent(3.0)</c> must fire once per three seconds. Live on 1.1.277 the same
/// script fired roughly five times a second.
/// </summary>
public class TimerCadenceTests
{
    private readonly ITestOutputHelper _out;
    public TimerCadenceTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void AThreeSecondTimerDoesNotFireFiveTimesASecond()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"
default
{
    state_entry()
    {
        llSetTimerEvent(3.0);
    }

    timer()
    {
        llSay(0, ""tick"");
    }
}
");
        h.Pump();
        h.ClearSaid(item);

        h.PumpFor(TimeSpan.FromSeconds(3.5));
        var ticks = h.Said.Count(m => m.Contains("tick"));

        _out.WriteLine($"ticks in 3.5s = {ticks}");
        // The defect signature was about five ticks a second - seventeen or so in this window.
        // The bound is deliberately loose: this is wall-clock timing on a shared machine, and a
        // tight bound here is a flaky test rather than a stronger one.
        Assert.True(ticks <= 4, $"a 3 s timer fired {ticks} times in 3.5 s");
        Assert.True(ticks >= 1, $"a 3 s timer did not fire at all in 3.5 s");
    }
}
