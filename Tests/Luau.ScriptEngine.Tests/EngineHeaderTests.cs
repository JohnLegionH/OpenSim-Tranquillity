/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>
/// The first-line header, read as YEngine reads its own ("//" at the very start, a first line ending at a
/// newline past index 5, the first ':' at index 3 or more, the name before it trimmed).
/// </summary>
public class EngineHeaderTests
{
    [Theory]
    [InlineData("//NGC.Luau:\ndefault {}", "NGC.Luau")]
    [InlineData("//NGC.Luau:lsl\ndefault {}", "NGC.Luau")]
    [InlineData("//NGC.Luau:slua\nprint(1)", "NGC.Luau")]
    [InlineData("// NGC.Luau :slua\nprint(1)", "NGC.Luau")]
    [InlineData("//NGC.Luau:\r\ndefault {}", "NGC.Luau")]
    [InlineData("//InWorldz.Phlox:\ndefault {}", "InWorldz.Phlox")]
    [InlineData("//YEngine:\ndefault {}", "YEngine")]
    [InlineData("--!slua\nprint(1)", null)]
    [InlineData("default {}", null)]
    [InlineData("//NGC.Luau\ndefault {}", null)]           // no colon
    [InlineData("//NG:\ndefault {}", null)]                // colon before index 3
    [InlineData("//NGC.Luau:", null)]                      // no newline: the line does not end past index 5
    [InlineData("//a:b\n", null)]                          // newline at index 5
    [InlineData(" //NGC.Luau:\ndefault {}", null)]         // not at the very start
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NamedEngineIsYEnginesParse(string script, string expected)
    {
        Assert.Equal(expected, EngineHeader.NamedEngine(script));
    }

    [Theory]
    [InlineData("//NGC.Luau:\nx", "")]
    [InlineData("//NGC.Luau:lsl\nx", "lsl")]
    [InlineData("//NGC.Luau:slua\nx", "slua")]
    [InlineData("//NGC.Luau: SLua \nx", "slua")]           // trimmed and lowercased
    [InlineData("//NGC.Luau:s\nx", "")]                    // fewer than two characters follow the colon: ignored
    [InlineData("//NGC.Luau:  \nx", "")]
    [InlineData("--!slua\nx", "")]
    [InlineData(null, "")]
    public void LanguageIsTheTextAfterTheColon(string script, string expected)
    {
        Assert.Equal(expected, EngineHeader.Language(script));
    }

    [Theory]
    [InlineData("//NGC.Luau:\ndefault {}", ScriptLanguage.Lsl)]
    [InlineData("//NGC.Luau:lsl\ndefault {}", ScriptLanguage.Lsl)]
    [InlineData("//NGC.Luau:LSL\ndefault {}", ScriptLanguage.Lsl)]
    [InlineData("//NGC.Luau:slua\nprint(1)", ScriptLanguage.Slua)]
    [InlineData("//NGC.Luau:SLua\nprint(1)", ScriptLanguage.Slua)]
    [InlineData("//NGC.Luau:python\nprint(1)", ScriptLanguage.Unsupported)]
    [InlineData("default {}", ScriptLanguage.Lsl)]                 // no header: the source decides
    [InlineData("--!slua\nprint(1)", ScriptLanguage.Slua)]
    [InlineData("  \n -- a comment\nprint(1)", ScriptLanguage.Slua)]  // leading white space, then "--"
    [InlineData("// a comment\ndefault {}", ScriptLanguage.Lsl)]
    [InlineData("//YEngine:slua\nx", ScriptLanguage.Lsl)]          // another engine's header gives no language here
    [InlineData("", ScriptLanguage.Lsl)]
    public void LanguageForPicksTheCompiler(string script, ScriptLanguage expected)
    {
        Assert.Equal(expected, EngineHeader.LanguageFor(script));
    }

    [Theory]
    [InlineData("//NGC.Luau:\nx", "InWorldz.Phlox", "NGC.Luau")]          // the header names a loaded engine: it wins
    [InlineData("//YEngine:\nx", "InWorldz.Phlox", "YEngine")]
    [InlineData("//InWorldz.Phlox:\nx", "NGC.Luau", "InWorldz.Phlox")]
    [InlineData("//Unloaded:\nx", "InWorldz.Phlox", "InWorldz.Phlox")]    // names no loaded engine: the scene's choice
    [InlineData("//ngc.luau:\nx", "InWorldz.Phlox", "InWorldz.Phlox")]    // the comparison is ordinal
    [InlineData("default {}", "NGC.Luau", "NGC.Luau")]
    public void OwnerIsTheNamedLoadedEngineElseTheScenesChoice(string script, string sceneChoice, string expected)
    {
        string[] loaded = { "InWorldz.Phlox", "YEngine", "NGC.Luau" };
        Assert.Equal(expected, EngineHeader.Owner(script, sceneChoice, loaded));
    }
}
