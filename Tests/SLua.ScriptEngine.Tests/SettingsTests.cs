/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>The engine's settings in its [SLua] section, and their defaults when a key or the section is absent.</summary>
public class SettingsTests
{
    private static SLuaSettings Read(params (string Key, string Value)[] keys)
    {
        var source = new IniConfigSource();
        IConfig c = source.AddConfig(EngineIdentity.ConfigSection);
        foreach ((string key, string value) in keys)
            c.Set(key, value);
        return SLuaSettings.Read(source, new RecordingLog());
    }

    [Fact]
    public void WithNoSectionTheEngineIsOffAndNoLibraryPathIsSet()
    {
        SLuaSettings s = SLuaSettings.Read(new IniConfigSource(), new RecordingLog());
        Assert.False(s.Enabled);
        Assert.Null(s.NativeLibraryPath);
    }

    [Fact]
    public void AnEmptySectionLeavesTheEngineOff()
    {
        Assert.False(Read().Enabled);
    }

    [Fact]
    public void AnEmptyConfigSourceLeavesTheEngineOff()
    {
        Assert.False(SLuaSettings.Read(null, new RecordingLog()).Enabled);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void EnabledIsRead(string value, bool expected)
    {
        Assert.Equal(expected, Read(("Enabled", value)).Enabled);
    }

    [Fact]
    public void NativeLibraryPathIsRead()
    {
        Assert.Equal("/opt/slua", Read(("NativeLibraryPath", "/opt/slua")).NativeLibraryPath);
    }

    [Fact]
    public void ABlankNativeLibraryPathIsNotSet()
    {
        Assert.Null(Read(("NativeLibraryPath", "  ")).NativeLibraryPath);
    }
    [Fact]
    public void ThePart2SettingsHaveTheirDefaults()
    {
        SLuaSettings s = Read(("Enabled", "true"));
        Assert.Equal("ScriptEngines", s.ScriptEnginesPath);
        Assert.Equal(System.IO.Path.Combine("ScriptEngines", "SLua"), s.StateRoot);
        Assert.True(s.CompileCache);
        Assert.True(s.SaveScriptState);
        Assert.Equal(3, s.StateLoadRetries);
        Assert.Equal(500, s.StateLoadRetryDelayMs);
        Assert.False(s.ClaimHeaderlessScripts);
    }

    [Fact]
    public void ThePart2SettingsAreRead()
    {
        SLuaSettings s = Read(("ScriptEnginesPath", "/var/opensim/engines"), ("CompileCache", "false"), ("SaveScriptState", "false"),
            ("StateLoadRetries", "7"), ("StateLoadRetryDelayMs", "20"), ("ClaimHeaderlessScripts", "true"));
        Assert.Equal("/var/opensim/engines", s.ScriptEnginesPath);
        Assert.Equal(System.IO.Path.Combine("/var/opensim/engines", "SLua"), s.StateRoot);
        Assert.False(s.CompileCache);
        Assert.False(s.SaveScriptState);
        Assert.Equal(7, s.StateLoadRetries);
        Assert.Equal(20, s.StateLoadRetryDelayMs);
        Assert.True(s.ClaimHeaderlessScripts);
    }

    [Fact]
    public void NegativeRetriesAndDelaysAreReadAsZeroAndABlankFolderAsTheDefault()
    {
        SLuaSettings s = Read(("StateLoadRetries", "-2"), ("StateLoadRetryDelayMs", "-5"), ("ScriptEnginesPath", "  "));
        Assert.Equal(0, s.StateLoadRetries);
        Assert.Equal(0, s.StateLoadRetryDelayMs);
        Assert.Equal("ScriptEngines", s.ScriptEnginesPath);
    }
}
