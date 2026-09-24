using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// B2 (O-121). A deferred syscall body that calls ScriptSleep ran on the async worker and wrote
/// RunState = Sleeping directly (LSLSystemAPI.ScriptSleep). If that write landed after the scheduler
/// had already taken the script off the run queue as Syscall, the script was left Sleeping but tracked
/// by nothing, and its return was dropped because it was no longer in Syscall - stranded for good.
/// llRezObject never signalled a return at all, so it depended entirely on winning that race.
/// Written against the pre-B2 code first; these must fail there and pass after the fix.
/// </summary>
public class SyscallSleepRaceTests
{
    private readonly ITestOutputHelper _out;
    public SyscallSleepRaceTests(ITestOutputHelper output) { _out = output; }

    private const int Scripts = 12;

    private void EveryScriptResumes(string call, string tag)
    {
        using var h = new SchedulerHarness();
        var items = new List<OpenMetaverse.UUID>();
        for (int i = 0; i < Scripts; i++)
            items.Add(h.RezScript(@"
default
{
    state_entry()
    {
        " + call + @"
        llSay(0, """ + tag + @" " + i + @""");
    }
}
"));
        // Every body sleeps at most 2000 ms; 4 s of pumping is ample for all of them to come back.
        h.PumpFor(TimeSpan.FromSeconds(4));

        int resumed = 0;
        foreach (var id in items)
            _out.WriteLine($"{id} RunState={h.RunStateOf(id)} onRunQueue={h.IsOnRunQueue(id)}");
        for (int i = 0; i < Scripts; i++)
            if (h.Said.Contains(tag + " " + i)) resumed++;
        _out.WriteLine($"{tag}: {resumed}/{Scripts} scripts resumed after the call; said=[{string.Join(",", h.Said.Where(m => m.StartsWith(tag)))}]");
        Assert.Equal(Scripts, resumed);
    }

    /// <summary>llRezObject of a missing item: the body only does ScriptSleep(100) and an error shout,
    /// and never posts a return.</summary>
    [Fact]
    public void RezObjectAlwaysResumesTheScript()
        => EveryScriptResumes(@"llRezObject(""no such object"", llGetPos(), ZERO_VECTOR, ZERO_ROTATION, 0);", "after-rez");

    /// <summary>llGiveInventory to an unparsable key: ScriptSleep(2000) first, then RunAsync's completion.</summary>
    [Fact]
    public void GiveInventoryAlwaysResumesTheScript()
        => EveryScriptResumes(@"llGiveInventory(""not a key"", ""nothing"");", "after-give");
}
