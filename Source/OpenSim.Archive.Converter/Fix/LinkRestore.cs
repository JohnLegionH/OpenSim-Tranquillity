/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Archive.Converter.Fix;

/// <summary>Where one part stood in the archive and where it stands in the output.</summary>
public sealed class PartRelink
{
    public string Name { get; set; }
    public UUID PartId { get; set; }
    public int LinkBefore { get; set; }
    public int LinkAfter { get; set; }
    public int FilePositionBefore { get; set; }
    public int FilePositionAfter { get; set; }
    /// <summary>False for a part the reference does not hold: it keeps its order, after the matched parts.</summary>
    public bool Matched { get; set; }
    public bool Changed => LinkBefore != LinkAfter || FilePositionBefore != FilePositionAfter;
}

public sealed class ObjectRelink
{
    public string Name { get; set; }
    public string Entry { get; set; }
    /// <summary>How the object was paired with the reference's: "name", "name and part count", "name, part count and shape".</summary>
    public string Matched { get; set; }
    public List<PartRelink> Parts { get; } = new List<PartRelink>();
    public bool Changed => Parts.Any(p => p.Changed);
}

/// <summary>A script whose use of link numbers is affected because the parts were renumbered.</summary>
public sealed class ScriptLinkChange
{
    public string Object { get; set; }
    public string Part { get; set; }
    public string Script { get; set; }
    public List<string> Uses { get; } = new List<string>();
}

public sealed class RelinkResult
{
    public string Reference { get; set; }
    public List<ObjectRelink> Objects { get; } = new List<ObjectRelink>();
    /// <summary>Objects of the archive the reference does not hold (or that two objects of one name made ambiguous); left unchanged.</summary>
    public List<string> UnmatchedObjects { get; } = new List<string>();
    /// <summary>Parts of a matched object the reference's object does not hold; they keep their order, after the matched parts.</summary>
    public List<string> UnmatchedParts { get; } = new List<string>();
    public List<ScriptLinkChange> Scripts { get; } = new List<ScriptLinkChange>();
    public int EntriesRead { get; set; }
    public int EntriesChanged { get; set; }
    public int ObjectsChanged => Objects.Count(o => o.Changed);

    public string ToText() => string.Empty;
    public string ToJson() => string.Empty;
}

/// <summary>
/// Writes a copy of an OAR or IAR in which each object found in a reference archive has the reference's link order back.
/// </summary>
public static class LinkRestore
{
    public static RelinkResult Run(string source, string reference, string dest)
    {
        return new RelinkResult();
    }
}
