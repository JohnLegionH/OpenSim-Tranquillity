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
/// Builds wearables whose parameters are the default values avatar_lad.xml gives, as the viewer does for a new
/// wearable: every parameter the file assigns to that wearable type (its <c>wearable</c> attribute), at its default.
/// </summary>
public static class WearableFactory
{
    /// <summary>A wearable of any type, with the type's default parameters and the textures given by slot.</summary>
    public static WearableDocument CreateDefault(
        WearableKind kind, string name, string description, PermissionBlock permissions,
        IEnumerable<KeyValuePair<TextureSlot, UUID>>? textures = null, AvatarLad? lad = null)
    {
        lad ??= AvatarLad.Embedded;
        var owner = kind.ToString().ToLowerInvariant();
        var prms = new SortedDictionary<int, float>();
        foreach (var p in lad.Params.Values)
            if (string.Equals(p.Wearable, owner, StringComparison.Ordinal)) prms[p.Id] = p.Default;
        var texs = new SortedDictionary<int, UUID>();
        if (textures != null) foreach (var t in textures) texs[(int)t.Key] = t.Value;
        return new WearableDocument
        {
            Name = name, Description = description, Permissions = permissions, Type = kind, Parameters = prms, Textures = texs,
        };
    }

    public static WearableDocument Shape(string name, string description, PermissionBlock permissions, AvatarLad? lad = null) =>
        CreateDefault(WearableKind.Shape, name, description, permissions, null, lad);

    /// <summary>A skin carrying the head, upper body and lower body paint textures (slots 0, 5 and 6).</summary>
    public static WearableDocument Skin(string name, string description, PermissionBlock permissions,
        UUID head, UUID upper, UUID lower, AvatarLad? lad = null) =>
        CreateDefault(WearableKind.Skin, name, description, permissions, new KeyValuePair<TextureSlot, UUID>[]
        {
            new(TextureSlot.HeadBodypaint, head), new(TextureSlot.UpperBodypaint, upper), new(TextureSlot.LowerBodypaint, lower),
        }, lad);

    /// <summary>Eyes carrying the iris texture (slot 3).</summary>
    public static WearableDocument Eyes(string name, string description, PermissionBlock permissions, UUID iris, AvatarLad? lad = null) =>
        CreateDefault(WearableKind.Eyes, name, description, permissions, new KeyValuePair<TextureSlot, UUID>[] { new(TextureSlot.EyesIris, iris) }, lad);

    /// <summary>Hair; the texture (slot 4) is optional.</summary>
    public static WearableDocument Hair(string name, string description, PermissionBlock permissions, UUID? texture = null, AvatarLad? lad = null) =>
        CreateDefault(WearableKind.Hair, name, description, permissions,
            texture is { } t ? new KeyValuePair<TextureSlot, UUID>[] { new(TextureSlot.Hair, t) } : null, lad);
}
