using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PROPS-1. Setting the touch label has to reach the viewer, not just the part.
///
/// <para>
/// The label a viewer shows comes from the FULL ObjectProperties reply -
/// <c>LLSelectMgr::processObjectProperties</c> fills <c>LLSelectNode::mTouchName</c>
/// (llselectmgr.cpp:6110) and the context menu reads it (llviewermenu.cpp:3096-3101). The region
/// sends that only on select (<c>Scene.PacketHandlers.cs:223</c>); a right-click asks for
/// <c>ObjectPropertiesFamily</c>, whose reply has no touch name. So a script that changes the label
/// after the last select was invisible - the menu still read "Touch" with TouchName set to "Enter".
/// </para>
/// </summary>
public class TouchLabelToViewerTests
{
    private readonly ITestOutputHelper _out;
    public TouchLabelToViewerTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void SettingTheTouchLabelPushesObjectPropertiesToClients()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        client.ObjectPropertiesSent.Clear();

        h.RezScript(@"
default
{
    state_entry()
    {
        llSetTouchText(""Enter"");
    }
}
");
        h.Pump();

        _out.WriteLine($"TouchName='{h.Prim.TouchName}' propertiesSent={client.ObjectPropertiesSent.Count}");

        Assert.Equal("Enter", h.Prim.TouchName);
        Assert.Contains(client.ObjectPropertiesSent, e => ReferenceEquals(e, h.Prim));
    }
}
