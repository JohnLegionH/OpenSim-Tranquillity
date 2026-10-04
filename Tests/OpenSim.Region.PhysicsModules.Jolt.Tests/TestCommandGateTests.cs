/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// [Jolt] TestCommands decides whether the console test commands exist. With it off (the default) only the
/// read-only `jolt` subcommands are registered and listed by help, and a test subcommand reaches the read-only
/// handler, which answers with the usage line. With it on every test subcommand and `jolt parity` is its own
/// command. [Startup] JoltAutoDropTest is honoured only with the key on.
/// </summary>
public class TestCommandGateTests
{
    private sealed class Harness
    {
        public readonly Commands Commands = new Commands();
        public readonly List<string> Calls = new List<string>();

        public Harness(bool testCommands)
        {
            JoltScene.RegisterConsoleCommands(Commands, testCommands,
                (_, cmd) => Calls.Add("read " + cmd[1]),
                (_, cmd) => Calls.Add("test " + cmd[1]));
            JoltScene.RegisterParityCommand(Commands, testCommands, (_, cmd) => Calls.Add("parity " + cmd[2]));
        }

        public string Run(params string[] cmd)
        {
            Calls.Clear();
            Commands.Resolve(cmd);
            return Assert.Single(Calls);
        }

        public List<string> PhysicsHelp() => Commands.GetHelp(new[] { "help", "Physics" });
    }

    private static readonly string[] TestNames = JoltScene.TestCommands.Select(t => t.Name).ToArray();

    [Fact]
    public void The_two_groups_are_disjoint_and_named_once()
    {
        Assert.Equal(TestNames.Length, TestNames.Distinct().Count());
        Assert.Equal(JoltScene.ReadOnlyCommands.Length, JoltScene.ReadOnlyCommands.Distinct().Count());
        Assert.Empty(TestNames.Intersect(JoltScene.ReadOnlyCommands));
        Assert.DoesNotContain("parity", JoltScene.ReadOnlyCommands);
    }

    [Fact]
    public void Key_off_registers_the_read_only_commands_only()
    {
        var c = new Harness(testCommands: false);

        foreach (string sub in JoltScene.ReadOnlyCommands)
            Assert.Equal("read " + sub, c.Run("jolt", sub, "1", "2"));

        // Not registered: each falls through to the read-only handler, which prints the usage line.
        foreach (string sub in TestNames)
            Assert.Equal("read " + sub, c.Run("jolt", sub));
        Assert.Equal("read parity", c.Run("jolt", "parity", "core"));
    }

    [Fact]
    public void Key_off_help_lists_no_test_command()
    {
        var help = new Harness(testCommands: false).PhysicsHelp();

        Assert.Contains(help, l => l.StartsWith(JoltScene.ReadOnlyUsage + " - ", StringComparison.Ordinal));
        foreach (string sub in TestNames.Append("parity"))
            Assert.DoesNotContain(help, l => l.StartsWith("jolt " + sub, StringComparison.Ordinal));
        foreach (string sub in TestNames)
            Assert.DoesNotContain(" " + sub, JoltScene.ReadOnlyUsage + " ");
    }

    [Fact]
    public void Key_on_registers_every_command()
    {
        var c = new Harness(testCommands: true);

        foreach (string sub in JoltScene.ReadOnlyCommands)
            Assert.Equal("read " + sub, c.Run("jolt", sub, "1", "2"));
        foreach (string sub in TestNames)
            Assert.Equal("test " + sub, c.Run("jolt", sub, "4"));
        Assert.Equal("parity core", c.Run("jolt", "parity", "core"));
    }

    [Fact]
    public void Key_on_help_lists_every_command()
    {
        var help = new Harness(testCommands: true).PhysicsHelp();

        Assert.Contains(help, l => l.StartsWith(JoltScene.ReadOnlyUsage + " - ", StringComparison.Ordinal));
        foreach (var (_, syntax, _) in JoltScene.TestCommands)
            Assert.Contains(help, l => l.StartsWith(syntax + " - ", StringComparison.Ordinal));
        Assert.Contains(help, l => l.StartsWith("jolt parity ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null, false)]        // neither key: off
    [InlineData(null, "true", false)]      // JoltAutoDropTest alone does nothing
    [InlineData("false", "true", false)]
    [InlineData("true", null, false)]      // TestCommands alone does not drop
    [InlineData("true", "false", false)]
    [InlineData("true", "true", true)]
    public void JoltAutoDropTest_needs_TestCommands(string testCommands, string autoDrop, bool expected)
    {
        var src = new IniConfigSource();
        if (testCommands != null)
            src.AddConfig("Jolt").Set("TestCommands", testCommands);
        if (autoDrop != null)
            src.AddConfig("Startup").Set("JoltAutoDropTest", autoDrop);

        Assert.Equal(testCommands == "true", JoltScene.TestCommandsEnabled(src));
        Assert.Equal(expected, JoltScene.AutoDropTestEnabled(src));
    }

    [Fact]
    public void TestCommands_defaults_to_off()
    {
        Assert.False(JoltScene.TestCommandsEnabled(new IniConfigSource()));
        Assert.False(JoltScene.TestCommandsEnabled(null));
    }

    // RegionLoaded needs a live Scene, so the call site is checked by reading it: the drop runs only behind
    // AutoDropTestEnabled, and nothing else reads the JoltAutoDropTest key.
    [Fact]
    public void RegionLoaded_drops_only_behind_the_gate()
    {
        string dir = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "Source", "OpenSim.Region.PhysicsModules.Jolt");
        string scene = File.ReadAllText(Path.Combine(dir, "JoltScene.cs"));
        string tests = File.ReadAllText(Path.Combine(dir, "JoltTestCommands.cs"));

        int at = scene.IndexOf("public void RegionLoaded(Scene scene)", StringComparison.Ordinal);
        int end = scene.IndexOf("internal static readonly string[] ReadOnlyCommands", at, StringComparison.Ordinal);
        string body = scene[at..end];
        int gate = body.IndexOf("if (AutoDropTestEnabled(m_Config))", StringComparison.Ordinal);
        Assert.True(gate > 0);
        Assert.True(body.IndexOf("DropOne(", StringComparison.Ordinal) > gate);
        Assert.Equal(3, Count(body, "DropOne(\"box\"", 0));

        Assert.DoesNotContain("\"JoltAutoDropTest\"", scene);
        Assert.Equal(1, Count(tests, "\"JoltAutoDropTest\"", 0));
    }

    private static int Count(string s, string what, int from)
    {
        int n = 0;
        for (int i = s.IndexOf(what, from, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string Here([CallerFilePath] string here = "") => here;
}
