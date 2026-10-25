/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Text.Json;
using OpenMetaverse;
using OpenSim.CharacterPublish;

namespace OpenSim.CharacterPublish.Objects;

/// <summary>Whether a mesh part is a whole body or a separate head. It decides the default attachment point.</summary>
public enum PartRole
{
    /// <summary>A body: worn at Avatar Center (40).</summary>
    Body,
    /// <summary>A separate head: worn at Skull (2).</summary>
    Head,
}

/// <summary>One mesh part of a character.</summary>
/// <param name="Name">The name of the attachment item and object.</param>
/// <param name="Role">Body or head; sets the attachment point unless <paramref name="AttachmentPoint"/> is given.</param>
/// <param name="Mesh">The mesh asset file, relative to the description file.</param>
/// <param name="Faces">The bake layer shown on each face, by face index (as text), for example <c>{"0": "head", "1": "upper"}</c>.</param>
/// <param name="AttachmentPoint">An explicit attachment point (1..55), overriding the role's.</param>
/// <param name="Lods">
/// Fill the lower levels of detail from the high level with the mesh library's generator before the asset is stored:
/// "viewer" (1/3, 1/9, 1/27), "character" (the profile for rigged characters) or "auto" (character when the mesh is rigged).
/// Absent, the mesh is stored as given.
/// </param>
public sealed record PartDescription(
    string Name,
    PartRole Role,
    string Mesh,
    Dictionary<string, string>? Faces = null,
    int? AttachmentPoint = null,
    string? Lods = null);

/// <summary>The shape wearable: the system defaults with these parameter values changed.</summary>
public sealed record ShapeDescription(string? Name = null, Dictionary<string, float>? Values = null);

/// <summary>
/// The skin wearable: the textures it paints (ids of textures already on the grid) and the skin colour values it changes
/// from the system defaults, by parameter id (for example 111 pigment, 110 ruddiness). A slot left out carries no
/// texture, so the baked layer shows the skin colour over the system skin grain.
/// </summary>
public sealed record SkinDescription(UUID? Head = null, UUID? Upper = null, UUID? Lower = null, string? Name = null, Dictionary<string, float>? Values = null);

/// <summary>The eyes wearable and its iris texture.</summary>
public sealed record EyesDescription(UUID Iris, string? Name = null);

/// <summary>The hair wearable. Leave it out of the description to keep the resident's own hair.</summary>
public sealed record HairDescription(UUID? Texture = null, string? Name = null);

public sealed record WearablesDescription(
    ShapeDescription? Shape = null, SkinDescription? Skin = null, EyesDescription? Eyes = null, HairDescription? Hair = null);

/// <summary>The credit that travels with the work: a short line for descriptions and the full text for a notecard.</summary>
/// <param name="Credit">One line, at most 127 characters, put in the description of every item and object.</param>
/// <param name="NotecardName">The notecard's name; default "[character] credits".</param>
/// <param name="NotecardText">The notecard's text.</param>
/// <param name="NotecardTextFile">A text file with the notecard's text, relative to the description file. Used when NotecardText is absent.</param>
public sealed record AttributionDescription(string Credit, string? NotecardName = null, string? NotecardText = null, string? NotecardTextFile = null);

/// <summary>A character as a person describes it, before it is turned into a publish bundle.</summary>
public sealed record CharacterDescription(
    string Name,
    AttributionDescription Attribution,
    List<PartDescription> Parts,
    WearablesDescription? Wearables = null)
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new BundleFiles.UuidJsonConverter(), new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static CharacterDescription Parse(string json) =>
        JsonSerializer.Deserialize<CharacterDescription>(json, Options) ?? throw new JsonException("empty character description");
}

/// <summary>What the publish itself decides, apart from the character.</summary>
/// <param name="OwnerId">The resident the outfit goes to.</param>
/// <param name="CreatorId">The operator's creator service account, written as creator on every item and object.</param>
/// <param name="FolderNumber">The number of this publish for this resident; it names the outfit folder.</param>
/// <param name="CreationDate">Unix seconds written on items and objects. Fixed by the caller, not the clock, so output is repeatable.</param>
/// <param name="Permissions">The mask the owner, base and next owner get. Default copy, modify and transfer.</param>
/// <param name="SaveCurrentLook">Whether the bundle plans a folder of links that saves the resident's current look.</param>
public sealed record PublishContext(
    UUID OwnerId,
    UUID CreatorId,
    int FolderNumber,
    int CreationDate,
    uint Permissions = PermissionMasks.CopyModifyTransfer,
    bool SaveCurrentLook = true);
