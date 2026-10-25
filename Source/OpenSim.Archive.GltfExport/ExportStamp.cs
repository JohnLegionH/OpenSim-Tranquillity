/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json.Nodes;

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
}
