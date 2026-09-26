using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetEnv's product and channel keys answer what YEngine answers in the same scene (LSL_Api.llGetEnv:
/// region_product_name => RegionInfo.RegionType, region_product_sku and sim_channel => "OpenSim"),
/// not a fixed product name of one grid.
/// </summary>
[Collection("phlox-state")]
public class GetEnvProductKeysTests
{
    private static readonly string[] Keys = { "region_product_name", "region_product_sku", "sim_channel" };

    private readonly ITestOutputHelper _out;
    public GetEnvProductKeysTests(ITestOutputHelper o) => _out = o;

    private static void SetRegionType(RegionInfo ri, string type)
        => typeof(RegionInfo).GetField("m_regionType", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ri, type);

    [Theory]
    [InlineData("")]
    [InlineData("Mainland")]
    public void PhloxAnswersWhatYEngineAnswers(string regionType)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        SetRegionType(h.Scene.RegionInfo, regionType);

        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "yengine-api", "default { }");
        var yengine = new LSL_Api();
        yengine.Initialize(h.YEngine, h.Prim, item);

        h.RezScript("default { state_entry() { list k = [" + string.Join(", ", Keys.Select(k => "\"" + k + "\""))
            + "]; integer i; for (i = 0; i < llGetListLength(k); ++i) llSay(0, llList2String(k, i) + \"=\" + llGetEnv(llList2String(k, i))); } }");
        h.Pump();
        _out.WriteLine(string.Join(" | ", h.Said));

        foreach (var key in Keys)
        {
            string expected = key + "=" + (string)yengine.llGetEnv(key);
            _out.WriteLine("YEngine " + expected);
            Assert.Contains(expected, h.Said);
        }
    }
}
