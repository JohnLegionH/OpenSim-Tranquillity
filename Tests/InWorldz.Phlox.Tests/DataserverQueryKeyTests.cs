using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-21 part F (C-5). The key a dataserver request returns is the key its dataserver event
/// carries - that is how a script matches the answer to the question
/// (https://wiki.secondlife.com/wiki/Dataserver). Each request here is used as an expression and
/// compared with the event's key.
/// </summary>
[Collection("phlox-state")]
public class DataserverQueryKeyTests
{
    private readonly ITestOutputHelper _out;
    public DataserverQueryKeyTests(ITestOutputHelper o) => _out = o;

    private string Run(Func<OpenSim.Tests.Common.TestClient, string> request)
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        h.RezScript("key q; default { state_entry() { q = " + request(client) + "; llSay(0, \"asked \" + (string)q); } " +
                    "dataserver(key id, string d) { llSay(0, \"answer match=\" + (string)(id == q) + \" data=\" + d); } }");
        h.PumpFor(TimeSpan.FromSeconds(3));
        var said = string.Join(" | ", h.Said);
        _out.WriteLine(said + " || debug: " + string.Join(" | ", h.SaidOn.Where(s => s.Channel == 0x7FFFFFFF).Select(s => s.Message)));
        Assert.True(h.Said.Any(s => s.StartsWith("asked ") && s != "asked " + OpenMetaverse.UUID.Zero), "no query key came back: " + said);
        Assert.True(h.Said.Any(s => s.StartsWith("answer match=1")), "the dataserver key is not the returned key: " + said);
        return h.Said.First(s => s.StartsWith("answer match=1"));
    }

    [Fact]
    public void RequestAgentDataNameReturnsTheEventsKey()
    {
        var answer = Run(c => $"llRequestAgentData(\"{c.AgentId}\", DATA_NAME)");
        Assert.Contains("data=", answer);
    }

    [Fact]
    public void RequestAgentDataPayinfoReturnsTheEventsKeyAndAPayinfoValue()
    {
        var answer = Run(c => $"llRequestAgentData(\"{c.AgentId}\", {SlConstantsTests.Fixture.Value["DATA_PAYINFO"].Value})");
        Assert.Matches("data=[0-3]$", answer);
    }

    [Fact]
    public void RequestDisplayNameReturnsTheEventsKey() => Run(c => $"llRequestDisplayName(\"{c.AgentId}\")");

    [Fact]
    public void RequestUsernameReturnsTheEventsKey() => Run(c => $"llRequestUsername(\"{c.AgentId}\")");

    [Fact]
    public void AvatarName2KeyReturnsTheEventsKey()
        => Run(c => $"iwAvatarName2Key(\"{c.FirstName}\", \"{c.LastName}\")");
}
