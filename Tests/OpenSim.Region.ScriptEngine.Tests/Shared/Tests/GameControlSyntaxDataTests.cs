/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

#nullable disable

using System.Reflection;
using System.Xml.Linq;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

namespace OpenSim.Region.ScriptEngine.Shared.Tests;

/// <summary>
/// The script editor of a viewer gets its keywords, tooltips and constants from the simulator's syntax data
/// (AppData/ScriptSyntax.xml, served through the LSLSyntax capability under the id on the file's first line). The game
/// control event, its three functions, PERMISSION_GAME_CONTROL and the GAME_CONTROL_* constants are in it, taken from
/// the definitions the Second Life viewer's own syntax file is generated from (secondlife/lsl-definitions), and the
/// id has changed so viewers that cached the old data fetch the new.
/// The class only reads the file, so it runs in parallel with the others.
/// </summary>
public class GameControlSyntaxDataTests
{
    /// <summary>The id the file carried before the game control entries were added.</summary>
    private const string IdBeforeGameControl = "3c5411df-387f-39c3-345f-4e1b7c7e7c45";

    private static string SyntaxFile()
    {
        for (DirectoryInfo dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Source", "OpenSim.Server.RegionServer", "AppData", "ScriptSyntax.xml");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("ScriptSyntax.xml was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Reads the file as the simulator does: the first line is the id, the rest is stripped line by line and joined.</summary>
    private static (string Id, XElement Root) Load()
    {
        string[] lines = File.ReadAllLines(SyntaxFile());
        string joined = string.Concat(lines.Skip(1)
            .Select(l => l.Trim(' ', '\t', '\n', '\r'))
            .Where(l => l.Length > 0 && !l.StartsWith("<!--", StringComparison.Ordinal)));
        return (lines[0].Trim(), XElement.Parse(joined));
    }

    private static XElement Section(XElement root, string name)
    {
        var keys = root.Element("map").Elements().ToList();
        for (int i = 0; i + 1 < keys.Count; i += 2)
        {
            if (keys[i].Name == "key" && keys[i].Value == name)
                return keys[i + 1];
        }
        throw new InvalidOperationException("no section " + name);
    }

    private static XElement Entry(XElement section, string name)
    {
        var kids = section.Elements().ToList();
        for (int i = 0; i + 1 < kids.Count; i += 2)
        {
            if (kids[i].Value == name)
                return kids[i + 1];
        }
        return null;
    }

    private static string Field(XElement entry, string key)
    {
        var kids = entry.Elements().ToList();
        for (int i = 0; i + 1 < kids.Count; i += 2)
        {
            if (kids[i].Value == key)
                return kids[i + 1].Value;
        }
        return null;
    }

    private static bool TryParseInt(string text, out int value)
    {
        text = text?.Trim() ?? "";
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        return int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    [Fact]
    public void TheFileStillParsesTheWayTheSimulatorReadsItAndItsIdIsNew()
    {
        var (id, root) = Load();

        Assert.True(UUID.TryParse(id, out UUID parsed), "the first line is not a UUID: " + id);
        Assert.NotEqual(UUID.Parse(IdBeforeGameControl), parsed);
        Assert.Equal("llsd", root.Name.LocalName);
    }

    [Fact]
    public void TheGameControlEventAndItsThreeFunctionsHaveEntries()
    {
        var (_, root) = Load();

        var game = Entry(Section(root, "events"), "game_control");
        Assert.NotNull(game);
        Assert.Equal(3, game.Element("array").Elements("map").Count());          // id, button_levels, axes

        foreach (string name in new[] { "llGetGameControlMode", "llGetGameControlModeButtons", "llGetGameControlModeAxes" })
            Assert.NotNull(Entry(Section(root, "functions"), name));
    }

    [Fact]
    public void ThePermissionAndEveryGameControlConstantScriptsCanUseHaveEntriesWithTheSameValue()
    {
        var (_, root) = Load();
        var constants = Section(root, "constants");
        var wrong = new List<string>();

        var defined = typeof(ScriptBaseClass)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.Name == "PERMISSION_GAME_CONTROL" || f.Name.StartsWith("GAME_CONTROL_", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(79, defined.Count);

        foreach (FieldInfo field in defined)
        {
            var entry = Entry(constants, field.Name);
            if (entry == null)
            {
                wrong.Add(field.Name + " missing");
                continue;
            }
            if (!TryParseInt(Field(entry, "value"), out int got))
            {
                wrong.Add(field.Name + " value " + Field(entry, "value"));
                continue;
            }
            int want = (int)field.GetRawConstantValue();
            if (got != want)
                wrong.Add(field.Name + " = " + got + ", scripts see " + want);
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void TheFileHoldsEveryGameControlConstantOfTheDefinitionsOnce()
    {
        // 78 GAME_CONTROL_* names in secondlife/lsl-definitions (the AXIS, BUTTON and MODE groups) at the time of writing.
        var (_, root) = Load();

        var names = Section(root, "constants").Elements().Where(e => e.Name == "key").Select(e => e.Value)
            .Where(n => n.StartsWith("GAME_CONTROL_", StringComparison.Ordinal)).ToList();

        Assert.Equal(78, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
