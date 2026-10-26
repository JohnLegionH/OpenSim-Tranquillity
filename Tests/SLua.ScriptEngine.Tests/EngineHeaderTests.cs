/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The first-line header, read as YEngine reads its own ("//" at the very start, a first line ending at a
/// newline past index 5, the first ':' at index 3 or more, the name before it trimmed).
/// </summary>
public class EngineHeaderTests
{
    [Theory]
    [InlineData("//SLua:\ndefault {}", "SLua")]
    [InlineData("//SLua:lsl\ndefault {}", "SLua")]
    [InlineData("//SLua:slua\nprint(1)", "SLua")]
    [InlineData("// SLua :slua\nprint(1)", "SLua")]
    [InlineData("//SLua:\r\ndefault {}", "SLua")]
    [InlineData("//InWorldz.Phlox:\ndefault {}", "InWorldz.Phlox")]
    [InlineData("//YEngine:\ndefault {}", "YEngine")]
    [InlineData("--!slua\nprint(1)", null)]
    [InlineData("default {}", null)]
    [InlineData("//SLua\ndefault {}", null)]           // no colon
    [InlineData("//NG:\ndefault {}", null)]                // colon before index 3
    [InlineData("//SLua:", null)]                      // no newline: the line does not end past index 5
    [InlineData("//a:b\n", null)]                          // newline at index 5
    [InlineData(" //SLua:\ndefault {}", null)]         // not at the very start
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NamedEngineIsYEnginesParse(string script, string expected)
    {
        Assert.Equal(expected, EngineHeader.NamedEngine(script));
    }

    [Theory]
    [InlineData("//SLua:\nx", "")]
    [InlineData("//SLua:lsl\nx", "lsl")]
    [InlineData("//SLua:slua\nx", "slua")]
    [InlineData("//SLua: SLua \nx", "slua")]           // trimmed and lowercased
    [InlineData("//SLua:s\nx", "")]                    // fewer than two characters follow the colon: ignored
    [InlineData("//SLua:  \nx", "")]
    [InlineData("--!slua\nx", "")]
    [InlineData(null, "")]
    public void LanguageIsTheTextAfterTheColon(string script, string expected)
    {
        Assert.Equal(expected, EngineHeader.Language(script));
    }

    [Theory]
    [InlineData("//SLua:\nprint(1)", ScriptLanguage.Slua)]                // no language part: SLua
    [InlineData("//SLua:\ndefault {}", ScriptLanguage.Slua)]              // the header decides, not the body
    [InlineData("//SLua:lsl\ndefault {}", ScriptLanguage.Lsl)]
    [InlineData("//SLua:LSL\ndefault {}", ScriptLanguage.Lsl)]
    [InlineData("//slua:lsl\ndefault {}", ScriptLanguage.Lsl)]            // the engine name is read without regard to case
    [InlineData("//SLUA:LSL\ndefault {}", ScriptLanguage.Lsl)]
    [InlineData("//slua:\nprint(1)", ScriptLanguage.Slua)]
    [InlineData("// SLUA :slua\nprint(1)", ScriptLanguage.Slua)]
    [InlineData("//SLua:slua\nprint(1)", ScriptLanguage.Slua)]
    [InlineData("//SLua:SLua\nprint(1)", ScriptLanguage.Slua)]
    [InlineData("//SLua:python\nprint(1)", ScriptLanguage.Unsupported)]
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
    [InlineData("//SLua:\nx", "InWorldz.Phlox", "SLua")]          // the header names a loaded engine: it wins
    [InlineData("//YEngine:\nx", "InWorldz.Phlox", "YEngine")]
    [InlineData("//InWorldz.Phlox:\nx", "SLua", "InWorldz.Phlox")]
    [InlineData("//Unloaded:\nx", "InWorldz.Phlox", "InWorldz.Phlox")]    // names no loaded engine: the scene's choice
    [InlineData("//slua:\nx", "InWorldz.Phlox", "SLua")]               // the name is read without regard to case
    [InlineData("//SLUA:LSL\nx", "InWorldz.Phlox", "SLua")]
    [InlineData("//yengine:\nx", "InWorldz.Phlox", "InWorldz.Phlox")]   // other engines are still compared as written
    [InlineData("default {}", "SLua", "SLua")]
    public void OwnerIsTheNamedLoadedEngineElseTheScenesChoice(string script, string sceneChoice, string expected)
    {
        string[] loaded = { "InWorldz.Phlox", "YEngine", "SLua" };
        Assert.Equal(expected, EngineHeader.Owner(script, sceneChoice, loaded));
    }
}
