/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>Every bound function, called once from a script of each language, reaches the fake ILSLSystemAPI.</summary>
[Collection("native-library")]
public class LlReachTests
{
    /// <summary>The functions an LSL script does not send to the host: the runtime evaluates them inside the compiled script.</summary>
    private static readonly string[] EvaluatedByTheRuntimeInLsl = { "llGetListLength" };

    private static string LslArg(string type) => type switch
    {
        "integer" => "1",
        "float" => "1.5",
        "string" => "\"s\"",
        "key" => "(key)\"k\"",
        "vector" => "<1, 2, 3>",
        "rotation" => "<1, 2, 3, 4>",
        _ => "[1, \"a\"]",
    };

    private static string SLuaArg(string type) => type switch
    {
        "integer" => "1",
        "float" => "1.5",
        "string" => "'s'",
        "key" => "'k'",
        "vector" => "vector(1, 2, 3)",
        "rotation" => "quaternion(1, 2, 3, 4)",
        _ => "{1, 'a'}",
    };

    /// <summary>Runs one script per bound function, one after the other, and returns the functions the fake never saw.</summary>
    private static string[] NotReached(Func<BoundFunction, string> script, bool lsl)
    {
        Assert.NotEmpty(LlBindings.All);
        var missing = new List<string>();
        using var rig = new BindingRig();
        foreach (BoundFunction f in LlBindings.All)
        {
            int before = rig.CallsOf(f.ApiName);
            rig.H.AddAndStart(script(f), lsl);
            for (int i = 0; i < 6; i++)
            {
                rig.H.Pump();
                rig.H.Advance(3.1); // long enough for any delay in the table
            }
            if (rig.CallsOf(f.ApiName) == before) missing.Add(f.ApiName);
        }
        Assert.Empty(rig.H.FaultedScripts);
        Assert.Empty(rig.H.ReportedErrors);
        return missing.ToArray();
    }

    [RequiresNativeFact]
    public void EveryBoundFunctionCalledFromAnLslScriptReachesTheApiExceptTheOneTheRuntimeEvaluatesItself()
    {
        string[] missing = NotReached(
            f => "default { state_entry() { " + f.ApiName + "(" + string.Join(", ", f.ParamTypes.Select(LslArg)) + "); } }", lsl: true);
        Assert.Equal(EvaluatedByTheRuntimeInLsl, missing);
    }

    [RequiresNativeFact]
    public void EveryBoundFunctionCalledFromAnSLuaScriptReachesTheApi()
    {
        string[] missing = NotReached(
            f => "ll." + f.Name + "(" + string.Join(", ", f.ParamTypes.Select(SLuaArg)) + ")", lsl: false);
        Assert.Empty(missing);
    }
}
