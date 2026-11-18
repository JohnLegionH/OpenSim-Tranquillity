/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.RegularExpressions;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A fact that runs only where the runtime's builtins.txt can be compared: SLUA_BUILTINS_PATH names the runtime's
/// builtins.txt. Without it the fact is reported as skipped with the reason, not passed.
/// </summary>
public sealed class RequiresBuiltinsFactAttribute : FactAttribute
{
    public RequiresBuiltinsFactAttribute()
    {
        string path = Environment.GetEnvironmentVariable("SLUA_BUILTINS_PATH");
        if (string.IsNullOrWhiteSpace(path))
            Skip = "SLUA_BUILTINS_PATH is not set; point it at the runtime's builtins.txt";
        else if (!File.Exists(path))
            Skip = "SLUA_BUILTINS_PATH does not name a file";
    }
}

/// <summary>Where the copy of the runtime's builtins.txt and the note about its source sit in the test output.</summary>
internal static class BuiltinsCopy
{
    internal static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "builtins.txt");

    internal static string NotePath => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "builtins-source.txt");
}

/// <summary>
/// The copy of builtins.txt in the test data is the runtime's file, byte for byte, and says which runtime commit it came from.
/// </summary>
public class BuiltinsCopyTests
{
    [Fact]
    public void TheCopyIsInTheTestDataAndHoldsTheRuntimesEventLines()
    {
        Assert.True(File.Exists(BuiltinsCopy.Path), "TestData/builtins.txt is missing from the test output");
        Assert.Contains(File.ReadLines(BuiltinsCopy.Path), line => line.StartsWith("event touch_start(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNoteNamesThePinnedRuntimeCommit()
    {
        Assert.True(File.Exists(BuiltinsCopy.NotePath), "TestData/builtins-source.txt is missing from the test output");
        Assert.Matches(@"(?m)^pinned runtime commit: [0-9a-f]{10,40}\s*$", File.ReadAllText(BuiltinsCopy.NotePath));
    }

    [RequiresBuiltinsFact]
    public void TheCopyIsByteIdenticalToTheRuntimesWhenItsPathIsSet()
    {
        byte[] runtime = File.ReadAllBytes(Environment.GetEnvironmentVariable("SLUA_BUILTINS_PATH"));
        byte[] copy = File.ReadAllBytes(BuiltinsCopy.Path);
        Assert.True(runtime.AsSpan().SequenceEqual(copy), $"the copy ({copy.Length} bytes) differs from the runtime's builtins.txt ({runtime.Length} bytes); copy the runtime's file over it");
    }
}

/// <summary>
/// The parameter types the engine uses to turn posted event arguments into values come from the runtime's own event
/// list (the "event" lines of its builtins.txt), through a generated table. These tests fail when the two disagree.
/// They read the copy of builtins.txt kept in the test data, so they run on any machine.
/// </summary>
[Collection("native-library")]
public class EventSignatureTests
{
    private static readonly Regex EventLine = new(@"^event\s+(\w+)\(\s*(.*?)\s*\)\s*$");

    /// <summary>The "event name( type name, ... )" lines of the copy of the runtime's builtins.txt, as name -> parameter types.</summary>
    internal static Dictionary<string, string[]> RuntimeEvents()
    {
        var events = new Dictionary<string, string[]>();
        foreach (string line in File.ReadLines(BuiltinsCopy.Path))
        {
            Match m = EventLine.Match(line);
            if (!m.Success)
                continue;
            string list = m.Groups[2].Value;
            events[m.Groups[1].Value] = list.Length == 0
                ? Array.Empty<string>()
                : list.Split(',').Select(p => p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToArray();
        }
        return events;
    }

    [Fact]
    public void TheTableHoldsExactlyTheRuntimesEventsWithTheirParameterTypes()
    {
        Dictionary<string, string[]> runtime = RuntimeEvents();
        Assert.NotEmpty(runtime);
        Assert.Equal(runtime.Keys.OrderBy(k => k, StringComparer.Ordinal), EventSignatures.Parameters.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach ((string name, string[] types) in runtime)
            Assert.True(types.SequenceEqual(EventSignatures.Parameters[name]), $"{name}: runtime ({string.Join(",", types)}) table ({string.Join(",", EventSignatures.Parameters[name])})");
    }

    [Fact]
    public void EveryKeyParameterOfEveryEventArrivesAsAKeyWhetherPostedAsUuidOrText()
    {
        foreach ((string name, string[] types) in RuntimeEvents())
        {
            foreach (bool asUuid in new[] { false, true })
            {
                object[] posted = types.Select(t => t switch
                {
                    "key" => asUuid ? (object)OpenMetaverse.UUID.Random() : OpenMetaverse.UUID.Random().ToString(),
                    "integer" => 1,
                    "float" => 1.5f,
                    "vector" => new OpenMetaverse.Vector3(1, 2, 3),
                    "rotation" => new OpenMetaverse.Quaternion(0, 0, 0, 1),
                    "list" => new object[] { 1, "a" },
                    _ => (object)"text",
                }).ToArray();

                Value[] values = EventArguments.Convert(name, posted);

                Assert.Equal(types.Length, values.Length);
                for (int i = 0; i < types.Length; i++)
                    Assert.True((types[i] == "key") == (values[i].Type == Bindings.ValueType.Key), $"{name} argument {i} ({types[i]}) became {values[i].Type}");
            }
        }
    }

    [RequiresNativeFact]
    public void TheRuntimeAcceptsEveryEventNameInTheTable()
    {
        Assert.NotEmpty(EventSignatures.Parameters);
        Assert.True(NativeRuntime.Load(new NativeRuntimeOptions()).Ok);
        foreach (string name in EventSignatures.Parameters.Keys)
            Assert.True(Host.EventIndex(name) >= 0, name);
        Assert.Throws<SluaException>(() => Host.EventIndex("not_an_event"));
    }
}
