/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The pre-check and the compile thread, with the native call replaced so no hostile text ever reaches the real
/// compiler (a native stack overflow would end the test host). These touch no process-wide state and run in parallel.
/// </summary>
public class SourceGuardTests
{
    private static readonly byte[] FakeAsset = { 1, 2, 3 };

    /// <summary>A native call that fails the test when it is reached: the pre-check must have refused the source first.</summary>
    private static NativeCompile MustNotRun(List<string> reached = null) => (text, lsl) =>
    {
        reached?.Add(text);
        throw new Xunit.Sdk.XunitException("the source reached the native compiler");
    };

    private static string Lsl(string body) => "default\n{\n    state_entry()\n    {\n" + body + "\n    }\n}\n";

    private static CompileResult Compile(string source, CompileLimits limits = null, NativeCompile native = null)
    {
        using var compiler = new ScriptCompiler(limits, native ?? MustNotRun());
        return compiler.Compile(source);
    }

    // ---- LSL nesting and chains are refused before the native compiler sees them ----

    [Fact]
    public void LslParenthesesNestedPastTheLimitAreRefusedWithAClearErrorOnTheirLine()
    {
        string source = Lsl("\n        integer x = " + new string('(', 1001) + "1" + new string(')', 1001) + ";");
        CompileResult r = Compile(source);
        Assert.False(r.Success);
        CompileError e = Assert.Single(r.Errors);
        Assert.Equal(6, e.Line);
        Assert.Contains("nested too deeply", e.Message);
        Assert.Contains("1000", e.Message);
    }

    [Fact]
    public void LslBracketsCountWithParenthesesTowardTheSameDepth()
    {
        string source = Lsl("        list x = " + string.Concat(Enumerable.Repeat("([", 501)) + ";");
        CompileResult r = Compile(source);
        Assert.False(r.Success);
        Assert.Contains("nested too deeply", r.Errors[0].Message);
    }

    [Fact]
    public void LslBracesNestedPastTheLimitAreRefused()
    {
        string source = Lsl(new string('{', 500) + new string('}', 500));
        CompileResult r = Compile(source);
        Assert.False(r.Success);
        Assert.Contains("blocks nested too deeply", r.Errors[0].Message);
        Assert.Contains("500", r.Errors[0].Message);
    }

    [Fact]
    public void AnOperatorChainPastTheLimitIsRefusedBeforeTheCompilerSeesIt()
    {
        // A long member-access sum was the one shape that overflowed the native stack of a default-sized thread and took quadratic time.
        string source = Lsl("        vector v;\n        float f = " + string.Concat(Enumerable.Repeat("v.x + ", 1001)) + "1.0;");
        CompileResult r = Compile(source);
        Assert.False(r.Success);
        CompileError e = Assert.Single(r.Errors);
        Assert.Equal(6, e.Line);
        Assert.Contains("operators", e.Message);
        Assert.Contains("1000", e.Message);
    }

    [Fact]
    public void TheOperatorCountStartsOverAtEachStatement()
    {
        var reached = new List<string>();
        string statements = string.Concat(Enumerable.Repeat("        float f = " + string.Concat(Enumerable.Repeat("1.0 + ", 400)) + "1.0;\n", 10));
        CompileResult r = Compile(Lsl(statements), native: (t, l) => { reached.Add(t); return FakeAsset; });
        Assert.True(r.Success);
        Assert.Single(reached);
    }

    [Fact]
    public void ExactlyTheLimitIsAccepted()
    {
        CompileResult paren = Compile(Lsl("        integer x = " + new string('(', 1000) + "1" + new string(')', 1000) + ";"), native: (t, l) => FakeAsset);
        Assert.True(paren.Success);
        CompileResult brace = Compile(Lsl(new string('{', 498) + new string('}', 498)), native: (t, l) => FakeAsset);
        Assert.True(brace.Success); // the state and the event add two more levels: 500 in all
        CompileResult chain = Compile(Lsl("        float f = " + string.Concat(Enumerable.Repeat("1.0 + ", 997)) + "1.0;"), native: (t, l) => FakeAsset);
        Assert.True(chain.Success);
    }

    [Fact]
    public void BracketsAndOperatorsInsideStringsAndCommentsAreNotCounted()
    {
        string body = "        string s = \"" + new string('(', 5000) + "\";\n"
                    + "        // " + new string('{', 5000) + "\n"
                    + "        /* " + new string('[', 5000) + " + + + */\n"
                    + "        string t = \"a\\\"" + string.Concat(Enumerable.Repeat("+ ", 3000)) + "\";";
        CompileResult r = Compile(Lsl(body), native: (t, l) => FakeAsset);
        Assert.True(r.Success);
    }

    // ---- source size, both languages ----

    [Theory]
    [InlineData("lsl")]
    [InlineData("slua")]
    public void SourceOverTheSizeLimitIsRefusedInEitherLanguage(string language)
    {
        string filler = new string('x', 300 * 1024);
        string source = language == "lsl" ? "default\n{\n    state_entry() { }\n}\n// " + filler : "--!slua\n-- " + filler;
        CompileResult r = Compile(source);
        Assert.False(r.Success);
        CompileError e = Assert.Single(r.Errors);
        Assert.Contains("too large", e.Message);
        Assert.Contains((256 * 1024).ToString(), e.Message);
    }

    [Fact]
    public void TheSizeIsMeasuredInUtf8BytesNotCharacters()
    {
        var limits = new CompileLimits { MaxSourceBytes = 100 };
        string source = "default { } // " + new string('é', 40); // 15 + 80 bytes: inside
        Assert.True(Compile(source, limits, (t, l) => FakeAsset).Success);
        Assert.False(Compile(source + new string('é', 10), limits, (t, l) => FakeAsset).Success); // 115 bytes
    }

    [Fact]
    public void SluaIsNotGivenTheLslNestingLimits()
    {
        // Luau limits its own nesting and was probed safe at 100,000 levels, so only the size limit applies.
        string source = "--!slua\nlocal x = " + new string('(', 5000) + "1" + new string(')', 5000) + "\n";
        var reached = new List<string>();
        CompileResult r = Compile(source, native: (t, l) => { reached.Add(t); return FakeAsset; });
        Assert.True(r.Success);
        Assert.Single(reached);
    }

    // ---- the compile thread ----

    [Fact]
    public void CompilingRunsOnAnotherThreadThanTheCaller()
    {
        int caller = Environment.CurrentManagedThreadId;
        int compileThread = 0;
        CompileResult r = Compile(Lsl(""), native: (t, l) => { compileThread = Environment.CurrentManagedThreadId; return FakeAsset; });
        Assert.True(r.Success);
        Assert.NotEqual(caller, compileThread);
    }

    [Fact]
    public void TheCompileThreadHasALargeFixedStack()
    {
        int depth = 0;
        CompileResult r = Compile(Lsl(""), native: (t, l) => { depth = Recurse(0); return FakeAsset; });
        Assert.True(r.Success);
        // Each frame holds 1 KiB; a 16 MiB stack holds well over 12,000 of them, a default thread's 1.5 MiB far fewer.
        Assert.True(depth > 12000, "recursed only " + depth + " KiB-frames");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe int Recurse(int n)
    {
        byte* buffer = stackalloc byte[1024];
        buffer[0] = (byte)n;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return n;
        return Recurse(n + 1) + (buffer[0] == 255 ? 0 : 0);
    }

    [Fact]
    public void ACompileThatThrowsSomethingUnexpectedIsAnErrorAndTheThreadCarriesOn()
    {
        using var compiler = new ScriptCompiler(null, (t, l) => t.Contains("boom") ? throw new InvalidOperationException("native blew up") : FakeAsset);
        CompileResult bad = compiler.Compile(Lsl("        boom"));
        Assert.False(bad.Success);
        CompileError e = Assert.Single(bad.Errors);
        Assert.Contains("not a script error", e.Message);
        Assert.Contains("InvalidOperationException", e.Message);
        Assert.DoesNotContain("native blew up", e.Message); // the detail stays in the log, not the resident's editor

        Assert.True(compiler.Compile(Lsl("        ok")).Success);
    }

    [Fact]
    public void ManyCallersAtOnceAreAllAnswered()
    {
        using var compiler = new ScriptCompiler(null, (t, l) => FakeAsset);
        Task<CompileResult>[] jobs = Enumerable.Range(0, 20).Select(i => Task.Run(() => compiler.Compile(Lsl("        // " + i)))).ToArray();
        Assert.True(Task.WaitAll(jobs.Cast<Task>().ToArray(), TimeSpan.FromSeconds(30)));
        Assert.All(jobs, j => Assert.True(j.Result.Success));
    }

    [Fact]
    public void ErrorsPastTheCapAreCountedInOneClosingEntry()
    {
        var limits = new CompileLimits { MaxErrors = 3 };
        string detail = string.Join("\n", Enumerable.Range(2, 10).Select(i => i + ": `x' is undeclared."));
        using var compiler = new ScriptCompiler(limits, (t, l) => throw new SluaException("slua_compile", -5, detail));
        CompileResult r = compiler.Compile(Lsl("        x();"));
        Assert.Equal(4, r.Errors.Count);
        Assert.Equal(new[] { 1, 2, 3 }, r.Errors.Take(3).Select(e => e.Line));
        Assert.Contains("7 more errors", r.Errors[3].Message);
    }
}
