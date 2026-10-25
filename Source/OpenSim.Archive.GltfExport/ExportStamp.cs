/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json.Nodes;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.GltfExport;

/// <summary>What an exporter writes into a .glb (asset extras) so a later program knows how the file was made.</summary>
public sealed class ExportStamp
{
    /// <summary>The version of the oar-export, oar-convert and oar-restore programs that share this geometry.</summary>
    public const string CurrentVersion = "0.2";

    /// <summary>The name of the asset extras entry.</summary>
    public const string Key = "oarExport";

    public string Version { get; set; }
    /// <summary>True when duplicate and loose vertices and zero-area triangles were removed.</summary>
    public bool Clean { get; set; }

    public JsonObject ToJson() => new JsonObject { ["version"] = Version, ["clean"] = Clean };

    /// <summary>
    /// Reads the stamp from the asset extras of a .glb file. Null when the file has none (written before the stamp existed, or
    /// by another tool, which means the geometry was not cleaned). A stamp from a version this build does not know is refused.
    /// </summary>
    /// <exception cref="InvalidDataException">The stamp names a version other than <see cref="CurrentVersion"/>.</exception>
    public static ExportStamp Read(JsonNode assetExtras)
    {
        if (assetExtras?[Key] is not JsonObject o)
            return null;
        string version = (string)o["version"] ?? string.Empty;
        if (version != CurrentVersion)
        {
            bool newer = System.Version.TryParse(version, out System.Version theirs) && theirs > System.Version.Parse(CurrentVersion);
            throw new InvalidDataException("the file was made by " + (newer ? "a newer" : "an unknown") + " oar-export (version " + Sanitize.Text(version) + "); this program knows version " + CurrentVersion
                + ". Use the oar-restore that came with the oar-export that made the file.");
        }
        return new ExportStamp { Version = version, Clean = o["clean"] is JsonValue c && c.TryGetValue(out bool b) && b };
    }
}
