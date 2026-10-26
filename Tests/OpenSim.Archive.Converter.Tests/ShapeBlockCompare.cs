/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>What comparing the extra-parameter blocks, material overrides and media entries of two parts (or of two archives, part by part) found.</summary>
public sealed class ShapeBlockReport
{
    /// <summary>Differences in meaning, one line each, naming the part, the block or face and the field.</summary>
    public List<string> Differences { get; } = new List<string>();
    /// <summary>Things that differ in form only (block order), one line each.</summary>
    public List<string> Notes { get; } = new List<string>();
    public int Parts, Blocks, PbrFaces, Overrides, MediaEntries;

    public string ToText() => "parts " + Parts + ", extra-parameter blocks " + Blocks + ", PBR faces " + PbrFaces + ", overrides " + Overrides + ", media entries " + MediaEntries
        + ", differences " + Differences.Count + ", notes " + Notes.Count + Environment.NewLine
        + string.Concat(Differences.Select(d => "  DIFFERENT: " + d + Environment.NewLine)) + string.Concat(Notes.Select(n => "  note: " + n + Environment.NewLine));
}

/// <summary>
/// Compares what a region keeps of a part's shape data across a save: the extra-parameter blocks (by type, and field by field for the sculpt
/// block and the PBR assignments), the material overrides (the text of each face), and the media entries (each field of each face's entry).
/// An empty place in a media list and a default entry are the same thing, as the region reads them.
/// </summary>
public static class ShapeBlockCompare
{
    public static void ComparePart(string name, ParsedPart before, ParsedPart after, ShapeBlockReport report) => throw new NotImplementedException();

    /// <summary>Every part the two archives share by id; a part in one only is a difference.</summary>
    public static ShapeBlockReport CompareArchives(string beforePath, string afterPath) => throw new NotImplementedException();
}
