/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// llHTTPRequest's in-flight caps (ten per object, two hundred per region) are switched by the engine's own HttpInFlightThrottle
/// setting, as they are for a Phlox script by Phlox's: on by default, off when the setting is false. The HTTP module here
/// never answers, so every request stays in flight.
/// </summary>
[Collection("native-library")]
public class HttpLimitTests
{
    private const int PerObject = 10;

    private static string AskForRequests(int count) =>
        "local nulls = 0\n" +
        "for i = 1, " + count + " do\n" +
        "  local key = ll.HTTPRequest('https://example.org/' .. i, {}, '')\n" +
        "  if tostring(key) == '00000000-0000-0000-0000-000000000000' then nulls = nulls + 1 end\n" +
        "end\n" +
        "ossl.Mark('nulls=' .. nulls)\n";

    private static string Run(Dictionary<string, string> settings, out FakeHttpModule http)
    {
        using var rig = new EngineRig(settings, productionFunctions: true);
        http = new FakeHttpModule { Hold = true };
        rig.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        UUID item = rig.Rez(Slua(AskForRequests(PerObject + 2)));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Any(m => m.StartsWith("nulls="))),
            "the script did not finish: marks=[" + string.Join(",", rig.MarksOf(item)) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "]");
        return rig.MarksOf(item).Single(m => m.StartsWith("nulls="));
    }

    [RequiresNativeFact]
    public void TheCapsApplyToAnSLuaScriptByDefaultAndTheRequestsPastTheObjectsCapGetNoKey()
    {
        string result = Run(null, out FakeHttpModule http);

        Assert.Equal("nulls=2", result);
        Assert.Equal(PerObject, http.Urls.Count);
    }

    [RequiresNativeFact]
    public void WithTheEnginesSwitchOffNoRequestIsCapped()
    {
        string result = Run(new Dictionary<string, string> { ["HttpInFlightThrottle"] = "false" }, out FakeHttpModule http);

        Assert.Equal("nulls=0", result);
        Assert.Equal(PerObject + 2, http.Urls.Count);
    }
}
