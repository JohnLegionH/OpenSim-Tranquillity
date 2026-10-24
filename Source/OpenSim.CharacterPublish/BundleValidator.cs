/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish;

/// <summary>One reason a bundle is refused. <see cref="Code"/> is stable, <see cref="Where"/> names the asset, item or field.</summary>
public sealed record BundleProblem(string Code, string Where, string Message)
{
    public override string ToString() => $"[{Code}] {Where}: {Message}";
}

public sealed class ValidationResult
{
    public IReadOnlyList<BundleProblem> Problems { get; }
    public bool Ok => Problems.Count == 0;
    public ValidationResult(IReadOnlyList<BundleProblem> problems) => Problems = problems;
    public override string ToString() => Ok ? "ok" : string.Join(Environment.NewLine, Problems);
}

/// <summary>Size limits of one bundle. Robust may set tighter ones.</summary>
public sealed record BundleLimits(int MaxAssets = 64, int MaxAssetBytes = 16 * 1024 * 1024, long MaxTotalBytes = 64L * 1024 * 1024);

/// <summary>
/// The checks a bundle passes before anything is stored. They need no region code, so the inventory server can run them:
/// every id is derived from content, every mesh decodes with the shared library, every object passes the structural
/// check, every wearable parses with this project's reader and with the server-side baker's parser, the notecard parses,
/// types, flags and permissions agree between items, assets and objects, descriptions are safe, and every reference
/// resolves to an asset in the bundle or one declared as already on the grid.
/// </summary>
public static class BundleValidator
{
    public const int MaxNameLength = 63;
    public const int MaxDescriptionLength = 127;
    const uint AllPermissions = 0x7FFFFFFF;

    static readonly Regex SafeFile = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ValidationResult Validate(Bundle b, IReadOnlyDictionary<UUID, byte[]> content, BundleLimits? limits = null)
    {
        limits ??= new BundleLimits();
        var problems = new List<BundleProblem>();
        void Fail(string code, string where, string message) => problems.Add(new BundleProblem(code, where, message));

        if (b.FormatVersion != Bundle.CurrentFormatVersion) Fail("format", "bundle", $"format version {b.FormatVersion} is not {Bundle.CurrentFormatVersion}");
        if (b.OwnerId.IsZero()) Fail("format", "bundle", "owner id is empty");
        if (b.CreatorId.IsZero()) Fail("format", "bundle", "creator id is empty");
        if (b.FolderNumber < 1) Fail("format", "bundle", "folder number must be 1 or more");
        if (b.CreationDate <= 0) Fail("format", "bundle", "creation date must be set");
        if (string.IsNullOrWhiteSpace(b.Character)) Fail("format", "bundle", "character name is empty");

        // ---- assets: ids, types, files, content
        if (b.Assets.Count > limits.MaxAssets) Fail("limit", "assets", $"{b.Assets.Count} assets; the limit is {limits.MaxAssets}");
        var assets = new Dictionary<UUID, BundleAsset>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var a in b.Assets)
        {
            var where = $"asset {a.Id}";
            if (a.Id.IsZero() || !assets.TryAdd(a.Id, a)) { Fail("asset-id", where, "id is empty or listed twice"); continue; }
            if (!SafeFile.IsMatch(a.File) || a.File.Contains("..") || a.File.Equals("bundle.json", StringComparison.OrdinalIgnoreCase))
                Fail("asset-file", where, $"file name '{a.File}' is not a plain file name");
            else if (!files.Add(a.File)) Fail("asset-file", where, $"file name '{a.File}' is used twice");
            if (a.AssetType is not (BundleTypes.AssetMesh or BundleTypes.AssetObject or BundleTypes.AssetBodypart or BundleTypes.AssetClothing or BundleTypes.AssetNotecard))
                Fail("asset-type", where, $"asset type {a.AssetType} is not one a character publish stores");
            if (!content.TryGetValue(a.Id, out var bytes)) { Fail("missing-content", where, $"no bytes for {a.File}"); continue; }
            if (bytes.Length != a.Length) Fail("asset-length", where, $"listed as {a.Length} bytes, holds {bytes.Length}");
            if (bytes.Length == 0 || bytes.Length > limits.MaxAssetBytes) Fail("limit", where, $"{bytes.Length} bytes; allowed 1..{limits.MaxAssetBytes}");
            if (ContentId.OfBytes(bytes) != a.Id) Fail("asset-id", where, "id is not derived from the content");
            total += bytes.Length;
        }
        if (total > limits.MaxTotalBytes) Fail("limit", "assets", $"{total} bytes in all; the limit is {limits.MaxTotalBytes}");

        var external = new HashSet<UUID>(b.ExternalAssets);
        if (external.Contains(UUID.Zero)) Fail("dangling", "externalAssets", "lists the empty id");
        foreach (var e in external) if (assets.ContainsKey(e)) Fail("dangling", $"external {e}", "is also an asset of the bundle");
        bool Resolves(UUID id) => assets.ContainsKey(id) || external.Contains(id);

        // ---- folder
        var f = b.OutfitFolder;
        if (f.Key != Bundle.OutfitFolderKey) Fail("folder", "outfit folder", $"key is '{f.Key}', not '{Bundle.OutfitFolderKey}'");
        if (f.Type != BundleTypes.FolderOutfit) Fail("folder", "outfit folder", $"type {f.Type} is not Outfit ({BundleTypes.FolderOutfit})");
        if (f.Parent != Bundle.MyOutfitsParent) Fail("folder", "outfit folder", $"parent '{f.Parent}' is not '{Bundle.MyOutfitsParent}'");
        if (f.Id.IsZero()) Fail("folder", "outfit folder", "id is empty");
        if (f.OwnerId != b.OwnerId) Fail("permissions", "outfit folder", "owner differs from the bundle's");
        CheckName(f.Name, "outfit folder", "folder");

        // ---- items
        var ids = new HashSet<UUID> { f.Id };
        var referenced = new HashSet<UUID>();
        var seenWearableTypes = new HashSet<int>();
        if (b.Items.Count == 0) Fail("item", "items", "the bundle has no items");
        foreach (var it in b.Items)
        {
            var where = $"item '{it.Name}'";
            if (it.Id.IsZero() || !ids.Add(it.Id)) Fail("item", where, "id is empty or used twice");
            if (it.Folder != Bundle.OutfitFolderKey) Fail("item", where, $"folder '{it.Folder}' is not the outfit folder");
            CheckName(it.Name, where, "item");
            if (it.Description.Length > MaxDescriptionLength) Fail("description", where, $"description is longer than {MaxDescriptionLength} characters");
            if (!SafeDescription.IsSafe(it.Description)) Fail("description", where, "description starts with a wearable section keyword (type, parameters, textures)");
            if (it.Description.Contains('\n') || it.Description.Contains('\r') || it.Description.Contains('\0')) Fail("description", where, "description holds a control character");
            if (it.CreatorId != b.CreatorId) Fail("permissions", where, "creator differs from the bundle's");
            if (it.OwnerId != b.OwnerId) Fail("permissions", where, "owner differs from the bundle's");
            if (it.CreationDate <= 0) Fail("item", where, "creation date must be set");
            if (it.SalePrice < 0) Fail("item", where, "sale price is negative");
            CheckMasks(it.BasePermissions, it.CurrentPermissions, it.NextPermissions, it.GroupPermissions, it.EveryonePermissions, where, Fail);

            if (!assets.TryGetValue(it.AssetId, out var asset)) { Fail("dangling", where, $"asset {it.AssetId} is not in the bundle"); continue; }
            referenced.Add(asset.Id);
            if (it.AssetType != asset.AssetType) Fail("asset-type", where, $"item asset type {it.AssetType} differs from the asset's {asset.AssetType}");
            if (!content.TryGetValue(asset.Id, out var bytes)) continue;

            switch (asset.AssetType)
            {
                case BundleTypes.AssetBodypart or BundleTypes.AssetClothing:
                    CheckWearableItem(it, asset, bytes, b, seenWearableTypes, resolves: Resolves, Fail);
                    break;
                case BundleTypes.AssetObject:
                    CheckObjectItem(it, bytes, b, assets, content, referenced, Fail);
                    break;
                case BundleTypes.AssetNotecard:
                    if (it.InvType != BundleTypes.InvNotecard) Fail("item", where, $"inventory type {it.InvType} is not Notecard ({BundleTypes.InvNotecard})");
                    if (it.Flags != 0) Fail("item", where, $"flags {it.Flags:x} should be 0");
                    try { _ = NotecardReader.Read(bytes); }
                    catch (Exception e) { Fail("notecard", where, "does not parse: " + e.Message); }
                    break;
                default:
                    Fail("asset-type", where, $"asset type {asset.AssetType} has no item in an outfit");
                    break;
            }
        }

        // ---- assets nothing refers to (a mesh is referenced by an object, everything else by an item)
        foreach (var a in b.Assets)
            if (!referenced.Contains(a.Id)) Fail("unreferenced", $"asset {a.Id}", $"{a.File} is not used by any item or object");

        // ---- the current-look plan
        if (b.CurrentLook is { } p)
        {
            if (p.FolderId.IsZero() || !ids.Add(p.FolderId)) Fail("plan", "current look", "folder id is empty or used twice");
            if (p.FolderType != BundleTypes.FolderOutfit) Fail("plan", "current look", $"folder type {p.FolderType} is not Outfit ({BundleTypes.FolderOutfit})");
            if (p.Parent != Bundle.MyOutfitsParent) Fail("plan", "current look", $"parent '{p.Parent}' is not '{Bundle.MyOutfitsParent}'");
            if (p.SourceFolderType != BundleTypes.FolderCurrentOutfit) Fail("plan", "current look", $"source folder type {p.SourceFolderType} is not Current Outfit ({BundleTypes.FolderCurrentOutfit})");
            if (p.LinkAssetType != BundleTypes.AssetLink) Fail("plan", "current look", $"link asset type {p.LinkAssetType} is not Link ({BundleTypes.AssetLink})");
            if (p.LinkPermissions != AllPermissions) Fail("plan", "current look", $"link permissions {p.LinkPermissions:x} are not {AllPermissions:x}");
            if (p.OwnerId != b.OwnerId) Fail("permissions", "current look", "owner differs from the bundle's");
            CheckName(p.FolderName, "current look", "folder");
            var date = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, p.CreationDate)).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!p.FolderName.Contains(date, StringComparison.Ordinal)) Fail("plan", "current look", $"folder name '{p.FolderName}' does not carry the date {date}");
        }

        return new ValidationResult(problems);

        void CheckName(string name, string where, string what)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) Fail("item", where, $"{what} name must be 1..{MaxNameLength} characters");
            if (name.Contains('\n') || name.Contains('\r') || name.Contains('\0') || name.Contains('|')) Fail("item", where, $"{what} name holds a control character or '|'");
        }
    }

    static void CheckMasks(uint baseMask, uint owner, uint next, uint group, uint everyone, string where, Action<string, string, string> fail)
    {
        if ((owner & ~baseMask) != 0) fail("permissions", where, "owner mask has bits the base mask lacks");
        if ((next & ~baseMask) != 0) fail("permissions", where, "next owner mask has bits the base mask lacks");
        if ((group & ~baseMask) != 0) fail("permissions", where, "group mask has bits the base mask lacks");
        if ((everyone & ~baseMask) != 0) fail("permissions", where, "everyone mask has bits the base mask lacks");
    }

    static void CheckWearableItem(BundleItem it, BundleAsset asset, byte[] bytes, Bundle b, HashSet<int> seenTypes,
        Func<UUID, bool> resolves, Action<string, string, string> fail)
    {
        var where = $"item '{it.Name}'";
        if (it.InvType != BundleTypes.InvWearable) fail("item", where, $"inventory type {it.InvType} is not Wearable ({BundleTypes.InvWearable})");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { fail("wearable", where, "is not valid UTF-8"); return; }

        WearableDocument doc;
        try { doc = WearableReader.Read(text); }
        catch (Exception e) { fail("wearable", where, "does not parse with the wearable reader: " + e.Message); return; }
        try
        {
            var baker = WearableParser.Parse(text);
            if (baker.Kind != doc.Type) fail("wearable", where, $"the baker's parser reads type {(int)baker.Kind}, the reader {(int)doc.Type}");
            if (baker.Params.Count != doc.Parameters.Count || baker.Textures.Count != doc.Textures.Count)
                fail("wearable", where, "the baker's parser reads a different number of parameters or textures than the reader");
        }
        catch (Exception e) { fail("wearable", where, "does not parse with the baker's parser: " + e.Message); }

        var type = (int)doc.Type;
        if (!seenTypes.Add(type)) fail("item", where, $"a second wearable of type {type}; an outfit holds one of each body part");
        if (asset.AssetType != BundleTypes.AssetTypeOfWearable(type))
            fail("asset-type", where, $"wearable type {type} is stored as asset type {asset.AssetType}, expected {BundleTypes.AssetTypeOfWearable(type)}");
        // The wearable type is the low byte of the item's flags (viewer llinventorydefines.h LLInventoryItemFlags::II_FLAGS_SUBTYPE_MASK);
        // left at 0 the item is a shape.
        if ((it.Flags & 0xFF) != (uint)type) fail("flags", where, $"flags {it.Flags:x} carry wearable type {it.Flags & 0xFF}, but the asset is type {type}");
        if ((it.Flags & ~0xFFu) != 0) fail("flags", where, $"flags {it.Flags:x} have bits above the wearable type");
        if (doc.Name != it.Name) fail("wearable", where, "the wearable's name differs from the item's");
        if (doc.Description != it.Description) fail("wearable", where, "the wearable's description differs from the item's");
        if (!SafeDescription.IsSafe(doc.Description)) fail("description", where, "the wearable's description starts with a wearable section keyword (type, parameters, textures)");
        var p = doc.Permissions;
        if (p.BaseMask != it.BasePermissions || p.OwnerMask != it.CurrentPermissions || p.NextOwnerMask != it.NextPermissions
            || p.GroupMask != it.GroupPermissions || p.EveryoneMask != it.EveryonePermissions)
            fail("permissions", where, "the masks inside the wearable differ from the item's");
        if (p.Creator != b.CreatorId || p.Owner != b.OwnerId) fail("permissions", where, "creator or owner inside the wearable differs from the bundle's");
        foreach (var (slot, tex) in doc.Textures)
            if (tex.IsZero() || !resolves(tex)) fail("dangling", where, $"texture slot {slot} names {tex}, which is neither in the bundle nor declared as already on the grid");
    }

    static void CheckObjectItem(BundleItem it, byte[] bytes, Bundle b, Dictionary<UUID, BundleAsset> assets,
        IReadOnlyDictionary<UUID, byte[]> content, HashSet<UUID> referenced, Action<string, string, string> fail)
    {
        var where = $"item '{it.Name}'";
        if (it.InvType != BundleTypes.InvAttachment) fail("item", where, $"inventory type {it.InvType} is not Attachment ({BundleTypes.InvAttachment})");
        // A single-part object leaves ObjectHasMultipleItems and every other flag clear.
        if (it.Flags != 0) fail("flags", where, $"flags {it.Flags:x} should be 0 for a one-part object");

        var reasons = new List<string>();
        var facts = ObjectCheck.Check(bytes, reasons);
        foreach (var r in reasons) fail("object", where, r);
        if (facts == null) return;

        if (facts.Name != it.Name) fail("object", where, "the object's name differs from the item's");
        if (facts.Description != it.Description) fail("object", where, "the object's description differs from the item's");
        if (!SafeDescription.IsSafe(facts.Description)) fail("description", where, "the object's description starts with a wearable section keyword (type, parameters, textures)");
        if (facts.Creator != b.CreatorId || facts.Owner != b.OwnerId) fail("permissions", where, "creator or owner inside the object differs from the bundle's");
        if (facts.BaseMask != it.BasePermissions || facts.OwnerMask != it.CurrentPermissions || facts.NextOwnerMask != it.NextPermissions
            || facts.GroupMask != it.GroupPermissions || facts.EveryoneMask != it.EveryonePermissions)
            fail("permissions", where, "the masks inside the object differ from the item's");

        if (!assets.TryGetValue(facts.SculptTexture, out var mesh))
        {
            fail("dangling", where, $"the object names mesh {facts.SculptTexture}, which is not in the bundle");
            return;
        }
        referenced.Add(mesh.Id);
        if (mesh.AssetType != BundleTypes.AssetMesh) { fail("asset-type", where, $"the object's mesh {mesh.Id} is asset type {mesh.AssetType}, not Mesh"); return; }
        if (!content.TryGetValue(mesh.Id, out var meshBytes)) return;
        MeshInfo info;
        try { info = MeshInspector.Inspect(meshBytes); }
        catch (Exception e) { fail("mesh", $"asset {mesh.Id}", "does not decode with the mesh library: " + e.Message); return; }
        if (info.FaceCount > 8) fail("mesh", $"asset {mesh.Id}", $"{info.FaceCount} faces; an object shows at most 8");
        if (info.NormalizedScale is { } ns)
        {
            // The object takes the size the model had before the uploader normalised it, as the viewer's upload does.
            if (!Near(ns.X, facts.ScaleX) || !Near(ns.Y, facts.ScaleY) || !Near(ns.Z, facts.ScaleZ))
                fail("object", where, $"size {facts.ScaleX} x {facts.ScaleY} x {facts.ScaleZ} differs from the mesh's NormalizedScale {ns.X} x {ns.Y} x {ns.Z}");
        }
    }

    static bool Near(float a, float b) => MathF.Abs(a - b) <= 1e-3f * MathF.Max(1f, MathF.Abs(a));
}
