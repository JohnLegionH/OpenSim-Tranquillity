/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.ObjectInspect;

/// <summary>
/// A list of account ids and the names to show for them, one per line: the id, then a space, tab, comma or equals sign, then the name.
/// Blank lines and lines starting with # are skipped.
/// </summary>
public static class NameList
{
    private const int IdLength = 36;

    /// <exception cref="FormatException">A line is not an id and a name, or names an id twice with different names; the message gives the line number.</exception>
    public static Dictionary<UUID, string> Parse(string text)
    {
        Dictionary<UUID, string> names = new Dictionary<UUID, string>();
        Dictionary<UUID, int> lineOf = new Dictionary<UUID, int>();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#')
                continue;

            int number = n + 1;
            if (line.Length <= IdLength || !UUID.TryParse(line.Substring(0, IdLength), out UUID id) || id == UUID.Zero)
                throw new FormatException("line " + number + ": expected an account id and a name");
            string rest = line.Substring(IdLength);
            if (!(char.IsWhiteSpace(rest[0]) || rest[0] == ',' || rest[0] == '='))
                throw new FormatException("line " + number + ": expected a space, comma or equals sign after the id");
            string name = Sanitize.Text(rest.TrimStart(' ', '\t', ',', '=').Trim());
            if (name.Length == 0)
                throw new FormatException("line " + number + ": the id has no name");

            if (names.TryGetValue(id, out string existing))
            {
                if (existing != name)
                    throw new FormatException("line " + number + ": " + id + " is already named on line " + lineOf[id] + " as a different name");
                continue;
            }
            names[id] = name;
            lineOf[id] = number;
        }
        return names;
    }
}
