/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.CharacterPublish;

namespace OpenSim.CharacterPublish.Objects;

/// <summary>Everything the object of one worn mesh part is made from.</summary>
/// <param name="MeshAssetId">The mesh asset the part shows.</param>
/// <param name="FaceCount">The number of faces the mesh has (its submesh count).</param>
/// <param name="FaceChannels">The bake layer shown on each face, by face index. A face with no entry keeps the plain white texture.</param>
/// <param name="AttachmentPoint">Where the part is worn, 1 to <see cref="AttachmentPoint.LastValid"/>.</param>
/// <param name="Size">The part's size in metres: the extent of the mesh's vertices, which is what the viewer's uploader sets.</param>
/// <param name="Permissions">The masks, creator and owner of the object.</param>
public sealed record AttachmentObjectSpec(
    UUID MeshAssetId,
    int FaceCount,
    IReadOnlyDictionary<int, BakeChannel> FaceChannels,
    AttachmentPoint AttachmentPoint,
    Vector3 Size,
    PermissionBlock Permissions,
    string Name,
    string Description)
{
    /// <summary>The id of the part. Left at zero it is derived from the other fields, so the same input gives the same bytes.</summary>
    public UUID PartId { get; init; } = UUID.Zero;

    /// <summary>The creation time written into the part, Unix seconds. Fixed, not the clock, so output is repeatable.</summary>
    public int CreationDate { get; init; }
}

/// <summary>
/// Writes the asset of an inventory object holding one rigged mesh part worn at an attachment point.
/// The text is produced by the region's own serializer (the call the attachments module makes when it saves a
/// worn object back to inventory), so the format is the region's.
/// </summary>
public static class AttachmentObjectWriter
{
    /// <summary>The longest object name the region and viewer accept.</summary>
    public const int MaxNameLength = 63;
    /// <summary>The longest object description the region and viewer accept.</summary>
    public const int MaxDescriptionLength = 127;

    public static string Write(AttachmentObjectSpec spec)
    {
        Validate(spec);

        var shape = PrimitiveBaseShape.CreateMesh(spec.FaceCount, spec.MeshAssetId);
        shape.TextureEntry = BuildTextureEntry(spec);

        var p = spec.Permissions;
        var part = new SceneObjectPart(p.Owner, shape, Vector3.Zero, Quaternion.Identity, Vector3.Zero)
        {
            UUID = spec.PartId.IsZero() ? DerivePartId(spec) : spec.PartId,
            Name = spec.Name,
            Description = spec.Description,
            Scale = spec.Size,
            CreatorID = p.Creator,
            OwnerID = p.Owner,
            LastOwnerID = p.LastOwner,
            RezzerID = p.Owner,
            GroupID = p.Group,
            BaseMask = p.BaseMask,
            OwnerMask = p.OwnerMask,
            GroupMask = p.GroupMask,
            EveryoneMask = p.EveryoneMask,
            NextOwnerMask = p.NextOwnerMask,
            CreationDate = spec.CreationDate,
        };

        // The part sits on its attachment point with no offset. The point is the shape's State; a viewer "wear"
        // sends point 0 and the region then uses this stored point (AttachmentsModule.AttachObject).
        var grp = new SceneObjectGroup(part) { AttachmentPoint = (uint)spec.AttachmentPoint };
        shape.LastAttachPoint = (byte)spec.AttachmentPoint;

        return SceneObjectSerializer.ToOriginalXmlFormat(grp, string.Empty);
    }

    static byte[] BuildTextureEntry(AttachmentObjectSpec spec)
    {
        var te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
        foreach (var (face, channel) in spec.FaceChannels.OrderBy(kv => kv.Key))
        {
            var f = te.CreateFace((uint)face);
            f.TextureID = BakeChannelIds.Of(channel);
            te.FaceTextures[face] = f;
        }
        return te.GetBytes(spec.FaceCount);
    }

    static UUID DerivePartId(AttachmentObjectSpec spec)
    {
        var key = $"{spec.MeshAssetId}|{spec.Permissions.Owner}|{(int)spec.AttachmentPoint}|{spec.Name}";
        return new UUID(MD5.HashData(Encoding.UTF8.GetBytes(key)), 0);
    }

    static void Validate(AttachmentObjectSpec s)
    {
        if (s.MeshAssetId.IsZero()) throw new ArgumentException("mesh asset id is empty");
        if (s.FaceCount < 1 || s.FaceCount > 8) throw new ArgumentException($"face count {s.FaceCount} is outside 1..8 (the viewer's per-object face limit)");
        foreach (var face in s.FaceChannels.Keys)
            if (face < 0 || face >= s.FaceCount) throw new ArgumentException($"face {face} does not exist on a mesh of {s.FaceCount} faces");
        var ap = (int)s.AttachmentPoint;
        if (ap < 1 || ap > (int)AttachmentPoint.LastValid) throw new ArgumentException($"attachment point {ap} is outside 1..{(int)AttachmentPoint.LastValid}");
        if (!(s.Size.X > 0 && s.Size.Y > 0 && s.Size.Z > 0) || float.IsInfinity(s.Size.X + s.Size.Y + s.Size.Z))
            throw new ArgumentException("size must be positive and finite");
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > MaxNameLength) throw new ArgumentException($"name must be 1..{MaxNameLength} characters");
        if (s.Description.Length > MaxDescriptionLength) throw new ArgumentException($"description longer than {MaxDescriptionLength} characters");
        if (!SafeDescription.IsSafe(s.Description)) throw new ArgumentException("description starts with a wearable section keyword (type, parameters, textures)");
        if (s.Permissions.Owner.IsZero() || s.Permissions.Creator.IsZero()) throw new ArgumentException("creator and owner are required");
    }
}
