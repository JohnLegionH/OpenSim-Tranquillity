/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Phlox.ScriptEngine;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>Where the ll bindings come from: the generator, the runtime's function list and ILSLSystemAPI.</summary>
public class LlBindingGeneratorTests
{
    // ---- locating the repository and running the generator ----

    internal static string RepoRoot()
    {
        for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "tools", "slua-bindings", "generate-bindings.py"))) return dir;
        throw new InvalidOperationException("tools/slua-bindings/generate-bindings.py was not found above " + AppContext.BaseDirectory);
    }

    internal static string BuiltinsPath() => Path.Combine(RepoRoot(), "Tests", "SLua.ScriptEngine.Tests", "TestData", "builtins.txt");

    private static readonly string[] GeneratedNames = { "LlBindings.g.cs", "LlCoverage.g.cs", "LlServed.g.cs", "LlIndexes.g.cs", "LlBooleans.g.cs", "OsBindings.g.cs" };

    private static string GeneratedDir() => Path.Combine(RepoRoot(), "Source", "SLua.ScriptEngine", "Bindings");

    private static string RunGenerator(string outDir)
    {
        string root = RepoRoot();
        Directory.CreateDirectory(outDir);
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("BT_PYTHON") ?? "python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(root, "tools", "slua-bindings", "generate-bindings.py"));
        psi.ArgumentList.Add(BuiltinsPath());
        psi.ArgumentList.Add(Path.Combine(root, "Source", "Phlox.ScriptEngine", "ILSLSystemAPI.cs"));
        psi.ArgumentList.Add(Path.Combine(root, "Source", "Phlox.ScriptEngine", "LSLSystemAPI.cs"));
        psi.ArgumentList.Add(Path.Combine(root, "Source", "InWorldz.Phlox", "Glue", "SyscallShim.cs"));
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(Path.Combine(root, "Source", "InWorldz.Phlox", "Types", "Defaults.cs"));
        using Process p = Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "the generator exited with " + p.ExitCode + ": " + error + output);
        return output;
    }

    private static string Normal(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string ScratchDir(string name) => Path.Combine(Path.GetTempPath(), "llbind-" + name + "-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RunningTheGeneratorTwiceGivesIdenticalFiles()
    {
        string a = ScratchDir("a"), b = ScratchDir("b");
        RunGenerator(a);
        RunGenerator(b);
        foreach (string name in GeneratedNames)
        {
            Assert.True(File.Exists(Path.Combine(a, name)), name + " was not written");
            Assert.Equal(File.ReadAllBytes(Path.Combine(a, name)), File.ReadAllBytes(Path.Combine(b, name)));
        }
    }

    [Fact]
    public void TheCommittedGeneratedFilesAreWhatTheGeneratorWritesNow()
    {
        string a = ScratchDir("c");
        RunGenerator(a);
        foreach (string name in GeneratedNames)
            Assert.Equal(Normal(Path.Combine(a, name)), Normal(Path.Combine(GeneratedDir(), name)));
    }

    [Fact]
    public void TheGeneratedFilesNameNoLocalPathsOrWorkOrders()
    {
        foreach (string name in GeneratedNames)
        {
            string text = Normal(Path.Combine(GeneratedDir(), name));
            Assert.True(text.Length > (name == "LlBooleans.g.cs" ? 1000 : 2000), name + " is empty or a stub");
            Assert.DoesNotMatch(@"(?<![A-Za-z])[A-Za-z]:[\\/]", text);
            Assert.DoesNotMatch(@"\b(PHLOX|SLUA|CORE)-\d+", text);
        }
    }

    // ---- the runtime's list and the seam, read independently of the generator ----

    internal sealed record Builtin(string Return, string Name, string[] Params);

    internal static List<Builtin> ReadBuiltins()
    {
        var list = new List<Builtin>();
        var line = new Regex(@"^(void|integer|float|string|key|vector|rotation|list)\s+(ll\w+)\(\s*(.*?)\s*\)\s*$");
        foreach (string text in File.ReadAllLines(BuiltinsPath()))
        {
            Match m = line.Match(text);
            if (!m.Success) continue;
            string[] ps = m.Groups[3].Length == 0 ? new string[0] : m.Groups[3].Value.Split(',').Select(p => p.Trim().Split(' ')[0]).ToArray();
            list.Add(new Builtin(m.Groups[1].Value, m.Groups[2].Value, ps));
        }
        return list;
    }

    internal static System.Type ClrType(string lsl) => lsl switch
    {
        "void" => typeof(void),
        "integer" => typeof(int),
        "float" => typeof(float),
        "string" or "key" => typeof(string),
        "vector" => typeof(Vector3),
        "rotation" => typeof(Quaternion),
        "list" => typeof(LSLList),
        _ => throw new ArgumentException(lsl),
    };

    internal static MethodInfo[] SeamMembers(string prefix) =>
        typeof(ILSLSystemAPI).GetMethods().Where(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    internal static MethodInfo Matching(Builtin b, MethodInfo[] members) =>
        members.FirstOrDefault(m => m.Name == b.Name
            && m.ReturnType == ClrType(b.Return)
            && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(b.Params.Select(ClrType)));

    [Fact]
    public void TheTableBindsExactlyTheRuntimeFunctionsTheSeamHasWithTheSameSignature()
    {
        MethodInfo[] members = SeamMembers("ll");
        string[] adapted = LlCoverage.Adapted.Select(r => r.Substring(0, r.IndexOf(':'))).ToArray();
        string[] expected = ReadBuiltins().Where(b => Matching(b, members) != null || adapted.Contains(b.Name)).Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] actual = LlBindings.All.Select(f => f.ApiName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(expected.Length > 400, "the oracle found only " + expected.Length + " matching functions");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryBoundFunctionCarriesTheRuntimesTypes()
    {
        Dictionary<string, Builtin> builtins = ReadBuiltins().ToDictionary(b => b.Name);
        Assert.NotEmpty(LlBindings.All);
        foreach (BoundFunction f in LlBindings.All)
        {
            Builtin b = builtins[f.ApiName];
            Assert.Equal(b.Return, f.ReturnType);
            Assert.Equal(b.Params, f.ParamTypes);
            Assert.Equal(b.Name.Substring(2), f.Name);
        }
    }

    [Fact]
    public void NamesInTheTableAreUnique()
    {
        Assert.NotEmpty(LlBindings.All);
        Assert.Equal(LlBindings.All.Length, LlBindings.All.Select(f => f.Name).Distinct().Count());
    }

    // ---- a call through the table reaches the API method, with the converted arguments ----

    private static Value SampleArg(string type, int i) => type switch
    {
        "integer" => Value.Int(i + 3),
        "float" => Value.Num(i + 1.5),
        "string" => Value.Str("s" + i),
        "key" => Value.Key("k" + i),
        "vector" => Value.Vec(i + 1, i + 2, i + 3),
        "rotation" => Value.Rot(i + 0.5f, i + 1.5f, i + 2.5f, i + 3.5f),
        "list" => Value.List(Value.Int(i), Value.Str("a" + i)),
        _ => throw new ArgumentException(type),
    };

    private static object SampleExpected(string type, int i) => type switch
    {
        "integer" => i + 3,
        "float" => (float)(i + 1.5),
        "string" or "key" => type == "key" ? "k" + i : "s" + i,
        "vector" => new Vector3(i + 1, i + 2, i + 3),
        "rotation" => new Quaternion(i + 0.5f, i + 1.5f, i + 2.5f, i + 3.5f),
        "list" => new LSLList(new object[] { i, "a" + i }),
        _ => throw new ArgumentException(type),
    };

    private static void AssertSame(string function, int index, object expected, object actual)
    {
        if (expected is LSLList e)
        {
            var a = Assert.IsType<LSLList>(actual);
            Assert.True(e.Data.SequenceEqual(a.Data), function + " argument " + index + ": the list differs");
        }
        else Assert.True(Equals(expected, actual), function + " argument " + index + ": expected " + expected + " but the API got " + actual);
    }

    private static (object Set, Func<Value, bool> Check) SampleReturn(string type) => type switch
    {
        "integer" => (42, v => v.Type == Bindings.ValueType.Int && v.I == 42),
        "float" => (2.5f, v => v.Type == Bindings.ValueType.Number && v.N == 2.5),
        "string" => ("ret", v => v.Type == Bindings.ValueType.String && v.S == "ret"),
        "key" => ("ret-key", v => v.Type == Bindings.ValueType.Key && v.S == "ret-key"),
        "vector" => (new Vector3(7, 8, 9), v => v.Type == Bindings.ValueType.Vector && v.X == 7 && v.Y == 8 && v.Z == 9),
        "rotation" => (new Quaternion(0.25f, 0.5f, 0.75f, 1f), v => v.Type == Bindings.ValueType.Rotation && v.X == 0.25f && v.W == 1f),
        "list" => (new LSLList(new object[] { 1, "b" }), v => v.Type == Bindings.ValueType.List && v.Items.Length == 2 && v.Items[1].S == "b"),
        _ => throw new ArgumentException(type),
    };

    private static readonly HashSet<string> AdaptedNames = LlCoverage.Adapted.Select(r => r.Substring(0, r.IndexOf(':'))).ToHashSet();

    [Fact]
    public void EveryBoundFunctionPassesItsArgumentsToItsApiMethodOnceAndNothingElse()
    {
        Assert.NotEmpty(LlBindings.All);
        foreach (BoundFunction f in LlBindings.All.Where(f => !AdaptedNames.Contains(f.ApiName))) // the adapted ones have their own tests (AdaptedFunctionTests)
        {
            var (api, fake) = FakeLslApi.Create();
            Value[] args = f.ParamTypes.Select((t, i) => SampleArg(t, i)).ToArray();
            f.Call(new ScriptContext(null, api), args);

            FakeLslApi.Call[] calls = fake.Calls;
            Assert.True(calls.Length == 1, f.ApiName + " made " + calls.Length + " API calls");
            Assert.Equal(f.ApiName, calls[0].Name);
            Assert.Equal(f.ParamTypes.Length, calls[0].Args.Length);
            for (int i = 0; i < f.ParamTypes.Length; i++)
                AssertSame(f.ApiName, i, SampleExpected(f.ParamTypes[i], i), calls[0].Args[i]);
        }
    }

    [Fact]
    public void EveryBoundFunctionGivesBackWhatTheApiReturnedAsTheRightType()
    {
        Assert.NotEmpty(LlBindings.All);
        foreach (BoundFunction f in LlBindings.All.Where(f => !AdaptedNames.Contains(f.ApiName))) // the adapted ones have their own tests (AdaptedFunctionTests)
        {
            var (api, fake) = FakeLslApi.Create();
            Value[] args = f.ParamTypes.Select((t, i) => SampleArg(t, i)).ToArray();
            if (f.ReturnType == "void")
            {
                Value[] none = f.Call(new ScriptContext(null, api), args);
                Assert.True(none == null || none.Length == 0, f.ApiName + " returns nothing but gave values");
                continue;
            }
            var (set, check) = SampleReturn(f.ReturnType);
            fake.Returns(f.ApiName, set);
            Value[] result = f.Call(new ScriptContext(null, api), args);
            Assert.True(result != null && result.Length == 1, f.ApiName + " gave " + (result?.Length ?? 0) + " values");
            Assert.True(check(result[0]), f.ApiName + " returned " + result[0]);
        }
    }

    [Fact]
    public void AMissingArgumentIsAScriptErrorThatNamesTheFunction()
    {
        Assert.NotEmpty(LlBindings.All);
        BoundFunction say = LlBindings.All.Single(f => f.ApiName == "llStringLength");
        var (api, fake) = FakeLslApi.Create();
        var e = Assert.Throws<ArgumentException>(() => say.Call(new ScriptContext(null, api), new Value[0]));
        Assert.Contains("StringLength", e.Message);
        Assert.Empty(fake.Calls);
    }
}
