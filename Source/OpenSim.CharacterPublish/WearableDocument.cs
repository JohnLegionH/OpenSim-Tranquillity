/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish;

/// <summary>
/// The content of one wearable asset (the "LLWearable" text): name, description, permissions, sale info,
/// type, visual parameters by id and textures by slot. Parameters and textures are kept in ascending key order,
/// which is the order the viewer writes them (it keeps both in a std::map).
/// </summary>
public sealed class WearableDocument
{
    /// <summary>The wearable definition version avatar_lad.xml declares (<c>wearable_definition_version</c>).</summary>
    public const int CurrentDefinitionVersion = 22;

    public int DefinitionVersion { get; init; } = CurrentDefinitionVersion;
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public required PermissionBlock Permissions { get; init; }
    public SaleType SaleType { get; init; } = SaleType.Not;
    public int SalePrice { get; init; }
    public required WearableKind Type { get; init; }
    public SortedDictionary<int, float> Parameters { get; init; } = new();
    public SortedDictionary<int, UUID> Textures { get; init; } = new();

    /// <summary>True when every field, parameter and texture is equal (parameters compared bit for bit).</summary>
    public bool ContentEquals(WearableDocument o) =>
        DefinitionVersion == o.DefinitionVersion && Name == o.Name && Description == o.Description
        && Permissions == o.Permissions && SaleType == o.SaleType && SalePrice == o.SalePrice && Type == o.Type
        && Parameters.Count == o.Parameters.Count
        && Parameters.All(p => o.Parameters.TryGetValue(p.Key, out var v) && BitConverter.SingleToInt32Bits(v) == BitConverter.SingleToInt32Bits(p.Value))
        && Textures.Count == o.Textures.Count
        && Textures.All(t => o.Textures.TryGetValue(t.Key, out var v) && v == t.Value);
}
