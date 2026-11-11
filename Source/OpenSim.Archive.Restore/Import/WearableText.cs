/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenSim.Archive.Restore.Import;

/// <summary>
/// The text of a wearable (system clothing or a body part) as the viewer reads it (LLWearable::importStream in the viewer's llwearable.cpp): the
/// "LLWearable version" line, the name and description, the permissions and sale blocks, the "type N" line (the wearable type), the parameters and the
/// textures block, "textures N" followed by N lines of a texture entry number and a texture id.
/// </summary>
internal static class WearableText
{
    public const string Header = "LLWearable version ";
    /// <summary>The wearable types the viewer knows: WT_SHAPE (0) to WT_UNIVERSAL (16), WT_COUNT being 17, in the viewer's llwearabletype.h.</summary>
    public const int WearableTypes = 17;
    /// <summary>Wearable types 0 to 3 (shape, skin, hair, eyes) are body parts; the rest are clothing.</summary>
    public const int LastBodyPart = 3;
    /// <summary>The placeholders a wearable file may hold for its creator and its owner.</summary>
    public const string CreatorPlaceholder = "@CREATOR@", OwnerPlaceholder = "@OWNER@";

    private static readonly Regex TypeLine = new Regex(@"^type (\d+)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex TexturesLine = new Regex(@"^textures (\d+)[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex IdLine = new Regex(@"^([ \t]*)(creator_id|owner_id|last_owner_id)([ \t]+)(\S+)[ \t]*$", RegexOptions.CultureInvariant);

    /// <summary>The text of a file: UTF-8 without a byte order mark, every line ending a line feed.</summary>
    public static string Read(byte[] bytes)
    {
        string text = new UTF8Encoding(false).GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿')
            text = text.Substring(1);
        return text.Replace("\r\n", "\n");
    }

    /// <summary>The wearable type of the text; a text with no "LLWearable version" line first or no "type N" line of a known type is refused.</summary>
    public static int WearableType(string text, string display)
    {
        if (!text.StartsWith(Header, StringComparison.Ordinal))
            throw new ImportException(display + " is not a wearable: its first line must be the \"LLWearable version\" line");
        Match m = TypeLine.Match(text);
        if (!m.Success)
            throw new ImportException(display + " is not a wearable: it needs a \"type N\" line (the wearable type)");
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int type) || type >= WearableTypes)
            throw new ImportException(display + ": the wearable type " + m.Groups[1].Value + " is not one the viewer knows (0 to " + (WearableTypes - 1).ToString(CultureInfo.InvariantCulture) + ")");
        return type;
    }

    /// <summary>The parts of a wearable text: every line, where its textures block is, and the entries of the block in their order.</summary>
    private sealed class Layout
    {
        public List<string> Lines;
        /// <summary>The index of the "textures N" line; -1 when there is none.</summary>
        public int TexturesAt = -1;
        /// <summary>The index of each line of the block, and its texture entry number and id.</summary>
        public List<(int Line, int Entry, string Id)> Entries = new List<(int, int, string)>();
    }

    private static Layout Parse(string text, string display)
    {
        Layout layout = new Layout { Lines = text.Split('\n').ToList() };
        for (int i = 0; i < layout.Lines.Count; i++)
        {
            Match m = TexturesLine.Match(layout.Lines[i]);
            if (!m.Success)
                continue;
            layout.TexturesAt = i;
            int count = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int at = i + 1;
            // the viewer skips empty lines between the entries (getNextPopulatedLine)
            for (int n = 0; n < count; n++)
            {
                while (at < layout.Lines.Count && layout.Lines[at].Trim().Length == 0)
                    at++;
                string[] fields = at < layout.Lines.Count ? layout.Lines[at].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
                if (fields.Length != 2 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int entry))
                    throw new ImportException(display + ": the textures block says " + count.ToString(CultureInfo.InvariantCulture) + " entries, but entry " + (n + 1).ToString(CultureInfo.InvariantCulture) + " is not a texture entry number and an id");
                layout.Entries.Add((at, entry, fields[1]));
                at++;
            }
            break;
        }
        return layout;
    }

    /// <summary>
    /// The text with the avatar as its creator, owner and last owner (the creator_id, owner_id and last_owner_id lines, and the @CREATOR@ and @OWNER@
    /// placeholders), and each given texture entry's line in the textures block naming the given texture: an entry the block has gets its id replaced, a new one
    /// is added after the others, and the count line says how many there are. A text with no textures block gets one at its end.
    /// </summary>
    public static string Rewrite(string text, Guid avatar, IReadOnlyDictionary<int, Guid> textures, string display)
    {
        string id = avatar.ToString("D");
        text = text.Replace(CreatorPlaceholder, id).Replace(OwnerPlaceholder, id);
        Layout layout = Parse(text, display);
        List<string> lines = layout.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            Match m = IdLine.Match(lines[i]);
            if (m.Success)
                lines[i] = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + id;
        }
        if (textures == null || textures.Count == 0)
            return string.Join("\n", lines);

        List<(int Entry, string Id)> entries = layout.Entries.Select(e => (e.Entry, e.Id)).ToList();
        foreach (KeyValuePair<int, Guid> t in textures.OrderBy(t => t.Key))
        {
            int at = entries.FindIndex(e => e.Entry == t.Key);
            if (at >= 0)
                entries[at] = (t.Key, t.Value.ToString("D"));
            else
                entries.Add((t.Key, t.Value.ToString("D")));
        }
        List<string> block = new List<string> { "textures " + entries.Count.ToString(CultureInfo.InvariantCulture) };
        block.AddRange(entries.Select(e => e.Entry.ToString(CultureInfo.InvariantCulture) + " " + e.Id));
        if (layout.TexturesAt < 0)
        {
            // no block: it goes at the end, before the last line feed
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.InsertRange(lines.Count - 1, block);
            else
            {
                lines.AddRange(block);
                lines.Add(string.Empty);
            }
            return string.Join("\n", lines);
        }
        int first = layout.TexturesAt, last = layout.Entries.Count > 0 ? layout.Entries[layout.Entries.Count - 1].Line : layout.TexturesAt;
        lines.RemoveRange(first, last - first + 1);
        lines.InsertRange(first, block);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The text split for comparing: the lines with the textures block taken out and the creator, owner and last owner ids and placeholders shown as *, the
    /// entries of the textures block, and the ids the creator_id, owner_id and last_owner_id lines name.
    /// </summary>
    public static (string Lines, Dictionary<int, string> Textures, Dictionary<string, string> Ids) Split(string text, string display)
    {
        text = text.Replace(CreatorPlaceholder, "*").Replace(OwnerPlaceholder, "*");
        Layout layout = Parse(text, display);
        Dictionary<string, string> ids = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<int, string> textures = new Dictionary<int, string>();
        foreach ((int _, int entry, string id) in layout.Entries)
            textures[entry] = id;
        List<string> rest = new List<string>();
        HashSet<int> block = new HashSet<int>(layout.Entries.Select(e => e.Line));
        if (layout.TexturesAt >= 0)
            block.Add(layout.TexturesAt);
        for (int i = 0; i < layout.Lines.Count; i++)
        {
            if (block.Contains(i))
                continue;
            Match m = IdLine.Match(layout.Lines[i]);
            if (m.Success)
            {
                ids[m.Groups[2].Value] = m.Groups[4].Value;
                rest.Add(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + "*");
            }
            else
                rest.Add(layout.Lines[i]);
        }
        return (string.Join("\n", rest), textures, ids);
    }
}
