/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using OpenMetaverse;
using OpenSim.CharacterPublish;
using OpenSim.MeshAsset;
using OpenSimNGC.Appearance.Baking;
using V3 = OpenMetaverse.Vector3;

namespace OpenSim.CharacterPublish.Objects;

/// <summary>An assembled bundle: its model, the bytes of every asset, and the result of validating both.</summary>
public sealed record AssembledBundle(Bundle Bundle, IReadOnlyDictionary<UUID, byte[]> Content, ValidationResult Validation)
{
    /// <summary>Writes the bundle folder (assets and bundle.json). Refuses a bundle that did not validate.</summary>
    public void WriteTo(string folder)
    {
        if (!Validation.Ok) throw new InvalidOperationException("the bundle did not validate:" + Environment.NewLine + Validation);
        BundleFiles.Write(Bundle, Content, folder);
    }
}

/// <summary>
/// Turns a character description into a publish bundle: the mesh assets, one attachment object per mesh part, the
/// wearables, the credit notecard, the items that hold them, the new outfit folder, and the plan for the "previous look"
/// folder. Every id is derived from content or from the names that identify it, so the same input gives the same bundle.
/// Which attachment point a part gets, and how big the object is, follow these conventions:
/// a body at Avatar Center, a separate head at Skull, and the size from the mesh's NormalizedScale.
/// </summary>
public static class OutfitAssembler
{
    /// <summary>The attachment point of a body: Avatar Center, id 40, joint mRoot (viewer avatar_lad.xml). The library names it differently, so the number is used.</summary>
    public const AttachmentPoint BodyPoint = (AttachmentPoint)40;

    /// <summary>The attachment point of a separate head: Skull, id 2 (viewer avatar_lad.xml).</summary>
    public const AttachmentPoint HeadPoint = (AttachmentPoint)2;

    public static AttachmentPoint DefaultPoint(PartRole role) => role == PartRole.Head ? HeadPoint : BodyPoint;

    /// <summary>
    /// Assembles from a description file. Mesh files and the notecard text file are read relative to the file's folder.
    /// </summary>
    public static AssembledBundle AssembleFromFile(string descriptionPath, PublishContext ctx)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(descriptionPath))!;
        var desc = CharacterDescription.Parse(File.ReadAllText(descriptionPath));
        string? note = desc.Attribution.NotecardText;
        if (note == null && desc.Attribution.NotecardTextFile != null)
            note = File.ReadAllText(PlainPath(dir, desc.Attribution.NotecardTextFile)).Replace("\r\n", "\n");
        return Assemble(desc with { Attribution = desc.Attribution with { NotecardText = note } }, ctx, name => File.ReadAllBytes(PlainPath(dir, name)));
    }

    static string PlainPath(string dir, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(dir, relative));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{relative}' is outside the folder of the description file");
        return full;
    }

    /// <summary>Assembles the bundle. <paramref name="readMesh"/> returns the bytes of the mesh file a part names.</summary>
    public static AssembledBundle Assemble(CharacterDescription d, PublishContext ctx, Func<string, byte[]> readMesh)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) throw new ArgumentException("the character has no name");
        if (string.IsNullOrWhiteSpace(d.Attribution.Credit)) throw new ArgumentException("the description needs attribution text (a credit line)");
        if (string.IsNullOrWhiteSpace(d.Attribution.NotecardText)) throw new ArgumentException("the description needs the attribution notecard text");
        if (d.Parts.Count == 0) throw new ArgumentException("the character has no mesh parts");

        var credit = d.Attribution.Credit;
        var perms = PermissionBlock.ForOwner(ctx.CreatorId, ctx.OwnerId, ctx.Permissions);
        var assets = new List<BundleAsset>();
        var content = new Dictionary<UUID, byte[]>();
        var external = new List<UUID>();
        var items = new List<BundleItem>();
        var folderId = ContentId.Derive("folder", ctx.OwnerId.ToString(), d.Name, ctx.FolderNumber.ToString(CultureInfo.InvariantCulture));

        UUID AddAsset(byte[] bytes, int type, string ext, string name)
        {
            var id = ContentId.OfBytes(bytes);
            if (content.TryAdd(id, bytes)) assets.Add(new BundleAsset(id, type, id + "." + ext, bytes.Length, name));
            return id;
        }

        void AddItem(string role, string name, string description, UUID assetId, int assetType, int invType, uint flags) =>
            items.Add(new BundleItem(
                ContentId.Derive("item", ctx.OwnerId.ToString(), d.Name, ctx.FolderNumber.ToString(CultureInfo.InvariantCulture), role),
                Bundle.OutfitFolderKey, role, name, description, assetId, assetType, invType, flags,
                perms.BaseMask, perms.OwnerMask, perms.NextOwnerMask, perms.GroupMask, perms.EveryoneMask,
                ctx.CreatorId, ctx.OwnerId, ctx.CreationDate, 0, (byte)SaleType.Not));

        UUID External(UUID texture)
        {
            if (texture.IsZero()) throw new ArgumentException("a wearable texture id is empty");
            if (!external.Contains(texture)) external.Add(texture);
            return texture;
        }

        // ---- wearables: shape, skin, eyes, hair
        var w = d.Wearables;
        void AddWearable(string role, WearableDocument doc)
        {
            var type = (int)doc.Type;
            var id = AddAsset(WearableWriter.WriteBytes(doc), BundleTypes.AssetTypeOfWearable(type), "wearable", doc.Name);
            AddItem(role, doc.Name, doc.Description, id, BundleTypes.AssetTypeOfWearable(type), BundleTypes.InvWearable, (uint)type);
        }

        if (w?.Shape != null)
        {
            var shape = WearableFactory.Shape(w.Shape.Name ?? d.Name + " Shape", credit, perms);
            foreach (var (key, value) in w.Shape.Values ?? new())
            {
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || !shape.Parameters.ContainsKey(pid))
                    throw new ArgumentException($"shape parameter '{key}' is not one of the shape wearable's parameters");
                shape.Parameters[pid] = value;
            }
            AddWearable("shape", shape);
        }
        if (w?.Skin != null)
            AddWearable("skin", WearableFactory.Skin(w.Skin.Name ?? d.Name + " Skin", credit, perms,
                External(w.Skin.Head), External(w.Skin.Upper), External(w.Skin.Lower)));
        if (w?.Eyes != null)
            AddWearable("eyes", WearableFactory.Eyes(w.Eyes.Name ?? d.Name + " Eyes", credit, perms, External(w.Eyes.Iris)));
        if (w?.Hair != null)
            AddWearable("hair", WearableFactory.Hair(w.Hair.Name ?? d.Name + " Hair", credit, perms, w.Hair.Texture is { } t ? External(t) : null));

        // ---- attachments: one mesh asset and one object per part
        for (var i = 0; i < d.Parts.Count; i++)
        {
            var part = d.Parts[i];
            var meshBytes = GenerateLods(part, readMesh(part.Mesh));
            MeshInfo info;
            try { info = MeshInspector.Inspect(meshBytes); }
            catch (Exception e) { throw new ArgumentException($"part '{part.Name}': mesh '{part.Mesh}' does not decode: {e.Message}", e); }
            var size = info.NormalizedScale
                ?? throw new ArgumentException($"part '{part.Name}': mesh '{part.Mesh}' has no NormalizedScale; the object's size comes from it");
            var meshId = AddAsset(meshBytes, BundleTypes.AssetMesh, "llmesh", part.Mesh);

            var faces = new Dictionary<int, BakeChannel>();
            foreach (var (face, channel) in part.Faces ?? new())
            {
                if (!int.TryParse(face, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fi))
                    throw new ArgumentException($"part '{part.Name}': '{face}' is not a face number");
                if (!Enum.TryParse<BakeChannel>(channel, ignoreCase: true, out var ch) || !Enum.IsDefined(ch))
                    throw new ArgumentException($"part '{part.Name}': '{channel}' is not a bake layer ({string.Join(", ", Enum.GetNames<BakeChannel>())})");
                faces[fi] = ch;
            }

            var point = part.AttachmentPoint is { } ap ? (AttachmentPoint)ap : DefaultPoint(part.Role);
            var spec = new AttachmentObjectSpec(meshId, info.FaceCount, faces, point, new V3(size.X, size.Y, size.Z), perms, part.Name, credit)
            {
                CreationDate = ctx.CreationDate,
            };
            var objectBytes = new System.Text.UTF8Encoding(false).GetBytes(AttachmentObjectWriter.Write(spec));
            var objectId = AddAsset(objectBytes, BundleTypes.AssetObject, "object.xml", part.Name);
            AddItem("attachment-" + (i + 1).ToString(CultureInfo.InvariantCulture), part.Name, credit, objectId,
                BundleTypes.AssetObject, BundleTypes.InvAttachment, 0);
        }

        // ---- the credit notecard
        var noteName = d.Attribution.NotecardName ?? d.Name + " credits";
        var noteId = AddAsset(NotecardWriter.WriteBytes(new NotecardDocument { Text = d.Attribution.NotecardText! }), BundleTypes.AssetNotecard, "notecard", noteName);
        AddItem("notecard", noteName, "", noteId, BundleTypes.AssetNotecard, BundleTypes.InvNotecard, 0);

        // ---- folders
        var number = ctx.FolderNumber.ToString(CultureInfo.InvariantCulture);
        var folder = new BundleFolder(folderId, Bundle.OutfitFolderKey, Fit($"{d.Name} #{number}"), BundleTypes.FolderOutfit, Bundle.MyOutfitsParent, ctx.OwnerId);
        CurrentLookPlan? plan = null;
        if (ctx.SaveCurrentLook)
        {
            var date = DateTimeOffset.FromUnixTimeSeconds(ctx.CreationDate).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            plan = new CurrentLookPlan(
                ContentId.Derive("lookfolder", ctx.OwnerId.ToString(), d.Name, number),
                Fit($"Previous look {date} before outfit {number}"), BundleTypes.FolderOutfit, Bundle.MyOutfitsParent, ctx.OwnerId,
                BundleTypes.FolderCurrentOutfit, BundleTypes.AssetLink, 0x7FFFFFFF, ctx.CreationDate);
        }

        var bundle = new Bundle(Bundle.CurrentFormatVersion, d.Name, ctx.FolderNumber, ctx.OwnerId, ctx.CreatorId, ctx.CreationDate,
            assets, external, folder, items, plan);
        return new AssembledBundle(bundle, content, BundleValidator.Validate(bundle, content));
    }

    /// <summary>Fills the lower levels of detail when the part asks for it; otherwise returns the bytes unchanged.</summary>
    static byte[] GenerateLods(PartDescription part, byte[] bytes)
    {
        if (part.Lods == null) return bytes;
        try
        {
            var model = MeshCodec.Decode(bytes);
            var profile = part.Lods.ToLowerInvariant() switch
            {
                "viewer" => LodGenerator.Profile.Viewer,
                "character" => LodGenerator.Profile.Character,
                "auto" => LodGenerator.DefaultProfile(model),
                _ => throw new ArgumentException($"lods '{part.Lods}' is not viewer, character or auto"),
            };
            LodGenerator.GenerateAll(model, profile: profile);
            return MeshCodec.Encode(model);
        }
        catch (Exception e) when (e is not ArgumentException)
        {
            throw new ArgumentException($"part '{part.Name}': levels of detail could not be generated: {e.Message}", e);
        }
    }

    /// <summary>A folder name is cut to the 63 characters inventory names allow.</summary>
    static string Fit(string name) => name.Length <= BundleValidator.MaxNameLength ? name : name[..BundleValidator.MaxNameLength];
}
