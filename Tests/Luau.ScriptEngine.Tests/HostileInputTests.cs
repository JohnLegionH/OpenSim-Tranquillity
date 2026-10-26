/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>A theory that runs only where the native library can be found; see <see cref="RequiresNativeFactAttribute"/>.</summary>
public sealed class RequiresNativeTheoryAttribute : TheoryAttribute
{
    public RequiresNativeTheoryAttribute()
    {
        Skip = new RequiresNativeFactAttribute().Skip;
    }
}

/// <summary>
/// Hostile source through the real library. Only shapes that were first run in a child process and survived are used
/// here, and the compile runs on the guarded thread; the shapes that overflowed a native stack are in
/// <see cref="SourceGuardTests"/> with the native call replaced.
/// </summary>
[Collection("native-library")]
public class HostileInputTests
{
    private static string Rep(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    private static string Lsl(string body) => "default\n{\n    state_entry()\n    {\n" + body + "\n    }\n}\n";

    private static CompileResult Compile(string source)
    {
        RealLibrary.Load();
        using var compiler = new ScriptCompiler();
        return compiler.Compile(source);
    }

    [RequiresNativeFact]
    public void LslAtTheNestingLimitStillCompilesWithTheRealCompiler()
    {
        Assert.True(Compile(Lsl("        integer x = " + Rep("(", 1000) + "1" + Rep(")", 1000) + ";")).Success);
        Assert.True(Compile(Lsl(Rep("{", 450) + Rep("}", 450))).Success);
    }

    [RequiresNativeFact]
    public void AnLslOperatorChainTheCompilerCannotHoldIsAnErrorNotACrash()
    {
        // 400 member accesses summed: inside the pre-check, past what the compiler can allocate registers for.
        CompileResult r = Compile(Lsl("        vector v;\n        float f = " + Rep("v.x + ", 400) + "1.0;"));
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
    }

    [RequiresNativeFact]
    public void ALslErrorFloodIsCutToTheErrorCapWithACount()
    {
        // 300 declarations of one name: the compiler reports 299 errors.
        CompileResult r = Compile(Lsl("        " + Rep("integer a = 1; ", 300)));
        Assert.False(r.Success);
        Assert.Equal(21, r.Errors.Count);
        Assert.Contains("more errors", r.Errors[^1].Message);
    }

    [RequiresNativeTheory]
    [InlineData("parentheses", 100000)]
    [InlineData("calls", 10000)]
    [InlineData("tables", 10000)]
    [InlineData("binary", 100000)]
    [InlineData("blocks", 100000)]
    [InlineData("ifs", 10000)]
    [InlineData("whiles", 100000)]
    [InlineData("functions", 10000)]
    [InlineData("elseifs", 10000)]
    [InlineData("indexes", 100000)]
    public void SluaNestedPastWhatTheCompilerAllowsIsACompileError(string shape, int n)
    {
        string source = shape switch
        {
            "parentheses" => "local x = " + Rep("(", n) + "1" + Rep(")", n),
            "calls" => "local x = " + Rep("math.abs(", n) + "1" + Rep(")", n),
            "tables" => "local x = " + Rep("{", n) + Rep("}", n),
            "binary" => "local x = 1" + Rep(" + 1", n),
            "blocks" => Rep("do ", n) + Rep(" end", n),
            "ifs" => Rep("if true then ", n) + Rep(" end", n),
            "whiles" => Rep("while false do ", n) + Rep(" end", n),
            "functions" => Rep("local function f() ", n) + Rep(" end", n),
            "elseifs" => "local a = 1\nif a == 0 then a = 1\n" + Rep("elseif a == 1 then a = 2\n", n) + "end",
            _ => "local t = {} local x = t" + Rep(".a", n),
        };
        CompileResult r = Compile("--!slua\n" + source);
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
        Assert.False(r.Refused);
    }

    [RequiresNativeFact]
    public void ALargeValidSluaFileCompiles()
    {
        Assert.True(Compile("--!slua\n" + Rep("local a = 1\n", 20000)).Success);
    }

    [RequiresNativeFact]
    public void TheCompilerStaysUsableAfterHostileInput()
    {
        Compile("--!slua\n" + Rep("(", 100000));
        Assert.True(Compile("--!slua\nll.Say(0, 'still here')\n").Success);
    }
}
